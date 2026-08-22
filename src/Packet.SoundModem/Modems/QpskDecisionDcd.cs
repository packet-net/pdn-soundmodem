namespace Packet.SoundModem.Modems;

/// <summary>
/// Packet DCD for the differential QPSK path, scored on the symbol decisions rather than on
/// slicer transitions: the fourth-power coherence of the symbol-instant sample as seen from
/// the detector's own reference, averaged over about 32 symbols, asserts DCD above one level
/// and releases it below a lower one. The differential path's twin of <see cref="PacketDcd"/>,
/// which the coherent path keeps.
/// </summary>
/// <remarks>
/// <para><b>Why not transition timing.</b> <see cref="PacketDcd"/> scores a slicer
/// transition good when it lands within an eighth of a symbol of the clock's expected instant
/// and wants 30 of the last 32 good, which suits a two-level slicer whose transitions are
/// clean. The QPSK differential path slices the one-symbol conjugate product's quadrant, and
/// that product nulls at every phase change: its angle there is whatever noise makes it, so it
/// flickers through the other quadrants around each null, several transitions per change at
/// scattered phases, and on the all-reversal preamble - a 180° change every symbol - a clean
/// product never changes quadrant at all. Measured (issue #329) only 40-60 % of those
/// transitions scored, so DCD asserted for a QPSK burst only with the carrier on frequency and
/// some noise present, never on a clean burst, never under a bank-step offset, never on the
/// 16-symbol zero-TXDELAY preamble, while the frame decoded in every one of those cases. The
/// baseband envelope's nulls were measured as a timing source before this and rejected: at
/// qpsk600's 0.20 roll-off the neighbouring symbols move each null by up to a third of a
/// symbol, so even a noise-free burst scatters its nulls over half a symbol and no
/// per-transition scorer can reach 30 of 32 from them.</para>
/// <para><b>What is scored instead.</b> At every clock instant the detector has already
/// formed the symbol-instant sample relative to its reference phasor (the decision-feedback
/// chain) or the de-rotated conjugate product (the plain-product chain); on a QPSK signal that
/// phasor sits near one of four axes and on noise it sits anywhere. Raising its unit phasor
/// to the fourth power strips the data and leaves cos(4e), e being the angle error from the
/// nearer axis: +1 on a clean decision, 0 at 22.5°, -1 on the boundary, and zero-mean on
/// noise. An exponential average over <see cref="MemorySymbols"/> symbols estimates exp(-8σ²)
/// of the decision's angle error σ. Noise alone leaves the average within about 0.09 of
/// zero (its standard deviation at this memory), so <see cref="AssertLevel"/> sits six of
/// those above it, where a false assert on an idle channel is a once-a-year event at 1800 Bd;
/// a signal reaches it once its decisions are better than about 16° rms, which is a dB
/// or so below the Reed-Solomon knee on every QPSK mode. <see cref="ReleaseLevel"/> is about
/// 31° rms, a decision that has long stopped copying, and deliberately far below the assert
/// level: the plain-product detector's coherence at qpsk3600's FM knee sits near 0.5, and a
/// release level of 0.25 measured DCD dropping inside a third of its decodable bursts, each
/// drop costing the frame (the seed and the deframers hang on the falling edge). A burst's
/// end drops the average through 0.10 in 70 to 100 symbols, the noise floor's own
/// excursions setting the spread; digital silence, which has no angle at all, is scored as
/// the boundary and releases in about twenty.</para>
/// </remarks>
internal sealed class QpskDecisionDcd
{
    /// <summary>Memory of the coherence average, in symbols.</summary>
    public const int MemorySymbols = 32;

    /// <summary>Coherence at which DCD asserts.</summary>
    public const double AssertLevel = 0.55;

    /// <summary>Coherence at which an asserted DCD releases.</summary>
    public const double ReleaseLevel = 0.10;

    private const double Rate = 1.0 / MemorySymbols;
    private double _coherence;
    private bool _asserted;

    /// <summary>True while the decisions say a QPSK packet signal is present.</summary>
    public bool Asserted => _asserted;

    /// <summary>The running fourth-power coherence of the decisions (-1..1). Bench seam.</summary>
    public double Coherence => _coherence;

    /// <summary>Feeds one symbol-instant decision variable: the sample relative to the
    /// detector's reference (or the de-rotated product), in-phase and quadrature, in any
    /// amplitude. A zero sample is no evidence of a carrier and scores as the boundary.</summary>
    public void OnDecision(double inPhase, double quadrature)
    {
        double power = (inPhase * inPhase) + (quadrature * quadrature);
        double coherence;
        if (power < 1e-24)
        {
            coherence = -1;
        }
        else
        {
            // cos 4e from cos 2e = (I² - Q²) / |z|², no trigonometry: the decided quadrant
            // rotates e by multiples of 90°, which the fourth power does not see.
            double cos2 = ((inPhase * inPhase) - (quadrature * quadrature)) / power;
            coherence = (2 * cos2 * cos2) - 1;
        }

        _coherence += Rate * (coherence - _coherence);
        if (_coherence >= AssertLevel)
        {
            _asserted = true;
        }
        else if (_coherence <= ReleaseLevel)
        {
            _asserted = false;
        }
    }

    /// <summary>Drops DCD immediately (e.g. when the channel's own transmitter keys).</summary>
    public void Reset()
    {
        _coherence = 0;
        _asserted = false;
    }
}
