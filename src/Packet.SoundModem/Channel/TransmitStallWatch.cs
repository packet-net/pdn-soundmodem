using M0LTE.Radio.Audio;
using Packet.SoundModem.Telemetry;

namespace Packet.SoundModem.Channel;

/// <summary>One keyup whose transmit audio fell behind the radio's sample clock.</summary>
/// <param name="KeyedAt">UTC, when the keyup's first audio was written.</param>
/// <param name="Length">From the first write to the end of the drain: how long the keyup took.</param>
/// <param name="Stalls">How many times the audio fell behind.</param>
/// <param name="Worst">The furthest behind it fell, which is the longest the radio can have been
/// left with nothing to send.</param>
public readonly record struct TransmitStallReport(
    DateTimeOffset KeyedAt, TimeSpan Length, int Stalls, TimeSpan Worst);

/// <summary>
/// Sits in front of an output that paces itself off the wall clock (a Flex's DAX transmit
/// stream) and counts the times the audio reached it late: the moments the radio was owed audio
/// that had not been sent (issue #748).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A sound card has its own clock and a buffer, and ALSA counts an underrun when
/// the buffer runs dry. A Flex has neither on our side: <c>FlexAudioOutput</c> sends each DAX
/// packet when it is due and sleeps until the next one is, so the radio holds almost nothing
/// ahead of what it is playing. If the sending thread is held up for longer than that (not
/// scheduled on a busy host, a garbage collection), the radio runs out, transmits nothing, and
/// then plays the late packets as they arrive. On the air that is a hole of band noise followed
/// by the rest of the burst shifted late by the length of the hole, which is what three MS110D
/// WN13 bursts from GB7RDG looked like in Sweden on 2026-10-30. Nothing anywhere counted it.</para>
/// <para><b>What is measured.</b> The audio is handed on one DAX packet at a time. Before each
/// packet the wall time since the keyup's first write is compared with the audio already
/// written: an output that keeps up has written exactly as much as has played. Falling more
/// than <see cref="Threshold"/> behind is one stall, counted once however long it lasts, and
/// over when the lag is back under half of that. The lag is what the radio was short by, less
/// whatever little it held, so it is an upper bound on the hole it sent.</para>
/// <para><b>What it changes.</b> Nothing on the air. The inner output still sees the same samples
/// in the same order, only in packet-sized calls rather than one per frame, and it already cuts
/// them into packets itself. A gap between two writes in one keyup (the transmitter rendering
/// the next frame) counts too, because the radio was owed audio then as well.</para>
/// <para><b>Threading.</b> <see cref="Write"/> and <see cref="Drain"/> run on the transmitter's
/// thread; <see cref="Snapshot"/> is read by the metrics listener, so the totals are written
/// with <see cref="Interlocked"/>.</para>
/// </remarks>
public sealed class TransmitStallWatch : IAudioOutput, IDisposable
{
    /// <summary>
    /// How far behind counts as a stall. One DAX packet is 5.3 ms, and the pacing itself wakes a
    /// millisecond or two late on an ordinary day; twice a packet is clear of both, and well
    /// under the 25 to 38 ms holes seen on the air.
    /// </summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(10);

    private readonly IAudioOutput _inner;
    private readonly int _chunkSamples;
    private readonly TimeProvider _time;

    private bool _keyed;
    private long _startedAt;
    private DateTimeOffset _keyedAt;
    private long _written;

    private bool _inStall;
    private TimeSpan _stallWorst;
    private int _keyupStalls;
    private TimeSpan _keyupWorst;

    private long _totalStalls;
    private long _totalStallTicks;

    /// <summary>Watches <paramref name="inner"/>.</summary>
    /// <param name="inner">The self-pacing output.</param>
    /// <param name="chunkSamples">Samples per call to <paramref name="inner"/>: its packet size,
    /// so each check falls where it has just sent a packet and slept to its due time.</param>
    /// <param name="time">The wall clock; FakeTimeProvider under test.</param>
    public TransmitStallWatch(IAudioOutput inner, int chunkSamples, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSamples, 1);
        _inner = inner;
        _chunkSamples = chunkSamples;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised from <see cref="Drain"/> at the end of a keyup that stalled at least once.
    /// Runs on the transmitter's thread, so a handler must not block.</summary>
    public event Action<TransmitStallReport>? KeyupStalled;

    /// <inheritdoc />
    public int SampleRate => _inner.SampleRate;

    /// <summary>Stalls since start and their summed size.</summary>
    public TransmitStallSnapshot Snapshot => new(
        Interlocked.Read(ref _totalStalls),
        TimeSpan.FromTicks(Interlocked.Read(ref _totalStallTicks)).TotalSeconds);

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        if (!_keyed)
        {
            _keyed = true;
            _startedAt = _time.GetTimestamp();
            _keyedAt = _time.GetUtcNow();
            _written = 0;
            _inStall = false;
            _keyupStalls = 0;
            _keyupWorst = TimeSpan.Zero;
        }

        int offset = 0;
        try
        {
            while (offset < samples.Length)
            {
                Check();
                int n = Math.Min(_chunkSamples, samples.Length - offset);
                _inner.Write(samples.Slice(offset, n));
                _written += n;
                offset += n;
            }
        }
        catch
        {
            // A write that fails ends the keyup with no drain to follow, so the next write must
            // start a clock of its own rather than run on from this one.
            _keyed = false;
            throw;
        }
    }

    /// <inheritdoc />
    public void Drain()
    {
        _inner.Drain();
        if (!_keyed)
        {
            return;
        }

        _keyed = false;
        EndStall();
        if (_keyupStalls > 0)
        {
            KeyupStalled?.Invoke(new TransmitStallReport(
                _keyedAt, _time.GetElapsedTime(_startedAt), _keyupStalls, _keyupWorst));
        }
    }

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();

    private void Check()
    {
        TimeSpan behind = _time.GetElapsedTime(_startedAt)
            - TimeSpan.FromTicks(_written * TimeSpan.TicksPerSecond / _inner.SampleRate);
        if (behind > Threshold)
        {
            if (!_inStall)
            {
                _inStall = true;
                _stallWorst = TimeSpan.Zero;
                _keyupStalls++;
                Interlocked.Increment(ref _totalStalls);
            }

            if (behind > _stallWorst)
            {
                _stallWorst = behind;
            }
        }
        else if (_inStall && behind < Threshold / 2)
        {
            EndStall();
        }
    }

    private void EndStall()
    {
        if (!_inStall)
        {
            return;
        }

        _inStall = false;
        Interlocked.Add(ref _totalStallTicks, _stallWorst.Ticks);
        if (_stallWorst > _keyupWorst)
        {
            _keyupWorst = _stallWorst;
        }
    }
}
