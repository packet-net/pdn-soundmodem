namespace Packet.SoundModem.Modems;

/// <summary>
/// A second-order decision-directed Costas loop — the coherent carrier-recovery front end
/// the NinoTNC uses for its PSK modes. It drives a numerically-controlled oscillator to
/// track the incoming carrier's absolute phase, so the mixed-down I/Q lands on the true
/// constellation (not a phase-change of it). The data is still recovered by differentially
/// decoding the <em>recovered absolute</em> symbols downstream, exactly as the NinoTNC does:
/// the IL2P wire format is differential and untouched — only the receiver's detection method
/// changes. That resolves the M-PSK phase ambiguity (π for BPSK, π/2 for QPSK) the loop
/// cannot on its own, and buys the coherent-detection noise margin the differential detector
/// gives away.
/// </summary>
/// <remarks>
/// The loop owns only the NCO and the PI loop filter; the caller mixes the band-passed input
/// against <see cref="Cos"/>/<see cref="Sin"/>, low-passes to baseband I/Q, forms the
/// order-appropriate phase error (see <see cref="BpskError"/>/<see cref="QpskError"/>), and
/// feeds it to <see cref="Advance"/>. Gains follow the standard 2nd-order PLL mapping from a
/// normalised loop bandwidth and 0.707 damping (Gardner). A pure-carrier preamble (the IL2P
/// NRZI-zero training sequence is one) is the loop's acquisition signal.
/// </remarks>
public sealed class CostasLoop
{
    private readonly double _nominalStep;
    private readonly double _alpha;
    private readonly double _beta;
    private readonly double _maxFreqDeviation;
    private double _phase;
    private double _freq;

    /// <summary>Creates a loop centred on <paramref name="carrierFrequency"/>.</summary>
    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="carrierFrequency">Nominal carrier centre the NCO starts at.</param>
    /// <param name="loopBandwidthHz">Loop noise bandwidth. Wider acquires faster and jitters
    /// more; narrower tracks more quietly and pulls in slower — the coherent-vs-differential
    /// trade lives here and is tuned per mode against measurement.</param>
    /// <param name="maxFreqDeviationHz">Clamp on the integrator's frequency correction, so a
    /// noise burst cannot walk the NCO off the carrier and hold it there.</param>
    public CostasLoop(
        int sampleRate, double carrierFrequency, double loopBandwidthHz,
        double maxFreqDeviationHz = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(loopBandwidthHz, 0);
        _nominalStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _maxFreqDeviation = 2 * Math.PI * maxFreqDeviationHz / sampleRate;

        // Standard 2nd-order loop-filter gains for damping ζ and normalised bandwidth Bn·T.
        const double zeta = 0.707;
        double bnT = loopBandwidthHz / sampleRate;
        double theta = bnT / (zeta + 0.25 / zeta);
        double denom = 1 + 2 * zeta * theta + theta * theta;
        _alpha = 4 * zeta * theta / denom;
        _beta = 4 * theta * theta / denom;
    }

    /// <summary>Cosine of the current NCO phase (the in-phase reference).</summary>
    public float Cos => (float)Math.Cos(_phase);

    /// <summary>Negative sine of the current NCO phase (the quadrature reference, so a
    /// positive frequency error rotates I toward Q).</summary>
    public float Sin => (float)(-Math.Sin(_phase));

    /// <summary>The NCO's tracked frequency offset from nominal, in Hz — a lock/health
    /// signal (settles near the true carrier offset once pulled in).</summary>
    public double FrequencyOffsetHz(int sampleRate) => _freq * sampleRate / (2 * Math.PI);

    /// <summary>Feeds this sample's phase error and advances the NCO one sample.</summary>
    public void Advance(float error)
    {
        _freq += _beta * error;
        _freq = Math.Clamp(_freq, -_maxFreqDeviation, _maxFreqDeviation);
        _phase += _nominalStep + _freq + _alpha * error;
        if (_phase > Math.PI)
        {
            _phase -= 2 * Math.PI;
        }
        else if (_phase < -Math.PI)
        {
            _phase += 2 * Math.PI;
        }
    }

    /// <summary>Decision-directed BPSK (2-PSK) phase error: Q·sign(I). Removes the ±1
    /// modulation, leaving ≈ the residual carrier phase error near lock.</summary>
    public static float BpskError(float i, float q) => q * (i >= 0 ? 1f : -1f);

    /// <summary>Decision-directed QPSK (4-PSK) phase error: sign(I)·Q − sign(Q)·I. Removes
    /// the 4-phase modulation; zero when a symbol sits on its quadrant, ≈ 2A·φ under a small
    /// rotation φ, identically for all four quadrants.</summary>
    public static float QpskError(float i, float q) =>
        (i >= 0 ? 1f : -1f) * q - (q >= 0 ? 1f : -1f) * i;
}
