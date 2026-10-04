using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The channel's half of packing several frames into one burst: frames queued together on a
/// packing modem go out as one rendered burst under one keyup, each still announced, answered
/// and timed as its own transmission.
/// </summary>
/// <remarks>
/// Native 9600 Hz and WN6 to keep the audio short. Everything is queued before the transmitter
/// starts, or the clock is fake, so nothing here depends on how fast the box is.
/// </remarks>
public class PackedBurstTests
{
    private const int SampleRate = 9600;

    /// <summary>Records each write the transmitter makes, and keeps the audio.</summary>
    private sealed class BurstRecorder(int sampleRate) : IAudioOutput
    {
        private readonly List<float[]> _writes = [];

        public int SampleRate { get; } = sampleRate;

        public IReadOnlyList<float[]> Writes
        {
            get
            {
                lock (_writes)
                {
                    return [.. _writes];
                }
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            lock (_writes)
            {
                _writes.Add(samples.ToArray());
            }
        }

        public void Drain()
        {
        }
    }

    private static byte[] UiFrame(int seed, int payload)
    {
        byte[] header = [0x9A, 0x86, 0x82, 0xA6, 0xA8, 0x40, 0x60, 0x8E, 0x84, 0x6E, 0xA4, 0x88, 0x8E, 0x61, 0x03, 0xF0];
        var frame = new byte[header.Length + payload];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(header.Length));
        return frame;
    }

    private static (SoundModemChannel Channel, Ms110dModem Modem) Station(
        FramePacking? packing, TimeProvider? time = null)
    {
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        Ms110dModem? modem = null;
        channel.AddModem(0, sink => modem = new Ms110dModem(SampleRate, sink) { Packing = packing });
        channel.Csma.Persistence = 255; // never defer, so the keyups are the test's own
        channel.Csma.TxDelayMilliseconds = 100;
        return (channel, modem!);
    }

    private static FramePacking Packing(double maxSeconds = 30, double gatherSeconds = 0) =>
        new(TimeSpan.FromSeconds(maxSeconds), TimeSpan.FromSeconds(gatherSeconds));

    private static async Task<(BurstRecorder Output, RecordingPtt Ptt)> RunAsync(
        SoundModemChannel channel, IEnumerable<Task> completions)
    {
        var output = new BurstRecorder(SampleRate);
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);
        await Task.WhenAll(completions.Select(c => c.ContinueWith(_ => { }, TaskScheduler.Default)))
            .WaitAsync(TimeSpan.FromSeconds(55));
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

    private static List<byte[]> Decode(IEnumerable<float[]> writes)
    {
        var received = new List<byte[]>();
        var rx = new Ms110dModem(SampleRate, received.Add);
        rx.Process(new float[1000]);
        foreach (float[] write in writes)
        {
            rx.Process(write);
        }

        rx.Process(new float[6000]);
        return received;
    }

    [Fact]
    public async Task Without_Packing_Every_Frame_Is_A_Burst_Of_Its_Own()
    {
        (SoundModemChannel channel, _) = Station(packing: null);
        byte[][] frames = [UiFrame(1, 80), UiFrame(2, 80), UiFrame(3, 80)];
        var sent = new List<byte[]>();
        channel.FrameTransmitted += (_, frame) => sent.Add(frame);

        (BurstRecorder output, RecordingPtt ptt) = await RunAsync(
            channel, frames.Select(f => channel.EnqueueTransmit(0, f)).ToList());

        output.Writes.Should().HaveCount(4, "three bursts and the TX tail, as before packing existed");
        ptt.Events.Should().Equal("key", "unkey");
        sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task Frames_Queued_Together_Go_Out_As_One_Burst_And_Each_Is_Announced()
    {
        (SoundModemChannel channel, _) = Station(Packing());
        byte[][] frames = [UiFrame(1, 80), UiFrame(2, 300), UiFrame(3, 5), UiFrame(4, 120)];
        var sent = new List<byte[]>();
        var reports = new List<TransmitReport>();
        channel.FrameTransmitted += (_, frame) => { lock (sent) { sent.Add(frame); } };
        channel.FrameTransmittedWithReport += (_, _, report) => { lock (reports) { reports.Add(report); } };
        List<Task> completions = frames.Select(f => channel.EnqueueTransmit(0, f)).ToList();

        (BurstRecorder output, RecordingPtt ptt) = await RunAsync(channel, completions);

        completions.Should().OnlyContain(c => c.IsCompletedSuccessfully);
        output.Writes.Should().HaveCount(2, "one packed burst and the TX tail");
        ptt.Events.Should().Equal("key", "unkey");
        reports.Should().HaveCount(4, "each frame is still its own transmission to the host");
        // The order on the air is the decode below: the announcements are raised from each
        // frame's own continuation, as they always have been, so their order is not the point.
        sent.Should().BeEquivalentTo(frames);

        List<byte[]> heard = Decode(output.Writes);
        heard.Should().HaveCount(4);
        for (int i = 0; i < frames.Length; i++)
        {
            heard[i].Should().Equal(frames[i]);
        }
    }

    [Fact]
    public async Task A_Run_Longer_Than_The_Limit_Is_Split_Into_Bursts_Within_It()
    {
        // WN6 carries 3200 bps: 300 bytes is about a second of air behind a 0.7 s preamble, so a
        // 3 s limit takes two of them per burst, and six frames need several bursts.
        (SoundModemChannel channel, _) = Station(Packing(maxSeconds: 3));
        byte[][] frames = [.. Enumerable.Range(1, 6).Select(i => UiFrame(i, 300))];
        var sent = new List<byte[]>();
        channel.FrameTransmitted += (_, frame) => { lock (sent) { sent.Add(frame); } };

        (BurstRecorder output, RecordingPtt ptt) = await RunAsync(
            channel, frames.Select(f => channel.EnqueueTransmit(0, f)).ToList());

        IReadOnlyList<float[]> bursts = output.Writes.Take(output.Writes.Count - 1).ToList();
        bursts.Count.Should().BeInRange(2, 5, "packed, but never past the limit");
        int first = bursts[0].Length - (SampleRate * 100 / 1000);
        (first / (double)SampleRate).Should().BeLessThanOrEqualTo(3.0);
        foreach (float[] later in bursts.Skip(1))
        {
            ((later.Length - (SampleRate * 30 / 1000)) / (double)SampleRate).Should().BeLessThanOrEqualTo(3.0);
        }

        ptt.Events.Should().Equal("key", "unkey");
        sent.Should().BeEquivalentTo(frames);
        List<byte[]> heard = Decode(output.Writes);
        heard.Should().HaveCount(6);
        heard.Zip(frames).Should().OnlyContain(pair => pair.First.SequenceEqual(pair.Second));
    }

    [Fact]
    public async Task A_Frame_The_Modem_Refuses_Goes_Alone_And_The_Rest_Still_Go()
    {
        (SoundModemChannel channel, _) = Station(Packing());
        byte[] tooLong = UiFrame(3, 1100);
        byte[][] frames = [UiFrame(1, 80), UiFrame(2, 80), tooLong, UiFrame(4, 80)];
        var sent = new List<byte[]>();
        var refused = new List<byte[]>();
        channel.FrameTransmitted += (_, frame) => { lock (sent) { sent.Add(frame); } };
        channel.TransmitRejected += (_, frame, _) => { lock (refused) { refused.Add(frame); } };
        List<Task> completions = frames.Select(f => channel.EnqueueTransmit(0, f)).ToList();

        (BurstRecorder output, _) = await RunAsync(channel, completions);

        refused.Should().ContainSingle().Which.Should().Equal(tooLong);
        completions[2].IsFaulted.Should().BeTrue();
        sent.Should().BeEquivalentTo(new[] { frames[0], frames[1], frames[3] });
        output.Writes.Should().HaveCount(3, "the two before it in one burst, the one after in another, and the tail");
    }

    [Fact]
    public async Task The_Gather_Holds_The_First_Frame_So_The_Next_One_Joins_Its_Burst()
    {
        var time = new FakeTimeProvider();
        (SoundModemChannel channel, _) = Station(Packing(gatherSeconds: 2), time);
        var reports = new List<TransmitReport>();
        channel.FrameTransmittedWithReport += (_, _, report) => { lock (reports) { reports.Add(report); } };

        var output = new BurstRecorder(SampleRate);
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource();
        Task first = channel.EnqueueTransmit(0, UiFrame(1, 80));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);
        Task second = channel.EnqueueTransmit(0, UiFrame(2, 80));

        // The fake clock has not moved, so the gather cannot have run out: nothing has keyed.
        output.Writes.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        output.Writes.Should().HaveCount(2, "both frames in one burst, then the tail");
        ptt.Events.Should().Equal("key", "unkey");
        reports.Should().HaveCount(2);
        reports.Should().OnlyContain(r => r.HeldFor == TimeSpan.FromSeconds(2),
            "the gather is time each frame waited, reported per frame");
    }
}
