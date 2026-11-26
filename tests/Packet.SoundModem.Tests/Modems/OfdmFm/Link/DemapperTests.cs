using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class DemapperTests
{
    private static readonly Lazy<JsonElement> Vectors = new(() => LinkVectorFile.Load("demap_vectors.json"));

    // Agreement is a relative or absolute 1e-9, whichever is looser, on the doubles before they become floats.
    private const double Tolerance = 1e-9;

    private static double[] Doubles(JsonElement array)
    {
        var values = new double[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement e in array.EnumerateArray())
        {
            values[i++] = e.GetDouble();
        }

        return values;
    }

    private static double Excess(double actual, double expected)
    {
        // How many times the allowed difference this difference is; at most 1 passes.
        return Math.Abs(actual - expected) / Math.Max(Tolerance, Tolerance * Math.Abs(expected));
    }

    [Fact]
    public void Single_Carriers_Of_Every_Size_Match_The_Prototype_At_Every_Noise_Level()
    {
        double worst = 0;
        int checkedValues = 0;
        var sizes = new HashSet<int>();
        foreach (JsonElement set in Vectors.Value.GetProperty("points").EnumerateArray())
        {
            int b = set.GetProperty("bits").GetInt32();
            sizes.Add(b);
            double[] re = Doubles(set.GetProperty("re"));
            double[] im = Doubles(set.GetProperty("im"));
            double[] var = Doubles(set.GetProperty("var"));
            double[] llr = Doubles(set.GetProperty("llr"));
            re.Length.Should().BeGreaterThan(200);
            llr.Length.Should().Be(re.Length * b);
            byte[] loading = [(byte)b];
            var got = new double[b];
            for (int i = 0; i < re.Length; i++)
            {
                int n = Demapper.DemapSymbol([re[i]], [im[i]], [var[i]], loading, got);
                n.Should().Be(b);
                for (int k = 0; k < b; k++)
                {
                    double e = Excess(got[k], llr[(i * b) + k]);
                    e.Should().BeLessThanOrEqualTo(1.0, $"bits {b} point {i} bit {k}: {got[k]:R} against {llr[(i * b) + k]:R}");
                    worst = Math.Max(worst, Math.Abs(got[k] - llr[(i * b) + k]));
                    checkedValues++;
                }
            }
        }

        sizes.Should().BeEquivalentTo([1, 2, 4, 6, 8, 10]);
        checkedValues.Should().BeGreaterThan(5000);
        TestContext.Current.SendDiagnosticMessage($"largest single-carrier difference {worst:E3}");
    }

    [Fact]
    public void Whole_Payload_Symbols_Match_The_Prototype()
    {
        foreach (JsonElement c in Vectors.Value.GetProperty("symbols").EnumerateArray())
        {
            int symbols = c.GetProperty("symbols").GetInt32();
            byte[] loading = Doubles(c.GetProperty("loading")).Select(v => (byte)v).ToArray();
            double[] re = Doubles(c.GetProperty("re"));
            double[] im = Doubles(c.GetProperty("im"));
            double[] var = Doubles(c.GetProperty("var"));
            double[] expected = Doubles(c.GetProperty("llr"));
            int perSymbol = Demapper.BitsPerSymbol(loading);
            expected.Length.Should().Be(symbols * perSymbol);
            loading.Length.Should().Be(StandardFormats.FullBandCarriers);

            var got = new double[expected.Length];
            for (int s = 0; s < symbols; s++)
            {
                int n = Demapper.DemapSymbol(re.AsSpan(s * loading.Length, loading.Length), im.AsSpan(s * loading.Length, loading.Length), var, loading, got.AsSpan(s * perSymbol));
                n.Should().Be(perSymbol);
            }

            double worst = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                Excess(got[i], expected[i]).Should().BeLessThanOrEqualTo(1.0, $"format {c.GetProperty("format")} value {i}");
                worst = Math.Max(worst, Math.Abs(got[i] - expected[i]));
            }

            TestContext.Current.SendDiagnosticMessage($"largest payload difference, format {c.GetProperty("format")}: {worst:E3}");

            // The whole-payload form gives the same values, as floats.
            var floats = new float[expected.Length];
            Demapper.DemapPayload(re, im, var, loading, symbols, floats).Should().Be(expected.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                floats[i].Should().Be((float)got[i]);
            }
        }
    }

    [Fact]
    public void Every_Sign_Is_Right_For_Noiseless_Points_Of_Every_Size()
    {
        var rng = new Random(838);
        foreach (int b in new[] { 1, 2, 4, 6, 8, 10 })
        {
            const int carriers = 64;
            var bits = new byte[carriers * b];
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = (byte)rng.Next(2);
            }

            var re = new double[carriers];
            var im = new double[carriers];
            Constellation.For(b).Map(bits, re, im);
            var var = new double[carriers];
            Array.Fill(var, 0.01);
            var loading = new byte[carriers];
            Array.Fill(loading, (byte)b);
            var llr = new float[bits.Length];
            Demapper.DemapSymbol(re, im, var, loading, llr).Should().Be(bits.Length);
            for (int i = 0; i < bits.Length; i++)
            {
                (bits[i] == 0 ? llr[i] > 0 : llr[i] < 0).Should().BeTrue($"bits {b} value {i}: bit {bits[i]} gave {llr[i]}");
            }
        }
    }

    [Fact]
    public void A_Mixed_Loading_Round_Trips_And_Skips_Unloaded_Carriers()
    {
        var rng = new Random(839);
        byte[] loading = [0, 1, 2, 4, 0, 6, 8, 10, 0, 2];
        int total = Demapper.BitsPerSymbol(loading);
        total.Should().Be(33);
        var bits = new byte[total];
        for (int i = 0; i < total; i++)
        {
            bits[i] = (byte)rng.Next(2);
        }

        var re = new double[loading.Length];
        var im = new double[loading.Length];
        Constellation.MapSymbol(bits, loading, re, im).Should().Be(total);
        var var = new double[loading.Length];
        Array.Fill(var, 0.001);
        var llr = new float[total + 5];
        Array.Fill(llr, 99f);
        Demapper.DemapSymbol(re, im, var, loading, llr).Should().Be(total);
        for (int i = 0; i < total; i++)
        {
            (bits[i] == 0 ? llr[i] > 0 : llr[i] < 0).Should().BeTrue($"value {i}");
        }

        llr.AsSpan(total).ToArray().Should().OnlyContain(x => x == 99f);
    }

    [Fact]
    public void Soft_Values_Shrink_As_The_Noise_Grows()
    {
        // A point on a constellation point: the more noise, the less sure each bit.
        byte[] loading = [4];
        var small = new float[4];
        var large = new float[4];
        Demapper.DemapSymbol([3 * Constellation.For(4).HalfDistance], [Constellation.For(4).HalfDistance], [0.001], loading, small);
        Demapper.DemapSymbol([3 * Constellation.For(4).HalfDistance], [Constellation.For(4).HalfDistance], [0.1], loading, large);
        for (int i = 0; i < 4; i++)
        {
            Math.Abs(large[i]).Should().BeLessThan(Math.Abs(small[i]));
            Math.Sign(large[i]).Should().Be(Math.Sign(small[i]));
        }
    }

    [Fact]
    public void Bad_Arguments_Are_Refused()
    {
        double[] one = [0.0];
        var llr = new float[10];
        ((Action)(() => Demapper.DemapSymbol(one, one, one, [3], llr))).Should().Throw<ArgumentException>();
        ((Action)(() => Demapper.DemapSymbol(one, one, [], [2], llr))).Should().Throw<ArgumentException>();
        ((Action)(() => Demapper.DemapSymbol(one, one, one, [10], new float[9]))).Should().Throw<ArgumentException>();
        ((Action)(() => Demapper.DemapPayload(new double[3], new double[3], one, [2], 2, llr))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Demapping_A_Full_Symbol_Allocates_Nothing()
    {
        const int carriers = StandardFormats.FullBandCarriers;
        var re = new double[carriers];
        var im = new double[carriers];
        var var = new double[carriers];
        Array.Fill(var, 0.01);
        var loading = new byte[carriers];
        Array.Fill(loading, (byte)10);
        var llr = new float[carriers * 10];
        Demapper.DemapSymbol(re, im, var, loading, llr);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Demapper.DemapSymbol(re, im, var, loading, llr);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}
