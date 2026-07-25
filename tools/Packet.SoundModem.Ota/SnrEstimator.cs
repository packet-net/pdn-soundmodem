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
/// carrier sitting in the guard interval raises no more than one bin's worth — on a real band
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
    /// <param name="noise">Real audio from a guard interval — no signal.</param>
    /// <param name="sampleRate">Sample rate of both.</param>
    /// <param name="bandwidthHz">Reference bandwidth; the rig's default is 3 kHz.</param>
    public static SnrEstimate Estimate(
        ReadOnlySpan<float> burst, ReadOnlySpan<float> noise, int sampleRate,
        double bandwidthHz = ReferenceBandwidthHz)
    {
        if (burst.Length == 0 || noise.Length == 0)
        {
            throw new ArgumentException("burst and noise must both be non-empty");
        }

        // Noise power per Hz, from the guard interval. Median-based so a stray carrier in the
        // gap moves one bin rather than the estimate.
        double noiseDensity = MedianPowerDensity(noise, sampleRate);
        double noiseInBand = noiseDensity * bandwidthHz;

        // Total power over the burst, then remove the noise riding on it. Both are measured
        // over the same band, so the subtraction is like for like.
        double total = 0;
        for (int k = 0; k < burst.Length; k++)
        {
            total += burst[k] * (double)burst[k];
        }

        double burstPower = total / burst.Length;
        double noiseOverBurstBand = noiseDensity * (sampleRate / 2.0);
        double signal = Math.Max(burstPower - noiseOverBurstBand, 1e-30);

        return new SnrEstimate(
            10.0 * Math.Log10(signal / noiseInBand), signal, noiseInBand, noiseDensity, bandwidthHz);
    }

    /// <summary>
    /// Median power spectral density of a real signal, in power per Hz.
    /// </summary>
    /// <remarks>Corrected for the fact that the median of an averaged periodogram approaches
    /// its mean as segments accumulate — the same Wilson–Hilferty factor
    /// <see cref="IqSpectrum.NoiseVariance"/> uses, and for the same reason: applying the
    /// single-periodogram ln2 factor to an averaged spectrum biases the answer by up to
    /// 1.59 dB.</remarks>
    private static double MedianPowerDensity(ReadOnlySpan<float> x, int sampleRate)
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

        // Only the positive half carries independent information for a real signal.
        int half = fftSize / 2;
        var bins = new double[half];
        for (int k = 0; k < half; k++)
        {
            bins[k] = power[k] / segments;
        }

        Array.Sort(bins);
        double median = bins[half / 2];

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
