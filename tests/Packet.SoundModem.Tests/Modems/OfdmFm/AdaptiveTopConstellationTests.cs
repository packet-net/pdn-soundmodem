using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The bound a station's own <see cref="OfdmFmParameters.AdaptiveTopConstellation"/> setting puts
/// on what its <see cref="OfdmFmRateController"/> will ever recommend, and the profile field that
/// carries it.
/// </summary>
/// <remarks>
/// See docs/dev/ofdm-fm/receiver-findings.md for the measurement the default
/// comes from: on a Raspberry Pi 4 Model B Rev 1.5, QAM-256 rate 2/3 cost more to decode than its
/// burst spent on air, and QAM-64 was the densest constellation that did not.
/// </remarks>
public class AdaptiveTopConstellationTests
{
    private static int At(OfdmFmRateController controller) => OfdmFmRateLadder.IndexOf(
        controller.Recommendation.Constellation, controller.Recommendation.Coding);

    private static OfdmFmBurst Burst(int rung, double spentFraction, bool copied = true)
    {
        OfdmFmRate rate = OfdmFmRateLadder.Rungs[rung];
        return new OfdmFmBurst(
            copied ? [1, 2, 3] : null,
            rate.Constellation,
            0,
            rate.Coding,
            spentFraction * OfdmFmRateLadder.CliffPreFecErrorRate(rate.Coding));
    }

    // A perfect path would climb an unbounded controller to the top of the ladder if nothing
    // stopped it (see The_Bound_Does_Nothing_To_A_Controller_That_Was_Not_Given_One below for
    // that baseline); a few hundred clean bursts is comfortably more than the whole ladder's
    // step-up backoff could ever need.
    private static void ClimbOnCleanBursts(OfdmFmRateController controller)
    {
        for (int i = 0; i < 500; i++)
        {
            controller.Heard(Burst(At(controller), 0.0));
        }
    }

    [Fact]
    public void The_Controller_Never_Recommends_Past_Its_Bound()
    {
        var controller = new OfdmFmRateController(topConstellation: OfdmFmConstellation.Qam16);

        ClimbOnCleanBursts(controller);

        // Exactly at the bound, not merely under it: settling short of a rung the bound allows
        // would be a second, undocumented limit, not the one this setting is for.
        controller.Recommendation.Constellation.Should().Be(OfdmFmConstellation.Qam16);
    }

    [Fact]
    public void The_Bound_Does_Nothing_To_A_Controller_That_Was_Not_Given_One()
    {
        // Every existing caller of this constructor, before this field existed, must see exactly
        // what it saw before. NOT OfdmFmRateLadder.Fastest: a clean path settles wherever the
        // 8 % step-up hysteresis stops paying for another step, and on this ladder that is QAM-64
        // K=7 5/6 - QAM-256 2/3 delivers less than it, and K=7 7/8's own 3.4 % edge over 5/6
        // never clears the 8 % bar once the controller is actually standing on 5/6. This is the
        // SAME plateau an unbounded controller has always stopped at; the point of this test is
        // that adding topConstellation with a default of Qam256 does not move it.
        var controller = new OfdmFmRateController();

        ClimbOnCleanBursts(controller);

        controller.Recommendation.Should().Be(
            OfdmFmRateLadder.Rungs[OfdmFmRateLadder.IndexOf(
                OfdmFmConstellation.Qam64, new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 5, 6, true))]);
    }

    [Fact]
    public void The_Bound_Never_Touches_What_The_Far_End_Is_Obeyed_At()
    {
        // "The far end's asks are still obeyed; only what this station asks for is bounded."
        // Transmit is what the far end asked this station for, and a bound below the ladder's
        // top must not clip it.
        var controller = new OfdmFmRateController(topConstellation: OfdmFmConstellation.Bpsk);
        OfdmFmRate askedForQam256 = OfdmFmRateLadder.Rungs[9];

        controller.Heard(Burst(0, 0.1) with { Recommendation = askedForQam256 });

        controller.Transmit.Should().Be(askedForQam256);
    }

    [Fact]
    public void The_Default_Profile_Bound_Is_What_The_Pi_Measurement_Found_Safe()
    {
        new OfdmFmParameters(48000, 2048, 64, 40, 300, 20)
            .AdaptiveTopConstellation.Should().Be(OfdmFmConstellation.Qam64);
    }

    [Fact]
    public void The_Bound_Round_Trips_Through_The_Profile_Loader()
    {
        // Bound by constructor-parameter name, exactly the way followOnFrames and every other
        // field of this record is - see OfdmFmParameters.LoadLocal.
        string path = Path.Combine(Path.GetTempPath(), $"ofdm-fm-bench-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "narrow": {
                "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
                "firstCarrier": 4, "dataCarriers": 16, "pilotCarriers": 4,
                "adaptiveTopConstellation": 4
              }
            }
            """);

        try
        {
            IReadOnlyDictionary<string, OfdmFmParameters>? loaded =
                OfdmFmParameters.LoadLocal(path);

            loaded.Should().NotBeNull();
            loaded!["narrow"].AdaptiveTopConstellation.Should().Be(OfdmFmConstellation.Qam16);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Profile_With_No_Bound_In_Its_JSON_Gets_The_Measured_Default()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ofdm-fm-bench-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "narrow": {
                "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
                "firstCarrier": 4, "dataCarriers": 16, "pilotCarriers": 4
              }
            }
            """);

        try
        {
            IReadOnlyDictionary<string, OfdmFmParameters>? loaded =
                OfdmFmParameters.LoadLocal(path);

            loaded!["narrow"].AdaptiveTopConstellation.Should().Be(OfdmFmConstellation.Qam64);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
