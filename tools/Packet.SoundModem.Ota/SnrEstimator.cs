using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>An SNR estimate for one burst, with the parts it was built from.</summary>
/// <param name="SnrDb">Signal-to-noise in the reference bandwidth.</param>
/// <param name="SignalPower">Mean signal power over the burst, noise removed.</param>
/// <param name="NoisePower">Noise power in the reference bandwidth.</param>
/// <param name="NoiseDensity">Noise power per Hz, from the guard interval.</param>
/// <param name="BandwidthHz">The reference bandwidth the SNR is quoted in.</param>
public sealed record SnrEstimate(
    double SnrDb, double SignalPower, double NoisePower, double NoiseDensity, double BandwidthHz);

/// <summary>
/// Per-burst SNR from a captured recording, in the same terms the Watterson rig uses.
/// </summary>
/// <remarks>
/// <para><b>The convention has to match the rig or the numbers are not comparable.</b>
/// <c>WattersonChannel.AddNoise</c> defines SNR as the mean power of the clean burst against
/// noise measured in a 3 kHz bandwidth (D.6.2). So the estimate here is built the same way:
/// noise <em>density</em> from a scheduled silent interval, scaled to 3 kHz; signal power from
/// the burst with that noise subtracted. Quoting an SNR measured any other way against the
/// gate table would be comparing two different quantities.</para>
/// <para>Noise density comes from the median of the periodogram rather than the mean, so a
/// carrier sitting in the guard interval raises no more than one bin's worth - on a real band
/// there is always something in the gap.</para>
/// <para>This estimator is audited against the rig itself: bursts are generated at known SNRs
/// and it must recover them. An unaudited estimator would quietly re-fit every over-the-air
/// number to its own bias, which is precisely the campaign-audit failure this project has
/// already paid for once.</para>
/// </remarks>
public static class SnrEstimator
{
    /// <summary>The bandwidth MIL-STD-188-110D D.6.2 quotes SNR in.</summary>
    public const double ReferenceBandwidthHz = 3000;

    /// <summary>
    /// Estimates SNR for a burst, given the burst samples and a separate stretch of
    /// signal-free noise from the same recording.
    /// </summary>
    /// <param name="burst">Real audio spanning the burst.</param>
    /// <param name="noise">Real audio from a guard interval - no signal.</param>
    /// <param name="sampleRate">Sample rate of both.</param>
    /// <param name="bandwidthHz">Reference bandwidth; the rig's default is 3 kHz.</param>
    /// <param name="occupiedLowHz">Low edge of the band the recording actually contains signal
    /// and noise in. 0 with <paramref name="occupiedHighHz"/> means the whole band.</param>
    /// <param name="occupiedHighHz">High edge of that band.</param>
    /// <returns>The estimate, or <see langword="null"/> when the measurement failed - the
    /// noise estimate met or exceeded the burst power, so there is no signal to quote an SNR for
    /// (see the noise-subtraction note below). A failed measurement is <em>unmeasurable</em>, not
    /// a real, catastrophically low SNR; callers surface it as "no reading" and the self-cal
    /// aggregate skips it, rather than letting a −250 dB floor masquerade as delivered SNR.</returns>
    /// <remarks>
    /// <b>Pass the occupied band for anything that came through an SSB filter.</b> A captured
    /// pass has been through the receive converter's passband (150-3450 Hz by default), so its
    /// noise occupies about 3.3 kHz of the 4.8 kHz the audio spans - and treating the empty
    /// third as though it held noise breaks the estimate twice over. The median bin drifts
    /// towards the stopband, and, worse, the noise subtracted from the burst is inflated by the
    /// ratio of the two bandwidths. That error is negligible where the signal dominates and
    /// severe where it does not: 0.2 dB at 10 dB SNR, 2.6 dB at 0 dB, which is precisely the
    /// bottom of a §E2 ladder. Narrower still and the median lands in the stopband entirely, and
    /// the estimator reports a noise floor of nothing and an SNR of everything.
    /// </remarks>
    public static SnrEstimate? Estimate(
        ReadOnlySpan<float> burst, ReadOnlySpan<float> noise, int sampleRate,
        double bandwidthHz = ReferenceBandwidthHz,
        double occupiedLowHz = 0, double occupiedHighHz = 0)
    {
        if (burst.Length == 0 || noise.Length == 0)
        {
            throw new ArgumentException("burst and noise must both be non-empty");
        }

        double nyquist = sampleRate / 2.0;
        bool banded = occupiedHighHz > occupiedLowHz;
        double low = banded ? Math.Max(0, occupiedLowHz) : 0;
        double high = banded ? Math.Min(nyquist, occupiedHighHz) : nyquist;
        double occupied = high - low;
        if (occupied <= 0)
        {
            throw new ArgumentException("the occupied band must be inside 0..Nyquist");
        }

        // Noise power per Hz, from the guard interval. Median-based so a stray carrier in the
        // gap moves one bin rather than the estimate - taken over the occupied band only.
        double noiseDensity = MedianPowerDensity(noise, sampleRate, low, high);
        double noiseInBand = noiseDensity * bandwidthHz;

        // Total power over the burst, then remove the noise riding on it. The noise removed is
        // the noise the recording actually holds - density across the OCCUPIED band, not across
        // the whole of Nyquist, which would over-subtract by the ratio of the two.
        double total = 0;
        for (int k = 0; k < burst.Length; k++)
        {
            total += burst[k] * (double)burst[k];
        }

        double burstPower = total / burst.Length;
        double noiseOverBurstBand = noiseDensity * occupied;
        double signal = burstPower - noiseOverBurstBand;
        if (signal <= 0)
        {
            // The noise estimate meets or exceeds the burst power: there is no signal left after
            // removing the noise riding on the burst, so this is a FAILED measurement, not a real
            // −250 dB delivered SNR. Flooring the signal to a tiny epsilon (as this once did) makes
            // 10·log10(signal / noiseInBand) read ≈ −250 dB, which then masquerades as a
            // catastrophic delivered SNR and drags the per-run self-cal aggregate to tens of dB.
            // Report it as unmeasurable instead; the scorers surface it as "no reading" and the
            // self-cal average skips it. On the rig this is the FIRST burst of a run, whose noise
            // lead-in sits in the capture's warm-up region (rx_sdr/RSP1/DC settling) where the noise
            // floor is momentarily inflated above the burst power. See issue #121.
            return null;
        }

        return new SnrEstimate(
            10.0 * Math.Log10(signal / noiseInBand), signal, noiseInBand, noiseDensity, bandwidthHz);
    }

    /// <summary>
    /// Median power spectral density of a real signal, in power per Hz.
    /// </summary>
    /// <remarks>Corrected for the fact that the median of an averaged periodogram approaches
    /// its mean as segments accumulate - the same Wilson-Hilferty factor
    /// <see cref="IqSpectrum.NoiseVariance"/> uses, and for the same reason: applying the
    /// single-periodogram ln2 factor to an averaged spectrum biases the answer by up to
    /// 1.59 dB.</remarks>
    private static double MedianPowerDensity(
        ReadOnlySpan<float> x, int sampleRate, double lowHz, double highHz)
    {
        int fftSize = 1024;
        while (fftSize > x.Length && fftSize > 64)
        {
            fftSize /= 2;
        }

        if (x.Length < fftSize)
        {
            throw new ArgumentException($"need at least {fftSize} samples of noise", nameof(x));
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
        for (int start = 0; start + fftSize <= x.Length; start += hop)
        {
            for (int n = 0; n < fftSize; n++)
            {
                re[n] = (float)(x[start + n] * window[n]);
                im[n] = 0;
            }

            Fft.Forward(re, im);
            for (int k = 0; k < fftSize; k++)
            {
                power[k] += (re[k] * (double)re[k]) + (im[k] * (double)im[k]);
            }

            segments++;
        }

        // Only the positive half carries independent information for a real signal, and only
        // the occupied part of that carries noise worth taking a median over.
        int half = fftSize / 2;
        double binHz = sampleRate / (double)fftSize;
        int firstBin = Math.Clamp((int)Math.Ceiling(lowHz / binHz), 0, half - 1);
        int lastBin = Math.Clamp((int)Math.Floor(highHz / binHz), firstBin, half - 1);
        int count = lastBin - firstBin + 1;
        var bins = new double[count];
        for (int k = 0; k < count; k++)
        {
            bins[k] = power[firstBin + k] / segments;
        }

        Array.Sort(bins);
        double median = bins[count / 2];

        double dof = 2.0 * Math.Max(1, segments);
        double medianOverMean = Math.Pow(1.0 - (2.0 / (9.0 * dof)), 3);
        double meanBin = median / medianOverMean;

        // For a windowed DFT of noise with per-sample power sigma^2, E|X[k]|^2 = sigma^2 *
        // sum(w^2), so the variance follows directly from a bin. A real signal's power lives
        // in the one-sided band [0, fs/2], which is what turns variance into density.
        double variance = meanBin / sumSquares;
        return variance / (sampleRate / 2.0);
    }
}
