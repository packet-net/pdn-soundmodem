using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>An averaged (Welch) power spectrum of a complex baseband capture.</summary>
/// <remarks>Bins are in raw FFT order — bin <c>k &lt; N/2</c> is <c>+k·fs/N</c>, bin
/// <c>k ≥ N/2</c> is <c>(k−N)·fs/N</c> — so no fftshift is needed anywhere; use
/// <see cref="BinForFrequency"/> and <see cref="FrequencyOfBin"/>.</remarks>
public sealed class IqSpectrum
{
    private readonly double[] _power; // mean |X[k]|² across segments
    private readonly double _windowSumSquares;
    private readonly int _fftSize;

    internal IqSpectrum(double[] power, double windowSumSquares, int fftSize, int sampleRate, int segments)
    {
        _power = power;
        _windowSumSquares = windowSumSquares;
        _fftSize = fftSize;
        SampleRate = sampleRate;
        Segments = segments;
    }

    /// <summary>Complex sample rate of the capture.</summary>
    public int SampleRate { get; }

    /// <summary>Number of averaged segments.</summary>
    public int Segments { get; }

    /// <summary>FFT bin width in Hz.</summary>
    public double BinWidthHz => SampleRate / (double)_fftSize;

    /// <summary>Number of bins.</summary>
    public int Length => _power.Length;

    /// <summary>Centre frequency of a bin, in Hz relative to the capture centre.</summary>
    public double FrequencyOfBin(int bin) =>
        (bin < _fftSize / 2 ? bin : bin - _fftSize) * SampleRate / (double)_fftSize;

    /// <summary>Nearest bin to a frequency offset, wrapped into FFT order.</summary>
    public int BinForFrequency(double hz)
    {
        int bin = (int)Math.Round(hz * _fftSize / SampleRate);
        return ((bin % _fftSize) + _fftSize) % _fftSize;
    }

    /// <summary>
    /// Mean-square power of a coherent tone centred on <paramref name="hz"/>, summed over the
    /// window main lobe. Full scale (unit-magnitude complex tone) is 1.0.
    /// </summary>
    /// <remarks>By Parseval, Σ|X[k]|² over the lobe = N·|A|²·Σw[n]² for a tone of amplitude A,
    /// so summing power rather than reading the peak bin removes scalloping loss entirely — a
    /// tone sitting between bins measures the same as one sitting on a bin.</remarks>
    public double TonePower(double hz, int lobeBins = 3)
    {
        int centre = BinForFrequency(hz);
        double sum = 0;
        for (int d = -lobeBins; d <= lobeBins; d++)
        {
            sum += _power[((centre + d) % _fftSize + _fftSize) % _fftSize];
        }

        return sum / (_fftSize * _windowSumSquares);
    }

    /// <summary>
    /// Noise variance per complex sample, estimated from the median bin (robust to tones and
    /// spurs anywhere in the span).
    /// </summary>
    /// <remarks>
    /// <para>The median is used rather than the mean so that signals anywhere in the span do
    /// not inflate the floor — but a median needs converting back to a mean, and the factor
    /// depends on how many segments were averaged.</para>
    /// <para>A single periodogram bin of Gaussian noise is exponentially distributed, whose
    /// median is ln2 ≈ 0.693 of its mean. Averaging <c>S</c> Welch segments gives
    /// <c>μ·χ²(2S)/(2S)</c>, whose median rises rapidly towards the mean — 0.98 at 22
    /// segments. Applying the single-periodogram ln2 factor to an averaged spectrum
    /// therefore over-corrects by up to 1.59 dB, which would bias every SNR the harness ever
    /// reports. The Wilson–Hilferty approximation <c>median(χ²_k) ≈ k(1 − 2/(9k))³</c> gives
    /// the right factor for any segment count, and degrades to ln2 at S = 1.</para>
    /// </remarks>
    public double NoiseVariance()
    {
        var sorted = new double[_power.Length];
        _power.CopyTo(sorted, 0);
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        double k = 2.0 * Math.Max(1, Segments);
        double medianOverMean = Math.Pow(1.0 - (2.0 / (9.0 * k)), 3);
        return median / (_windowSumSquares * medianOverMean);
    }

    /// <summary>Noise power in a stated bandwidth, in the same full-scale units as
    /// <see cref="TonePower"/>.</summary>
    public double NoiseInBandwidth(double bandwidthHz) => NoiseVariance() * bandwidthHz / SampleRate;

    /// <summary>Raw mean |X[k]|² for a bin.</summary>
    public double BinPower(int bin) => _power[bin];

    /// <summary>Finds the strongest bin whose frequency lies in a range, refining the peak
    /// position by parabolic interpolation on the log-power curve.</summary>
    public (double Hz, double Power) FindPeak(double lowHz, double highHz)
    {
        int best = -1;
        double bestPower = double.NegativeInfinity;
        for (int k = 0; k < _power.Length; k++)
        {
            double f = FrequencyOfBin(k);
            if (f < lowHz || f > highHz)
            {
                continue;
            }

            if (_power[k] > bestPower)
            {
                bestPower = _power[k];
                best = k;
            }
        }

        if (best < 0)
        {
            return (double.NaN, 0);
        }

        double lo = Math.Log(_power[((best - 1) % _fftSize + _fftSize) % _fftSize] + double.Epsilon);
        double mid = Math.Log(_power[best] + double.Epsilon);
        double hi = Math.Log(_power[(best + 1) % _fftSize] + double.Epsilon);
        double denom = lo - (2 * mid) + hi;
        double delta = Math.Abs(denom) < 1e-30 ? 0 : 0.5 * (lo - hi) / denom;
        delta = Math.Clamp(delta, -0.5, 0.5);
        return (FrequencyOfBin(best) + (delta * BinWidthHz), TonePower(FrequencyOfBin(best) + (delta * BinWidthHz)));
    }
}

/// <summary>Time-domain level statistics of a capture.</summary>
/// <param name="PeakDbfs">Largest |I+jQ| in dBFS.</param>
/// <param name="RmsDbfs">Overall RMS in dBFS.</param>
/// <param name="PerSecondRmsDbfs">RMS per one-second block — a flat trace is evidence the
/// receiver is not applying AGC, a trace that moves with the signal is evidence it is.</param>
public sealed record IqLevels(double PeakDbfs, double RmsDbfs, IReadOnlyList<double> PerSecondRmsDbfs);

/// <summary>Spectral and level analysis of a complex-baseband capture.</summary>
/// <remarks>
/// The measurement half of the tone bring-up (ota-execution-plan §E0.5/§I): it answers "can we
/// hear it, at what level, on what frequency, how clean" with no modem involved. Every number
/// here is a <em>ratio</em> or a frequency — deliberately, because the dummy-load-to-loop
/// coupling factor is physical and uncalibrated, so absolute received level is never evidence.
/// </remarks>
public static class IqAnalysis
{
    /// <summary>Computes an averaged periodogram with a Hann window and 50% overlap.</summary>
    public static IqSpectrum Welch(ReadOnlySpan<float> i, ReadOnlySpan<float> q, int sampleRate, int fftSize = 8192)
    {
        if (i.Length != q.Length)
        {
            throw new ArgumentException("I and Q must be the same length", nameof(q));
        }

        if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two", nameof(fftSize));
        }

        if (i.Length < fftSize)
        {
            throw new ArgumentException($"need at least {fftSize} samples, got {i.Length}", nameof(i));
        }

        var window = new double[fftSize];
        double sumSquares = 0;
        for (int n = 0; n < fftSize; n++)
        {
            window[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / fftSize));
            sumSquares += window[n] * window[n];
        }

        var power = new double[fftSize];
        var re = new float[fftSize];
        var im = new float[fftSize];
        int hop = fftSize / 2;
        int segments = 0;

        for (int start = 0; start + fftSize <= i.Length; start += hop)
        {
            for (int n = 0; n < fftSize; n++)
            {
                re[n] = (float)(i[start + n] * window[n]);
                im[n] = (float)(q[start + n] * window[n]);
            }

            Fft.Forward(re, im);
            for (int k = 0; k < fftSize; k++)
            {
                power[k] += (re[k] * (double)re[k]) + (im[k] * (double)im[k]);
            }

            segments++;
        }

        for (int k = 0; k < fftSize; k++)
        {
            power[k] /= segments;
        }

        return new IqSpectrum(power, sumSquares, fftSize, sampleRate, segments);
    }

    /// <summary>
    /// Averaged periodogram of one time window of a capture — the segment analyser a stepped
    /// sweep needs.
    /// </summary>
    /// <remarks>Measuring every step inside a <em>single</em> capture is what makes a sweep
    /// trustworthy: the dummy-load-to-loop coupling is physical and uncalibrated, so comparing
    /// levels across separate captures would let a nudged cable read as non-linearity.</remarks>
    public static IqSpectrum WelchWindow(
        ReadOnlySpan<float> i, ReadOnlySpan<float> q, int sampleRate,
        double startSeconds, double lengthSeconds, int fftSize = 8192)
    {
        int start = Math.Clamp((int)Math.Round(startSeconds * sampleRate), 0, Math.Max(0, i.Length - 1));
        int count = Math.Min((int)Math.Round(lengthSeconds * sampleRate), i.Length - start);
        if (count < fftSize)
        {
            throw new ArgumentException(
                $"window of {count} samples is shorter than the {fftSize}-point FFT", nameof(lengthSeconds));
        }

        return Welch(i.Slice(start, count), q.Slice(start, count), sampleRate, fftSize);
    }

    /// <summary>Peak, RMS and per-second RMS of a complex capture.</summary>
    public static IqLevels Levels(ReadOnlySpan<float> i, ReadOnlySpan<float> q, int sampleRate)
    {
        double peak = 0, total = 0;
        var perSecond = new List<double>();
        double blockSum = 0;
        int blockCount = 0;

        for (int n = 0; n < i.Length; n++)
        {
            double m = (i[n] * (double)i[n]) + (q[n] * (double)q[n]);
            if (m > peak)
            {
                peak = m;
            }

            total += m;
            blockSum += m;
            if (++blockCount == sampleRate)
            {
                perSecond.Add(Db(blockSum / blockCount));
                blockSum = 0;
                blockCount = 0;
            }
        }

        return new IqLevels(Db(peak), Db(i.Length == 0 ? 0 : total / i.Length), perSecond);
    }

    /// <summary>Power ratio to dB, floored so silence prints rather than throwing.</summary>
    public static double Db(double power) => 10.0 * Math.Log10(Math.Max(power, 1e-30));

    /// <summary>
    /// Finds the largest spur outside the bands occupied by the signals of interest.
    /// </summary>
    /// <param name="spectrum">The spectrum to search.</param>
    /// <param name="excludeHz">Frequencies to mask out (tones, their image, DC).</param>
    /// <param name="guardHz">Half-width of the mask around each excluded frequency.</param>
    public static (double Hz, double Power) LargestSpur(
        IqSpectrum spectrum, IReadOnlyList<double> excludeHz, double guardHz)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(excludeHz);

        int best = -1;
        double bestPower = double.NegativeInfinity;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            bool masked = false;
            for (int e = 0; e < excludeHz.Count; e++)
            {
                if (Math.Abs(f - excludeHz[e]) <= guardHz)
                {
                    masked = true;
                    break;
                }
            }

            if (masked)
            {
                continue;
            }

            double p = spectrum.BinPower(k);
            if (p > bestPower)
            {
                bestPower = p;
                best = k;
            }
        }

        return best < 0
            ? (double.NaN, 0)
            : (spectrum.FrequencyOfBin(best), spectrum.TonePower(spectrum.FrequencyOfBin(best)));
    }

    /// <summary>
    /// Mean power in contiguous slots across the whole span — a band survey, for picking an
    /// operating frequency that is actually clear rather than nominally clear.
    /// </summary>
    /// <remarks>Reports both the mean and the peak in each slot: a slot can have a low mean
    /// and still hold one strong carrier, which is exactly the thing that would sit on top of
    /// a measurement.</remarks>
    public static IReadOnlyList<(double CentreHz, double MeanPower, double PeakPower)> Survey(
        IqSpectrum spectrum, double slotWidthHz)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        int slots = (int)Math.Floor(spectrum.SampleRate / slotWidthHz);
        var sum = new double[slots];
        var peak = new double[slots];
        var count = new int[slots];

        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            int slot = (int)Math.Floor((f + (spectrum.SampleRate / 2.0)) / slotWidthHz);
            if (slot < 0 || slot >= slots)
            {
                continue;
            }

            double p = spectrum.BinPower(k);
            sum[slot] += p;
            count[slot]++;
            if (p > peak[slot])
            {
                peak[slot] = p;
            }
        }

        var result = new List<(double, double, double)>(slots);
        for (int s = 0; s < slots; s++)
        {
            if (count[s] == 0)
            {
                continue;
            }

            double centre = (-spectrum.SampleRate / 2.0) + ((s + 0.5) * slotWidthHz);
            result.Add((centre, sum[s] / count[s], peak[s]));
        }

        return result;
    }

    /// <summary>Third- and fifth-order intermodulation products of a two-tone stimulus, in
    /// dBc relative to the stronger tone.</summary>
    public static IReadOnlyList<(string Label, double Hz, double Dbc)> Intermod(
        IqSpectrum spectrum, double f1, double f2)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        double reference = Math.Max(spectrum.TonePower(f1), spectrum.TonePower(f2));
        (string, double)[] products =
        [
            ("IMD3 lo", (2 * f1) - f2),
            ("IMD3 hi", (2 * f2) - f1),
            ("IMD5 lo", (3 * f1) - (2 * f2)),
            ("IMD5 hi", (3 * f2) - (2 * f1)),
        ];

        var result = new List<(string, double, double)>(products.Length);
        foreach ((string label, double hz) in products)
        {
            result.Add((label, hz, Db(spectrum.TonePower(hz)) - Db(reference)));
        }

        return result;
    }
}
