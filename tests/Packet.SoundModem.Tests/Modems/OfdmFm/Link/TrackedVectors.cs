using System.IO.Compression;
using System.Text.Json;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>One of the tracked receiver's cases: a burst through a wandering clock, and what the prototype made of it.</summary>
internal sealed class TrackedCase
{
    public string Name { get; init; } = "";

    public string Opening { get; init; } = "";

    public string Group { get; init; } = "";

    public double Ppm { get; init; }

    public double Drift { get; init; }

    public bool HasStep { get; init; }

    public int BitsPerCarrier { get; init; }

    public int Start { get; init; }

    public int Known { get; init; }

    public float[] Capture { get; init; } = [];

    public BurstHeader Header { get; init; }

    public byte[] HeaderBits { get; init; } = [];

    public bool Converged { get; init; }

    public bool Check { get; init; }

    public byte[] Payload { get; init; } = [];

    public int Passes { get; init; }

    public int SymbolPasses { get; init; }

    public bool FineRan { get; init; }

    public bool UntrackedCheck { get; init; }

    public bool UntrackedConverged { get; init; }

    public double[] Curve { get; init; } = [];

    public double[] FineKnots { get; init; } = [];

    public double[] FineDelays { get; init; } = [];

    public ResampleVector? Resample { get; init; }

    public TrackerVector? Tracker { get; init; }
}

internal sealed class ResampleVector
{
    public string Variant { get; init; } = "";

    public int At { get; init; }

    public int N { get; init; }

    public int Lead { get; init; }

    public int Head { get; init; }

    public int Tail { get; init; }

    public int Stride { get; init; }

    public double[] Knots { get; init; } = [];

    public double[] Delays { get; init; } = [];

    /// <summary>The kept outputs: indexes below Head, from N - Tail, and every Stride-th.</summary>
    public double[] Output { get; init; } = [];

    public bool Kept(int i) => i < Head || i >= N - Tail || i % Stride == 0;
}

internal sealed class TrackerVector
{
    public float[] Capture { get; init; } = [];

    public double[] Reference { get; init; } = [];

    public double[] Times { get; init; } = [];

    public double[] Delays { get; init; } = [];
}

internal static class TrackedVectors
{
    public static readonly Lazy<List<TrackedCase>> Cases = new(Load);

    private static List<TrackedCase> Load()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");
        byte[] captures = Unzip(Path.Combine(dir, "tracked_receive_captures.bin.gz"));
        byte[] values = Unzip(Path.Combine(dir, "tracked_receive_values.bin.gz"));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tracked_receive_vectors.json")));
        var list = new List<TrackedCase>();
        int index = 0;
        foreach (JsonElement e in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            int n = e.GetProperty("samples").GetInt32();
            long so = e.GetProperty("sample_offset").GetInt64();
            long vo = e.GetProperty("values_offset").GetInt64();
            var capture = new float[n];
            for (int i = 0; i < n; i++)
            {
                capture[i] = BitConverter.ToSingle(captures, (int)((so + i) * 4));
            }

            JsonElement h = e.GetProperty("header");
            int known = e.GetProperty("known").GetInt32();
            int symbols = h.GetProperty("payload_symbols").GetInt32();
            int fine = e.GetProperty("fine_knots").GetInt32();
            long at = vo;
            double[] Doubles(int c)
            {
                var d = new double[c];
                for (int i = 0; i < c; i++)
                {
                    d[i] = BitConverter.ToDouble(values, (int)(at + (i * 8L)));
                }

                at += c * 8L;
                return d;
            }

            float[] Floats(int c)
            {
                var d = new float[c];
                for (int i = 0; i < c; i++)
                {
                    d[i] = BitConverter.ToSingle(values, (int)(at + (i * 4L)));
                }

                at += c * 4L;
                return d;
            }

            double[] curve = Doubles(known + symbols);
            double[] knots = Doubles(fine);
            double[] delays = Doubles(fine);
            ResampleVector? resample = null;
            TrackerVector? tracker = null;
            JsonElement alone = e.GetProperty("standalone");
            if (alone.ValueKind == JsonValueKind.Object)
            {
                JsonElement r = alone.GetProperty("resample");
                at = vo + r.GetProperty("offset").GetInt64();
                int m = r.GetProperty("knots").GetInt32();
                double[] rk = Doubles(m);
                double[] rd = Doubles(m);
                resample = new ResampleVector
                {
                    Variant = r.GetProperty("variant").GetString()!,
                    At = r.GetProperty("at").GetInt32(),
                    N = r.GetProperty("n").GetInt32(),
                    Lead = r.GetProperty("lead").GetInt32(),
                    Head = r.GetProperty("head").GetInt32(),
                    Tail = r.GetProperty("tail").GetInt32(),
                    Stride = r.GetProperty("stride").GetInt32(),
                    Knots = rk,
                    Delays = rd,
                    Output = Doubles(r.GetProperty("count").GetInt32()),
                };
                JsonElement t = alone.GetProperty("tracker");
                at = vo + t.GetProperty("offset").GetInt64();
                int length = t.GetProperty("length").GetInt32();
                int blocks = t.GetProperty("blocks").GetInt32();
                float[] rx = Floats(length);
                float[] reference = Floats(length);
                tracker = new TrackerVector
                {
                    Capture = rx,
                    Reference = reference.Select(x => (double)x).ToArray(),
                    Times = Doubles(blocks),
                    Delays = Doubles(blocks),
                };
            }

            JsonElement step = e.GetProperty("step");
            bool check = e.GetProperty("check").GetBoolean();
            list.Add(new TrackedCase
            {
                Name = $"case {index++} ({e.GetProperty("name").GetString()})",
                Opening = e.GetProperty("opening").GetString()!,
                Group = e.GetProperty("group").GetString()!,
                Ppm = e.GetProperty("ppm").GetDouble(),
                Drift = e.GetProperty("drift_ppm_per_s").GetDouble(),
                HasStep = step.ValueKind == JsonValueKind.Object,
                BitsPerCarrier = e.GetProperty("bits_per_carrier").GetInt32(),
                Start = e.GetProperty("start").GetInt32(),
                Known = known,
                Capture = capture,
                Header = BurstHeader.Data(h.GetProperty("sender").GetInt32(), h.GetProperty("destination").GetInt32(), h.GetProperty("format").GetInt32(), symbols, h.GetProperty("burst").GetInt32()),
                HeaderBits = LinkVectorFile.Bits(e.GetProperty("header_bits").GetString()!, BurstHeaderCodec.HeaderBits),
                Converged = e.GetProperty("converged").GetBoolean(),
                Check = check,
                Payload = check ? Convert.FromHexString(e.GetProperty("payload").GetString()!) : [],
                Passes = e.GetProperty("passes").GetInt32(),
                SymbolPasses = e.GetProperty("symbol_passes").GetInt32(),
                FineRan = e.GetProperty("fine_ran").GetBoolean(),
                UntrackedCheck = e.GetProperty("untracked").GetProperty("check").GetBoolean(),
                UntrackedConverged = e.GetProperty("untracked").GetProperty("converged").GetBoolean(),
                Curve = curve,
                FineKnots = knots,
                FineDelays = delays,
                Resample = resample,
                Tracker = tracker,
            });
        }

        return list;
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
