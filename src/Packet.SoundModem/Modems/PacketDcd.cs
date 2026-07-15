using System.Numerics;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Packet-signal DCD by DPLL transition-quality scoring, after Dire Wolf 1.6's design
/// (fsk_demod_state.h): every observed slicer transition is classified good when it lands
/// near the expected instant (DPLL phase ≈ 0), and a 32-transition history asserts DCD at
/// ≥ 30/32 good and drops it at ≤ 6/32 — hysteresis that ignores both random noise (which
/// transitions everywhere) and brief fades. Unlike flag-pattern DCD this keeps working
/// under FX.25/IL2P bit patterns, which legitimately contain long runs.
/// </summary>
/// <remarks>
/// Transition scoring alone can only ever <em>drop</em> DCD when it sees badly-timed
/// transitions, i.e. it depends on receiver noise to notice a signal has gone. That holds
/// on an open squelch, but a genuinely quiet channel (a squelched radio, a wired bench
/// loop, or our own squelching demodulator) delivers no transitions at all and DCD would
/// latch on for ever. <see cref="OnSymbol"/> closes that hole: silence is itself evidence,
/// so a run of symbols with no transition drops DCD on its own.
/// </remarks>
public sealed class PacketDcd
{
    private readonly double _window;
    private readonly int _quietSymbolsToDrop;
    private uint _history = 0; // 1 bit per recent transition: 1 = well-timed
    private int _quietSymbols;
    private bool _asserted;

    /// <summary>Creates a detector.</summary>
    /// <param name="window">Half-width of the "well-timed" phase window around zero, as a
    /// fraction of a symbol. Dire Wolf uses 512×2²⁰ of 2³² ≈ 0.125.</param>
    /// <param name="quietSymbolsToDrop">Symbols without any transition that drop DCD. Must
    /// clear the longest transition-free run a live signal can contain: 6 for HDLC/NRZI (a
    /// 0x7E flag's six 1 bits; bit stuffing bounds data at five), and vanishingly unlikely
    /// runs in IL2P, whose scrambler exists to keep transitions coming. 24 leaves ample
    /// room while still releasing within a few symbol times of the carrier stopping.</param>
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

    /// <summary>Feeds one recovered symbol. Wire to <see cref="BitDpll"/>'s symbol
    /// observer so DCD can notice a signal that simply stopped.</summary>
    public void OnSymbol()
    {
        if (_asserted && ++_quietSymbols >= _quietSymbolsToDrop)
        {
            _history = 0;
            _asserted = false;
        }
    }

    /// <summary>Drops DCD immediately (e.g. when the channel's own transmitter keys).</summary>
    public void Reset()
    {
        _history = 0;
        _quietSymbols = 0;
        _asserted = false;
    }
}
