using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The throttle around the prospector.
/// </summary>
/// <remarks>
/// The claim this makes is not "it is fast" but "it cannot matter to the modems", and the only
/// way to make that checkable rather than argued is to bound it by construction: one capture at
/// a time, one thread below normal priority, a proportional sleep after each, and a backlog
/// dropped rather than queued. These pin the parts of that a test can see.
/// </remarks>
public class ProspectorWorkerTests
{
    /// <summary>
    /// A channel rate no mode runs at (modes run at 12000 or 48000, or at a rate that divides the
    /// station's), so the worker's sweep tries nothing and is instant. These tests are about the
    /// queue, the throttle and the shutdown, none of which depends on what a sweep reads, and a
    /// real sweep of 17 modes takes as long as the machine makes it: under load it was the one
    /// thing here that could outlast a hang guard.
    /// </summary>
    private const int NoModesRate = 8000;

    [Fact]
    public void The_Instant_Sweep_Really_Tries_Nothing()
    {
        // What the tests below lean on: if a mode ever ran at this rate they would be sweeping again.
        CaptureSweep.ModesFor(NoModesRate).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Queued_Capture_Is_Examined_And_The_Caller_Does_Not_Wait()
    {
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        Task examined = Examined(prospector);
        using var worker = new ProspectorWorker(prospector, NoModesRate);

        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000], 12000);

        worker.Examine(Capture(), wav);

        await Eventually.SignalAsync(examined, "the worker to pick the capture up");
        prospector.Examined.Should().Be(1);
        worker.Dropped.Should().Be(0);
    }

    [Fact]
    public async Task A_Capture_That_Has_Been_Pruned_Away_Is_A_Lost_Race_Rather_Than_An_Error()
    {
        // The capture writer's byte budget deletes oldest-first while this worker is reading, so
        // a file that is no longer there is expected. It must not take the worker down: the next
        // capture is the one that matters, and there is always a next capture.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        Task examined = Examined(prospector);
        using var worker = new ProspectorWorker(prospector, NoModesRate);

        worker.Examine(Capture(), Path.Combine(scratch.FullName, "gone.wav"));

        string present = Path.Combine(scratch.FullName, "here.wav");
        WavFile.WriteMono(present, new float[12000], 12000);
        worker.Examine(Capture(), present);

        await Eventually.SignalAsync(examined, "the real capture to be examined after the missing one is skipped");
        prospector.Examined.Should().Be(1);
    }

    [Fact]
    public void A_Backlog_Is_Dropped_And_Counted_Rather_Than_Queued_Without_Limit()
    {
        // A station suddenly hearing a great deal is a station whose CPU is wanted elsewhere,
        // and the captures are on disk either way: what is lost here is promptness, not evidence.
        // Counting it is what keeps that a choice rather than a silence.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        using var worker = new ProspectorWorker(prospector, 12000);

        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000 * 4], 12000);

        for (int i = 0; i < 200; i++)
        {
            worker.Examine(Capture(), wav);
        }

        worker.Dropped.Should().BeGreaterThan(0, "200 captures do not fit a bounded queue");
        (prospector.Examined + worker.Dropped).Should().BeLessThan(
            200 + 1, "nothing is examined twice");
    }

    [Fact]
    public async Task Disposing_Does_Not_Wait_Out_The_Throttle()
    {
        // The sleep after each capture is nineteen times the sweep, which on a real capture is
        // tens of seconds. A shutdown that waited for it would look like a hang, so the sleep is
        // interruptible and shutdown cancels it. The clock here moves only when the test moves
        // it: the sweep is made to take one second, so the sleep is nineteen, and the clock is
        // never moved again. The sleep cannot end by running out, so if the worker leaves at
        // all, Dispose is what ended it.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000], 12000);

        var clock = new FakeTimeProvider();
        long origin = clock.GetTimestamp();
        var time = new SleepWatcher(clock);
        prospector.ExaminedCapture += (_, _) => clock.Advance(TimeSpan.FromSeconds(1));
        var worker = new ProspectorWorker(prospector, NoModesRate, time, shutdownWait: TimeSpan.Zero);
        worker.Examine(Capture(), wav);

        TimeSpan sleep = await Eventually.SignalAsync(time.Sleeping, "the worker to start the throttle's sleep");
        sleep.Should().Be(TimeSpan.FromSeconds(ProspectorWorker.Idle), "19x a one-second sweep");

        worker.Dispose();

        // On the fake clock the throttle has nineteen seconds to go for ever, and only the
        // cancellation can end it.
        await Eventually.SignalAsync(worker.Exited, "the worker to leave the throttle once disposed");
        clock.GetElapsedTime(origin).Should().Be(TimeSpan.FromSeconds(1), "the clock never moved on");
    }

    [Fact]
    public async Task A_Sweep_Still_Running_When_Dispose_Gives_Up_Waiting_Finishes_Without_Crashing()
    {
        // Issue #546: Dispose waited a bounded time for the worker and then disposed the token
        // source anyway. A sweep slower than that wait (a loaded box, a long capture) came back
        // to a disposed source, read its token for the throttle's sleep, and the
        // ObjectDisposedException on the worker's own thread ended the process. Here the sweep is
        // held inside the prospector, on the worker thread, until Dispose has returned; with no
        // wait at all that is exactly the slow-sweep case, every time, with no clock involved.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        prospector.ExaminedCapture += (_, _) =>
        {
            inside.TrySetResult();

            // The limit only stops a failed test from leaving this thread parked for ever.
            release.Wait(Eventually.HangGuard);
        };

        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000], 12000);

        var worker = new ProspectorWorker(prospector, NoModesRate, shutdownWait: TimeSpan.Zero);
        worker.Examine(Capture(), wav);
        await Eventually.SignalAsync(inside.Task, "the worker to pick the capture up and be held in the sweep");

        worker.Dispose();
        worker.Exited.IsCompleted.Should().BeFalse("the sweep is still being held");

        // A capture that lands while the station is going down is ignored, not an exception on
        // the capture writer's thread.
        worker.Invoking(w => w.Examine(Capture(), wav)).Should().NotThrow();

        release.Set();
        await Eventually.SignalAsync(worker.Exited, "the worker to finish the held sweep and leave");
        prospector.Examined.Should().Be(1);
    }

    /// <summary>Completes when the prospector has finished examining a capture.</summary>
    private static Task Examined(ModemProspector prospector)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prospector.ExaminedCapture += (_, _) => done.TrySetResult();
        return done.Task;
    }

    /// <summary>The fake clock, and a signal when the worker starts a sleep on it.</summary>
    private sealed class SleepWatcher(FakeTimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource<TimeSpan> _sleeping =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The length of the first sleep the worker starts.</summary>
        public Task<TimeSpan> Sleeping => _sleeping.Task;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            _sleeping.TrySetResult(dueTime);
            return timer;
        }
    }

    private static BurstCapture Capture() => new(
        DateTimeOffset.UnixEpoch,
        SurveyVerdict.Unclaimed,
        AudioCentreHz: 1120,
        AudioLowHz: 1000,
        AudioHighHz: 1240,
        WidthHz: 240,
        DurationSeconds: 1,
        PeakSnrDb: 20,
        MeanSnrDb: 18,
        RfCentreHz: null,
        DialHz: null,
        Sideband: null,
        SampleRate: 12000,
        Modems: []);
}
