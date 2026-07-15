namespace Packet.SoundModem.Modems;

/// <summary>
/// Symbol-clock recovery DPLL in the Dire Wolf style (demod_9600.c / fsk_demod_state.h):
/// a phase accumulator advances by baud/sampleRate per sample and the bit is sampled when
/// it wraps; every observed transition multiplies the phase by an inertia factor, pulling
/// transitions toward phase zero so sampling lands mid-bit. Robust, self-centring, and
/// tolerant of missing transitions.
/// </summary>
public sealed class BitDpll
{
    private readonly double _increment;
    private readonly double _inertia;
    private readonly Action<int> _bitSink;
    private readonly Action<double>? _transitionObserver;
    private readonly Action? _symbolObserver;
    private double _phase; // −0.5 … +0.5, wrap = sampling instant
    private int _lastLevel;

    /// <summary>Creates a DPLL emitting one sampled level per symbol to
    /// <paramref name="bitSink"/>.</summary>
    /// <param name="baud">Symbol rate.</param>
    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="bitSink">Receives the sampled level (0/1) once per symbol.</param>
    /// <param name="inertia">Phase retained on each transition nudge. Dire Wolf uses 0.74
    /// when locked; lower values acquire faster but jitter more.</param>
    /// <param name="transitionObserver">Called with the pre-nudge phase of every observed
    /// transition — the timing-quality signal <see cref="PacketDcd"/> scores.</param>
    /// <param name="symbolObserver">Called once per recovered symbol, alongside
    /// <paramref name="bitSink"/>. <see cref="PacketDcd"/> uses it to notice the absence
    /// of transitions, which <paramref name="transitionObserver"/> can never report.</param>
    public BitDpll(
        int baud, int sampleRate, Action<int> bitSink, double inertia = 0.74,
        Action<double>? transitionObserver = null, Action? symbolObserver = null)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, baud);
        _increment = (double)baud / sampleRate;
        _inertia = inertia;
        _bitSink = bitSink;
        _transitionObserver = transitionObserver;
        _symbolObserver = symbolObserver;
    }

    /// <summary>Advances the clock by one sample of sliced level (0/1).</summary>
    /// <param name="level">The sliced level at this sample.</param>
    /// <param name="crossingFraction">How far <b>before</b> this sample the underlying
    /// analog crossing occurred, as a fraction of one sample (0 = at this sample). At
    /// coarse samples-per-bit ratios (10 at 12 kHz/1200 Bd) quantised nudges inject up to
    /// ±5 % of a bit of clock jitter per transition; interpolating the crossing from the
    /// pre/post slicer input removes it (measured on WA8LMF Track 2 at 12 kHz: the single
    /// decoder went 60 → 268). A searching/locked inertia switch was also tried and
    /// REGRESSED badly (268 → 31) — with the crossing interpolation the fixed inertia is
    /// already stable through acquisition, so keep it fixed. Callers that cannot estimate
    /// the crossing pass 0.</param>
    public void Sample(int level, double crossingFraction = 0)
    {
        _phase += _increment;
        if (_phase >= 0.5)
        {
            _phase -= 1.0;
            _bitSink(level);
            _symbolObserver?.Invoke();
        }

        if (level != _lastLevel)
        {
            _lastLevel = level;
            // Nudge as if at the true crossing instant, then re-advance to now. A
            // crossing that lands just before the sampling wrap would take the phase
            // out of the [-0.5, 0.5) domain and invert the nudge direction — clamp the
            // look-back at the wrap edge (the emit for that cycle already happened).
            double back = crossingFraction * _increment;
            double atCrossing = Math.Max(_phase - back, -0.4999);
            back = _phase - atCrossing;
            _transitionObserver?.Invoke(atCrossing);
            _phase = atCrossing * _inertia + back;
        }
    }
}
