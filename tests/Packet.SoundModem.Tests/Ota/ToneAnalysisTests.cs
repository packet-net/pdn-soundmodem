using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The tone generator and the spectral analyser, checked against each other <b>and</b> against
/// analytic expectations.
/// </summary>
/// <remarks>
/// Checking generator-against-analyser alone would be circular - a shared sign or scaling error
/// would cancel. So every assertion here is anchored to a value derived from first principles:
/// a unit-amplitude complex tone has mean-square power 1.0 (0 dBFS) whatever its frequency; a
/// positive-frequency complex exponential has <em>no</em> negative-frequency content; injected
/// noise of known variance must come back out of the noise estimator at the level it went in.
/// </remarks>
public class ToneAnalysisTests
{
    private const int Rate = 24000;

    private static (float[] I, float[] Q) Split(float[] interleaved)
    {
        int n = interleaved.Length / 2;
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = interleaved[2 * k];
            q[k] = interleaved[(2 * k) + 1];
        }

        return (i, q);
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(2000.0)]
    [InlineData(-3500.0)]
    [InlineData(2000.5)]   // deliberately between bins - the power-sum must not care
    public void A_generated_tone_measures_at_the_frequency_and_level_it_was_asked_for(double hz)
    {
        float[] iq = ToneGenerator.Complex([hz], amplitudePerTone: 1.0, seconds: 1.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate);
        (double foundHz, double power) = spectrum.FindPeak(hz - 200, hz + 200);

        foundHz.Should().BeApproximately(hz, 0.5);
        // A unit-amplitude complex exponential has mean-square power exactly 1.0 → 0 dBFS.
        IqAnalysis.Db(power).Should().BeApproximately(0.0, 0.2);
    }

    [Fact]
    public void A_positive_frequency_tone_has_no_negative_frequency_image()
    {
        float[] iq = ToneGenerator.Complex([2000.0], 0.5, seconds: 1.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate);
        double signal = IqAnalysis.Db(spectrum.TonePower(2000.0));
        double image = IqAnalysis.Db(spectrum.TonePower(-2000.0));

        // Our own generator is analytically single-sided; anything above the window's own
        // sidelobe floor here would be a bug in the generator, not the radio.
        (image - signal).Should().BeLessThan(-90);
    }

    [Fact]
    public void Amplitude_scales_the_measured_level_by_exactly_the_expected_decibels()
    {
        double Level(double amplitude)
        {
            float[] iq = ToneGenerator.Complex([1500.0], amplitude, 1.0, Rate, rampSeconds: 0);
            (float[] i, float[] q) = Split(iq);
            return IqAnalysis.Db(IqAnalysis.Welch(i, q, Rate).TonePower(1500.0));
        }

        (Level(1.0) - Level(0.5)).Should().BeApproximately(6.0206, 0.05);
        (Level(0.5) - Level(0.25)).Should().BeApproximately(6.0206, 0.05);
    }

    [Fact]
    public void The_noise_estimator_recovers_an_injected_noise_variance()
    {
        // A tone plus white Gaussian noise of known per-sample variance. The median-bin
        // estimator (with its ln2 correction) must return that variance, not something 1.6 dB
        // low, or every SNR the harness ever reports is biased.
        const double sigmaSquared = 1e-4;
        var random = new Random(20260725);
        float[] iq = ToneGenerator.Complex([2000.0], 0.5, 4.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);
        double sigma = Math.Sqrt(sigmaSquared / 2.0); // per real component
        for (int k = 0; k < i.Length; k++)
        {
            i[k] += (float)(sigma * Gaussian(random));
            q[k] += (float)(sigma * Gaussian(random));
        }

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate);

        IqAnalysis.Db(spectrum.NoiseVariance()).Should().BeApproximately(IqAnalysis.Db(sigmaSquared), 0.5);
    }

    [Fact]
    public void Measured_snr_in_a_stated_bandwidth_matches_the_signal_and_noise_put_in()
    {
        // Tone power 0.25 (amplitude 0.5), noise variance 1e-4 spread over 24 kHz, so the
        // noise inside 3 kHz is 1e-4 × 3000/24000. Expected SNR = 10log10(0.25 / 1.25e-5).
        const double amplitude = 0.5;
        const double sigmaSquared = 1e-4;
        double expected = IqAnalysis.Db(amplitude * amplitude) - IqAnalysis.Db(sigmaSquared * 3000.0 / Rate);

        var random = new Random(7);
        float[] iq = ToneGenerator.Complex([2000.0], amplitude, 4.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);
        double sigma = Math.Sqrt(sigmaSquared / 2.0);
        for (int k = 0; k < i.Length; k++)
        {
            i[k] += (float)(sigma * Gaussian(random));
            q[k] += (float)(sigma * Gaussian(random));
        }

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate);
        double measured = IqAnalysis.Db(spectrum.TonePower(2000.0))
            - IqAnalysis.Db(spectrum.NoiseInBandwidth(3000));

        measured.Should().BeApproximately(expected, 0.6);
    }

    [Fact]
    public void A_two_tone_stimulus_carries_both_tones_and_no_intermodulation_of_its_own()
    {
        // The stimulus must be clean, or measured IMD would be ours rather than the radio's.
        float[] iq = ToneGenerator.Complex([1500.0, 2500.0], 0.4, 2.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate);
        IqAnalysis.Db(spectrum.TonePower(1500.0)).Should().BeApproximately(IqAnalysis.Db(0.16), 0.2);
        IqAnalysis.Db(spectrum.TonePower(2500.0)).Should().BeApproximately(IqAnalysis.Db(0.16), 0.2);

        foreach ((string label, double _, double dbc) in IqAnalysis.Intermod(spectrum, 1500.0, 2500.0))
        {
            dbc.Should().BeLessThan(-90, $"the generated two-tone stimulus must be clean ({label})");
        }
    }

    [Fact]
    public void Two_equal_tones_peak_at_twice_a_single_tones_amplitude()
    {
        // The envelope of an N-tone sum peaks at N × the per-tone amplitude - the reason the
        // transmitter checks peak magnitude rather than the requested amplitude.
        float[] one = ToneGenerator.Complex([1500.0], 0.4, 0.2, Rate, rampSeconds: 0);
        float[] two = ToneGenerator.Complex([1500.0, 2500.0], 0.4, 0.2, Rate, rampSeconds: 0);

        ToneGenerator.PeakMagnitude(one).Should().BeApproximately(0.4, 1e-3);
        ToneGenerator.PeakMagnitude(two).Should().BeApproximately(0.8, 1e-2);
    }

    [Fact]
    public void The_burst_is_cosine_ramped_at_both_ends_so_keying_does_not_splatter()
    {
        float[] iq = ToneGenerator.Complex([1000.0], 1.0, 0.1, Rate, rampSeconds: 0.005);
        int n = iq.Length / 2;

        Magnitude(iq, 0).Should().BeLessThan(0.01);
        Magnitude(iq, n - 1).Should().BeLessThan(0.01);
        Magnitude(iq, n / 2).Should().BeApproximately(1.0, 1e-3);

        static double Magnitude(float[] iq, int k) =>
            Math.Sqrt((iq[2 * k] * (double)iq[2 * k]) + (iq[(2 * k) + 1] * (double)iq[(2 * k) + 1]));
    }

    [Fact]
    public void A_tone_beyond_the_waveform_nyquist_is_refused_rather_than_aliased()
    {
        Action beyond = () => ToneGenerator.Complex([13000.0], 0.5, 0.1, Rate);

        beyond.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Levels_report_peak_rms_and_a_flat_per_second_trace_for_a_steady_tone()
    {
        float[] iq = ToneGenerator.Complex([2000.0], 0.5, 3.0, Rate, rampSeconds: 0);
        (float[] i, float[] q) = Split(iq);

        IqLevels levels = IqAnalysis.Levels(i, q, Rate);

        levels.PeakDbfs.Should().BeApproximately(IqAnalysis.Db(0.25), 0.01);
        levels.RmsDbfs.Should().BeApproximately(IqAnalysis.Db(0.25), 0.01);
        levels.PerSecondRmsDbfs.Should().HaveCount(3);
        (levels.PerSecondRmsDbfs.Max() - levels.PerSecondRmsDbfs.Min()).Should().BeLessThan(0.05);
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
