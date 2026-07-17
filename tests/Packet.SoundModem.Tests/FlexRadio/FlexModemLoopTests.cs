using M0LTE.Radio.Audio;
using M0LTE.Flex;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// The decisive offline proof: a real modem's audio out through the Flex TX path
/// (<see cref="FlexAudioOutput"/> → mock DAX-TX capture), replayed back in as DAX-RX
/// (<see cref="FlexAudioInput"/>), through the demodulator — byte-exact frame recovery,
/// with no hardware, driven through the <b>headless</b> bring-up (the default
/// <c>--device flex:</c> deployment: GUI-register + create-slice, no SmartSDR). Exercises the
/// whole path end to end: the headless setup, the <c>IAudioInput</c> refactor, the VITA
/// packetize/depacketize, and the sample-rate bridge (12 kHz ↔ reduced-bw 24 kHz s16; 48 kHz ↔
/// full-bw 48 kHz float32). See docs/flex-integration.md §5/§8.
/// </summary>
public sealed class FlexModemLoopTests
{
    private static byte[] SampleFrame(int length)
    {
        var frame = new byte[length];
        byte[] header =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0,
        ];
        Array.Copy(header, frame, Math.Min(header.Length, length));
        if (length > header.Length)
        {
            new Random(7).NextBytes(frame.AsSpan(header.Length));
        }

        return frame;
    }

    [Fact]
    public async Task Afsk1200_recovers_byte_exact_through_the_reduced_bandwidth_dax_loop()
    {
        byte[] frame = SampleFrame(30);
        List<byte[]> decoded = await RunLoopAsync(
            dspRate: 12000, sink => new Afsk1200Modem(12000, sink), frame);

        decoded.Should().ContainSingle();
        decoded[0].Should().Equal(frame);
    }

    [Fact]
    public async Task Freedv_datac3_recovers_byte_exact_through_the_full_bandwidth_dax_loop()
    {
        byte[] frame = SampleFrame(60);
        List<byte[]> decoded = await RunLoopAsync(
            dspRate: 48000, sink => FreeDvDatacModem.Datac3(48000, sink), frame);

        decoded.Should().ContainSingle();
        decoded[0].Should().Equal(frame);
    }

    private static async Task<List<byte[]>> RunLoopAsync(
        int dspRate, Func<Action<byte[]>, IModem> modemFactory, byte[] frame)
    {
        DaxStreamFormat format = DaxStreamFormat.ForDspRate(dspRate);
        // Headless mock (no SmartSDR) — the default deployment. MockSetupMode.Headless is the
        // constructor default; spelled out here for clarity.
        var mock = new MockFlexRadio(format, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var mockLifetime = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, format, new FlexStationOptions { Keepalive = false });

        // Deterministic offline transport: deliver the DAX VITA packets in-process (lossless)
        // rather than over real loopback UDP. A full OFDM burst is hundreds of DAX packets and
        // FreeDV has no per-packet retransmit, so one dropped datagram would break byte-exact
        // decode (~1-in-5 flake). The point under test is that the packetize/depacketize +
        // rate-bridge is byte-correct — not that UDP is lossless — and the real
        // FlexAudioOutput/FlexAudioInput code (reorder ring included) is still exercised.
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        // --- Transmit: modulate the frame through the real channel → Flex DAX TX. ---
        var txChannel = new SoundModemChannel(dspRate, randomSeed: 1);
        txChannel.Csma.Persistence = 255; // transmit on the first clear roll
        txChannel.AddModem(0, modemFactory);

        FlexAudioOutput flexOutput = station.CreateAudioOutput(paceRealTime: false);
        IAudioOutput output = format.SampleRate == dspRate
            ? flexOutput
            : new UpsamplingAudioOutput(flexOutput, dspRate);
        FlexPtt ptt = station.CreatePtt();

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            Task transmitter = txChannel.RunTransmitterAsync(output, ptt, cts.Token);
            await txChannel.EnqueueTransmit(0, frame).WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300); // let the TX-tail packets reach the mock
            await cts.CancelAsync();
            try
            {
                await transmitter;
            }
            catch (OperationCanceledException)
            {
            }
        }

        float[] captured = [.. mock.CapturedTxSamples];
        captured.Should().NotBeEmpty();

        // --- Receive: replay the captured DAX-TX audio back in as DAX-RX, then demodulate. ---
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);
        int expectedPackets = (captured.Length + format.SamplesPerPacket - 1) / format.SamplesPerPacket;
        await mock.ReplayRxAsync(captured);
        await WaitForAsync(() => input.PacketsReceived >= expectedPackets);
        input.Flush();

        var decoded = new List<byte[]>();
        var rxChannel = new SoundModemChannel(dspRate);
        rxChannel.AddModem(0, modemFactory);
        rxChannel.FrameReceived += (_, f) => decoded.Add(f);

        Decimator? decimator = format.SampleRate == dspRate
            ? null
            : new Decimator(format.SampleRate, format.SampleRate / dspRate);
        var buffer = new float[4096];
        var dsp = new float[decimator?.MaxOutput(buffer.Length) ?? buffer.Length];
        int got;
        while ((got = input.Read(buffer)) > 0)
        {
            if (decimator is null)
            {
                rxChannel.ProcessReceive(buffer.AsSpan(0, got));
            }
            else
            {
                int produced = decimator.Process(buffer.AsSpan(0, got), dsp);
                rxChannel.ProcessReceive(dsp.AsSpan(0, produced));
            }
        }

        // A trailing silence flush so the demodulator closes the final frame.
        rxChannel.ProcessReceive(new float[dspRate / 2]);
        return decoded;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }
}
