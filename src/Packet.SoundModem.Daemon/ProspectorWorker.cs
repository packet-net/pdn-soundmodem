using System.Collections.Concurrent;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Runs the <see cref="ModemProspector"/> beside a live station, slowly enough that it cannot
/// matter to the modems.
/// </summary>
/// <remarks>
/// <para><b>Why a worker and not a call.</b> Reading one capture every way it might have been
/// sent is a couple of seconds of DSP for a couple of seconds of audio - trivial once, and not
/// trivial thirty times an hour on a Pi that is also demodulating four modems in real time. The
/// receive path must never wait for it and must never lose a slice to it.</para>
/// <para><b>How the throttle works.</b> One capture at a time, on one thread below normal
/// priority, and after each capture the worker sleeps for <see cref="Idle"/> times as long as
/// the sweep took. So the prospector's share of a core is bounded by construction rather than by
/// hoping the queue stays short - at the default it is a twentieth of one core, whatever the
/// station is hearing, and a slower box makes it slower rather than busier.</para>
/// <para><b>A backlog is dropped, not queued.</b> A station that suddenly hears a great deal is
/// a station whose CPU is already wanted elsewhere, and the captures are on disk: what is lost
/// is promptness, not evidence. The count is reported so the loss is visible.</para>
/// <para><b>The audio comes back off disk</b> rather than being handed over in memory. The
/// writer has just written it, so it is in page cache; and reading it back means this worker
/// holds no reference into anything the receive path allocated, which is the property that makes
/// "it cannot matter to the modems" checkable rather than argued.</para>
/// </remarks>
internal sealed class ProspectorWorker : IDisposable
{
    /// <summary>Sleep after each capture, as a multiple of the time that capture took to sweep.
    /// 19 puts the worker on a twentieth of one core.</summary>
    public const int Idle = 19;

    private readonly BlockingCollection<Pending> _queue =
        new(new ConcurrentQueue<Pending>(), boundedCapacity: 4);

    private readonly ModemProspector _prospector;
    private readonly IReadOnlyList<string> _modes;
    private readonly Thread _worker;
    private readonly CancellationTokenSource _stopping = new();
    private long _dropped;

    /// <param name="prospector">The analysis this worker feeds.</param>
    /// <param name="dspRate">The station's DSP rate, which its captures are written at.</param>
    public ProspectorWorker(ModemProspector prospector, int dspRate)
    {
        ArgumentNullException.ThrowIfNull(prospector);
        _prospector = prospector;

        // Resolved once: the answer depends only on the station's rate, and working it out per
        // capture would walk the catalogue thirty times an hour for the same list.
        _modes = CaptureSweep.ModesFor(dspRate);

        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "survey-prospector",

            // Below normal on purpose. The one thing this must never do is take a slice from a
            // demodulator; being slow is free, being late is not a failure mode it has.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>Captures the queue refused because the worker was still on the last one.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues a capture, by the path its audio was written to. Returns immediately.</summary>
    public void Examine(BurstCapture capture, string wavPath)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!_queue.TryAdd(new Pending(capture, wavPath)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private void Loop()
    {
        foreach (Pending item in _queue.GetConsumingEnumerable())
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                (float[] samples, int rate) = WavFile.ReadMono(item.WavPath);
                if (rate == item.Capture.SampleRate)
                {
                    _prospector.Examine(
                        item.Capture, samples, _modes, () => _stopping.IsCancellationRequested);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or UnauthorizedAccessException)
            {
                // Pruned out from under us by the capture writer's byte budget, most likely.
                // A capture that is no longer there is not an error, it is a race we lost.
                continue;
            }

            // The throttle. Sleeping proportionally to the work just done is what bounds the
            // share rather than the rate: a capture that took four seconds to sweep buys the
            // station seventy-six seconds of quiet, and one that took a tenth of a second buys
            // two. Interruptible, so shutdown does not wait out a sleep.
            TimeSpan spent = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            _stopping.Token.WaitHandle.WaitOne(spent * Idle);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
        _stopping.Dispose();
    }

    private readonly record struct Pending(BurstCapture Capture, string WavPath);
}
