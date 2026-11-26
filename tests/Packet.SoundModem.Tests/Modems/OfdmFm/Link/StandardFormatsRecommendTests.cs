using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>Recommend and MarginDb against the prototype's formats.recommend and formats.margin_db (proto/vectors/station_vectors.py).</summary>
public class StandardFormatsRecommendTests
{
    private static readonly double[] MarginSteps = { 0.0, 1.5, 3.0, 6.0 };

    [Fact]
    public void Recommend_Matches_The_Prototype_For_Every_Profile_Margin_And_Ceiling()
    {
        var doc = StationVectorFile.Functions;
        int profiles = doc.GetProperty("count").GetInt32();
        profiles.Should().BeGreaterThanOrEqualTo(200);
        var cases = doc.GetProperty("cases").EnumerateArray().ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, cases.Length, index =>
        {
            var c = cases[index];
            double drive = c.GetProperty("drive").GetDouble();
            int cap = c.GetProperty("cap").GetInt32();
            var expected = c.GetProperty("recommend").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            expected.Length.Should().Be(2 * MarginSteps.Length);
            for (int m = 0; m < MarginSteps.Length; m++)
            {
                int plain = StandardFormats.Recommend(StationVectorFile.Profile(index), MarginSteps[m], drive);
                int capped = StandardFormats.Recommend(StationVectorFile.Profile(index), MarginSteps[m], drive, cap);
                if (plain != expected[2 * m])
                {
                    failures.Add($"profile {index} ({c.GetProperty("kind").GetString()}), margin {MarginSteps[m]}, no ceiling: got {plain}, prototype {expected[2 * m]}");
                }

                if (capped != expected[(2 * m) + 1])
                {
                    failures.Add($"profile {index} ({c.GetProperty("kind").GetString()}), margin {MarginSteps[m]}, no faster than {cap}: got {capped}, prototype {expected[(2 * m) + 1]}");
                }
            }
        });

        failures.Should().BeEmpty();
        cases.Length.Should().Be(profiles);
    }

    [Fact]
    public void Margin_Matches_The_Prototype_Exactly_For_Every_Profile_And_Sampled_Format()
    {
        // The margin is a point of the bisection's lattice (-40 plus a whole number of steps of 80/512 dB), so it is exact
        // whenever the same decisions are made: a difference would be a different decision, not rounding.
        var cases = StationVectorFile.Functions.GetProperty("cases").EnumerateArray().ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, cases.Length, index =>
        {
            var c = cases[index];
            double drive = c.GetProperty("drive").GetDouble();
            var formats = c.GetProperty("formats").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var margins = c.GetProperty("margins").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            formats.Length.Should().Be(40);
            for (int i = 0; i < formats.Length; i++)
            {
                double got = StandardFormats.MarginDb(formats[i], StationVectorFile.Profile(index), drive);
                if (got != margins[i])
                {
                    failures.Add($"profile {index} ({c.GetProperty("kind").GetString()}), format {formats[i]}, heard drive {drive}: got {got}, prototype {margins[i]}");
                }
            }
        });

        failures.Should().BeEmpty();
    }

    [Fact]
    public void A_Ceiling_At_The_Winners_Own_Rate_Keeps_It_And_One_Below_Moves_Down()
    {
        foreach (var e in StationVectorFile.Functions.GetProperty("extra").EnumerateArray())
        {
            int profile = e.GetProperty("profile").GetInt32();
            double margin = e.GetProperty("margin").GetDouble();
            int winner = StandardFormats.Recommend(StationVectorFile.Profile(profile), margin);
            winner.Should().Be(e.GetProperty("winner").GetInt32(), "profile {0}", profile);
            StandardFormats.Recommend(StationVectorFile.Profile(profile), margin, 0.0, winner)
                .Should().Be(e.GetProperty("at_winner").GetInt32(), "profile {0} capped at the winner", profile);
            int stricter = StandardFormats.Recommend(StationVectorFile.Profile(profile), 6.0);
            StandardFormats.Recommend(StationVectorFile.Profile(profile), margin, 0.0, stricter)
                .Should().Be(e.GetProperty("below_winner").GetInt32(), "profile {0} capped at the stricter winner", profile);
        }
    }

    [Fact]
    public void Rates_Are_Compared_To_The_Thousandth_As_The_Prototype_Rounds_Them()
    {
        var expected = StationVectorFile.Functions.GetProperty("round3_thousandths").EnumerateArray().Select(e => e.GetInt64()).ToArray();
        expected.Length.Should().Be(256);
        for (int n = 0; n < 256; n++)
        {
            StandardFormats.RateInThousandths(n).Should().Be(expected[n], "format {0}", n);
        }
    }

    [Fact]
    public void The_Bits_A_Carrier_Table_Reads_As_The_Prototype_Reads_It()
    {
        var check = StationVectorFile.Functions.GetProperty("gmi_check");
        var snrs = check.GetProperty("snr_db").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        foreach (var size in check.GetProperty("sizes").EnumerateObject())
        {
            int bits = int.Parse(size.Name, System.Globalization.CultureInfo.InvariantCulture);
            var expected = size.Value.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            for (int i = 0; i < snrs.Length; i++)
            {
                StandardFormats.Carried(bits, snrs[i]).Should().BeApproximately(expected[i], 1e-9, "{0}-bit constellation at {1} dB", bits, snrs[i]);
            }
        }
    }

    [Fact]
    public void Nothing_Holds_On_A_Silent_Path_And_The_Most_Robust_Format_Is_Named()
    {
        var silent = new double[StationVectorFile.Carriers];
        StandardFormats.Recommend(silent, 3.0).Should().Be(StandardFormats.MostRobust);
        StandardFormats.MarginDb(StandardFormats.MostRobust, silent).Should().Be(-40.0);
    }

    [Fact]
    public void A_Carrier_With_No_Value_Fails_Every_Format_That_Uses_It()
    {
        var path = new double[StationVectorFile.Carriers];
        Array.Fill(path, 1e4);
        double whole = StandardFormats.MarginDb(0, path);
        whole.Should().BeGreaterThan(0.0);
        path[300] = double.NaN;
        StandardFormats.MarginDb(0, path).Should().Be(-40.0);
        StandardFormats.MarginDb(7 * StandardFormats.Rungs, path).Should().BeGreaterThan(0.0, "the 1 kHz band does not use carrier 300");
    }

    [Fact]
    public void Quality_Of_The_Wrong_Length_Is_Refused_And_So_Is_A_Format_Out_Of_Range()
    {
        var shortPath = new double[100];
        Action a = () => StandardFormats.MarginDb(0, shortPath);
        a.Should().Throw<ArgumentException>();
        a = () => StandardFormats.Recommend(shortPath, 3.0);
        a.Should().Throw<ArgumentException>();
        a = () => StandardFormats.Recommend(StationVectorFile.Profile(0), 3.0, 0.0, 256);
        a.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Recommend_Does_Not_Allocate()
    {
        var path = StationVectorFile.Profile(5).ToArray();
        StandardFormats.Recommend(path, 3.0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            StandardFormats.Recommend(path, 3.0, 0.0, 100);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}
