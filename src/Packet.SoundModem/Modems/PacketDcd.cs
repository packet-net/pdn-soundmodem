using System.Numerics;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Packet-signal DCD by DPLL transition-quality scoring, after Dire Wolf 1.6's design
/// (fsk_demod_state.h): every observed slicer transition is classified good when it lands
/// near the expected instant (DPLL phase ≈ 0), and a 32-transition history asserts DCD at
/// ≥ 30/32 good and drops it at ≤ 6/32 - hysteresis that ignores both random noise (which
/// transitions everywhere) and brief fades. Unlike flag-pattern DCD this keeps working
/// under FX.25/IL2P bit patterns, which legitimately contain long runs.
/// </summary>
/// <remarks>
/// <para>Transition scoring alone can only ever <em>drop</em> DCD when it sees badly-timed
/// transitions, i.e. it depends on receiver noise to notice a signal has gone. That holds
/// on an open squelch, but a genuinely quiet channel (a squelched radio, a wired bench
/// loop, or our own squelching demodulator) delivers no transitions at all and DCD would
/// latch on for ever. <see cref="OnSymbol"/> closes that hole: silence is itself evidence,
/// so a run of symbols with no transition <em>and no signal</em> drops DCD on its own.</para>
/// <para>Both halves of that evidence are required (issue #339). Absence of transitions
/// alone is not absence of carrier: a legitimate run of identical symbols - a held tone, a
/// constant phase, an unbroken stretch of one scrambled bit value - is a full-strength
/// signal the slicer never crosses on, and counting it as silence dropped DCD in the middle
/// of decodable frames. IL2P's scrambler makes such runs improbable, not impossible: 4 of
/// 256 otherwise ordinary 56-byte UI frames, differing in one payload byte, encode a 48-bit
/// run and were deterministically lost to the mid-frame drop (the modem resets its
/// deframers on the falling edge). So each symbol arrives with the demodulator's own
/// decision magnitude, and only a symbol whose magnitude has collapsed - a quarter of its
/// recent in-burst mean - counts toward the quiet drop.</para>
/// </remarks>
public sealed class PacketDcd
{
    /// <summary>Memory of the decision-magnitude mean, in symbols - long enough to smooth
    /// the pattern-dependent spread of a burst's decisions, short enough to have settled on
    /// the burst it describes well before <see cref="OnSymbol"/>'s quiet count can matter.</summary>
    private const int MagnitudeMemorySymbols = 32;

    /// <summary>Fraction of the magnitude mean below which a symbol reads as signal-free.
    /// Sits orders of magnitude above the residue digital silence leaves once the filters
    /// flush, and well below any decision a live carrier produces - including the inner
    /// levels of a 4-ary eye and a symbol at the bottom of ordinary fading spread.</summary>
    private const double QuietFraction = 0.25;

    private const double MagnitudeRate = 1.0 / MagnitudeMemorySymbols;

    private readonly double _window;
    private readonly int _quietSymbolsToDrop;
    private uint _history = 0; // 1 bit per recent transition: 1 = well-timed
    private double _magnitudeMean;
    private int _quietSymbols;
    private bool _asserted;

    /// <summary>Creates a detector.</summary>
    /// <param name="window">Half-width of the "well-timed" phase window around zero, as a
    /// fraction of a symbol. Dire Wolf uses 512×2²⁰ of 2³² ≈ 0.125.</param>
    /// <param name="quietSymbolsToDrop">Consecutive signal-free symbols that drop DCD - a
    /// symbol counts only when its decision magnitude says the carrier is gone, so this no
    /// longer needs to outlast the longest transition-free run a live signal can contain
    /// (IL2P measurably produces 48-bit runs - see the class remarks - which no fixed count
    /// could clear without slowing release). 24 releases within a few symbol times of the
    /// carrier stopping, once the receive filters have flushed the burst's tail.</param>
    public PacketDcd(double window = 0.125, int quietSymbolsToDrop = 24)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(window, 0.5);
        ArgumentOutOfRangeException.ThrowIfLessThan(quietSymbolsToDrop, 8);
        _window = window;
        _quietSymbolsToDrop = quietSymbolsToDrop;
    }

    /// <summary>True while the timing history says a coherent packet signal is present.</summary>
    public bool Asserted => _asserted;

    /// <summary>Feeds one slicer transition, with the DPLL phase (−0.5…0.5) at which it
    /// occurred. Wire to <see cref="BitDpll"/>'s transition observer.</summary>
    public void OnTransition(double phase)
    {
        _quietSymbols = 0;
        bool good = Math.Abs(phase) <= _window;
        _history = (_history << 1) | (good ? 1u : 0u);
        int score = BitOperations.PopCount(_history);
        if (score >= 30)
        {
            _asserted = true;
        }
        else if (score <= 6)
        {
            _asserted = false;
        }
    }

    /// <summary>Feeds one recovered symbol with the demodulator's decision magnitude at its
    /// instant - the differential product, the slicer's envelope-midpoint excess, the
    /// coherent baseband power: any per-symbol quantity, in any consistent units, that is
    /// large while the carrier is present and collapses when it stops. Wire to
    /// <see cref="BitDpll"/>'s symbol observer so DCD can notice a signal that simply
    /// stopped, without mistaking a transition-free run of identical symbols - which keeps
    /// its full magnitude - for silence (see the class remarks).</summary>
    public void OnSymbol(double decisionMagnitude)
    {
        bool quiet = decisionMagnitude < QuietFraction * _magnitudeMean;
        _magnitudeMean += MagnitudeRate * (decisionMagnitude - _magnitudeMean);
        if (!quiet)
        {
            _quietSymbols = 0;
        }
        else if (_asserted && ++_quietSymbols >= _quietSymbolsToDrop)
        {
            _history = 0;
            _asserted = false;
        }
    }

    /// <summary>Drops DCD immediately (e.g. when the channel's own transmitter keys).</summary>
    public void Reset()
    {
        _history = 0;
        _magnitudeMean = 0;
        _quietSymbols = 0;
        _asserted = false;
    }
}
