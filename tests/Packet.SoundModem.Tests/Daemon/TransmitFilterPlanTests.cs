using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Working out what a radio's transmit filter has to pass for a set of modems placed by audio
/// centre (the case with no band plan to read it off). The filter is a global, persistent radio
/// setting, so an inherited one silently truncates anything wider than it - a 3 kHz waveform
/// against the 3000 Hz a rig is usually left on being the case this exists for.
/// </summary>
public class TransmitFilterPlanTests
{
    private const int AudioRate = 12000;
    private const int WidebandRate = 48000;

    private static ModemConfig Modem(int sub, string mode, double? frequency = null, double? bandwidth = null) =>
        new() { SubChannel = sub, Mode = mode, Frequency = frequency, Bandwidth = bandwidth };

    /// <summary>The high cut these modems need - measure, then aggregate, as start-up does.</summary>
    private static int? HighCut(int dspRate, params ModemConfig[] modems) =>
        TransmitFilterPlan.HighCutFor(TransmitFilterPlan.Bands(modems, dspRate));

    [Fact]
    public void A_Three_Kilohertz_Waveform_Asks_For_More_Than_A_Radio_Is_Left_On()
    {
        int? highCut = HighCut(WidebandRate, Modem(0, "ms110d-wn4"));

        // MS110D sits at a fixed 1800 Hz centre and occupies ~3 kHz, so it reaches past the
        // 3000 Hz high cut a rig is usually left on. Deriving the filter is the whole point:
        // inherit that 3000 and the top of every burst goes out truncated, with nothing said.
        highCut.Should().NotBeNull();
        highCut!.Value.Should().BeGreaterThan(3000, "a 3000 Hz filter clips this waveform");
        highCut.Value.Should().BeInRange(3300, 3500);
        (highCut.Value % 50).Should().Be(0, "the number is a radio setting, not a measurement");
    }

    [Fact]
    public void A_Narrow_Mode_Does_Not_Ask_For_A_Wide_Filter()
    {
        int? highCut = HighCut(AudioRate, Modem(0, "bpsk300", frequency: 1500));

        highCut.Should().NotBeNull();
        highCut!.Value.Should().BeInRange(1700, 2100, "300 baud BPSK at 1500 Hz is a few hundred Hz wide");
    }

    [Fact]
    public void The_Filter_Follows_A_Modem_Moved_Up_The_Passband()
    {
        int low = HighCut(AudioRate, Modem(0, "bpsk300", frequency: 1500))!.Value;
        int high = HighCut(AudioRate, Modem(0, "bpsk300", frequency: 2200))!.Value;

        // Measured at the centre each modem will actually transmit on, so where a modem sits
        // changes the answer - the same mode 700 Hz up needs 700 Hz more filter.
        (high - low).Should().BeInRange(650, 750);
    }

    [Fact]
    public void The_Highest_Reaching_Modem_Sets_The_Filter()
    {
        // Not the highest centre - the highest edge. A 1200 baud AFSK modem centred at 1700 Hz
        // reaches further up than a 300 baud BPSK one centred 500 Hz above it.
        ModemConfig[] all =
            [Modem(0, "bpsk300", frequency: 800), Modem(1, "bpsk300", frequency: 2200), Modem(2, "afsk1200", frequency: 1700)];

        int? together = HighCut(AudioRate, all);

        int widest = all.Max(m => HighCut(AudioRate, m)!.Value);
        together.Should().Be(widest, "one filter serves the whole passband, so the widest modem sets it");
        together.Should().Be(HighCut(AudioRate, all[2]),
            "afsk1200 at 1700 Hz is the one that reaches highest here");
    }

    [Fact]
    public void Ardop_Plans_For_The_Width_It_Can_Negotiate()
    {
        // Nothing to measure - ARDOP's bandwidth is a per-session negotiation - so the cap
        // stands in, and without one, the widest session it could ask for.
        int widest = HighCut(AudioRate, Modem(0, "ardop", frequency: 1500))!.Value;
        int capped = HighCut(AudioRate, Modem(0, "ardop", frequency: 1500, bandwidth: 500))!.Value;

        widest.Should().Be(2700, "1500 Hz centre + half of 2000 Hz + margin");
        capped.Should().Be(1950, "1500 Hz centre + half of 500 Hz + margin");
    }

    [Fact]
    public void An_Unmeasurable_Mode_Leaves_The_Radio_Alone()
    {
        // A filter derived from a guess would be worse than the one the radio already had, and
        // an unknown mode is reported far more usefully by the start-up checks.
        int? highCut = HighCut(AudioRate, Modem(0, "not-a-mode"));

        highCut.Should().BeNull();
    }

    [Fact]
    public void Every_Band_Is_Measured_Around_The_Centre_It_Was_Configured_For()
    {
        IReadOnlyList<TransmitFilterPlan.Band> bands = TransmitFilterPlan.Bands(
            [Modem(3, "bpsk300", frequency: 1200)], AudioRate);

        TransmitFilterPlan.Band band = bands.Should().ContainSingle().Subject;
        band.SubChannel.Should().Be(3);
        band.Mode.Should().Be("bpsk300");
        band.LowHz.Should().BeLessThan(1200);
        band.HighHz.Should().BeGreaterThan(1200);
    }

    [Fact]
    public void A_Fixed_Centre_Mode_Is_Measured_Rather_Than_Refused()
    {
        // ms110d-* and freedv-* reject a centre frequency, so the probe must be built without
        // one - asking for a band must not throw the way ModemCatalog.Create would.
        IReadOnlyList<TransmitFilterPlan.Band> bands = TransmitFilterPlan.Bands(
            [Modem(0, "ms110d-wn4"), Modem(1, "freedv-datac1")], WidebandRate);

        bands.Should().HaveCount(2);
        bands.Should().AllSatisfy(b => b.HighHz.Should().BeGreaterThan(b.LowHz));
    }
}
