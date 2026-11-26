using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class BurstRendererTests
{
    private static readonly Lazy<RenderVectors> Vectors = new(RenderVectors.Load);

    // Whether a case is a short one (every sample and the whole grid stored) or a long one (sums and probes).
    private static IEnumerable<RenderCase> Short => Vectors.Value.Cases.Where(c => !c.IsLong);

    private static IEnumerable<RenderCase> Long => Vectors.Value.Cases.Where(c => c.IsLong);

    [Fact]
    public void The_Grid_Of_Every_Short_Case_Matches_The_Prototype_Exactly()
    {
        // The same operations in the same order (training and header points, the constellation map, the level gain), so
        // the doubles are expected to be identical, not merely close.
        var r = new BurstRenderer(Vectors.Value.Lead);
        float[] buffer = new float[600_000];
        foreach (RenderCase c in Short)
        {
            c.Render(r, buffer);
            r.GridSymbols.Should().Be(c.GridSymbols, c.Name);
            ReadOnlySpan<double> re = r.GridReal;
            ReadOnlySpan<double> im = r.GridImaginary;
            double[] expected = Vectors.Value.Grid(c);
            int mismatches = 0;
            for (int i = 0; i < re.Length; i++)
            {
                if (re[i] != expected[2 * i] || im[i] != expected[(2 * i) + 1])
                {
                    mismatches++;
                }
            }

            mismatches.Should().Be(0, c.Name);
        }
    }

    [Fact]
    public void The_Samples_Of_Every_Short_Case_Match_The_Prototype()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        float[] buffer = new float[600_000];
        foreach (RenderCase c in Short)
        {
            int n = c.Render(r, buffer);
            n.Should().Be(c.Samples, c.Name);
            float[] expected = Vectors.Value.Samples(c);
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                worst = Math.Max(worst, Math.Abs(buffer[i] - (double)expected[i]));
            }

            worst.Should().BeLessThan(2e-6, c.Name);
        }
    }

    [Fact]
    public void The_Lead_In_Is_Silence_And_Anything_Past_The_Burst_Is_Left_Alone()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        RenderCase c = Short.First();
        float[] buffer = new float[c.Samples + 10];
        Array.Fill(buffer, 7f);
        int n = c.Render(r, buffer);
        buffer.AsSpan(0, Vectors.Value.Lead).ToArray().Should().OnlyContain(x => x == 0f);
        buffer.AsSpan(n).ToArray().Should().OnlyContain(x => x == 7f);
    }

    [Fact]
    public void The_Limited_Count_Of_Every_Short_Case_Matches_The_Prototype()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        float[] buffer = new float[600_000];
        foreach (RenderCase c in Short)
        {
            c.Render(r, buffer);
            r.LimitedSamples.Should().Be(c.Limited, c.Name);
        }
    }

    [Fact]
    public void The_Long_Cases_Match_The_Prototype_In_Count_Sum_Sum_Of_Squares_And_Probes()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        float[] buffer = new float[600_000];
        foreach (RenderCase c in Long)
        {
            int n = c.Render(r, buffer);
            n.Should().Be(c.Samples, c.Name);
            r.LimitedSamples.Should().Be(c.Limited, c.Name);
            double sum = 0;
            double squares = 0;
            for (int i = 0; i < n; i++)
            {
                sum += buffer[i];
                squares += (double)buffer[i] * buffer[i];
            }

            // Each sample is within 2e-6 of the prototype's, but the sums are of single-precision values on both sides.
            Math.Abs(sum - c.Sum).Should().BeLessThan(1e-3, c.Name);
            Math.Abs(squares - c.SumSquares).Should().BeLessThan(1e-3, c.Name);
            for (int i = 0; i < c.ProbePositions.Length; i++)
            {
                Math.Abs(buffer[c.ProbePositions[i]] - c.ProbeValues[i]).Should().BeLessThan(2e-6, $"{c.Name} probe {i}");
            }
        }
    }

    [Fact]
    public void Sample_Counts_Follow_SamplesFor_And_ReportSamples()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        foreach (RenderCase c in Vectors.Value.Cases)
        {
            int expected = c.IsReport ? r.ReportSamples((FormatOpening)c.Opening) : r.SamplesFor(c.Format, c.Symbols);
            expected.Should().Be(c.Samples, c.Name);
        }
    }

    [Fact]
    public void A_Second_Render_Of_The_Same_Shape_Allocates_Nothing()
    {
        var r = new BurstRenderer();
        float[] buffer = new float[600_000];
        foreach (RenderCase c in new[] { Short.First(), Short.First(x => x.Opening == 2), Short.First(x => x.IsReport) })
        {
            c.Render(r, buffer);
            long before = GC.GetAllocatedBytesForCurrentThread();
            c.Render(r, buffer);
            GC.GetAllocatedBytesForCurrentThread().Should().Be(before, c.Name);
        }
    }

    [Fact]
    public void A_Buffer_That_Is_Too_Small_Is_Refused()
    {
        var r = new BurstRenderer(Vectors.Value.Lead);
        foreach (RenderCase c in Short.Take(4).Concat(Short.Where(x => x.IsReport).Take(2)))
        {
            float[] small = new float[c.Samples - 1];
            Action act = () => c.Render(r, small);
            act.Should().Throw<ArgumentException>(c.Name);
        }
    }

    [Fact]
    public void A_Payload_That_Does_Not_Fit_Is_Refused()
    {
        var r = new BurstRenderer();
        int room = PayloadCodec.Capacity(0, 4);
        Action act = () => r.RenderData(BurstHeader.Data(1, 0, 0, 4, 0), new byte[room + 1], new float[r.SamplesFor(0, 4)]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_Wrong_Kind_Of_Header_Is_Refused()
    {
        var r = new BurstRenderer();
        float[] buffer = new float[100_000];
        Action data = () => r.RenderData(BurstHeader.Report(1, 2, 0, false), default, buffer);
        data.Should().Throw<ArgumentException>();
        Action report = () => r.RenderReport(BurstHeader.Data(1, 2, 0, 4, 0), FormatOpening.Full, 0, buffer);
        report.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Different_Lead_In_Shifts_The_Burst_Without_Changing_It()
    {
        RenderCase c = Short.First();
        var plain = new BurstRenderer(Vectors.Value.Lead);
        var shorter = new BurstRenderer(100);
        float[] a = new float[c.Samples];
        float[] b = new float[c.Samples];
        c.Render(plain, a);
        int n = c.Render(shorter, b);
        n.Should().Be(c.Samples - Vectors.Value.Lead + 100);
        b.AsSpan(100, n - 100).ToArray().Should().Equal(a.AsSpan(Vectors.Value.Lead).ToArray());
    }

    [Fact]
    public void The_Longest_Burst_Fits_The_Buffers_Made_For_It()
    {
        var r = new BurstRenderer();
        int most = 0;
        for (int n = 0; n < StandardFormats.Count; n++)
        {
            most = Math.Max(most, r.SamplesFor(n, StandardFormats.MaxSymbols));
        }

        float[] buffer = new float[most];
        r.RenderData(BurstHeader.Data(1, 0, StandardFormats.MostRobust, StandardFormats.MaxSymbols, 0), default, buffer).Should().Be(most);
        r.LimitedSamples.Should().BeGreaterThanOrEqualTo(0);
    }

    private sealed class RenderCase
    {
        public required string Name { get; init; }

        public required bool IsReport { get; init; }

        public required int Format { get; init; }

        public required int Opening { get; init; }

        public required int Sender { get; init; }

        public required int Destination { get; init; }

        public required int Burst { get; init; }

        public required int Symbols { get; init; }

        public required byte[] Payload { get; init; }

        public required double DriveDb { get; init; }

        public required int Samples { get; init; }

        public required int Limited { get; init; }

        public required int GridSymbols { get; init; }

        public required long SampleOffset { get; init; }

        public required long GridOffset { get; init; }

        public required bool IsLong { get; init; }

        public double Sum { get; init; }

        public double SumSquares { get; init; }

        public int[] ProbePositions { get; init; } = [];

        public double[] ProbeValues { get; init; } = [];

        public int Render(BurstRenderer r, Span<float> samples) => IsReport
            ? r.RenderReport(BurstHeader.Report(Sender, Destination, Format, (Burst & 8) != 0), (FormatOpening)Opening, DriveDb, samples)
            : r.RenderData(BurstHeader.Data(Sender, Destination, Format, Symbols, Burst), Payload, samples);
    }

    private sealed class RenderVectors
    {
        private byte[] _samples = [];
        private byte[] _grids = [];

        public int Lead { get; private set; }

        public List<RenderCase> Cases { get; } = [];

        public static RenderVectors Load()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");
            var v = new RenderVectors
            {
                _samples = Unzip(Path.Combine(dir, "render_samples.bin.gz")),
                _grids = Unzip(Path.Combine(dir, "render_grids.bin.gz")),
            };
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "render_vectors.json")));
            v.Lead = doc.RootElement.GetProperty("lead").GetInt32();
            int index = 0;
            foreach (JsonElement e in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                bool isLong = e.TryGetProperty("sum", out _);
                bool report = e.GetProperty("kind").GetString() == "report";
                v.Cases.Add(new RenderCase
                {
                    Name = $"case {index++} ({e.GetProperty("kind").GetString()}, format {e.GetProperty("format").GetInt32()}, {e.GetProperty("symbols").GetInt32()} symbols)",
                    IsReport = report,
                    Format = e.GetProperty("format").GetInt32(),
                    Opening = e.GetProperty("opening").GetInt32(),
                    Sender = e.GetProperty("sender").GetInt32(),
                    Destination = e.GetProperty("destination").GetInt32(),
                    Burst = e.GetProperty("burst").GetInt32(),
                    Symbols = e.GetProperty("symbols").GetInt32(),
                    Payload = Convert.FromHexString(e.GetProperty("payload").GetString()!),
                    DriveDb = e.GetProperty("drive_db").GetDouble(),
                    Samples = e.GetProperty("samples").GetInt32(),
                    Limited = e.GetProperty("limited").GetInt32(),
                    GridSymbols = e.GetProperty("grid_symbols").GetInt32(),
                    IsLong = isLong,
                    SampleOffset = isLong ? 0 : e.GetProperty("sample_offset").GetInt64(),
                    GridOffset = isLong ? 0 : e.GetProperty("grid_offset").GetInt64(),
                    Sum = isLong ? double.Parse(e.GetProperty("sum").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0,
                    SumSquares = isLong ? double.Parse(e.GetProperty("sum_squares").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0,
                    ProbePositions = isLong ? e.GetProperty("probe_positions").EnumerateArray().Select(x => x.GetInt32()).ToArray() : [],
                    ProbeValues = isLong ? e.GetProperty("probe_values").EnumerateArray().Select(x => double.Parse(x.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : [],
                });
            }

            return v;
        }

        public float[] Samples(RenderCase c)
        {
            var x = new float[c.Samples];
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = BinaryPrimitives.ReadSingleLittleEndian(_samples.AsSpan((int)((c.SampleOffset + i) * 4), 4));
            }

            return x;
        }

        public double[] Grid(RenderCase c)
        {
            var x = new double[c.GridSymbols * BurstRenderer.Carriers * 2];
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = BinaryPrimitives.ReadDoubleLittleEndian(_grids.AsSpan((int)((c.GridOffset + i) * 8), 8));
            }

            return x;
        }

        private static byte[] Unzip(string path)
        {
            using var file = File.OpenRead(path);
            using var z = new GZipStream(file, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
