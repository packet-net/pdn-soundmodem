using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

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
    public void An_Fm_Stations_Modems_Are_Left_Exactly_Where_They_Are()
    {
        // Tom's FM radio: everything on one 2 m channel. This set was refused twice over before
        // issue #413 - "fm" was not a sideband, and a baseband mode cannot be band-planned - so
        // the only way to enter the channel at all was to claim the radio was USB and have every
        // audio frequency labelled as an RF one it is not.
        var modems = new List<ModemConfig>
        {
            new() { SubChannel = 0, Mode = "c4fsk9600", RfFrequency = 145_300_000 },
            new() { SubChannel = 1, Mode = "afsk1200", RfFrequency = 145_300_000, Frequency = 1500 },
        };

        RfPlan.Result? plan = BandPlanner.Plan(modems, "fm", pinnedDialHz: null, dspRate: 48000);

        plan.Should().NotBeNull();
        plan!.IsFm.Should().BeTrue();
        plan.DialHz.Should().Be(145_300_000, "the channel is the whole of the plan");
        plan.Modems.Should().AllSatisfy(m => m.Slot.RfCentreHz.Should().Be(145_300_000));

        // Nothing is written back, because nothing was worked out: a baseband mode still has no
        // audio centre and one that had a centre keeps the one it was given.
        modems[0].Frequency.Should().BeNull("a baseband mode has no centre to be given one");
        modems[1].Frequency.Should().Be(1500, "an FM plan moves nobody");
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

    private static List<ModemConfig> JohnsHeadEnd() =>
        [new() { SubChannel = 0, Mode = "ms110d-wn4", RfFrequency = 7_053_800 }];

    private static void EveryModemBuilds(IEnumerable<ModemConfig> modems)
    {
        foreach (ModemConfig m in modems)
        {
            Action build = () => ModemCatalog.Create(
                m.Mode, 48000, _ => { }, new ModemOptions(CentreFrequencyHz: m.Frequency));
            build.Should().NotThrow($"modem {m.SubChannel} ({m.Mode}) at {m.Frequency} Hz audio");
        }
    }

    [Fact]
    public void Johns_Single_Wide_Modem_Gets_A_Dial_Where_It_Builds()
    {
        List<ModemConfig> modems = JohnsHeadEnd();

        RfPlan.Result plan = BandPlanner.Plan(modems, "usb", null, 48000)!;

        plan.DialHz.Should().Be(7_052_300);
        modems[0].Frequency.Should().Be(1500);
        EveryModemBuilds(modems);

        var report = new StringWriter();
        BandPlanner.Report(plan, report);
        report.ToString().Trim().Should().Be("band plan: dial 7.052300 MHz USB, set your radio to this; ms110d-wn4 at 1500 Hz audio");
        plan.Warnings.Should().BeEmpty("a wide MS110D on an ordinary rig is normal");
    }

    [Theory]
    [InlineData("usb")]
    [InlineData("lsb")]
    public void Mixed_Ensembles_Are_Built_At_The_Centres_The_Plan_Chose(string sideband)
    {
        // The wide modem lowest, highest and in the middle of the RF order, so that on both
        // sidebands it is sometimes the one nearest DC.
        int[][] orders = [[0, 1, 2], [2, 1, 0], [1, 0, 2]];
        int built = 0;
        foreach (int[] order in orders)
        {
            double[] rf = [7_050_300, 7_051_600, 7_053_800];
            string[] modes = ["afsk300-il2pc", "bpsk300", "ms110d-wn4"];
            List<ModemConfig> modems = [.. order.Select((o, n) => new ModemConfig
            {
                SubChannel = n, Mode = modes[o], RfFrequency = rf[n],
            })];

            Action plan = () => BandPlanner.Plan(
                modems, sideband, null, 48000, Passband.WideCeilingHz);

            // Either the plan places every modem where it builds, or it refuses and says so; it
            // never hands back a placement the modem then throws on.
            try
            {
                plan();
            }
            catch (InvalidDataException)
            {
                continue;
            }

            EveryModemBuilds(modems);
            built++;
        }

        built.Should().BeGreaterThan(0, "refusing every arrangement would make this test vacuous");
    }

    [Theory]
    [InlineData("usb")]
    [InlineData("lsb")]
    public void A_Wide_Modem_Beside_A_Narrow_One_Is_Built_On_Either_Sideband(string sideband)
    {
        List<ModemConfig> modems =
        [
            new() { SubChannel = 0, Mode = "bpsk300", RfFrequency = 7_053_000 },
            new() { SubChannel = 1, Mode = "ms110d-wn4", RfFrequency = 7_053_800 },
        ];

        RfPlan.Result plan = BandPlanner.Plan(modems, sideband, null, 48000)!;

        plan.Should().NotBeNull();
        EveryModemBuilds(modems);
    }

    [Fact]
    public void A_Pinned_Dial_That_Puts_A_Modem_Out_Of_Range_Says_So_Before_The_Modem_Does()
    {
        // A guard-bound family (freedv) is still refused at plan time, naming the dial that works.
        var modems = new List<ModemConfig>
        {
            new() { SubChannel = 0, Mode = "freedv-datac3", RfFrequency = 7_053_800 },
        };
        (double min, double _) = ModemCatalog.AudioCentreRange("freedv-datac3", 48000)!.Value;
        double badDial = 7_053_800 - (min - 200);

        Action plan = () => BandPlanner.Plan(modems, "usb", badDial, 48000);

        plan.Should().Throw<InvalidDataException>().WithMessage("*dialFrequency*freedv-datac3*use *MHz*");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1278)]
    [InlineData(1500)]
    [InlineData(2000)]
    public void Ms110d_Is_Built_Wherever_The_User_Puts_It(double centre)
    {
        List<ModemConfig> modems =
        [
            new() { SubChannel = 0, Mode = "ms110d-wn4", RfFrequency = 7_053_800 },
        ];

        // Pinning the dial is how a user places the signal: audio = rf - dial.
        RfPlan.Result plan = BandPlanner.Plan(modems, "usb", 7_053_800 - centre, 48000)!;

        modems[0].Frequency.Should().Be(centre);
        plan.Warnings.Should().NotBeEmpty("a pinned dial far from the plan's centring is still noted");
        EveryModemBuilds(modems);
    }
}
