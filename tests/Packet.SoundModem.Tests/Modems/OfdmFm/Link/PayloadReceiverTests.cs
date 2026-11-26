using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class PayloadReceiverTests
{
    private static readonly Lazy<Vectors> Loaded = new(Vectors.Load);

    [Fact]
    public void The_Vectors_Cover_Every_Opening_Constellation_Drive_And_Outcome()
    {
        Case[] cases = Loaded.Value.Cases.ToArray();
        cases.Length.Should().BeGreaterThanOrEqualTo(30);
        cases.Count(c => !c.Check).Should().BeGreaterThanOrEqualTo(6);
        cases.Select(c => c.Opening).Distinct().Count().Should().Be(3);
        cases.Select(c => c.BitsPerCarrier).Distinct().OrderBy(x => x).Should().Equal(1, 2, 4, 6, 8, 10);
        cases.Select(c => c.DriveDb).Distinct().OrderBy(x => x).Should().Equal(0.0, 3.0, 6.0);
        cases.Count(c => c.Header.Destination == 0).Should().BeGreaterThan(3);
        cases.Count(c => c.Header.Destination != 0).Should().BeGreaterThan(3);
        cases.Select(c => c.Header.PayloadSymbols).Should().Contain(52);
        cases.Min(c => c.Header.PayloadSymbols).Should().Be(4);
        cases.Count(c => c.Sudden).Should().BeGreaterThanOrEqualTo(2);
        cases.Count(c => c.Extra == "tone" && c.Guarded > 0).Should().Be(2);
        cases.Count(c => Math.Abs(c.Ppm) > 50).Should().BeGreaterThan(5);
    }

    [Fact]
    public void The_Outcome_Of_Every_Case_Matches_The_Prototype()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        var wrong = new List<string>();
        foreach (Case c in Loaded.Value.Cases)
        {
            PayloadReceiveResult r = c.Run(receiver, payload);
            if (!r.Accepted || r.AllBlocksSatisfied != c.Converged || r.CheckPassed != c.Check || r.GuardedCarriers != c.Guarded
                || receiver.GuardedCarriers != c.Guarded || receiver.Sudden != c.Sudden)
            {
                wrong.Add($"{c.Name}: accepted {r.Accepted} converged {r.AllBlocksSatisfied}/{c.Converged} check {r.CheckPassed}/{c.Check} guard {r.GuardedCarriers}/{c.Guarded} sudden {receiver.Sudden}/{c.Sudden}");
            }
            else if (c.Check && (r.PayloadBytes != c.Payload.Length || !payload.AsSpan(0, r.PayloadBytes).SequenceEqual(c.Payload)))
            {
                wrong.Add(c.Name + ": payload");
            }
            else if (!c.Check && r.PayloadBytes != 0)
            {
                wrong.Add(c.Name + ": payload bytes on a failed check");
            }
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void The_Payload_Of_A_Passing_Case_Unpacks_To_The_Datagrams_Sent()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        foreach (Case c in Loaded.Value.Cases.Where(x => x.Check))
        {
            PayloadReceiveResult r = c.Run(receiver, payload);
            PayloadCodec.TryUnpack(payload.AsSpan(0, r.PayloadBytes), c.Header.Destination == 0, out PayloadPrelude prelude, out DatagramReader reader).Should().BeTrue(c.Name);
            prelude.Wanted.Should().Be(c.PreludeWanted, c.Name);
            prelude.Recommended.Should().Be(c.PreludeRecommended, c.Name);
            prelude.Capability.Should().Be(c.PreludeCapability, c.Name);
            foreach (byte[] sent in c.Datagrams)
            {
                reader.TryNext(out ReadOnlySpan<byte> got).Should().BeTrue(c.Name);
                got.SequenceEqual(sent).Should().BeTrue(c.Name);
            }

            reader.TryNext(out _).Should().BeFalse(c.Name);
        }
    }

    [Fact]
    public void The_Delay_Of_Every_Symbol_Matches_The_Prototype_To_A_Picosecond()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        double largest = 0;
        foreach (Case c in Loaded.Value.Cases)
        {
            c.Run(receiver, payload);
            receiver.Delays.Length.Should().Be(c.Delays.Length, c.Name);
            for (int k = 0; k < c.Delays.Length; k++)
            {
                double d = Math.Abs(receiver.Delays[k] - c.Delays[k]);
                largest = Math.Max(largest, d);
                d.Should().BeLessThan(1e-12, c.Name + " symbol " + k);
            }
        }

        largest.Should().BeLessThan(1e-12);
    }

    [Fact]
    public void The_Miss_Of_Every_Symbol_Matches_The_Prototype()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        foreach (Case c in Loaded.Value.Cases)
        {
            c.Run(receiver, payload);
            receiver.KnownSymbols.Should().Be(c.Known, c.Name);
            Worst(receiver.Miss, c.Miss, 1e-12).Should().BeLessThan(1e-6, c.Name);
        }
    }

    [Fact]
    public void The_Measured_Quality_Of_Every_Passing_Case_Matches_The_Prototype()
    {
        // Relative 1e-6, with an absolute floor of 1e-9 times the case's largest value for carriers near zero.
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        foreach (Case c in Loaded.Value.Cases)
        {
            c.Run(receiver, payload);
            if (!c.Check)
            {
                receiver.Quality.Length.Should().Be(0, c.Name);
                continue;
            }

            receiver.Quality.Length.Should().Be(333, c.Name);
            Worst(receiver.Quality, c.Quality, 1e-9 * c.Quality.Max()).Should().BeLessThan(1e-6, c.Name);
        }
    }

    [Fact]
    public void The_Opening_Quality_Of_Every_Case_Matches_The_Prototype()
    {
        // Relative 1e-6 (the values are never below 1e-3, so no floor is needed); NaN where the opening has no carrier.
        var receiver = new PayloadReceiver();
        var quality = new double[333];
        foreach (Case c in Loaded.Value.Cases)
        {
            receiver.MeasureOpening(c.Capture, c.Start, c.Header, c.HeaderBits, quality).Should().BeTrue(c.Name);
            for (int i = 0; i < 333; i++)
            {
                double expected = c.OpeningQuality[i];
                if (double.IsNaN(expected))
                {
                    double.IsNaN(quality[i]).Should().BeTrue($"{c.Name} carrier {i}");
                }
                else
                {
                    Math.Abs(quality[i] - expected).Should().BeLessThanOrEqualTo(1e-6 * Math.Abs(expected), $"{c.Name} carrier {i}");
                }
            }
        }
    }

    [Fact]
    public void The_Decided_Symbols_Are_The_Decoders_Best_Guess_Mapped()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        var re = new double[333];
        var im = new double[333];
        foreach (Case c in Loaded.Value.Cases)
        {
            c.Run(receiver, payload);
            FormatSpec spec = StandardFormats.Describe(c.Header.Format);
            var loading = new byte[333];
            loading.AsSpan(0, spec.Carriers).Fill((byte)spec.ConstellationBits);
            int per = spec.Carriers * spec.ConstellationBits;
            receiver.DecidedReal.Length.Should().Be(c.Header.PayloadSymbols * 333, c.Name);
            for (int s = 0; s < c.Header.PayloadSymbols; s++)
            {
                Constellation.MapSymbol(c.DecidedBits.AsSpan(s * per, per), loading, re, im);
                receiver.DecidedReal.Slice(s * 333, 333).SequenceEqual(re).Should().BeTrue($"{c.Name} symbol {s} real");
                receiver.DecidedImaginary.Slice(s * 333, 333).SequenceEqual(im).Should().BeTrue($"{c.Name} symbol {s} imaginary");
            }
        }
    }

    [Fact]
    public void The_Sudden_Test_Is_True_Only_For_A_Collapse_Part_Way()
    {
        PayloadReceiver.IsSudden([2.0, 2.0, 1.0, 1.1, 0.9, 1.0, 1.0, 1.1, 9.0, 9.5, 8.7, 9.1], 2).Should().BeTrue();
        PayloadReceiver.IsSudden([2.0, 2.0, 8.0, 8.1, 9.0, 8.0, 7.9, 8.1, 9.0, 9.5, 8.7, 9.1], 2).Should().BeFalse();
        PayloadReceiver.IsSudden([2.0, 2.0, 1.0, 1.1, 0.9, 1.0, 20.0], 2).Should().BeFalse();    // fewer than six payload symbols
        PayloadReceiver.IsSudden([1.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0], 0).Should().BeFalse();   // a cut before the third symbol is not looked for
        PayloadReceiver.IsSudden([1.0, 1.0, 1.0, 5.0, 5.0, 5.0, 5.0, 5.0], 0).Should().BeTrue();
    }

    [Fact]
    public void A_Capture_Too_Short_Is_Refused_And_Leaves_No_Result()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        Case c = Loaded.Value.Cases.First(x => x.Check);
        int need = PayloadReceiver.SamplesNeeded(c.Header);
        need.Should().Be((c.Known + c.Header.PayloadSymbols) * 2112);
        receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload).CheckPassed.Should().BeTrue();
        receiver.HasResult.Should().BeTrue();
        PayloadReceiveResult r = receiver.Receive(c.Capture.AsSpan(0, c.Start + need - 1), c.Start, c.Header, c.HeaderBits, payload);
        r.Accepted.Should().BeFalse();
        r.CheckPassed.Should().BeFalse();
        receiver.HasResult.Should().BeFalse();
        receiver.Delays.Length.Should().Be(0);
        receiver.Quality.Length.Should().Be(0);
        receiver.DecidedReal.Length.Should().Be(0);
        receiver.Receive(c.Capture, c.Start + 100, c.Header, c.HeaderBits, payload).Accepted.Should().BeFalse();
        receiver.Receive(c.Capture, -1, c.Header, c.HeaderBits, payload).Accepted.Should().BeFalse();
        receiver.MeasureOpening(c.Capture.AsSpan(0, 100), 0, c.Header, c.HeaderBits, new double[333]).Should().BeFalse();
    }

    [Fact]
    public void A_Header_That_Is_Not_A_Data_Burst_Is_Refused()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        Case c = Loaded.Value.Cases.First();
        foreach (BurstHeader h in new[] { BurstHeader.Report(1, 2, 3, false), new BurstHeader(1, BurstKind.Data, 0, 0, 0, 0, 0), BurstHeader.Data(1, 2, c.Header.Format, 0, 0) })
        {
            PayloadReceiver.SamplesNeeded(h).Should().Be(-1);
            receiver.Receive(c.Capture, c.Start, h, c.HeaderBits, payload).Accepted.Should().BeFalse();
        }

        Action small = () => receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, new byte[1]);
        small.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Second_Receive_Of_The_Same_Shape_Allocates_Nothing()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[8000];
        foreach (string opening in new[] { "Full", "UpTo3000Hz", "UpTo1500Hz" })
        {
            foreach (bool passing in new[] { true, false })
            {
                Case c = Loaded.Value.Cases.First(x => x.Opening == opening && x.Check == passing);
                var quality = new double[333];
                for (int i = 0; i < 5; i++)
                {
                    c.Run(receiver, payload);
                    receiver.MeasureOpening(c.Capture, c.Start, c.Header, c.HeaderBits, quality);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50; i++)
                {
                    c.Run(receiver, payload);
                    receiver.MeasureOpening(c.Capture, c.Start, c.Header, c.HeaderBits, quality);
                }

                (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, c.Name);
            }
        }
    }

    [Fact]
    public void A_Rendered_Burst_Without_Noise_Is_Received_On_Every_Opening()
    {
        var renderer = new BurstRenderer(0);
        var receiver = new PayloadReceiver();
        // a full-band format, one on the 3 kHz opening and one on the 1.5 kHz opening, to one station and to everyone
        foreach ((int format, int symbols, int destination) in new[] { (3 * 32, 8, 5), (2 * 32 + 12, 12, 0), (4 * 32 + 8, 8, 9), (5 * 32 + 14, 12, 0), (6 * 32 + 10, 8, 7), (7 * 32 + 20, 8, 0) })
        {
            BurstHeader header = BurstHeader.Data(3, destination, format, symbols, 6);
            int room = PayloadCodec.Capacity(format, symbols);
            var datagram = new byte[Math.Min(room - 6, 40)];
            for (int i = 0; i < datagram.Length; i++)
            {
                datagram[i] = (byte)((i * 37) + format);
            }

            var packed = new byte[room];
            PayloadPrelude? prelude = destination == 0 ? null : new PayloadPrelude(true, 77, false, null);
            int packedLength = PayloadCodec.Pack(prelude, new ReadOnlyMemory<byte>[] { datagram }, packed);
            packedLength.Should().BeGreaterThan(0);
            var samples = new float[renderer.SamplesFor(format, symbols)];
            renderer.RenderData(header, packed.AsSpan(0, packedLength), samples);
            var bits = new byte[BurstHeaderCodec.HeaderBits];
            BurstHeaderCodec.Write(header, bits);
            PayloadReceiver.SamplesNeeded(header).Should().Be(samples.Length);
            var payload = new byte[room];
            PayloadReceiveResult r = receiver.Receive(samples, 0, header, bits, payload);
            string name = $"format {format}";
            r.CheckPassed.Should().BeTrue(name);
            r.AllBlocksSatisfied.Should().BeTrue(name);
            r.PayloadBytes.Should().Be(room, name);
            payload.AsSpan(0, packedLength).SequenceEqual(packed.AsSpan(0, packedLength)).Should().BeTrue(name);
            PayloadCodec.TryUnpack(payload, destination == 0, out _, out DatagramReader reader).Should().BeTrue(name);
            reader.TryNext(out ReadOnlySpan<byte> got).Should().BeTrue(name);
            got.SequenceEqual(datagram).Should().BeTrue(name);
            receiver.Quality.Length.Should().Be(333, name);
        }
    }

    [Fact]
    public void A_Silent_Capture_Is_Received_As_A_Failure_Without_Errors()
    {
        var receiver = new PayloadReceiver();
        foreach (int format in new[] { 20, 4 * 32 + 10, 6 * 32 + 10 })
        {
            BurstHeader header = BurstHeader.Data(3, 5, format, 8, 1);
            var bits = new byte[BurstHeaderCodec.HeaderBits];
            BurstHeaderCodec.Write(header, bits);
            var samples = new float[PayloadReceiver.SamplesNeeded(header)];
            PayloadReceiveResult r = receiver.Receive(samples, 0, header, bits, new byte[PayloadReceiver.PayloadRoom(header)]);
            r.Accepted.Should().BeTrue();
            r.CheckPassed.Should().BeFalse();
            foreach (double d in receiver.Delays)
            {
                double.IsFinite(d).Should().BeTrue();
            }
        }
    }

    // The largest |a - b| / (|b| + floor).
    private static double Worst(ReadOnlySpan<double> actual, double[] expected, double floor)
    {
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]) / (Math.Abs(expected[i]) + floor));
        }

        return worst;
    }

    private sealed class Case
    {
        public string Name { get; init; } = "";

        public string Opening { get; init; } = "";

        public string Group { get; init; } = "";

        public string Extra { get; init; } = "";

        public double Ppm { get; init; }

        public double DriveDb { get; init; }

        public int BitsPerCarrier { get; init; }

        public int Start { get; init; }

        public int Known { get; init; }

        public float[] Capture { get; init; } = [];

        public BurstHeader Header { get; init; }

        public byte[] HeaderBits { get; init; } = [];

        public bool Converged { get; init; }

        public bool Check { get; init; }

        public byte[] Payload { get; init; } = [];

        public int Guarded { get; init; }

        public bool Sudden { get; init; }

        public double[] Delays { get; init; } = [];

        public double[] Miss { get; init; } = [];

        public double[] Quality { get; init; } = [];

        public double[] OpeningQuality { get; init; } = [];

        public byte[] DecidedBits { get; init; } = [];

        public byte[][] Datagrams { get; init; } = [];

        public bool PreludeWanted { get; init; }

        public int? PreludeRecommended { get; init; }

        public int? PreludeCapability { get; init; }

        public PayloadReceiveResult Run(PayloadReceiver receiver, byte[] payload) =>
            receiver.Receive(Capture, Start, Header, HeaderBits, payload);
    }

    private sealed class Vectors
    {
        public List<Case> Cases { get; } = [];

        public static Vectors Load()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");
            byte[] captures = Unzip(Path.Combine(dir, "payload_receive_captures.bin.gz"));
            byte[] values = Unzip(Path.Combine(dir, "payload_receive_values.bin.gz"));
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "payload_receive_vectors.json")));
            var v = new Vectors();
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
                int count = known + symbols;
                int measured = e.GetProperty("measured").GetInt32();
                int coded = e.GetProperty("coded_bits").GetInt32();
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

                double[] delays = Doubles(count);
                double[] miss = Doubles(count);
                double[] quality = Doubles(measured);
                double[] opening = Doubles(333);
                byte[] decided = LinkVectorFile.Bits(Convert.ToHexString(values.AsSpan((int)at, (coded + 7) / 8)), coded);
                JsonElement pre = e.GetProperty("prelude");
                bool check = e.GetProperty("check").GetBoolean();
                v.Cases.Add(new Case
                {
                    Name = $"case {index++} ({e.GetProperty("name").GetString()})",
                    Opening = e.GetProperty("opening").GetString()!,
                    Group = e.GetProperty("group").GetString()!,
                    Extra = e.GetProperty("extra").GetString()!,
                    Ppm = e.GetProperty("ppm").GetDouble(),
                    DriveDb = e.GetProperty("drive_db").GetDouble(),
                    BitsPerCarrier = e.GetProperty("bits_per_carrier").GetInt32(),
                    Start = e.GetProperty("start").GetInt32(),
                    Known = known,
                    Capture = capture,
                    Header = BurstHeader.Data(h.GetProperty("sender").GetInt32(), h.GetProperty("destination").GetInt32(), h.GetProperty("format").GetInt32(), symbols, h.GetProperty("burst").GetInt32()),
                    HeaderBits = LinkVectorFile.Bits(e.GetProperty("header_bits").GetString()!, BurstHeaderCodec.HeaderBits),
                    Converged = e.GetProperty("converged").GetBoolean(),
                    Check = check,
                    Payload = check ? Convert.FromHexString(e.GetProperty("payload").GetString()!) : [],
                    Guarded = e.GetProperty("guarded").GetInt32(),
                    Sudden = e.GetProperty("sudden").GetBoolean(),
                    Delays = delays,
                    Miss = miss,
                    Quality = quality,
                    OpeningQuality = opening,
                    DecidedBits = decided,
                    Datagrams = e.GetProperty("datagrams").EnumerateArray().Select(d => Convert.FromHexString(d.GetString()!)).ToArray(),
                    PreludeWanted = pre.GetProperty("wanted").GetBoolean(),
                    PreludeRecommended = pre.GetProperty("recommended") is { ValueKind: JsonValueKind.Number } r ? r.GetInt32() : null,
                    PreludeCapability = pre.GetProperty("capability") is { ValueKind: JsonValueKind.Number } k ? k.GetInt32() : null,
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
