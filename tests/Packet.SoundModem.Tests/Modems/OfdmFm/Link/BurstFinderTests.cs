using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class BurstFinderTests
{
    private static readonly Lazy<(JsonElement Root, float[] Samples)> Vectors = new(Load);

    private static (JsonElement, float[]) Load()
    {
        JsonElement root = LinkVectorFile.Load("find_vectors.json");
        string path = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors", "find_captures.bin.gz");
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return (root, samples);
    }

    [Fact]
    public void Every_Burst_Index_And_Peak_Matches_The_Prototype_For_Every_Capture_And_Opening()
    {
        (JsonElement root, float[] all) = Vectors.Value;
        int searches = 0;
        int found = 0;
        double worstPeak = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
            foreach (JsonElement r in c.GetProperty("results").EnumerateArray())
            {
                var opening = (FormatOpening)r.GetProperty("opening").GetInt32();
                r.GetProperty("symbols").GetInt32().Should().Be(BurstFinder.TrainingSymbolCount(opening));
                List<BurstCandidate> got = BurstFinder.Find(capture, opening, out double threshold);
                var expected = new List<(int Index, double Peak)>();
                foreach (JsonElement p in r.GetProperty("found").EnumerateArray())
                {
                    expected.Add((p[0].GetInt32(), p[1].GetDouble()));
                }

                string where = $"{name} {opening} (threshold {threshold:F4})";
                got.Select(g => g.Index).Should().Equal(expected.Select(e => e.Index), where);
                for (int i = 0; i < got.Count; i++)
                {
                    double d = Math.Abs(got[i].Peak - expected[i].Peak);
                    worstPeak = Math.Max(worstPeak, d);
                    d.Should().BeLessThan(1e-6, $"{where} peak {i}");
                }

                searches++;
                found += got.Count;
            }
        }

        searches.Should().Be(root.GetProperty("cases").GetArrayLength() * 3);
        found.Should().BeGreaterThan(20);
        TestContext.Current.SendDiagnosticMessage($"{searches} searches, {found} bursts, largest peak difference {worstPeak:E3}");
    }

    [Fact]
    public void The_Bursts_Put_In_Each_Capture_Are_Found_At_Their_First_Training_Sample_By_Their_Own_Opening()
    {
        // Not every burst is found (the near-threshold captures are there to lose some), but where one is, it is at the sample it was put.
        (JsonElement root, float[] all) = Vectors.Value;
        int hits = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
            foreach (JsonElement b in c.GetProperty("bursts").EnumerateArray())
            {
                if (c.GetProperty("name").GetString() == "two_close_bursts")
                {
                    continue;
                }

                int at = b.GetProperty("first_training_sample").GetInt32();
                List<BurstCandidate> got = BurstFinder.Find(capture, (FormatOpening)b.GetProperty("opening").GetInt32());
                if (got.Any(g => Math.Abs(g.Index - at) <= 2))
                {
                    hits++;
                }
            }
        }

        hits.Should().BeGreaterThan(15);
    }

    [Fact]
    public void A_Capture_Of_Noise_Alone_Finds_Nothing()
    {
        (JsonElement root, float[] all) = Vectors.Value;
        JsonElement c = root.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("name").GetString() == "noise_only");
        float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
        foreach (FormatOpening o in Enum.GetValues<FormatOpening>())
        {
            BurstFinder.Find(capture, o).Should().BeEmpty();
        }
    }

    [Fact]
    public void A_Capture_Shorter_Than_The_Reference_Finds_Nothing()
    {
        BurstFinder.Find(new float[100], FormatOpening.UpTo1500Hz).Should().BeEmpty();
    }

    [Fact]
    public void A_Rendered_Report_Is_Found_Where_It_Starts()
    {
        var renderer = new BurstRenderer();
        float[] burst = new float[renderer.ReportSamples(FormatOpening.UpTo3000Hz)];
        renderer.RenderReport(BurstHeader.Report(5, 6, 100, false), FormatOpening.UpTo3000Hz, 0.0, burst);
        var capture = new float[40_000];
        var rng = new Random(840);
        for (int i = 0; i < capture.Length; i++)
        {
            capture[i] = (float)((rng.NextDouble() - 0.5) * 0.02);
        }

        for (int i = 0; i < burst.Length; i++)
        {
            capture[7000 + i] += burst[i];
        }

        List<BurstCandidate> got = BurstFinder.Find(capture, FormatOpening.UpTo3000Hz);
        BurstCandidate best = got.MaxBy(g => g.Peak);
        best.Index.Should().Be(7000 + renderer.LeadSamples);
        best.Peak.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void A_Given_Threshold_Replaces_The_Median_Rule()
    {
        (JsonElement root, float[] all) = Vectors.Value;
        JsonElement c = root.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("name").GetString() == "strong_mixed");
        float[] capture = all.AsSpan(c.GetProperty("offset").GetInt32(), c.GetProperty("samples").GetInt32()).ToArray();
        List<BurstCandidate> normal = BurstFinder.Find(capture, FormatOpening.Full, out double used);
        used.Should().BeGreaterThanOrEqualTo(BurstFinder.DefaultLeast);
        List<BurstCandidate> strict = BurstFinder.Find(capture, FormatOpening.Full, out double given, fixedThreshold: 0.5);
        given.Should().Be(0.5);
        strict.Should().NotBeEmpty();
        strict.Count.Should().BeLessThan(normal.Count);
        strict.Should().OnlyContain(s => s.Peak >= 0.5);
    }

    [Fact]
    public void Openings_Search_With_One_Three_And_Six_Training_Symbols()
    {
        BurstFinder.TrainingSymbolCount(FormatOpening.Full).Should().Be(1);
        BurstFinder.TrainingSymbolCount(FormatOpening.UpTo3000Hz).Should().Be(3);
        BurstFinder.TrainingSymbolCount(FormatOpening.UpTo1500Hz).Should().Be(6);
        BurstFinder.ReferenceLength(FormatOpening.UpTo1500Hz).Should().Be(6 * 2112);
    }

    [Fact]
    public void Peaks_Are_Strict_Local_Maxima_With_Flat_Tops_Counted_Once_At_Their_Middle()
    {
        double[] x = [0, 1, 0, 2, 2, 0, 3, 3, 3, 0, 4, 4, 5, 0, 1, 1, 1];
        // 1 at index 1; the flat top at 3-4 (middle 3); the flat top at 6-8 (middle 7); 10-11 is lower than 12 (a step up, not a peak);
        // 12; the run at the end reaches the last sample and is not a peak.
        BurstFinder.Peaks(x, 0.5, 1).Should().Equal(1, 3, 7, 12);
        BurstFinder.Peaks([1.0, 2.0], 0.0, 1).Should().BeEmpty();
        BurstFinder.Peaks([2.0, 1.0, 2.0], 0.0, 1).Should().BeEmpty();
    }

    [Fact]
    public void Peaks_Below_The_Height_Are_Dropped_And_A_Peak_Exactly_At_It_Stays()
    {
        double[] x = [0, 1, 0, 2, 0, 3, 0];
        BurstFinder.Peaks(x, 2.0, 1).Should().Equal(3, 5);
    }

    [Fact]
    public void The_Distance_Rule_Keeps_The_Highest_And_Removes_Smaller_Peaks_Closer_Than_It()
    {
        // Peaks at 1 (height 3), 3 (5), 5 (4), 9 (2); a distance of 3 keeps 3 (removing 1 and 5, both 2 away), then 9 (6 away).
        double[] x = [0, 3, 0, 5, 0, 4, 0, 0, 0, 2, 0];
        BurstFinder.Peaks(x, 0.0, 3).Should().Equal(3, 9);
        // At a distance of exactly 2 neighbours two apart are kept (closer than 2 is removed, two apart is not).
        BurstFinder.Peaks(x, 0.0, 2).Should().Equal(1, 3, 5, 9);
        // The highest wins even when it was found after a lower one has already removed nothing: chain 1 (4), 3 (5), 5 (4.5).
        BurstFinder.Peaks([0, 4, 0, 5, 0, 4.5, 0], 0.0, 3).Should().Equal(3);
    }
}
