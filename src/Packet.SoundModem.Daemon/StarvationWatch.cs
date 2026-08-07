namespace Packet.SoundModem.Daemon;

/// <summary>
/// Detects a starved receive feed: no samples delivered for a threshold of wall-clock time.
/// The complement of <see cref="DeadFeedWatch"/>, which counts silence carried IN samples and
/// so cannot see a device that stops delivering samples at all. A <c>Read</c> that returns 0
/// forever (a Flex whose DAX UDP path broke while its TCP session stayed up, an UberSDR
/// WebSocket hung on a half-open connection) leaves the receive loop turning with nothing for
/// the silence watch to observe; a <c>Read</c> that blocks forever (an ALSA card that stopped
/// producing period interrupts) stops the loop turning at all. Either way: no decodes, no
/// raw-capture samples, no error, indefinitely.
/// </summary>
/// <remarks>
/// The receive loop stamps deliveries with <see cref="NoteDelivery"/>; a timer OUTSIDE the
/// loop polls <see cref="IsStarved"/>, which is what lets a blocked <c>Read</c> still be seen.
/// Wall clock through an injected <see cref="TimeProvider"/> using monotonic timestamps, so an
/// NTP step can neither fire it early nor defer it - and so it is deterministic under
/// FakeTimeProvider in tests. Thread-safe between one stamping thread and one polling thread.
/// The caller decides the recovery; the daemon takes the same orderly-shutdown-and-exit-1 the
/// silence watch proved out, so systemd rebuilds the device session from scratch.
/// </remarks>
internal sealed class StarvationWatch
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _threshold;
    private long _lastDelivery;
    private volatile bool _fired;

    /// <summary>Creates the watch, with the clock starting now.</summary>
    /// <param name="time">The wall clock (injected; FakeTimeProvider under test).</param>
    /// <param name="thresholdSeconds">Wall-clock seconds without a delivery that declare the
    /// feed starved. Must be positive - "off" is the caller not constructing a watch, which
    /// is how the daemon reads a configured 0.</param>
    public StarvationWatch(TimeProvider time, double thresholdSeconds)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(thresholdSeconds, 0);
        _time = time;
        _threshold = TimeSpan.FromSeconds(thresholdSeconds);
        _lastDelivery = time.GetTimestamp();
    }

    /// <summary>The receive loop got samples: restart the clock and re-arm. Called per block
    /// on the hot path - a timestamp read and two volatile writes, no allocation.</summary>
    public void NoteDelivery()
    {
        Volatile.Write(ref _lastDelivery, _time.GetTimestamp());
        _fired = false;
    }

    /// <summary>
    /// The device says quiet is expected right now, so hold the clock rather than counting
    /// deliberate quiet as death. An UberSDR between sessions is the case in point: reconnect
    /// backoff and quota refusals are the reconnect policy pacing itself against a public
    /// receiver, and a starvation restart every threshold would re-ask with a fresh session
    /// each time - the exact hammering that policy exists to avoid.
    /// </summary>
    public void NoteExpectedQuiet() => NoteDelivery();

    /// <summary>Polled from outside the receive loop. Returns <see langword="true"/> exactly
    /// once per starvation, on the first poll past the threshold; a delivery re-arms it.</summary>
    public bool IsStarved()
    {
        if (_fired)
        {
            return false;
        }

        if (_time.GetElapsedTime(Volatile.Read(ref _lastDelivery)) < _threshold)
        {
            return false;
        }

        _fired = true;
        return true;
    }
}
