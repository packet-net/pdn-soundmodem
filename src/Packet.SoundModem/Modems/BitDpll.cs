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
    public BitDpll(
        int baud, int sampleRate, Action<int> bitSink, double inertia = 0.74,
        Action<double>? transitionObserver = null)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, baud);
        _increment = (double)baud / sampleRate;
        _inertia = inertia;
        _bitSink = bitSink;
        _transitionObserver = transitionObserver;
    }

    /// <summary>Advances the clock by one sample of sliced level (0/1).</summary>
    public void Sample(int level)
    {
        _phase += _increment;
        if (_phase >= 0.5)
        {
            _phase -= 1.0;
            _bitSink(level);
        }

        if (level != _lastLevel)
        {
            _lastLevel = level;
            _transitionObserver?.Invoke(_phase);
            _phase *= _inertia;
        }
    }
}
