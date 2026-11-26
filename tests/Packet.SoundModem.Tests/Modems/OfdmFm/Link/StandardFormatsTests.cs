using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class StandardFormatsTests
{
    private static readonly System.Text.Json.JsonElement Vectors = LinkVectorFile.Load("formats.json");

    private static IEnumerable<System.Text.Json.JsonElement> Formats => Vectors.GetProperty("formats").EnumerateArray();

    [Fact]
    public void All_256_Formats_Describe_Themselves_As_The_Prototype_Does()
    {
        int n = 0;
        foreach (var f in Formats)
        {
            FormatSpec s = StandardFormats.Describe(f.GetProperty("number").GetInt32());
            s.Number.Should().Be(n);
            s.Band.Should().Be(f.GetProperty("band").GetInt32());
            s.Rung.Should().Be(f.GetProperty("rung").GetInt32());
            s.TopHz.Should().Be(f.GetProperty("top_hz").GetDouble());
            s.Carriers.Should().Be(f.GetProperty("carriers").GetInt32());
            s.InfoBitsPerCarrier.Should().Be(f.GetProperty("eta").GetDouble());
            s.ConstellationBits.Should().Be(f.GetProperty("bits").GetInt32());
            s.CodeRate.Should().Be(f.GetProperty("code_rate").GetDouble());
            s.DriveDb.Should().Be(f.GetProperty("drive_db").GetDouble());
            s.Opening.Should().Be(f.GetProperty("layout").GetString() switch { "full" => FormatOpening.Full, "n3x" => FormatOpening.Low3, _ => FormatOpening.Low1 });
            s.KbitPerSecond.Should().Be(f.GetProperty("kbit_s").GetDouble());
            StandardFormats.Rate(n).Should().Be(f.GetProperty("rate").GetDouble());
            n++;
        }

        n.Should().Be(256);
    }

    [Fact]
    public void Information_Bits_Match_For_Every_Format_At_Every_Length()
    {
        int checkedCount = 0;
        foreach (var f in Formats)
        {
            int number = f.GetProperty("number").GetInt32();
            int symbols = 4;
            foreach (var expected in f.GetProperty("info").EnumerateArray())
            {
                StandardFormats.InfoBits(number, symbols).Should().Be(expected.GetInt32(), "format {0} at {1} symbols", number, symbols);
                symbols += 4;
                checkedCount++;
            }
        }

        checkedCount.Should().Be(256 * 63);
    }

    [Fact]
    public void Slower_Matches_For_Every_Format_And_Every_Step()
    {
        foreach (var f in Formats)
        {
            int number = f.GetProperty("number").GetInt32();
            int rungs = 1;
            foreach (var expected in f.GetProperty("slower").EnumerateArray())
            {
                StandardFormats.Slower(number, rungs).Should().Be(expected.GetInt32(), "format {0} by {1}", number, rungs);
                rungs++;
            }
        }
    }

    [Fact]
    public void The_Most_Robust_And_First_Contact_Formats_Are_The_Prototypes()
    {
        StandardFormats.MostRobust.Should().Be(Vectors.GetProperty("most_robust").GetInt32());
        StandardFormats.FirstContact.Should().Be(Vectors.GetProperty("first_contact").GetInt32());
        StandardFormats.MostRobust.Should().Be(192);
        StandardFormats.Describe(192).KbitPerSecond.Should().BeApproximately(0.25, 0.01);
    }

    [Fact]
    public void A_Step_Slower_Never_Lands_On_Something_Faster()
    {
        for (int n = 0; n < 256; n++)
        {
            for (int r = 0; r <= 8; r++)
            {
                StandardFormats.Rate(StandardFormats.Slower(n, r)).Should().BeLessThanOrEqualTo(StandardFormats.Rate(n));
            }
        }
    }

    [Fact]
    public void Numbers_Outside_The_List_Are_Refused()
    {
        ((Action)(() => StandardFormats.Describe(256))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => StandardFormats.Describe(-1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => StandardFormats.Slower(0, -1))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
