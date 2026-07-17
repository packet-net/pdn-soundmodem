using System.Diagnostics;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using M0LTE.Il2p;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ofdm;
using Packet.SoundModem.Tests.Channel;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The FreeDV datac modes through the <see cref="IModem"/> surface on the 48 kHz DSP path:
/// Modulate (upsample ×6 inside) → audio → Process (decimate ÷6 inside) → frames. The
/// payload content under test is the pdn convention — the family-standard IL2P+CRC bit
/// stream packed into the fixed-size datac payloads (see <see cref="FreeDvDatacModem"/>).
/// </summary>
public class FreeDvDatacModemTests(ITestOutputHelper output)
{
    private const int DspRate = 48000;

    /// <summary>An AX.25-looking frame: UI header then deterministic pseudo-random body.</summary>
    private static byte[] SampleFrame(int length)
    {
        byte[] frame = new byte[length];
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

    private static FreeDvDatacModem Make(string mode, Action<byte[]> sink, int rate = DspRate) => mode switch
    {
        "freedv-datac0" => FreeDvDatacModem.Datac0(rate, sink),
        "freedv-datac1" => FreeDvDatacModem.Datac1(rate, sink),
        "freedv-datac3" => FreeDvDatacModem.Datac3(rate, sink),
        "freedv-datac4" => FreeDvDatacModem.Datac4(rate, sink),
        "freedv-datac13" => FreeDvDatacModem.Datac13(rate, sink),
        "freedv-datac14" => FreeDvDatacModem.Datac14(rate, sink),
        _ => throw new ArgumentException(mode),
    };

    /// <summary>Feeds audio in 100 ms blocks — the daemon's capture block size — so the
    /// streaming (FIFO/Nin) path is what gets exercised, not one giant span.</summary>
    private static void FeedBlocks(IModem modem, ReadOnlySpan<float> samples, int rate)
    {
        int block = rate / 10;
        for (int pos = 0; pos < samples.Length; pos += block)
        {
            modem.Process(samples.Slice(pos, Math.Min(block, samples.Length - pos)));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Round trips through the full IModem surface at 48 kHz.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("freedv-datac0", 30)]    // IL2P wire 52 B spans 4 of datac0's 14-byte packets —
                                         // the spanning capability the bit-stream framing exists for
    [InlineData("freedv-datac0", 60)]    // spans 6 packets
    [InlineData("freedv-datac3", 60)]    // typical UI frame, single 126-byte packet
    [InlineData("freedv-datac3", 124)]   // spans 2 packets
    [InlineData("freedv-datac1", 60)]    // single 510-byte packet
    [InlineData("freedv-datac1", 508)]   // spans 2 packets — datac1's first end-to-end burst
    [InlineData("freedv-datac1", 1000)]  // spans 3 packets — no hard frame cap below IL2P's own
    [InlineData("freedv-datac4", 60)]    // narrow RX-BPF mode, spans 2 of datac4's 54-byte packets
    [InlineData("freedv-datac13", 60)]   // narrowest mode (200 Hz), spans 6 of its 14-byte packets
    [InlineData("freedv-datac14", 60)]   // 3-byte packets — the extreme spanning case (~28 packets)
    public void Round_Trips_A_Frame_Through_The_IModem_Surface_At_48k(string mode, int frameBytes)
    {
        byte[] frame = SampleFrame(frameBytes);
        var received = new List<byte[]>();
        var qualities = new List<FrameQuality>();

        var tx = Make(mode, _ => { });
        var rx = Make(mode, received.Add);
        rx.FrameDecoded += (_, quality) => qualities.Add(quality);

        var stopwatch = Stopwatch.StartNew();
        float[] audio = tx.Modulate(frame, txDelayMilliseconds: 100);
        long modulateMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        FeedBlocks(rx, [.. audio, .. new float[DspRate / 2]], DspRate);
        stopwatch.Stop();

        output.WriteLine(
            $"{mode} {frameBytes} B: {audio.Length / (double)DspRate:F2} s of audio, " +
            $"modulate {modulateMs} ms, demodulate {stopwatch.ElapsedMilliseconds} ms, " +
            $"{received.Count} frame(s)");

        received.Should().ContainSingle();
        received[0].Should().Equal(frame);
        qualities.Should().ContainSingle();
        qualities[0].Mode.Should().Be(mode);
        qualities[0].CrcValid.Should().BeTrue();
        qualities[0].CorrectedBytes.Should().Be(0, "a clean loopback needs no RS correction");
        qualities[0].FrameBytes.Should().Be(frameBytes);
    }

    [Fact]
    public void Round_Trips_Back_To_Back_Bursts_As_The_Channel_Sends_Them()
    {
        // Two Modulate calls played back-to-back — the channel's one-keyup shape, where the
        // second frame gets only a 30 ms token TXDELAY. The first burst's trailing guard
        // silence must cover the receiver's end-of-burst window so the second burst's
        // preamble is not eaten by a still-synced demodulator.
        byte[] first = SampleFrame(30);
        byte[] second = SampleFrame(24);
        var received = new List<byte[]>();

        var tx = FreeDvDatacModem.Datac0(DspRate, _ => { });
        var rx = FreeDvDatacModem.Datac0(DspRate, received.Add);

        float[] audio = [.. tx.Modulate(first, 100), .. tx.Modulate(second, 30), .. new float[DspRate / 2]];
        FeedBlocks(rx, audio, DspRate);

        received.Should().HaveCount(2);
        received[0].Should().Equal(first);
        received[1].Should().Equal(second);
    }

    [Fact]
    public void Round_Trips_At_The_Native_8k_Rate_Without_Resamplers()
    {
        // factor == 1: no decimator, no upsampler — the engine-native path.
        byte[] frame = SampleFrame(60);
        var received = new List<byte[]>();

        var tx = FreeDvDatacModem.Datac3(8000, _ => { });
        var rx = FreeDvDatacModem.Datac3(8000, received.Add);

        float[] audio = tx.Modulate(frame, 50);
        FeedBlocks(rx, [.. audio, .. new float[4000]], 8000);

        received.Should().ContainSingle();
        received[0].Should().Equal(frame);
    }

    // ---------------------------------------------------------------------------------------
    // The IL2P payload stream: several frames per packet, garbage and corruption rejection.
    // ---------------------------------------------------------------------------------------

    /// <summary>Renders raw datac payloads as a 48 kHz burst the modem's receive side can eat.</summary>
    private static float[] BurstAudio48k(OfdmMode mode, byte[][] payloads)
    {
        var tx = new DatacTransmitter(mode);
        Cf[] burst = tx.ModulateBurst(payloads);
        var native = new float[800 + burst.Length + 3200];
        for (int i = 0; i < burst.Length; i++)
        {
            native[800 + i] = burst[i].Re * (1f / 32768f);
        }

        var upsampler = new Upsampler(DspRate, DspRate / 8000);
        var audio = new float[upsampler.OutputLength(native.Length)];
        upsampler.Process(native, audio);
        return audio;
    }

    [Fact]
    public void Two_Frames_Inside_One_Packet_Payload_Both_Decode()
    {
        // The payload carries a bit stream, so nothing stops two small IL2P frames sharing
        // one datac3 payload byte-aligned back-to-back — the deframer must emit both.
        byte[] first = SampleFrame(15);
        byte[] second = SampleFrame(16);
        byte[] wireA = [.. Il2pFramer.FrameBits(Il2pCodec.Encode(first, appendCrc: true), 0).Chunk(8).Select(PackByte)];
        byte[] wireB = [.. Il2pFramer.FrameBits(Il2pCodec.Encode(second, appendCrc: true), 0).Chunk(8).Select(PackByte)];
        var payload = new byte[126];
        (wireA.Length + wireB.Length).Should().BeLessThanOrEqualTo(126, "both frames must fit one datac3 payload");
        wireA.CopyTo(payload, 0);
        wireB.CopyTo(payload, wireA.Length);

        var received = new List<byte[]>();
        var rx = FreeDvDatacModem.Datac3(DspRate, received.Add);
        FeedBlocks(rx, [.. BurstAudio48k(OfdmMode.Datac3, [payload]), .. new float[DspRate / 2]], DspRate);

        received.Should().HaveCount(2);
        received[0].Should().Equal(first);
        received[1].Should().Equal(second);

        static byte PackByte(byte[] bits)
        {
            byte value = 0;
            for (int i = 0; i < bits.Length; i++)
            {
                value |= (byte)(bits[i] << (7 - i));
            }

            return value;
        }
    }

    [Fact]
    public void Non_Il2p_Payload_Bits_Yield_No_Frame()
    {
        // A CRC-valid datac packet whose payload is not an IL2P stream (what a non-pdn
        // FreeDV sender would produce): the sync hunt + Reed-Solomon must reject it all.
        var payload = new byte[126];
        new Random(11).NextBytes(payload);

        var received = new List<byte[]>();
        var rx = FreeDvDatacModem.Datac3(DspRate, received.Add);
        FeedBlocks(rx, [.. BurstAudio48k(OfdmMode.Datac3, [payload]), .. new float[DspRate / 2]], DspRate);

        received.Should().BeEmpty();
    }

    [Fact]
    public void A_Corrupted_Packet_Mid_Burst_Yields_No_Frame()
    {
        // Bury one second of a 2-packet burst under full-scale noise — far beyond what the
        // rate-½ LDPC can repair (a first cut merely ZEROED 100 ms and the LDPC calmly
        // corrected it: near-zero samples are erasures, well inside the code's budget). The
        // damaged packet fails its LDPC/CRC (or sync), the bit stream has a hole, and
        // IL2P's Reed-Solomon/CRC must reject the frame — corruption never surfaces as data.
        byte[] frame = SampleFrame(124);
        var received = new List<byte[]>();

        var tx = FreeDvDatacModem.Datac3(DspRate, _ => { });
        var rx = FreeDvDatacModem.Datac3(DspRate, received.Add);

        float[] audio = tx.Modulate(frame, 100);
        var noise = new Random(13);
        for (int i = 0; i < DspRate; i++)
        {
            audio[(audio.Length * 2 / 5) + i] = (float)((noise.NextDouble() * 2.0) - 1.0);
        }

        FeedBlocks(rx, [.. audio, .. new float[DspRate / 2]], DspRate);

        received.Should().BeEmpty();
    }

    [Fact]
    public void Oversize_And_Empty_Frames_Are_Rejected()
    {
        // The bounds are IL2P's own (Il2pCodec.Encode), exactly as the family's other
        // il2pc modes reject — no mode-specific cap exists since frames span packets.
        var modem = FreeDvDatacModem.Datac1(DspRate, _ => { });
        var oversize = () => modem.Modulate(new byte[Il2pCodec.MaxPayloadBytes + 1], 0);
        oversize.Should().Throw<ArgumentException>().WithMessage("*exceeds the IL2P maximum*");

        var empty = () => modem.Modulate([], 0);
        empty.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------------------
    // Carrier sense.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Busy_Asserts_During_A_Burst_And_Clears_After()
    {
        byte[] frame = SampleFrame(30);
        var tx = FreeDvDatacModem.Datac0(DspRate, _ => { });
        var rx = FreeDvDatacModem.Datac0(DspRate, _ => { });

        float[] audio = tx.Modulate(frame, 100);
        bool sawEnergyBusy = false;
        bool sawCarrierDetect = false;
        int block = DspRate / 100;   // 10 ms blocks to sample the flags densely
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            rx.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            sawEnergyBusy |= rx.ChannelBusy;
            sawCarrierDetect |= rx.CarrierDetect;
        }

        sawEnergyBusy.Should().BeTrue("the energy detector must flag the burst");
        sawCarrierDetect.Should().BeTrue("burst sync must assert packet DCD once acquired");

        // A second of silence: the end-of-burst drop and the energy hold must both release.
        rx.Process(new float[DspRate]);
        rx.CarrierDetect.Should().BeFalse("sync must drop after the burst");
        rx.ChannelBusy.Should().BeFalse("energy busy must release in silence");
    }

    // ---------------------------------------------------------------------------------------
    // Channel wiring — mirrors ChannelLoopbackTests.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Channel_Wires_FreeDv_Datac_Transmit_To_Receive()
    {
        byte[] first = SampleFrame(30);
        byte[] second = SampleFrame(24);
        var txChannel = new SoundModemChannel(DspRate, randomSeed: 42);
        txChannel.AddModem(0, sink => FreeDvDatacModem.Datac0(DspRate, sink));
        txChannel.Csma.Persistence = 255;
        txChannel.Csma.TxDelayMilliseconds = 100;

        var outputDevice = new FakeAudioOutput(DspRate);
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = txChannel.RunTransmitterAsync(outputDevice, ptt, cancellation.Token);
        Task[] completions = [txChannel.EnqueueTransmit(0, first), txChannel.EnqueueTransmit(0, second)];
        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().Equal("key", "unkey");

        var received = new List<(int SubChannel, byte[] Frame)>();
        var qualities = new List<FrameQuality>();
        var rxChannel = new SoundModemChannel(DspRate);
        rxChannel.AddModem(0, sink => FreeDvDatacModem.Datac0(DspRate, sink));
        rxChannel.FrameReceived += (subChannel, frame) => received.Add((subChannel, frame));
        rxChannel.FrameReceivedWithQuality += (_, _, quality) => qualities.Add(quality);
        rxChannel.ProcessReceive([.. outputDevice.Snapshot(), .. new float[DspRate / 2]]);

        received.Should().HaveCount(2);
        received[0].SubChannel.Should().Be(0);
        received[0].Frame.Should().Equal(first);
        received[1].Frame.Should().Equal(second);
        qualities.Should().HaveCount(2);
        qualities[0].Mode.Should().Be("freedv-datac0");
    }

    [Fact]
    public async Task Channel_Drops_An_Oversize_Frame_And_Keeps_Transmitting()
    {
        byte[] good = SampleFrame(30);
        var channel = new SoundModemChannel(DspRate, randomSeed: 42);
        channel.AddModem(0, sink => FreeDvDatacModem.Datac0(DspRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 50;
        var rejections = new List<(int SubChannel, Exception Error)>();
        channel.TransmitRejected += (subChannel, _, error) => rejections.Add((subChannel, error));

        var outputDevice = new FakeAudioOutput(DspRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(outputDevice, new RecordingPtt(), cancellation.Token);

        Task oversize = channel.EnqueueTransmit(0, new byte[Il2pCodec.MaxPayloadBytes + 1]);
        await oversize.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(20)))
            .Should().ThrowAsync<ArgumentException>("the rejected frame's task must fault");

        // The transmitter loop must survive the rejection and still send the next frame.
        await channel.EnqueueTransmit(0, good).WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        rejections.Should().ContainSingle().Which.SubChannel.Should().Be(0);

        var received = new List<byte[]>();
        var rxChannel = new SoundModemChannel(DspRate);
        rxChannel.AddModem(0, sink => FreeDvDatacModem.Datac0(DspRate, sink));
        rxChannel.FrameReceived += (_, frame) => received.Add(frame);
        rxChannel.ProcessReceive([.. outputDevice.Snapshot(), .. new float[DspRate / 2]]);

        received.Should().ContainSingle();
        received[0].Should().Equal(good);
    }
}
