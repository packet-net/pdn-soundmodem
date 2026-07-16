namespace Packet.SoundModem.Modems;

/// <summary>
/// A modem whose demodulator can emit its per-symbol decision point — the raw material for
/// a constellation / eye diagram. Only the phase-shift-keyed modes implement this; AFSK and
/// direct-FSK have no phase constellation.
/// </summary>
/// <remarks>
/// The point is the demodulator's per-symbol decision variable at the sampling instant, in
/// its natural domain. For the differential PSK detectors here that is the differential
/// product — the phase <em>change</em> over one symbol — so a clean QPSK signal clusters at
/// the four dibit phases and a clean BPSK signal on the real axis (±1); it is a
/// differential constellation, not an absolute one (see <see cref="ConstellationPoint"/>).
/// It is emitted once per recovered symbol, synchronously from <c>Process</c>.
/// </remarks>
public interface IConstellationSource
{
    /// <summary>Raised once per recovered symbol with that symbol's decision point.</summary>
    event Action<ConstellationPoint>? SymbolPlotted;
}

/// <summary>
/// One symbol's decision point. <see cref="I"/>/<see cref="Q"/> are the demodulator's
/// decision variable — for the differential detectors, the differential product (phase
/// change over a symbol). BPSK populates <see cref="Q"/> = 0 (a 1-D constellation).
/// </summary>
/// <param name="I">In-phase / real component of the decision.</param>
/// <param name="Q">Quadrature / imaginary component (0 for BPSK).</param>
public readonly record struct ConstellationPoint(float I, float Q);
