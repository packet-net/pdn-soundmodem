using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Turning a band plan expressed in RF into a dial and the audio centres that follow. An SSB
/// dial is shared by everything in the passband, so the modems are not independent — one dial
/// has to place all of them, and the arithmetic is exactly what an operator would otherwise do
/// by hand and check by eye.
/// </summary>
public class RfPlanTests
{
    // M0LTE's 40m plan: three modes in a 1.3 kHz stretch of band.
    private const double Afsk300Rf = 7_050_300;
    private const double ArdopRf = 7_050_950;
    private const double Bpsk300Rf = 7_051_600;

    private static RfSlot Slot(int sub, string mode, double rf, double bw) => new(sub, mode, rf, bw);

    private static List<RfSlot> FortyMetres(double ardopBandwidth = 500) =>
    [
        Slot(0, "afsk300-il2pc", Afsk300Rf, 300),
        Slot(1, "ardop", ArdopRf, ardopBandwidth),
        Slot(2, "bpsk300", Bpsk300Rf, 300),
    ];

    [Fact]
    public void A_Band_Plan_Becomes_A_Dial_And_Audio_Centres()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        plan.Sideband.Should().Be("usb");
        plan.Modems.Should().HaveCount(3);

        // Every modem's audio centre must be exactly its RF offset from the chosen dial.
        foreach (PlannedModem m in plan.Modems)
        {
            m.AudioCentreHz.Should().BeApproximately(m.Slot.RfCentreHz - plan.DialHz, 0.001);
        }
    }

    [Fact]
    public void The_Chosen_Dial_Keeps_Everything_Clear_Of_The_Filter_Skirts()
    {
        // The obvious round dial (7050.000) puts afsk300 at 300 Hz — half of it below where a
        // typical SSB filter starts. Choosing the dial should do better than the round number.
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        foreach (PlannedModem m in plan.Modems)
        {
            double low = m.AudioCentreHz - (m.Slot.BandwidthHz / 2);
            double high = m.AudioCentreHz + (m.Slot.BandwidthHz / 2);
            low.Should().BeGreaterThanOrEqualTo(RfPlan.PassbandLowHz, $"modem {m.Slot.SubChannel} low edge");
            high.Should().BeLessThanOrEqualTo(RfPlan.PassbandHighHz, $"modem {m.Slot.SubChannel} high edge");
        }

        plan.Modems.Min(m => m.AudioCentreHz).Should().BeGreaterThan(
            300, "the lowest modem should not be left sitting on the filter corner");
    }

    [Fact]
    public void The_Ensemble_Is_Centred_Rather_Than_Pushed_To_One_End()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        double lowestEdge = plan.Modems.Min(m => m.AudioCentreHz - (m.Slot.BandwidthHz / 2));
        double highestEdge = plan.Modems.Max(m => m.AudioCentreHz + (m.Slot.BandwidthHz / 2));
        double headroomBelow = lowestEdge - RfPlan.PassbandLowHz;
        double headroomAbove = RfPlan.PassbandHighHz - highestEdge;

        headroomBelow.Should().BeApproximately(headroomAbove, RfPlan.DialStepHz,
            "spare room either side should be shared, not all left at one end");
    }

    [Fact]
    public void The_Dial_Lands_On_A_Step_An_Operator_Can_Actually_Set()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        (plan.DialHz % RfPlan.DialStepHz).Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Lower_Sideband_Mirrors_The_Arithmetic()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "lsb");

        plan.Sideband.Should().Be("lsb");
        foreach (PlannedModem m in plan.Modems)
        {
            // On LSB the audio spectrum is inverted: RF = dial - audio.
            m.AudioCentreHz.Should().BeApproximately(plan.DialHz - m.Slot.RfCentreHz, 0.001);
        }
    }

    [Fact]
    public void A_Wider_Ardop_Still_Fits_This_Plan_But_Uses_The_Room_Up()
    {
        // Peers are not obliged to stay at 500; planning for 2000 has to still work here.
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(ardopBandwidth: 2000), "usb");

        plan.Modems.Should().HaveCount(3);
        plan.Modems.Min(m => m.AudioCentreHz - (m.Slot.BandwidthHz / 2))
            .Should().BeGreaterThanOrEqualTo(RfPlan.PassbandLowHz);
        plan.Modems.Max(m => m.AudioCentreHz + (m.Slot.BandwidthHz / 2))
            .Should().BeLessThanOrEqualTo(RfPlan.PassbandHighHz);
    }

    [Fact]
    public void Modems_Spread_Wider_Than_A_Passband_Are_Refused_With_The_Reason()
    {
        // The original typo'd plan: 7030.3 and 7051.6 are 21 kHz apart. No dial helps.
        List<RfSlot> impossible =
        [
            Slot(0, "afsk300-il2pc", 7_030_300, 300),
            Slot(2, "bpsk300", 7_051_600, 300),
        ];

        Action solve = () => RfPlan.Solve(impossible, "usb");

        solve.Should().Throw<InvalidDataException>()
            .WithMessage("*more than the 2400 Hz a single SSB passband can carry*")
            .WithMessage("*No dial frequency can place them all*");
    }

    [Fact]
    public void A_Pinned_Dial_Is_Honoured_Exactly_Rather_Than_Re_Chosen()
    {
        // A dial that does fit: the operator's choice is used verbatim, no second-guessing.
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb", pinnedDialHz: 7_049_650);

        plan.DialHz.Should().Be(7_049_650);
        plan.Warnings.Should().BeEmpty();
        plan.Modems.Single(m => m.Slot.SubChannel == 0).AudioCentreHz.Should().Be(650);
        plan.Modems.Single(m => m.Slot.SubChannel == 1).AudioCentreHz.Should().Be(1300);
        plan.Modems.Single(m => m.Slot.SubChannel == 2).AudioCentreHz.Should().Be(1950);
    }

    [Fact]
    public void The_Obvious_Round_Dial_Is_The_One_That_Puts_Afsk300_On_The_Filter_Skirt()
    {
        // 7050.000 is what you reach for by hand, and it gives the tidy-looking 300/950/1600.
        // But afsk300 then occupies 150-450 Hz, half of it below where an SSB filter starts —
        // the exact hazard that made choosing the dial worth doing at all. Warned, not refused:
        // the passband figure is nominal and the operator asked for this dial.
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb", pinnedDialHz: 7_050_000);

        plan.DialHz.Should().Be(7_050_000);
        plan.Modems.Single(m => m.Slot.SubChannel == 0).AudioCentreHz.Should().Be(300);
        plan.Warnings.Should().ContainSingle()
            .Which.Should().Contain("150-450 Hz").And.Contain("nominal");
    }

    [Fact]
    public void A_Pinned_Dial_Far_Off_The_Plan_Warns_Against_The_Pin()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb", pinnedDialHz: 7_045_000);

        plan.DialHz.Should().Be(7_045_000);
        plan.Warnings.Should().ContainSingle()
            .Which.Should().Contain("pinned to 7.045000 MHz USB").And.Contain("fall outside");
    }

    [Fact]
    public void A_Single_Modem_Is_Planned_Like_Any_Other()
    {
        RfPlan.Result plan = RfPlan.Solve([Slot(0, "bpsk300", Bpsk300Rf, 300)], "usb");

        plan.Modems.Should().ContainSingle();
        plan.Modems[0].AudioCentreHz.Should().BeApproximately(
            (RfPlan.PassbandLowHz + RfPlan.PassbandHighHz) / 2, RfPlan.DialStepHz,
            "one modem should land in the middle of the passband");
    }

    [Fact]
    public void A_Sideband_That_Is_Not_A_Sideband_Is_Rejected()
    {
        Action solve = () => RfPlan.Solve(FortyMetres(), "upper");

        solve.Should().Throw<InvalidDataException>().WithMessage("*not a sideband*usb*lsb*");
    }

    [Fact]
    public void Dials_Are_Reported_The_Way_One_Is_Set()
    {
        RfPlan.Mhz(7_049_650).Should().Be("7.049650 MHz");
    }

    [Fact]
    public void The_Transmit_Filter_Is_Opened_Above_The_Highest_Modem()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        int high = BandPlanner.TransmitFilterHighHz(plan);

        double highestEdge = plan.Modems.Max(m => m.AudioCentreHz + (m.Slot.BandwidthHz / 2));
        high.Should().BeGreaterThan((int)highestEdge,
            "a filter cutting exactly at the highest modem puts its skirt on top of it");
        (high % 50).Should().Be(0, "radios take round filter cuts");
        high.Should().BeLessThan(3000, "no wider than the plan needs");
    }

    [Fact]
    public void A_Wider_Ardop_Opens_The_Filter_Further()
    {
        int narrow = BandPlanner.TransmitFilterHighHz(RfPlan.Solve(FortyMetres(500), "usb"));
        int wide = BandPlanner.TransmitFilterHighHz(RfPlan.Solve(FortyMetres(2000), "usb"));

        wide.Should().BeGreaterThan(narrow,
            "planning for a 2000 Hz session has to leave room for one");
    }

    [Fact]
    public void The_Report_Tells_A_Manual_Operator_To_Tune_And_A_Flex_Owner_What_Happened()
    {
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        var manual = new StringWriter();
        BandPlanner.Report(plan, manual, radioIsSelfTuning: false);
        var selfTuning = new StringWriter();
        BandPlanner.Report(plan, selfTuning, radioIsSelfTuning: true);

        manual.ToString().Should().Contain("set your radio to this");
        selfTuning.ToString().Should().NotContain("set your radio",
            "the daemon is setting it; telling the operator to do it as well is just confusing");
        selfTuning.ToString().Should().Contain("7.049450 MHz USB");
    }
}
