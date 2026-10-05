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

        // Every packed burst is a keyup of its own, with its own TXDELAY and TX tail, so the
        // writes alternate burst, tail, burst, tail.
        List<float[]> bursts = output.Writes.Where((_, i) => i % 2 == 0).ToList();
        bursts.Count.Should().BeInRange(2, 5, "packed, but never past the limit");
        foreach (float[] burst in bursts)
        {
            ((burst.Length - (SampleRate * 100 / 1000)) / (double)SampleRate).Should().BeLessThanOrEqualTo(3.0);
        }

        ptt.Events.Should().HaveCount(2 * bursts.Count, "a keyup per burst, never one for the whole queue");
        ptt.Events.Should().Equal(Enumerable.Range(0, bursts.Count).SelectMany(_ => new[] { "key", "unkey" }));
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
        output.Writes.Should().HaveCount(4, "the two before it in one keyup, the one after in another, each with its tail");
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

    /// <summary>
    /// A packing modem that renders instantly, says when it renders, and packs as the test tells
    /// it to - for the scheduling, without the DSP.
    /// </summary>
    private sealed class FakePacker : IModem, IFramePackingModem
    {
        private int _renders;

        public Func<IReadOnlyList<byte[]>, int> Sizer { get; set; } = frames => frames.Count;

        public Action<int>? Rendering { get; set; }

        public string Mode => "fake-packer";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public FramePacking? Packing { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[100];

        public void ResetCarrierState()
        {
        }

        public int FramesPerBurst(IReadOnlyList<byte[]> frames) => Sizer(frames);

        public float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds)
        {
            Rendering?.Invoke(Interlocked.Increment(ref _renders));
            return new float[1000 * frames.Count];
        }
    }

    /// <summary>Hands each write to the test, so it can hold a burst "on the air".</summary>
    private sealed class HookedOutput(int sampleRate, Action<int> written) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples) => written(samples.Length);

        public void Drain()
        {
        }
    }

    private sealed class LoggingPtt(List<string> log) : IPttControl
    {
        public void Key()
        {
            lock (log)
            {
                log.Add("key");
            }
        }

        public void Unkey()
        {
            lock (log)
            {
                log.Add("unkey");
            }
        }
    }

    [Fact]
    public async Task Each_Burst_Is_Rendered_Before_Its_Keyup_And_The_Next_While_It_Plays()
    {
        var log = new List<string>();
        using var secondRenderStarted = new ManualResetEventSlim();
        var packer = new FakePacker
        {
            Sizer = _ => 1, // one frame per burst, so three frames are three bursts
            Packing = Packing(),
        };
        packer.Rendering = n =>
        {
            lock (log)
            {
                log.Add($"render {n}");
            }

            if (n == 2)
            {
                secondRenderStarted.Set();
            }
        };

        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, _ => packer);
        channel.Csma.Persistence = 255;
        bool overlapped = false;
        int bursts = 0;
        var output = new HookedOutput(SampleRate, length =>
        {
            if (length == 1000 && Interlocked.Increment(ref bursts) == 1)
            {
                // The first burst is "on the air" until the second has started rendering. The
                // bound is a failure bound, not a pace: the render is started before the write.
                overlapped = secondRenderStarted.Wait(TimeSpan.FromMinutes(2));
            }
        });

        List<Task> sends = Enumerable.Range(1, 3).Select(i => channel.EnqueueTransmit(0, UiFrame(i, 20))).ToList();
        using var cancellation = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(output, new LoggingPtt(log), cancellation.Token);
        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        overlapped.Should().BeTrue("the next burst renders while the current one plays");
        List<string> keys = log.Where(e => e == "key").ToList();
        keys.Should().HaveCount(3, "each packed burst is a keyup of its own");
        for (int k = 1; k <= 3; k++)
        {
            int nthKey = log.Select((e, i) => (e, i)).Where(x => x.e == "key").ElementAt(k - 1).i;
            log.IndexOf($"render {k}").Should().BeInRange(0, nthKey - 1,
                $"burst {k} is rendered before the radio keys for it, never while it waits keyed");
        }
    }

    [Fact]
    public async Task A_Modem_That_Cannot_Size_A_Run_Sends_Its_Frames_One_At_A_Time()
    {
        var packer = new FakePacker
        {
            Sizer = _ => throw new InvalidOperationException("sizing broke"),
            Packing = Packing(),
        };
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, _ => packer);
        channel.Csma.Persistence = 255;

        List<Task> sends = Enumerable.Range(1, 3).Select(i => channel.EnqueueTransmit(0, UiFrame(i, 20))).ToList();
        (BurstRecorder output, RecordingPtt ptt) = await RunAsync(channel, sends);

        sends.Should().OnlyContain(s => s.IsCompletedSuccessfully, "the transmitter survives and every frame goes");
        ptt.Events.Count(e => e == "key").Should().Be(3);
        output.Writes.Count(w => w.Length == 1000).Should().Be(3, "each frame in a burst of its own");
    }

    [Fact]
    public async Task A_Gathering_Modem_Holds_Up_Nobody_Else()
    {
        // The clock never moves until the end, so the gather cannot end: whatever goes out before
        // then went out while it was gathering, not after it.
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Ms110dModem(SampleRate, sink) { Packing = Packing(gatherSeconds: 5) });
        channel.AddModem(1, sink => new Ms110dModem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 0;
        channel.Csma.TxTailMilliseconds = 0;

        Task gathering = channel.EnqueueTransmit(0, UiFrame(1, 40));
        Task plain = channel.EnqueueTransmit(1, UiFrame(2, 40));
        Task urgent = channel.EnqueueTransmit(_ => new float[100], ownsChannelTiming: true, source: new object());

        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(new BurstRecorder(SampleRate), ptt, cancellation.Token);
        await Task.WhenAll(plain, urgent).WaitAsync(TimeSpan.FromSeconds(30));
        gathering.IsCompleted.Should().BeFalse("its gather has not run out on a clock that has not moved");

        time.Advance(TimeSpan.FromSeconds(5));
        await gathering.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A modem that holds the channel busy until told, and logs every time carrier
    /// sense asks it.</summary>
    private sealed class LoggingBusyModem(List<string> log) : IModem
    {
        private volatile bool _busy = true;

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Mode => "busy";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy
        {
            get
            {
                lock (log)
                {
                    log.Add(_busy ? "asked busy" : "asked clear");
                }

                Asked.TrySetResult();
                return _busy;
            }
        }

        public void Clear() => _busy = false;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState()
        {
        }
    }

    [Fact]
    public async Task A_Burst_That_Must_Be_Rendered_Again_Is_Rendered_Before_Carrier_Sense_Not_After()
    {
        // TXDELAY changes while the burst waits out a busy channel, so the audio it was rendered
        // with is stale by the time the channel clears. It must be rendered again and the channel
        // asked again afterwards: a render after the last check would key on an old reading.
        var log = new List<string>();
        var packer = new FakePacker { Packing = Packing() };
        packer.Rendering = n =>
        {
            lock (log)
            {
                log.Add($"render {n}");
            }
        };
        var busy = new LoggingBusyModem(log);
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, _ => packer);
        channel.AddModem(1, _ => busy);
        channel.Csma.Persistence = 255;
        channel.Csma.SlotTimeMilliseconds = 0;

        Task send = channel.EnqueueTransmit(0, UiFrame(1, 20));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new BurstRecorder(SampleRate), new LoggingPtt(log), cancellation.Token);
        await busy.Asked.Task.WaitAsync(TimeSpan.FromMinutes(1));
        channel.Csma.TxDelayMilliseconds = 120;
        busy.Clear();
        await send.WaitAsync(TimeSpan.FromMinutes(1));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        List<string> events;
        lock (log)
        {
            events = [.. log];
        }

        int key = events.IndexOf("key");
        int secondRender = events.IndexOf("render 2");
        secondRender.Should().BeGreaterThan(-1, "the stale burst is rendered again");
        secondRender.Should().BeLessThan(key);
        events.FindLastIndex(key, e => e == "asked clear").Should().BeGreaterThan(secondRender,
            "the channel is asked again after the render, so the keyup follows a fresh reading");
    }
}
