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
    [Fact]
    public void A_Queued_Capture_Is_Examined_And_The_Caller_Does_Not_Wait()
    {
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        using var worker = new ProspectorWorker(prospector, 12000);

        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000], 12000);

        worker.Examine(Capture(), wav);

        Wait(() => prospector.Examined == 1).Should().BeTrue("the worker should pick it up");
        worker.Dropped.Should().Be(0);
    }

    [Fact]
    public void A_Capture_That_Has_Been_Pruned_Away_Is_A_Lost_Race_Rather_Than_An_Error()
    {
        // The capture writer's byte budget deletes oldest-first while this worker is reading, so
        // a file that is no longer there is expected. It must not take the worker down: the next
        // capture is the one that matters, and there is always a next capture.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        using var worker = new ProspectorWorker(prospector, 12000);

        worker.Examine(Capture(), Path.Combine(scratch.FullName, "gone.wav"));

        string present = Path.Combine(scratch.FullName, "here.wav");
        WavFile.WriteMono(present, new float[12000], 12000);
        worker.Examine(Capture(), present);

        Wait(() => prospector.Examined == 1).Should().BeTrue(
            "the missing one is skipped and the real one still runs");
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
    public void Disposing_Does_Not_Wait_Out_The_Throttle()
    {
        // The sleep after each capture is nineteen times the sweep, which on a real capture is
        // tens of seconds. A shutdown that waited for it would look like a hang, so the sleep is
        // interruptible and shutdown cancels it.
        using var scratch = new ScratchDirectory("prospector-worker-tests");
        var prospector = new ModemProspector(new ModemProspectorOptions(), []);
        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000 * 2], 12000);

        var worker = new ProspectorWorker(prospector, 12000);
        worker.Examine(Capture(), wav);
        Wait(() => prospector.Examined == 1).Should().BeTrue();

        // Now inside the throttle's sleep, which is 19x whatever that sweep cost.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        worker.Dispose();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(3), "Dispose joins with a bounded wait and cancels the sleep");
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
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        prospector.ExaminedCapture += (_, _) =>
        {
            inside.Set();
            release.Wait();
        };

        string wav = Path.Combine(scratch.FullName, "capture.wav");
        WavFile.WriteMono(wav, new float[12000], 12000);

        var worker = new ProspectorWorker(prospector, 12000, shutdownWait: TimeSpan.Zero);
        worker.Examine(Capture(), wav);
        inside.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the worker should pick it up");

        worker.Dispose();
        worker.Exited.IsCompleted.Should().BeFalse("the sweep is still being held");

        // A capture that lands while the station is going down is ignored, not an exception on
        // the capture writer's thread.
        worker.Invoking(w => w.Examine(Capture(), wav)).Should().NotThrow();

        release.Set();
        await worker.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        prospector.Examined.Should().Be(1);
    }

    /// <summary>Polls for a condition the worker thread satisfies. Not a wall-clock assertion:
    /// the deadline only bounds a failure, and every passing run leaves as soon as it is
    /// true.</summary>
    private static bool Wait(Func<bool> until)
    {
        for (int i = 0; i < 600; i++)
        {
            if (until())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
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
