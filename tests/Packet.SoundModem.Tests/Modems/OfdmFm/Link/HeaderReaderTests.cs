using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class HeaderReaderTests
{
    private static readonly Lazy<ReadVectors> Vectors = new(ReadVectors.Load);

    [Fact]
    public void The_Vectors_Cover_Every_Opening_With_Strong_And_Failing_Cases()
    {
        foreach (string opening in new[] { "Full", "UpTo3000Hz", "UpTo1500Hz" })
        {
            ReadCase[] cases = Vectors.Value.Cases.Where(c => c.Opening == opening).ToArray();
            cases.Length.Should().BeGreaterThanOrEqualTo(30, opening);
            cases.Count(c => c.Fields is not null).Should().BeGreaterThanOrEqualTo(10, opening);
            cases.Count(c => c.Fields is null).Should().BeGreaterThanOrEqualTo(5, opening);
            cases.Select(c => c.Group).Distinct().Count().Should().Be(4, opening);
            cases.Count(c => c.Start != Vectors.Value.Margin).Should().BeGreaterThan(5, opening);
        }
    }

    [Fact]
    public void The_Outcome_Fields_And_Bits_Of_Every_Case_Match_The_Prototype()
    {
        var reader = new HeaderReader();
        var wrong = new List<string>();
        foreach (ReadCase c in Vectors.Value.Cases)
        {
            bool read = reader.TryRead(c.Samples, c.Start, c.OpeningValue, out BurstHeader header);
            reader.HasResult.Should().BeTrue(c.Name);
            reader.Converged.Should().Be(c.Converged, c.Name);
            // As in the prototype, the header's check is the gate whether or not the code converged, so the bits and
            // the outcome are compared in every case.
            if (Hex(reader.Bits) != c.Bits)
            {
                wrong.Add(c.Name + ": bits");
            }

            if (read != (c.Fields is not null))
            {
                wrong.Add(c.Name + ": outcome");
            }
            else if (read && c.Fields is { } f && (header.Version != f.Version
                || (f.Version == 0 && (header.Kind != (BurstKind)f.Kind || header.Sender != f.Sender || header.Destination != f.Destination
                    || header.Format != f.Format || header.PayloadSymbols != f.PayloadSymbols || header.BurstNumber != f.Burst))))
            {
                wrong.Add(c.Name + ": fields");
            }
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void The_Channel_And_Noise_Estimates_Match_The_Prototype()
    {
        var reader = new HeaderReader();
        foreach (ReadCase c in Vectors.Value.Cases)
        {
            reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _);
            double[] re = c.ChannelReal;
            double[] im = c.ChannelImaginary;
            reader.ChannelReal.Length.Should().Be(re.Length, c.Name);
            Worst(reader.ChannelReal, re, 1e-3).Should().BeLessThan(1e-9, c.Name + " channel real");
            Worst(reader.ChannelImaginary, im, 1e-3).Should().BeLessThan(1e-9, c.Name + " channel imaginary");
            Worst(reader.Noise, c.Noise, 0).Should().BeLessThan(1e-9, c.Name + " noise");
        }
    }

    [Fact]
    public void The_Delay_Of_Every_Header_Symbol_Matches_The_Prototype()
    {
        var reader = new HeaderReader();
        foreach (ReadCase c in Vectors.Value.Cases)
        {
            reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _);
            reader.Delays.Length.Should().Be(c.Delays.Length, c.Name);
            for (int k = 0; k < c.Delays.Length; k++)
            {
                reader.LadderChoices[k].Should().Be(c.Ladder[k], c.Name);
                Math.Abs(reader.Delays[k] - c.Delays[k]).Should().BeLessThan(1e-12, c.Name);
            }
        }
    }

    [Fact]
    public void The_Soft_Values_Match_The_Prototype()
    {
        var reader = new HeaderReader();
        foreach (ReadCase c in Vectors.Value.Cases)
        {
            reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _);
            ReadOnlySpan<float> soft = reader.SoftValues;
            soft.Length.Should().Be(c.Soft.Length, c.Name);
            double scale = 0;
            foreach (float v in c.Soft)
            {
                scale = Math.Max(scale, Math.Abs(v));
            }

            double worst = 0;
            for (int i = 0; i < soft.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(soft[i] - (double)c.Soft[i]) / (Math.Abs(c.Soft[i]) + (1e-9 * scale)));
            }

            worst.Should().BeLessThan(1e-6, c.Name);
        }
    }

    [Fact]
    public void A_Capture_Too_Short_For_The_Opening_Is_Refused()
    {
        var reader = new HeaderReader();
        foreach (FormatOpening opening in Enum.GetValues<FormatOpening>())
        {
            int need = HeaderReader.OpeningSamples(opening);
            var capture = new float[need + 10];
            reader.TryRead(capture.AsSpan(0, need - 1), 0, opening, out BurstHeader header).Should().BeFalse();
            reader.HasResult.Should().BeFalse();
            reader.Bits.Length.Should().Be(0);
            header.Should().Be(default(BurstHeader));
            reader.TryRead(capture, 11, opening, out _).Should().BeFalse();
            reader.HasResult.Should().BeFalse();
            reader.TryRead(capture, -1, opening, out _).Should().BeFalse();
            reader.HasResult.Should().BeFalse();

            // exactly enough samples are enough to compute, whether or not a header comes out of silence
            reader.TryRead(capture, 10, opening, out _).Should().BeFalse();
            reader.HasResult.Should().BeTrue();
        }
    }

    [Fact]
    public void A_Result_Survives_Until_The_Next_Call_And_Is_Cleared_By_A_Refused_One()
    {
        var reader = new HeaderReader();
        ReadCase c = Vectors.Value.Cases.First(x => x.Fields is not null && x.Opening == "UpTo3000Hz");
        reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _).Should().BeTrue();
        reader.Bits.Length.Should().Be(BurstHeaderCodec.HeaderBits);
        reader.CarriersReal.Length.Should().Be(5 * BurstRenderer.Carriers);
        reader.Delays.Length.Should().Be(2);
        reader.TryRead(c.Samples.AsSpan(0, 100), 0, c.OpeningValue, out _).Should().BeFalse();
        reader.HasResult.Should().BeFalse();
        reader.Delays.Length.Should().Be(0);
        reader.Noise.Length.Should().Be(0);
    }

    [Fact]
    public void The_Opening_Sizes_Are_Those_Of_The_Renderer()
    {
        HeaderReader.OpeningSamples(FormatOpening.Full).Should().Be(2 * 2112);
        HeaderReader.OpeningSamples(FormatOpening.UpTo3000Hz).Should().Be(5 * 2112);
        HeaderReader.OpeningSamples(FormatOpening.UpTo1500Hz).Should().Be(9 * 2112);
        HeaderReader.OpeningCarriers(FormatOpening.Full).Should().Be(333);
        HeaderReader.OpeningCarriers(FormatOpening.UpTo3000Hz).Should().Be(120);
        HeaderReader.OpeningCarriers(FormatOpening.UpTo1500Hz).Should().Be(56);
        var renderer = new BurstRenderer(0);
        foreach (FormatOpening opening in Enum.GetValues<FormatOpening>())
        {
            renderer.ReportSamples(opening).Should().Be(HeaderReader.OpeningSamples(opening));
        }
    }

    [Fact]
    public void A_Rendered_Report_Is_Read_Back_On_Every_Opening()
    {
        var renderer = new BurstRenderer(0);
        var reader = new HeaderReader();
        var samples = new float[20_000];
        foreach (FormatOpening opening in Enum.GetValues<FormatOpening>())
        {
            BurstHeader sent = BurstHeader.Report(9, 33, 77, failed: true);
            int n = renderer.RenderReport(sent, opening, 3.0, samples);
            n.Should().Be(HeaderReader.OpeningSamples(opening));
            reader.TryRead(samples.AsSpan(0, n), 0, opening, out BurstHeader got).Should().BeTrue(opening.ToString());
            got.Should().Be(sent);
            reader.Converged.Should().BeTrue();
        }
    }

    [Fact]
    public void A_Second_Read_Allocates_Nothing()
    {
        var reader = new HeaderReader();
        foreach (string opening in new[] { "Full", "UpTo3000Hz", "UpTo1500Hz" })
        {
            ReadCase c = Vectors.Value.Cases.First(x => x.Opening == opening && x.Fields is not null);
            for (int i = 0; i < 5; i++)
            {
                reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                reader.TryRead(c.Samples, c.Start, c.OpeningValue, out _);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, opening);
        }
    }

    [Fact]
    public void The_Smoother_Reproduces_A_Quadratic_Exactly_Edges_Included()
    {
        var smoother = new QuadraticSmoother(40, 5);
        var re = new double[40];
        var im = new double[40];
        for (int i = 0; i < 40; i++)
        {
            re[i] = 3 - (2 * i) + (0.5 * i * i);
            im[i] = -1 + (0.25 * i * i);
        }

        var outRe = new double[40];
        var outIm = new double[40];
        smoother.Smooth(re, im, outRe, outIm);
        for (int i = 0; i < 40; i++)
        {
            outRe[i].Should().BeApproximately(re[i], 1e-9);
            outIm[i].Should().BeApproximately(im[i], 1e-9);
        }
    }

    [Fact]
    public void The_Smoother_Edge_Is_The_Fit_Of_The_Short_Window_Evaluated_At_The_Carrier()
    {
        // At carrier 0 the window is carriers 0 to 5. A quadratic through six points, evaluated at the first, is not the
        // points' mean: this is the least-squares value (checked here by the normal equations, solved independently).
        var smoother = new QuadraticSmoother(30, 5);
        double[] y = [1, 4, 2, 8, 5, 7, 3, 9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 1, 2, 3, 4];
        var outRe = new double[30];
        smoother.Smooth(y, new double[30], outRe, new double[30]);
        double expected = FitAtZero(y, 0, 6);
        outRe[0].Should().BeApproximately(expected, 1e-12);
        outRe[0].Should().NotBeApproximately(y.Take(6).Average(), 0.1);
        outRe[29].Should().BeApproximately(FitAtEnd(y, 24, 30), 1e-12);
    }

    [Fact]
    public void The_Running_Median_Cuts_The_Window_Short_At_The_Ends_And_Averages_An_Even_Count()
    {
        var median = new RunningMedian(2);
        double[] x = [5, 1, 4, 2, 9, 3];
        var y = new double[6];
        median.Apply(x, y);
        y[0].Should().Be(4);                 // 5, 1, 4
        y[1].Should().Be(3);                 // 5, 1, 4, 2 -> 1 2 4 5 -> (2 + 4) / 2
        y[2].Should().Be(4);                 // 5, 1, 4, 2, 9
        y[3].Should().Be(3);                 // 1, 4, 2, 9, 3
        y[4].Should().Be(3.5);               // 4, 2, 9, 3 -> 2 3 4 9
        y[5].Should().Be(3);                 // 2, 9, 3
    }

    [Fact]
    public void The_Transform_Puts_A_Tone_On_Its_Bin_Of_Each_Symbol()
    {
        var transform = new OpeningTransform(3);
        var capture = new float[(3 * BurstRenderer.SymbolSamples) + 5];
        for (int i = 0; i < capture.Length; i++)
        {
            capture[i] = (float)Math.Cos(2 * Math.PI * 20 * (i - 5 - BurstRenderer.CyclicPrefix) / BurstRenderer.FftSize);
        }

        transform.Transform(capture, 5, 2);
        transform.Symbols.Should().Be(2);
        transform.Real.Length.Should().Be(2 * BurstRenderer.Carriers);
        for (int k = 0; k < 2; k++)
        {
            for (int c = 0; c < BurstRenderer.Carriers; c++)
            {
                double mag = Math.Sqrt(Math.Pow(transform.Real[(k * BurstRenderer.Carriers) + c], 2) + Math.Pow(transform.Imaginary[(k * BurstRenderer.Carriers) + c], 2));
                if (c == 20 - BurstRenderer.FirstBin)
                {
                    mag.Should().BeApproximately(BurstRenderer.FftSize / 2.0, 1e-3);
                }
                else
                {
                    mag.Should().BeLessThan(1e-3);
                }
            }
        }
    }

    private static double FitAtZero(double[] y, int lo, int hi)
    {
        // The window's carriers are 0 to hi-lo-1 and the fit is evaluated at 0 (the first carrier).
        return Fit(y, lo, hi, 0);
    }

    private static double FitAtEnd(double[] y, int lo, int hi) => Fit(y, lo, hi, hi - 1 - lo);

    // Least-squares quadratic through y[lo..hi) at positions 0.., evaluated at position at, by the normal equations of the
    // monomials solved with Cramer's rule.
    private static double Fit(double[] y, int lo, int hi, double at)
    {
        var s = new double[5];
        var t = new double[3];
        for (int j = lo; j < hi; j++)
        {
            double x = j - lo;
            for (int p = 0; p < 5; p++)
            {
                s[p] += Math.Pow(x, p);
            }

            for (int p = 0; p < 3; p++)
            {
                t[p] += Math.Pow(x, p) * y[j];
            }
        }

        double[,] m = { { s[0], s[1], s[2] }, { s[1], s[2], s[3] }, { s[2], s[3], s[4] } };
        double det = Det(m);
        var coef = new double[3];
        for (int col = 0; col < 3; col++)
        {
            double[,] mc = (double[,])m.Clone();
            for (int r = 0; r < 3; r++)
            {
                mc[r, col] = t[r];
            }

            coef[col] = Det(mc) / det;
        }

        return coef[0] + (coef[1] * at) + (coef[2] * at * at);
    }

    private static double Det(double[,] m) =>
        (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
        - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
        + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

    // The largest |a - b| / (|b| + floor), the floor being a fraction of the largest |b|.
    private static double Worst(ReadOnlySpan<double> actual, double[] expected, double floorFraction)
    {
        double max = 0;
        foreach (double v in expected)
        {
            max = Math.Max(max, Math.Abs(v));
        }

        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]) / (Math.Abs(expected[i]) + (floorFraction * max)));
        }

        return worst;
    }

    private static string Hex(ReadOnlySpan<byte> bits) => LinkVectorFile.Hex(bits);

    private sealed class ReadCase
    {
        public string Name { get; init; } = "";

        public string Opening { get; init; } = "";

        public FormatOpening OpeningValue { get; init; }

        public string Group { get; init; } = "";

        public int Start { get; init; }

        public float[] Samples { get; init; } = [];

        public bool Converged { get; init; }

        public string Bits { get; init; } = "";

        public CaseFields? Fields { get; init; }

        public double[] Delays { get; init; } = [];

        public int[] Ladder { get; init; } = [];

        public double[] ChannelReal { get; init; } = [];

        public double[] ChannelImaginary { get; init; } = [];

        public double[] Noise { get; init; } = [];

        public float[] Soft { get; init; } = [];
    }

    private sealed record CaseFields(int Version, int Kind, int Sender, int Destination, int Format, int PayloadSymbols, int Burst);

    private sealed class ReadVectors
    {
        public int Margin { get; private set; }

        public List<ReadCase> Cases { get; } = [];

        public static ReadVectors Load()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");
            byte[] samples = Unzip(Path.Combine(dir, "header_read_samples.bin.gz"));
            byte[] values = Unzip(Path.Combine(dir, "header_read_values.bin.gz"));
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "header_read_vectors.json")));
            var v = new ReadVectors { Margin = doc.RootElement.GetProperty("margin").GetInt32() };
            int index = 0;
            foreach (JsonElement e in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                string opening = e.GetProperty("opening").GetString()!;
                int n = e.GetProperty("samples").GetInt32();
                int nb = e.GetProperty("carriers").GetInt32();
                long so = e.GetProperty("sample_offset").GetInt64();
                long vo = e.GetProperty("values_offset").GetInt64();
                var capture = new float[n];
                for (int i = 0; i < n; i++)
                {
                    capture[i] = BitConverter.ToSingle(samples, (int)((so + i) * 4));
                }

                double[] Doubles(int first, int count)
                {
                    var d = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        d[i] = BitConverter.ToDouble(values, (int)(vo + ((first + i) * 8L)));
                    }

                    return d;
                }

                int softCount = e.GetProperty("soft").GetInt32();
                var soft = new float[softCount];
                for (int i = 0; i < softCount; i++)
                {
                    soft[i] = BitConverter.ToSingle(values, (int)(vo + (3L * nb * 8) + (i * 4L)));
                }

                CaseFields? fields = null;
                if (e.GetProperty("fields") is { ValueKind: JsonValueKind.Object } f)
                {
                    bool known = f.GetProperty("version").GetInt32() == 0;
                    fields = new CaseFields(
                        f.GetProperty("version").GetInt32(),
                        known ? f.GetProperty("kind").GetInt32() : 0,
                        known ? f.GetProperty("sender").GetInt32() : 0,
                        known ? f.GetProperty("destination").GetInt32() : 0,
                        known ? f.GetProperty("format").GetInt32() : 0,
                        known ? f.GetProperty("payloadSymbols").GetInt32() : 0,
                        known ? f.GetProperty("burst").GetInt32() : 0);
                }

                v.Cases.Add(new ReadCase
                {
                    Name = $"case {index++} ({opening}, {e.GetProperty("group").GetString()}, {e.GetProperty("level_db").GetDouble()} dB)",
                    Opening = opening,
                    OpeningValue = Enum.Parse<FormatOpening>(opening),
                    Group = e.GetProperty("group").GetString()!,
                    Start = e.GetProperty("start").GetInt32(),
                    Samples = capture,
                    Converged = e.GetProperty("converged").GetBoolean(),
                    Bits = e.GetProperty("bits").GetString()!,
                    Fields = fields,
                    Delays = e.GetProperty("delays").EnumerateArray().Select(d => double.Parse(d.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                    Ladder = e.GetProperty("ladder").EnumerateArray().Select(d => d.GetInt32()).ToArray(),
                    ChannelReal = Doubles(0, nb),
                    ChannelImaginary = Doubles(nb, nb),
                    Noise = Doubles(2 * nb, nb),
                    Soft = soft,
                });
            }

            return v;
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
