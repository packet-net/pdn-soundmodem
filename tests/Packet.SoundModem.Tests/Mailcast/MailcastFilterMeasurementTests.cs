using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>"Measure my filter": the dial arithmetic (<see cref="MailcastOnAir"/>) and the
/// passband-edge analysis (<see cref="MailcastFilterAnalysis"/>), each on data built by hand so
/// the exact bin and the exact rounding are known in advance.</summary>
public sealed class MailcastFilterMeasurementTests
{
    [Theory]
    [InlineData(1278, 7_052_520)] // the FT-450D worked example in docs/14-mailcast.md
    [InlineData(1800, 7_052_000)] // the default dial, round-tripped
    [InlineData(1500, 7_052_300)] // the old narrow-filter dial, round-tripped
    public void Suggested_Dial_Is_7_0538_Mhz_Minus_The_Passband_Centre_Rounded_To_10_Hz(
        double centreHz, double expectedDialHz)
    {
        MailcastOnAir.SuggestedDialHz(centreHz).Should().Be(expectedDialHz);
    }

    [Theory]
    [InlineData(7_052_522, 7_052_520)] // 2 Hz above 7052520, 8 short of 7052530: rounds down
    [InlineData(7_052_525, 7_052_520)] // exactly midway: .NET's default (banker's) rounding - to even
    [InlineData(7_052_535, 7_052_540)] // exactly midway the other way: also to even
    [InlineData(7_052_528, 7_052_530)] // 8 Hz above 7052520, 2 short of 7052530: rounds up
    public void Suggested_Dial_Rounds_To_The_Nearest_10_Hz(double rawDialHz, double expectedDialHz)
    {
        // Centres are not usually round numbers once a real filter is measured, so the dial that
        // falls out is not either; this is the rounding an operator tuning by hand actually gets.
        double centreHz = MailcastOnAir.SignalCentreHz - rawDialHz;
        MailcastOnAir.SuggestedDialHz(centreHz).Should().Be(expectedDialHz);
    }

    [Fact]
    public void Passband_Centre_Is_The_Plain_Average_Of_The_Edges()
    {
        // 367 to 2190 Hz: the FT-450D worked example. (367 + 2190) / 2 = 1278.5, which .NET's
        // default rounding takes to 1278 (the nearer even integer) - the same 1278 Hz the docs
        // and the decode test (MailcastReceiveTests) use.
        MailcastOnAir.PassbandCentreHz(367, 2190).Should().Be(1278);
    }

    private static double[] FlatBand(double lowHz, double highHz, double binWidthHz, int bins, double onPower = 1.0, double offPower = 1e-6)
    {
        var power = new double[bins];
        int lowBin = (int)Math.Round(lowHz / binWidthHz);
        int highBin = (int)Math.Round(highHz / binWidthHz);
        for (int bin = 0; bin < bins; bin++)
        {
            power[bin] = bin >= lowBin && bin < highBin ? onPower : offPower;
        }

        return power;
    }

    [Fact]
    public void A_Flat_Passband_s_Edges_Are_Found_6_Db_Down_From_Its_Middle()
    {
        const double BinWidthHz = 48000.0 / 8192; // WaterfallSource's own bin width at 48 kHz
        double[] power = FlatBand(700, 3200, BinWidthHz, 700); // 2500 Hz wide: not narrow

        MailcastFilterMeasurement measured = MailcastFilterAnalysis.Analyze(power, BinWidthHz);

        measured.LowHz.Should().BeApproximately(700, BinWidthHz * 2);
        measured.HighHz.Should().BeApproximately(3200, BinWidthHz * 2);
        measured.WidthHz.Should().BeApproximately(2500, BinWidthHz * 4);
        measured.Note.Should().BeNull("2500 Hz is wide enough for MS110D's full width");
        measured.SuggestedDialHz.Should().Be(
            MailcastOnAir.SuggestedDialHz(MailcastOnAir.PassbandCentreHz(measured.LowHz!.Value, measured.HighHz!.Value)));
    }

    [Fact]
    public void A_Passband_Narrower_Than_2_4_Khz_Gets_A_Note_To_Use_The_Widest_Or_Data_Filter()
    {
        const double BinWidthHz = 48000.0 / 8192;
        double[] power = FlatBand(900, 2100, BinWidthHz, 700); // 1200 Hz wide

        MailcastFilterMeasurement measured = MailcastFilterAnalysis.Analyze(power, BinWidthHz);

        measured.WidthHz.Should().BeLessThan(MailcastFilterAnalysis.NarrowFilterWidthHz);
        measured.Note.Should().NotBeNull().And.Contain("narrower than about 2.4 kHz").And.Contain("DATA");
    }

    [Fact]
    public void A_Passband_That_Never_Rolls_Off_Across_The_Whole_Search_Range_Is_Unclear()
    {
        const double BinWidthHz = 48000.0 / 8192;
        // Flat from well below the search floor to well above its ceiling: no shoulder to find.
        double[] power = FlatBand(0, 4000, BinWidthHz, 700);

        MailcastFilterMeasurement measured = MailcastFilterAnalysis.Analyze(power, BinWidthHz);

        measured.LowHz.Should().BeNull();
        measured.Note.Should().NotBeNull().And.Contain("unclear");
    }

    [Fact]
    public void Silence_Is_Unclear_Rather_Than_A_Division_By_Zero()
    {
        const double BinWidthHz = 48000.0 / 8192;
        var power = new double[700];

        MailcastFilterMeasurement measured = MailcastFilterAnalysis.Analyze(power, BinWidthHz);

        measured.LowHz.Should().BeNull();
        measured.Note.Should().NotBeNull().And.Contain("unclear");
    }
}
