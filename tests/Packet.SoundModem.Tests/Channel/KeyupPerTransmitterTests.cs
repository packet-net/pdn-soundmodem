using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A keyup carries one transmitter's traffic. Two modems sharing an audio channel share the PA,
/// not the PTT.
/// </summary>
/// <remarks>
/// GB7RDG-2 runs afsk300-il2pc at 850 Hz and bpsk300 at 2150 Hz on one Flex slice. The
/// transmitter used to drain whatever was queued into a single keyup, so a frame for one modem
/// went out behind a frame for the other: one burst starting at 850 Hz and finishing at 2150 Hz,
/// which on the waterfall reads as a frame torn in half. Both frames were whole - but the station
/// stayed keyed, and so deaf, through the appended one: of the 83 frames in that station's frame
/// log sent with another modem's frame appended behind them, 2 were answered within 6 s, against
/// 35.5 % of the 14,634 that ended their keyup.
/// </remarks>
public class KeyupPerTransmitterTests
{
    private const int SampleRate = 12000;

    /// <summary>Records the bursts a keyup is made of.</summary>
    private sealed class BurstRecorder(int sampleRate) : IAudioOutput
    {
        public List<int> BurstLengths { get; } = [];

        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples) => BurstLengths.Add(samples.Length);

        public void Drain()
        {
        }
    }

    private static byte[] Frame(byte marker)
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame.AsSpan(16).Fill(marker);
        return frame;
    }

    private static SoundModemChannel Station()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;         // never defer, so the keyups are the test's own
        channel.Csma.TxDelayMilliseconds = 300; // the shipped default, and GB7RDG-2's
        return channel;
    }

    /// <summary>
    /// Queues everything before the transmitter starts, so the frames are certainly all waiting
    /// when it keys - the live station's race, made deterministic.
    /// </summary>
    private static async Task<(BurstRecorder Output, RecordingPtt Ptt)> TransmitAsync(
        SoundModemChannel channel, params (int SubChannel, byte[] Frame)[] frames)
    {
        var completions = frames.Select(f => channel.EnqueueTransmit(f.SubChannel, f.Frame)).ToArray();
        return await RunAsync(channel, completions);
    }

    private static async Task<(BurstRecorder Output, RecordingPtt Ptt)> RunAsync(
        SoundModemChannel channel, params Task[] completions)
    {
        var output = new BurstRecorder(SampleRate);
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);
        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(25));

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return (output, ptt);
    }

    [Fact]
    public async Task Two_Modems_Queued_Together_Take_A_Keyup_Each()
    {
        (_, RecordingPtt ptt) = await TransmitAsync(Station(), (0, Frame(0x41)), (2, Frame(0x42)));

        ptt.Events.Should().Equal(
            ["key", "unkey", "key", "unkey"],
            "the second modem's frame must contend for its own keyup rather than lengthen the "
            + "first modem's, because the station is deaf for as long as it stays keyed");
    }

    [Fact]
    public async Task One_Modems_Frames_Still_Share_A_Keyup()
    {
        (_, RecordingPtt ptt) = await TransmitAsync(Station(), (0, Frame(0x41)), (0, Frame(0x42)));

        ptt.Events.Should().Equal(
            ["key", "unkey"],
            "back-to-back frames on ONE modem are what a shared keyup is for: the far end is "
            + "already locked to the waveform, so the second needs no preamble of its own");
    }

    [Fact]
    public async Task A_Frame_That_Changes_Modem_Gets_A_Full_Txdelay_Again()
    {
        (BurstRecorder shared, _) = await TransmitAsync(Station(), (0, Frame(0x41)), (0, Frame(0x42)));
        (BurstRecorder separate, _) = await TransmitAsync(Station(), (0, Frame(0x41)), (2, Frame(0x42)));

        // Two frames plus a TX tail per keyup: one tail when they share, two when they do not.
        shared.BurstLengths.Should().HaveCount(3);
        separate.BurstLengths.Should().HaveCount(4);

        int savedByTheToken = SampleRate * (300 - 30) / 1000;
        shared.BurstLengths[1].Should().BeLessThan(
            shared.BurstLengths[0] - (savedByTheToken / 2),
            "the second frame on the same modem rides the first frame's preamble");

        // The 850 Hz burst that preceded it taught a 2150 Hz receiver nothing, so the token
        // preamble's premise does not hold and the frame pays for TXDELAY like any keyup's first.
        separate.BurstLengths[2].Should().BeGreaterThan(
            shared.BurstLengths[1] + (savedByTheToken / 2),
            "a frame that changes modem starts a keyup, and a keyup's first frame gets TXDELAY");
    }

    [Fact]
    public async Task A_Service_Transmitter_Does_Not_Ride_On_A_Modems_Keyup()
    {
        SoundModemChannel channel = Station();
        Task frame = channel.EnqueueTransmit(0, Frame(0x41));
        Task service = channel.EnqueueTransmit(_ => new float[SampleRate / 10]);

        (_, RecordingPtt ptt) = await RunAsync(channel, frame, service);

        ptt.Events.Should().Equal(
            ["key", "unkey", "key", "unkey"],
            "paging, ARDOP and the CW ident name themselves so their own runs share a keyup; a "
            + "transmitter that names nobody is nobody's keyup-mate");
    }
}
