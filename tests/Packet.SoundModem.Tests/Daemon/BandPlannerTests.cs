using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The planner working on real modem entries - the seam above <see cref="RfPlanTests"/>, which
/// exercises the dial arithmetic on hand-built slots. What earns a test here is the behaviour
/// that changed when the spec-fixed families became movable: an <c>ms110d-*</c> or
/// <c>freedv-*</c> modem with an <c>rfFrequency</c> used to dictate the dial (it was the only
/// modem that could not be moved to suit one), and now it is placed like everything else.
/// </summary>
public class BandPlannerTests
{
    [Fact]
    public void A_Spec_Fixed_Mode_Is_Planned_Like_Any_Other_Modem()
    {
        // Tom's 40 m layout: the production pair low, an MS110D test modem in the space above.
        // Before the shift decorator this plan could only exist with ms110d dictating the dial,
        // which forced it LOWEST on USB - the exact opposite of the test-space layout.
        var modems = new List<ModemConfig>
        {
            new() { SubChannel = 0, Mode = "afsk300-il2pc", RfFrequency = 7_050_300 },
            new() { SubChannel = 2, Mode = "bpsk300", RfFrequency = 7_051_600 },
            new() { SubChannel = 3, Mode = "ms110d-wn6", RfFrequency = 7_053_500 },
        };

        RfPlan.Result? plan = BandPlanner.Plan(
            modems, "usb", pinnedDialHz: null, dspRate: 48000,
            passbandCeilingHz: Passband.WideCeilingHz);

        plan.Should().NotBeNull();

        // The dial is the planner's choice, not 7053500 - 1800: the spec-fixed modem no longer
        // dictates it. Every modem's audio centre must place it exactly on its RF frequency.
        foreach (ModemConfig modem in modems)
        {
            modem.Frequency.Should().NotBeNull($"modem {modem.SubChannel} must be placed");
            (plan!.DialHz + modem.Frequency!.Value).Should().BeApproximately(
                modem.RfFrequency!.Value, 0.5,
                $"modem {modem.SubChannel} must land on the RF frequency the operator asked for");
        }

        // And the layout survives: ms110d ABOVE the packet modems, which the old dictated dial
        // could not produce on USB.
        ModemConfig ms110d = modems.Single(m => m.Mode == "ms110d-wn6");
        ms110d.Frequency!.Value.Should().BeGreaterThan(
            modems.Single(m => m.Mode == "bpsk300").Frequency!.Value);
    }

    [Fact]
    public void The_Whole_Ensemble_Still_Fits_The_Radio_It_Has_To_Fit()
    {
        // The same plan on a radio the daemon cannot open (nominal 2400 Hz window) is refused
        // with the span named - the wide window is a Flex capability, not an assumption.
        var modems = new List<ModemConfig>
        {
            new() { SubChannel = 0, Mode = "afsk300-il2pc", RfFrequency = 7_050_300 },
            new() { SubChannel = 3, Mode = "ms110d-wn6", RfFrequency = 7_053_500 },
        };

        FluentActions.Invoking(() => BandPlanner.Plan(modems, "usb", null, 48000))
            .Should().Throw<InvalidDataException>().WithMessage("*span*");
    }
}
