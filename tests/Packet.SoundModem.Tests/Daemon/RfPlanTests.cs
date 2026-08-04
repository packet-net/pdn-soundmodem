using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Turning a band plan expressed in RF into a dial and the audio centres that follow. An SSB
/// dial is shared by everything in the passband, so the modems are not independent - one dial
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
        // The obvious round dial (7050.000) puts afsk300 at 300 Hz - half of it below where a
        // typical SSB filter starts. Choosing the dial should do better than the round number.
        RfPlan.Result plan = RfPlan.Solve(FortyMetres(), "usb");

        foreach (PlannedModem m in plan.Modems)
        {
            double low = m.AudioCentreHz - (m.Slot.BandwidthHz / 2);
            double high = m.AudioCentreHz + (m.Slot.BandwidthHz / 2);
            low.Should().BeGreaterThanOrEqualTo(Passband.Nominal.LowHz, $"modem {m.Slot.SubChannel} low edge");
            high.Should().BeLessThanOrEqualTo(Passband.Nominal.HighHz, $"modem {m.Slot.SubChannel} high edge");
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
        double headroomBelow = lowestEdge - Passband.Nominal.LowHz;
        double headroomAbove = Passband.Nominal.HighHz - highestEdge;

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
            .Should().BeGreaterThanOrEqualTo(Passband.Nominal.LowHz);
        plan.Modems.Max(m => m.AudioCentreHz + (m.Slot.BandwidthHz / 2))
            .Should().BeLessThanOrEqualTo(Passband.Nominal.HighHz);
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
        // But afsk300 then occupies 150-450 Hz, half of it below where an SSB filter starts -
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
            (Passband.Nominal.LowHz + Passband.Nominal.HighHz) / 2, RfPlan.DialStepHz,
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

    // ---- The passband a plan is fitted into, which is worked out rather than configured ----

    /// <summary>A 3 kHz waveform: wider than an ordinary SSB passband all by itself.</summary>
    private static RfSlot Ms110dSlot(double rf = 7_050_000) =>
        new(0, "ms110d-wn4", rf, 2789, FixedCentreHz: 1800);

    [Fact]
    public void An_Ordinary_Plan_Is_Placed_Identically_Whatever_The_Radio_Could_Do()
    {
        // The guard on the whole idea: widening the window only when it is needed means a station
        // that worked yesterday lands on exactly the same dial today, on any radio.
        RfPlan.Result nominal = RfPlan.Solve(FortyMetres(), "usb");
        RfPlan.Result openable = RfPlan.Solve(
            FortyMetres(), "usb", window: Passband.Fit(FortyMetres(), Passband.WideCeilingHz));

        openable.DialHz.Should().Be(nominal.DialHz);
        openable.Window.IsNominal.Should().BeTrue("2400 Hz of room was enough");
        openable.Modems.Select(m => m.AudioCentreHz)
            .Should().Equal(nominal.Modems.Select(m => m.AudioCentreHz));
    }

    [Fact]
    public void A_Three_Kilohertz_Waveform_Does_Not_Fit_An_Ordinary_Passband()
    {
        // Why the window has to be able to grow at all: no dial places a 2789 Hz-wide modem
        // inside 2400 Hz of room, so MS110D could not be planned in RF terms before this.
        Action solve = () => RfPlan.Solve([Ms110dSlot()], "usb");

        solve.Should().Throw<InvalidDataException>()
            .WithMessage("*more than the 2400 Hz a single SSB passband can carry*");
    }

    [Fact]
    public void The_Same_Waveform_Fits_A_Radio_Whose_Filters_The_Daemon_Opens()
    {
        List<RfSlot> slots = [Ms110dSlot(7_051_600)];

        Passband window = Passband.Fit(slots, Passband.WideCeilingHz);
        RfPlan.Result plan = RfPlan.Solve(slots, "usb", window: window);

        plan.Window.IsNominal.Should().BeFalse();
        plan.Modems.Should().ContainSingle();
        // Its centre is not the planner's to choose, so the dial is what moves: the one dial that
        // puts an 1800 Hz-fixed modem on 7.0516 MHz.
        plan.Modems[0].AudioCentreHz.Should().Be(1800);
        plan.DialHz.Should().Be(7_049_800);
    }

    [Fact]
    public void A_Spec_Fixed_Modem_Dictates_The_Dial_And_The_Others_Follow()
    {
        // The wide-slice shape: a 3 kHz waveform low in the plan, ordinary modems stacked above
        // it, one dial and one passband covering both.
        List<RfSlot> slots = [Ms110dSlot(7_051_600), Slot(1, "bpsk300", 7_054_000, 300)];

        RfPlan.Result plan = RfPlan.Solve(
            slots, "usb", window: Passband.Fit(slots, Passband.WideCeilingHz));

        plan.DialHz.Should().Be(7_049_800, "the fixed modem leaves no choice");
        plan.Modems.Single(m => m.Slot.SubChannel == 0).AudioCentreHz.Should().Be(1800);
        plan.Modems.Single(m => m.Slot.SubChannel == 1).AudioCentreHz.Should().Be(4200);
    }

    [Fact]
    public void A_Modem_On_The_Wrong_Side_Of_A_Fixed_Modems_Dial_Is_Told_Why()
    {
        // The easy mistake: putting the spec-fixed modem above the others, which forces a dial
        // that leaves them below it. A negative audio frequency means nothing to an operator.
        List<RfSlot> slots = [Ms110dSlot(7_056_000), Slot(1, "bpsk300", 7_051_600, 300)];

        Action solve = () => RfPlan.Solve(
            slots, "usb", window: Passband.Fit(slots, Passband.WideCeilingHz));

        solve.Should().Throw<InvalidDataException>()
            .WithMessage("*fixed at 1800 Hz by its own standard*")
            .WithMessage("*Put the spec-fixed modem lowest in the plan*");
    }

    [Fact]
    public void Two_Spec_Fixed_Modems_That_Want_Different_Dials_Are_Refused()
    {
        // No radio has two dials. Both centres are pinned, so neither can move to meet the other.
        List<RfSlot> slots =
        [
            Ms110dSlot(7_051_600),
            new(1, "freedv-datac1", 7_055_000, 500, FixedCentreHz: 1500),
        ];

        Action solve = () => RfPlan.Solve(slots, "usb");

        solve.Should().Throw<InvalidDataException>()
            .WithMessage("*both have an audio centre fixed by their own standard*")
            .WithMessage("*7.049800 MHz*7.053500 MHz*");
    }

    [Fact]
    public void Two_Spec_Fixed_Modems_Whose_Offsets_Agree_Share_A_Dial()
    {
        // 300 Hz apart on the band, and their centres are 300 Hz apart, so one dial serves both.
        List<RfSlot> slots =
        [
            Ms110dSlot(7_051_600),
            new(1, "freedv-datac1", 7_051_300, 500, FixedCentreHz: 1500),
        ];

        RfPlan.Result plan = RfPlan.Solve(
            slots, "usb", window: Passband.Fit(slots, Passband.WideCeilingHz));

        plan.DialHz.Should().Be(7_049_800);
        plan.Modems.Single(m => m.Slot.SubChannel == 1).AudioCentreHz.Should().Be(1500);
    }

    [Fact]
    public void A_Window_Grows_Only_As_Far_As_The_Modems_Need()
    {
        // Every extra Hz is noise bandwidth on receive and filter to open on transmit, so a
        // 2789 Hz waveform gets ~3 kHz of window, not the 10 kHz the radio would allow.
        Passband window = Passband.Fit([Ms110dSlot()], Passband.WideCeilingHz);

        window.LowHz.Should().Be(Passband.Nominal.LowHz, "the low edge has no reason to move");
        window.HighHz.Should().BeInRange(3200, 3400);
        window.WidthHz.Should().BeLessThan(Passband.WideCeilingHz / 2);
    }

    [Fact]
    public void A_Window_Never_Grows_Past_What_The_Radio_Can_Do()
    {
        // Two modems 30 kHz apart: no window fits them, and the refusal quotes the real ceiling
        // rather than the nominal figure, so the operator is not told to fix an imaginary limit.
        List<RfSlot> spread = [Slot(0, "bpsk300", 7_030_000, 300), Slot(1, "bpsk300", 7_060_000, 300)];

        Passband window = Passband.Fit(spread, Passband.WideCeilingHz);
        Action solve = () => RfPlan.Solve(spread, "usb", window: window);

        window.HighHz.Should().Be(Passband.WideCeilingHz);
        solve.Should().Throw<InvalidDataException>()
            .WithMessage("*this radio can be opened to pass in one go*");
    }

    [Fact]
    public void A_Radio_The_Daemon_Cannot_Open_Keeps_The_Nominal_Window()
    {
        // An ALSA sound card behind somebody's rig: the daemon has no way to widen a filter it
        // cannot see, so it must not plan as though it had.
        Passband window = Passband.Fit([Ms110dSlot()], Passband.Nominal.HighHz);

        window.Should().Be(Passband.Nominal);
    }

    [Fact]
    public void A_Widened_Window_Is_Reported_Because_The_Radio_Has_To_Match_It()
    {
        List<RfSlot> slots = [Ms110dSlot()];
        RfPlan.Result plan = RfPlan.Solve(
            slots, "usb", window: Passband.Fit(slots, Passband.WideCeilingHz));

        var report = new StringWriter();
        BandPlanner.Report(plan, report, radioIsSelfTuning: true);

        report.ToString().Should().Contain("passband:");

        var ordinary = new StringWriter();
        BandPlanner.Report(RfPlan.Solve(FortyMetres(), "usb"), ordinary, radioIsSelfTuning: true);
        ordinary.ToString().Should().NotContain("passband:",
            "an ordinary window is the assumption; saying so every time is noise");
    }
}
