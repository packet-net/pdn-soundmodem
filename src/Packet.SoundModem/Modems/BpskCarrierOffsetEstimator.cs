using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Estimates the carrier-frequency offset of a BPSK signal from a nominal centre without decoding
/// it — for characterising how far off-frequency the stations on a channel actually sit (and so
/// how to size a <see cref="BpskMultiModem"/> bank's step and span).
/// </summary>
/// <remarks>
/// <para>
/// The input is band-passed and mixed to baseband at the nominal centre, then the one-symbol-delayed
/// differential product z·conj(z one symbol ago) is formed. Its angle is the per-symbol carrier
/// rotation plus a 0-or-π data step; squaring the <em>normalised</em> product turns that data step
/// into a full turn (removed) for both random payload and the all-reversal training preamble, so
/// the accumulated squared phasor points at twice the per-symbol offset. Halving its angle recovers
/// the offset, unambiguous over ±baud/4.
/// </para>
/// <para>
/// Working at symbol spacing (not per sample) is what tolerates the preamble: a per-sample squarer
/// reads the alternating preamble as a tone at ±baud/2 and false-locks to it, whereas one symbol
/// apart those reversals are a constant π step the squaring removes. Samples whose differential
/// magnitude is below its running mean (the amplitude nulls a reversal sweeps through) are dropped,
/// so only full-amplitude symbol centres contribute. This is the estimator that measured the
/// committed GB7RDG off-air frame at ≈8 Hz (0.88 confidence).
/// </para>
/// </remarks>
public sealed class BpskCarrierOffsetEstimator
{
    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly int _sampleRate;
    private readonly int _samplesPerSymbol;
    private readonly float[] _historyReal;
    private readonly float[] _historyImag;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;
    private int _historyPosition;
    private int _historyFilled;
    private double _averageDiffMagnitude;
    private double _windowReal;
    private double _windowImag;
    private double _peakCoherence;
    private double _peakOffsetHz;

    /// <summary>Creates an estimator for a BPSK signal near <paramref name="centreFrequency"/>.</summary>
    /// <param name="sampleRate">Input sample rate (a multiple of <paramref name="baud"/>).</param>
    /// <param name="centreFrequency">Nominal carrier centre the offset is measured against.</param>
    /// <param name="baud">Symbol rate (300 or 1200 for the NinoTNC BPSK modes); also bounds the
    /// unambiguous estimate to ±baud/4.</param>
    public BpskCarrierOffsetEstimator(int sampleRate, double centreFrequency = 1500, int baud = 300)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        // Mirror the demodulator's front-end filters so the estimate reflects what it sees.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            centreFrequency - baud, centreFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _sampleRate = sampleRate;
        _samplesPerSymbol = sampleRate / baud;
        _historyReal = new float[_samplesPerSymbol];
        _historyImag = new float[_samplesPerSymbol];
        _oscillatorStep = 2 * Math.PI * centreFrequency / sampleRate;
    }

    /// <summary>Estimated carrier offset from the nominal centre, in Hz (positive = above centre),
    /// taken at the point of peak coherence since the last <see cref="Reset"/> — so a brief burst is
    /// not washed out by long idle either side of it. Meaningful once <see cref="HasEstimate"/> is
    /// true; 0 before any signal.</summary>
    public double OffsetHz => _peakOffsetHz;

    /// <summary>Peak coherence seen, 0..1: the resultant length of the windowed unit squared-phasors
    /// at its strongest. A real signal drives it toward ~0.9; band-limited noise averages toward 0.</summary>
    public double Confidence => _peakCoherence;

    /// <summary>True once a stretch of coherent signal has been seen (peak coherence ≥ 0.5 —
    /// comfortably above the noise floor, well below a clean signal's ~0.9).</summary>
    public bool HasEstimate => _peakCoherence >= 0.5;

    /// <summary>Feeds a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _bandPass.Next(sample);
            _oscillatorPhase += _oscillatorStep;
            if (_oscillatorPhase > 2 * Math.PI)
            {
                _oscillatorPhase -= 2 * Math.PI;
            }

            float inPhase = _lowPassI.Next(filtered * (float)Math.Cos(_oscillatorPhase));
            float quadrature = _lowPassQ.Next(filtered * (float)(-Math.Sin(_oscillatorPhase)));

            float agoReal = _historyReal[_historyPosition];
            float agoImag = _historyImag[_historyPosition];
            _historyReal[_historyPosition] = inPhase;
            _historyImag[_historyPosition] = quadrature;
            if (++_historyPosition == _samplesPerSymbol)
            {
                _historyPosition = 0;
            }

            if (_historyFilled < _samplesPerSymbol)
            {
                _historyFilled++;
                continue;
            }

            double diffReal = inPhase * agoReal + quadrature * agoImag;
            double diffImag = quadrature * agoReal - inPhase * agoImag;
            double diffMagnitude = Math.Sqrt(diffReal * diffReal + diffImag * diffImag);
            _averageDiffMagnitude += 0.001 * (diffMagnitude - _averageDiffMagnitude);
            if (diffMagnitude <= _averageDiffMagnitude || diffMagnitude < 1e-9)
            {
                continue; // a reversal null — no reliable phase here
            }

            double normReal = diffReal / diffMagnitude;
            double normImag = diffImag / diffMagnitude;
            double squaredReal = normReal * normReal - normImag * normImag; // (d/|d|)² strips ±1 data
            double squaredImag = 2 * normReal * normImag;

            // Exponential window (~1000-sample memory) started from zero: its resultant length is the
            // coherence over the recent signal — climbs toward 1 through a burst (consistent phasors
            // reinforce), stays near 0 in idle noise (random phasors cancel). Starting from zero,
            // not the first phasor, keeps a lone early sample from reading as full coherence.
            const double rate = 0.001;
            _windowReal += rate * (squaredReal - _windowReal);
            _windowImag += rate * (squaredImag - _windowImag);

            // Remember the offset at peak coherence, so a short burst inside a long recording wins.
            double coherence = Math.Sqrt(_windowReal * _windowReal + _windowImag * _windowImag);
            if (coherence > _peakCoherence)
            {
                _peakCoherence = coherence;
                _peakOffsetHz = Math.Atan2(_windowImag, _windowReal)
                    / (2.0 * _samplesPerSymbol) * _sampleRate / (2 * Math.PI);
            }
        }
    }

    /// <summary>Clears all accumulated state.</summary>
    public void Reset()
    {
        _historyPosition = 0;
        _historyFilled = 0;
        _averageDiffMagnitude = 0;
        _windowReal = 0;
        _windowImag = 0;
        _peakCoherence = 0;
        _peakOffsetHz = 0;
    }
}
