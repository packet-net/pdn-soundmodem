using System.Numerics;
using M0LTE.Dsp;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// The channel-sounding probe against its own descriptor: the numbers a receiver builds its
/// reference from, the property the probe exists for (a periodic autocorrelation that is zero
/// everywhere but lag 0), and the two figures a transmitter cares about, peak-to-average ratio
/// and occupied bandwidth.
/// </summary>
public class ProbeSignalTests(ITestOutputHelper output)
{
    private static readonly ProbeDescriptor Probe = ProbeSignal.Zc255;

    [Fact]
    public void The_First_Probe_Is_The_One_The_Plan_Describes()
    {
        Probe.Id.Should().Be("zc255-2400-rrc015-v1");
        Probe.Kind.Should().Be("zc255");
        Probe.SequenceLength.Should().Be(255);
        Probe.Root.Should().Be(1);
        Probe.ChipRate.Should().Be(2400);
        Probe.RollOff.Should().Be(0.15);
        Probe.Periods.Should().Be(61);
        Probe.RampSeconds.Should().Be(0.010);
        Probe.PeriodSeconds.Should().BeApproximately(0.10625, 1e-12);
        Probe.DurationSeconds.Should().BeApproximately(6.50125, 1e-9);
        Probe.HalfBandwidthHz.Should().BeApproximately(1380, 1e-9);
        ProbeSignal.ForKind("zc255").Should().BeSameAs(Probe);
        ProbeSignal.ForKind("zc256").Should().BeNull();
        ProbeSignal.ForKind(null).Should().BeNull();
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(12000)]
    [InlineData(9600)]
    [InlineData(44100)]
    public void The_Rendering_Is_The_Descriptor_Worked_Out_Sample_By_Sample(int rate)
    {
        // An independent evaluation of the descriptor's own words, at a scattering of samples
        // across the ramps and the periods: the chips, the pulse, the normalisation, the ramp and
        // the carrier, each written out again here rather than called.
        const double audioHz = 1800, peak = 0.8;
        float[] audio = ProbeSignal.Render(Probe, audioHz, peak, rate);
        Complex[] envelope = ProbeSignal.Envelope(Probe, rate);

        audio.Length.Should().Be((int)Math.Round(6.50125 * rate));
        envelope.Length.Should().Be(audio.Length);
        Probe.FirstPeriodSample(rate).Should().Be((int)Math.Round(0.010 * rate));

        double normaliser = 0;
        for (int i = 0; i < 255 * 64; i++)
        {
            normaliser = Math.Max(normaliser, Reference(i / 64.0).Magnitude);
        }

        int ramp = (int)Math.Round(0.010 * rate);
        int[] picks = [0, 1, ramp / 3, ramp - 1, ramp, ramp + 7, audio.Length / 2, audio.Length - ramp - 1,
            audio.Length - ramp, audio.Length - 3, audio.Length - 1];
        foreach (int n in picks.Concat(Enumerable.Range(0, 40).Select(i => (i * 7919) % audio.Length)))
        {
            double x = (n * 2400.0 / rate) - 24;
            double weight = n < ramp ? 0.5 * (1 - Math.Cos(Math.PI * n / ramp))
                : audio.Length - 1 - n < ramp ? 0.5 * (1 - Math.Cos(Math.PI * (audio.Length - 1 - n) / ramp))
                : 1.0;
            Complex expected = Reference(x) * (weight / normaliser);
            double t = n / (double)rate;
            double carrier = (expected * Complex.FromPolarCoordinates(1, 2 * Math.PI * audioHz * t)).Real * peak;

            (envelope[n] - expected).Magnitude.Should().BeLessThan(1e-9, $"envelope sample {n} at {rate} Hz");
            ((double)audio[n]).Should().BeApproximately(carrier, 1e-6, $"audio sample {n} at {rate} Hz");
        }

        static Complex Reference(double x)
        {
            Complex sum = Complex.Zero;
            for (int k = (int)Math.Ceiling(x - 32); k <= (int)Math.Floor(x + 32); k++)
            {
                int m = ((k % 255) + 255) % 255;
                sum += Complex.FromPolarCoordinates(1, -Math.PI * m * (m + 1) / 255.0)
                    * FilterDesign.RootRaisedCosine(x - k, 0.15);
            }

            return sum;
        }
    }

    [Fact]
    public void The_Chips_Have_A_Perfect_Periodic_Autocorrelation()
    {
        Complex[] chips = ProbeSignal.Chips(Probe);
        chips.Should().HaveCount(255);
        chips.Should().OnlyContain(c => Math.Abs(c.Magnitude - 1) < 1e-12, "a Zadoff-Chu chip has unit magnitude");

        double worst = 0;
        for (int lag = 0; lag < 255; lag++)
        {
            Complex sum = Complex.Zero;
            for (int n = 0; n < 255; n++)
            {
                sum += chips[n] * Complex.Conjugate(chips[(n + lag) % 255]);
            }

            if (lag == 0)
            {
                sum.Magnitude.Should().BeApproximately(255, 1e-9);
            }
            else
            {
                worst = Math.Max(worst, sum.Magnitude);
            }
        }

        output.WriteLine($"largest off-peak periodic autocorrelation: {worst:E2} of 255");
        worst.Should().BeLessThan(1e-9, "every lag but zero cancels exactly");
    }

    [Fact]
    public void The_Shaped_Periods_Repeat_Exactly_So_The_Cyclic_Prefix_Works()
    {
        // At 48 kHz a period is 5100 samples exactly, so period p and period p + 1 must agree to
        // rounding: that is what lets a receiver drop the first and treat the rest as circular.
        const int rate = 48000;
        Complex[] envelope = ProbeSignal.Envelope(Probe, rate);
        int start = Probe.FirstPeriodSample(rate), period = 5100;
        double worst = 0;
        for (int n = start; n < start + (60 * period); n++)
        {
            worst = Math.Max(worst, (envelope[n] - envelope[n + period]).Magnitude);
        }

        worst.Should().BeLessThan(1e-9);
    }

    [Fact]
    public void Its_Peak_Envelope_Is_The_Tones_And_Its_Peak_To_Average_Ratio_Is_About_3_dB()
    {
        // The peak that matters is the envelope's: an SSB transmitter's RF envelope is the audio's
        // complex envelope, so a probe whose envelope peaks at the tone's level presents the PA
        // with the tone's peak envelope power. A tone's envelope is flat, so its PAPR is 0 dB;
        // the probe's is the figure below, and its average power sits that far under the tone's.
        const int rate = 48000;
        float[] audio = ProbeSignal.Render(Probe, 1800, 0.8, rate);
        Complex[] envelope = ProbeSignal.Envelope(Probe, rate);
        float[] tone = new TestTone([1800], 0.8, rate, 1).Render();

        int start = Probe.FirstPeriodSample(rate), end = audio.Length - start;
        double peak = 0, power = 0, samplePeak = 0, samplePower = 0;
        for (int n = start; n < end; n++)
        {
            double magnitude = 0.8 * envelope[n].Magnitude;
            peak = Math.Max(peak, magnitude);
            power += magnitude * magnitude;
            samplePeak = Math.Max(samplePeak, Math.Abs(audio[n]));
            samplePower += audio[n] * (double)audio[n];
        }

        power /= end - start;
        samplePower /= end - start;
        double paprDb = 10 * Math.Log10(peak * peak / power);
        double samplePaprDb = 10 * Math.Log10(samplePeak * samplePeak / samplePower);
        output.WriteLine(
            $"envelope peak {peak:F4} (tone {tone.Max(s => Math.Abs(s)):F4}), envelope PAPR {paprDb:F2} dB; "
            + $"audio sample peak {samplePeak:F4}, audio PAPR {samplePaprDb:F2} dB");

        peak.Should().BeApproximately(0.8, 0.001, "the probe's envelope peaks where the tone's does");
        audio.Max(s => Math.Abs(s)).Should().BeLessThanOrEqualTo(0.8f + 1e-6f, "no sample, ramps included, goes above it");
        paprDb.Should().BeInRange(2.9, 3.3, "a Zadoff-Chu envelope is nearly constant");
        (samplePaprDb - paprDb).Should().BeInRange(2.0, 3.1, "the carrier adds a sine wave's 3 dB, less what falls between samples");
    }

    [Fact]
    public void It_Occupies_420_To_3180_Hz_At_An_1800_Hz_Centre()
    {
        const int rate = 48000;
        float[] audio = ProbeSignal.Render(Probe, 1800, 0.8, rate);
        ReadOnlySpan<float> periods = audio.AsSpan(
            Probe.FirstPeriodSample(rate), audio.Length - (2 * Probe.FirstPeriodSample(rate)));

        (double lo99, double hi99, double width99, _) = OccupiedBandwidth.Measure(periods, rate, 0.99, 16384);
        (double lo999, double hi999, _, _) = OccupiedBandwidth.Measure(periods, rate, 0.999, 16384);
        output.WriteLine($"99 % OBW {lo99:F0} to {hi99:F0} Hz ({width99:F0} Hz); 99.9 % {lo999:F0} to {hi999:F0} Hz");

        lo999.Should().BeGreaterThanOrEqualTo(400, "the shaped band starts at 1800 - 1380 Hz");
        hi999.Should().BeLessThanOrEqualTo(3200, "and ends at 1800 + 1380 Hz");
        width99.Should().BeInRange(2300, 2760, "a flat 2400 Hz band with 15 % shoulders");
        ((lo99 + hi99) / 2).Should().BeApproximately(1800, 15);
    }

    [Theory]
    [InlineData(1380)]
    [InlineData(4620)]
    [InlineData(double.NaN)]
    public void A_Centre_That_Puts_The_Band_Outside_The_Channel_Is_Refused(double audioHz)
    {
        ProbeSignal.BandProblem(Probe, audioHz, 12000).Should().NotBeNull();
        Action render = () => ProbeSignal.Render(Probe, audioHz, 0.8, 12000);
        render.Should().Throw<ArgumentOutOfRangeException>();
        ProbeSignal.BandProblem(Probe, 1800, 12000).Should().BeNull();
    }
}
