using AwesomeAssertions;
using M0LTE.Ardop;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Moving ARDOP off its fixed 1500 Hz centre so it can share a passband with the packet modems.
/// The shift happens outside the TNC, which knows nothing about it, so these tests work in the
/// only terms that matter: where the energy actually ends up.
/// </summary>
public class ArdopChannelShiftTests
{
    private const int Rate = ArdopModulator.SampleRate;

    /// <summary>The strongest tones in a signal, one per cluster, low to high.</summary>
    private static List<double> Tones(IReadOnlyList<float> audio, double floorFraction = 0.01)
    {
        const int N = 16384;
        int offset = Math.Max(0, (audio.Count - N) / 2);
        var windowed = new double[N];
        for (int i = 0; i < N && offset + i < audio.Count; i++)
        {
            windowed[i] = audio[offset + i] * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (N - 1))));
        }

        double binHz = Rate / (double)N;
        var bins = new List<(double Hz, double Power)>();
        for (int k = 1; k < N / 2; k++)
        {
            double hz = k * binHz;
            if (hz > 3500)
            {
                break;
            }

            double re = 0, im = 0;
            for (int i = 0; i < N; i++)
            {
                double angle = -2 * Math.PI * k * i / N;
                re += windowed[i] * Math.Cos(angle);
                im += windowed[i] * Math.Sin(angle);
            }

            bins.Add((hz, (re * re) + (im * im)));
        }

        double peak = bins.Max(b => b.Power);
        var tones = new List<(double Hz, double Power)>();
        foreach ((double hz, double power) in bins.Where(b => b.Power > peak * floorFraction))
        {
            if (tones.Count == 0 || hz - tones[^1].Hz > 20)
            {
                tones.Add((hz, power));
            }
            else if (power > tones[^1].Power)
            {
                tones[^1] = (hz, power);
            }
        }

        return [.. tones.Select(t => t.Hz)];
    }

    private static float[] TwoTone()
    {
        short[] pcm = new ArdopModulator(driveLevel: 100).TwoToneTest();
        return [.. pcm.Select(s => s / 32768f)];
    }

    [Fact]
    public void Ardops_Native_Centre_Really_Is_The_Constant_We_Shift_From()
    {
        // ArdopChannelShift.NativeCentreHz is measured, not assumed — M0LTE.Ardop exposes no
        // centre constant. If the package ever moves its waveforms, this fails rather than
        // silently mis-centring every shifted station.
        List<double> tones = Tones(TwoTone());

        tones.Should().HaveCount(2, "the two-tone test emits the ardopcf calibration pair");
        double midpoint = (tones[0] + tones[1]) / 2;
        midpoint.Should().BeApproximately(ArdopChannelShift.NativeCentreHz, 2.0);
        (tones[1] - tones[0]).Should().BeApproximately(50.0, 2.0, "the pair straddles centre by ±25 Hz");
    }

    [Theory]
    [InlineData(950)]    // Tom's 40m plan: ARDOP low, bpsk300 above it
    [InlineData(1200)]
    [InlineData(2000)]
    public void Transmitting_Moves_The_Signal_To_The_Configured_Centre(double centre)
    {
        var shift = ArdopChannelShift.For(centre, Rate);

        List<double> tones = Tones(shift.Transmit(TwoTone()));

        tones.Should().HaveCount(2);
        ((tones[0] + tones[1]) / 2).Should().BeApproximately(centre, 5.0,
            "the whole point is that the signal lands where the operator asked");
        (tones[1] - tones[0]).Should().BeApproximately(50.0, 3.0,
            "a frequency shift must translate the signal, not stretch it");
    }

    [Fact]
    public void Receiving_Puts_It_Back_Where_The_Tnc_Expects_To_Find_It()
    {
        // The TNC is unaware of the shift, so a round trip has to return its own centre.
        var shift = ArdopChannelShift.For(950, Rate);

        float[] onAir = shift.Transmit(TwoTone());
        List<double> recovered = Tones(shift.Receive(onAir));

        recovered.Should().HaveCount(2);
        ((recovered[0] + recovered[1]) / 2).Should().BeApproximately(ArdopChannelShift.NativeCentreHz, 5.0);
    }

    [Fact]
    public void A_Round_Trip_Keeps_The_Signal_Rather_Than_Wrecking_It()
    {
        var shift = ArdopChannelShift.For(950, Rate);
        float[] original = TwoTone();

        float[] recovered = shift.Receive(shift.Transmit(original));

        // Compare well inside the signal, past both shifters' filter delay.
        int from = original.Length / 2, count = 4000;
        double energyIn = 0, energyOut = 0;
        for (int i = from; i < from + count; i++)
        {
            energyIn += original[i] * original[i];
            energyOut += recovered[i] * recovered[i];
        }

        (energyOut / energyIn).Should().BeInRange(0.5, 1.5,
            "a shift up and back must not swallow or inflate the signal");
    }

    [Fact]
    public void No_Configured_Centre_Is_A_Pass_Through_Not_A_Filter()
    {
        var shift = ArdopChannelShift.For(null, Rate);
        float[] original = TwoTone();

        shift.IsShifted.Should().BeFalse();
        shift.Transmit(original).Should().BeSameAs(original, "an unshifted TNC pays nothing");
        shift.Describe().Should().BeEmpty();
    }

    [Theory]
    // Centre too low or too high for ARDOP's widest 2000 Hz session to fit in the passband.
    [InlineData(950, true)]
    [InlineData(500, true)]
    [InlineData(1500, false)]
    [InlineData(2500, true)]
    // Outside the audio band altogether.
    [InlineData(0, true)]
    [InlineData(6000, true)]
    public void A_Centre_That_Cannot_Fit_Every_Bandwidth_Is_Flagged(double centre, bool expectConcern)
    {
        string? concern = ArdopChannelShift.Concern(centre, Rate);

        (concern is not null).Should().Be(expectConcern);
    }
}
