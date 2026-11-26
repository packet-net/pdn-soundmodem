using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class TrainingSymbolsTests
{
    private static double D(System.Text.Json.JsonElement e) => double.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_Table_Matches_The_Prototypes_Six_Rows()
    {
        var rows = LinkVectorFile.Load("symbol_vectors.json").GetProperty("training").GetProperty("rows");
        rows.GetArrayLength().Should().Be(TrainingSymbols.Rows);
        for (int r = 0; r < TrainingSymbols.Rows; r++)
        {
            string expected = rows[r].GetString()!;
            expected.Length.Should().Be(TrainingSymbols.Carriers);
            string actual = string.Concat(TrainingSymbols.Quadrants(r).ToArray().Select(q => (char)('0' + q)));
            actual.Should().Be(expected);
        }
    }

    [Fact]
    public void The_Four_Points_Are_The_Doubles_The_Prototype_Gives_Exactly()
    {
        var pts = LinkVectorFile.Load("symbol_vectors.json").GetProperty("training").GetProperty("points");
        for (int q = 0; q < 4; q++)
        {
            TrainingSymbols.Point(q, out double re, out double im);
            re.Should().Be(D(pts.GetProperty(q.ToString())[0]));
            im.Should().Be(D(pts.GetProperty(q.ToString())[1]));
        }
    }

    [Fact]
    public void The_Points_Lie_Within_A_Few_Ulps_Of_Plus_Or_Minus_Root_Half()
    {
        double a = Math.Sqrt(0.5);
        for (int q = 0; q < 4; q++)
        {
            TrainingSymbols.Point(q, out double re, out double im);
            Math.Abs(re).Should().BeApproximately(a, 4e-16);
            Math.Abs(im).Should().BeApproximately(a, 4e-16);
        }
    }

    [Fact]
    public void Points_Of_A_Row_Follow_Its_Quadrants()
    {
        var re = new double[TrainingSymbols.Carriers];
        var im = new double[TrainingSymbols.Carriers];
        for (int r = 0; r < TrainingSymbols.Rows; r++)
        {
            TrainingSymbols.Points(r, re, im);
            var q = TrainingSymbols.Quadrants(r);
            for (int c = 0; c < q.Length; c++)
            {
                TrainingSymbols.Point(q[c], out double pr, out double pi);
                re[c].Should().Be(pr);
                im[c].Should().Be(pi);
            }
        }
    }

    [Fact]
    public void A_Row_Outside_The_Table_Is_Refused()
    {
        Action act = () => TrainingSymbols.Quadrants(6);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
