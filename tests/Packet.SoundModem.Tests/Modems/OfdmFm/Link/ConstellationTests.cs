using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class ConstellationTests
{
    private static double D(System.Text.Json.JsonElement e) => double.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public void Every_Bit_Pattern_Gives_The_Prototypes_Point_Exactly(int bits)
    {
        var v = LinkVectorFile.Load("symbol_vectors.json").GetProperty("constellations").GetProperty(bits.ToString());
        var re = v.GetProperty("re");
        var im = v.GetProperty("im");
        re.GetArrayLength().Should().Be(1 << bits);
        var c = Constellation.For(bits);
        for (int p = 0; p < 1 << bits; p++)
        {
            c.Point(p, out double r, out double i);
            r.Should().Be(D(re[p]), $"pattern {p} real");
            i.Should().Be(D(im[p]), $"pattern {p} imaginary");
        }

        c.HalfDistance.Should().Be(D(v.GetProperty("half_distance")));
    }

    [Fact]
    public void Map_Takes_Bits_First_Bit_Most_Significant_One_Carrier_After_Another()
    {
        var c = Constellation.For(4);
        byte[] bits = [0, 1, 1, 0, 1, 1, 0, 0];
        var re = new double[2];
        var im = new double[2];
        c.Map(bits, re, im);
        c.Point(0b0110, out double r0, out double i0);
        c.Point(0b1100, out double r1, out double i1);
        re.Should().Equal(r0, r1);
        im.Should().Equal(i0, i1);
    }

    [Fact]
    public void Whole_Symbols_Match_The_Prototype_Including_Mixed_Loadings()
    {
        var cases = LinkVectorFile.Load("symbol_vectors.json").GetProperty("symbols");
        cases.GetArrayLength().Should().BeGreaterThanOrEqualTo(10);
        foreach (var c in cases.EnumerateArray())
        {
            var loading = c.GetProperty("loading").EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
            int symbols = c.GetProperty("symbols").GetInt32();
            for (int s = 0; s < symbols; s++)
            {
                string text = c.GetProperty("bits")[s].GetString()!;
                var bits = text.Select(ch => (byte)(ch - '0')).ToArray();
                var re = new double[loading.Length];
                var im = new double[loading.Length];
                Constellation.MapSymbol(bits, loading, re, im).Should().Be(bits.Length);
                for (int k = 0; k < loading.Length; k++)
                {
                    re[k].Should().Be(D(c.GetProperty("re")[s][k]), $"format {c.GetProperty("format")} symbol {s} carrier {k}");
                    im[k].Should().Be(D(c.GetProperty("im")[s][k]), $"format {c.GetProperty("format")} symbol {s} carrier {k}");
                }
            }
        }
    }

    [Fact]
    public void Unsupported_Sizes_Are_Refused()
    {
        foreach (int b in new[] { 0, 3, 5, 7, 9, 11 })
        {
            Action act = () => Constellation.For(b);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Fact]
    public void Every_Size_Has_Unit_Mean_Power()
    {
        foreach (int b in new[] { 1, 2, 4, 6, 8, 10 })
        {
            var c = Constellation.For(b);
            double sum = 0;
            for (int p = 0; p < 1 << b; p++)
            {
                c.Point(p, out double r, out double i);
                sum += (r * r) + (i * i);
            }

            (sum / (1 << b)).Should().BeApproximately(1.0, 1e-12);
        }
    }
}
