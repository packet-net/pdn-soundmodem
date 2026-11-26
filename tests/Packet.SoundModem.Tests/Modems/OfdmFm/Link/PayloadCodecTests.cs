using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class PayloadCodecTests
{
    private static readonly System.Text.Json.JsonElement Vectors = LinkVectorFile.Load("payloads.json");

    private static ReadOnlyMemory<byte>[] Grams(System.Text.Json.JsonElement a) =>
        a.EnumerateArray().Select(x => (ReadOnlyMemory<byte>)Convert.FromHexString(x.GetString()!)).ToArray();

    private static int? OptInt(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : e.GetInt32();

    [Fact]
    public void Every_Prototype_Payload_Packs_To_The_Same_Bytes_And_Bits()
    {
        var cases = Vectors.GetProperty("cases");
        cases.GetArrayLength().Should().BeGreaterThanOrEqualTo(60);
        foreach (var c in cases.EnumerateArray())
        {
            bool everyone = c.GetProperty("everyone").GetBoolean();
            PayloadPrelude? pre = everyone ? null : new PayloadPrelude(c.GetProperty("wanted").GetBoolean(), OptInt(c.GetProperty("recommended")), c.GetProperty("failed").GetBoolean(), OptInt(c.GetProperty("capability")));
            var buffer = new byte[c.GetProperty("room").GetInt32()];
            int n = PayloadCodec.Pack(pre, Grams(c.GetProperty("datagrams")), buffer);
            Convert.ToHexString(buffer.AsSpan(0, n)).ToLowerInvariant().Should().Be(c.GetProperty("packed").GetString());

            int info = c.GetProperty("info_bit_count").GetInt32();
            int fmt = c.GetProperty("format").GetInt32(), symbols = c.GetProperty("symbols").GetInt32();
            StandardFormats.InfoBits(fmt, symbols).Should().Be(info);
            int s = c.GetProperty("sender").GetInt32(), d = c.GetProperty("dest").GetInt32(), b = c.GetProperty("burst").GetInt32();
            var bits = new byte[info];
            PayloadCodec.Encode(buffer.AsSpan(0, n), info, s, d, b, bits);
            LinkVectorFile.Hex(bits).Should().Be(c.GetProperty("info_bits").GetString());
        }
    }

    [Fact]
    public void Every_Prototype_Payload_Decodes_And_Unpacks_As_The_Prototype_Did()
    {
        foreach (var c in Vectors.GetProperty("cases").EnumerateArray())
        {
            int info = c.GetProperty("info_bit_count").GetInt32();
            var bits = LinkVectorFile.Bits(c.GetProperty("info_bits").GetString()!, info);
            var payload = new byte[(info - 32) / 8];
            PayloadCodec.TryDecode(bits, c.GetProperty("sender").GetInt32(), c.GetProperty("dest").GetInt32(), c.GetProperty("burst").GetInt32(), payload, out int length).Should().BeTrue();
            length.Should().Be(payload.Length);
            Convert.ToHexString(payload).ToLowerInvariant().Should().StartWith(c.GetProperty("packed").GetString());

            var u = c.GetProperty("unpacked");
            PayloadCodec.TryUnpack(payload, c.GetProperty("everyone").GetBoolean(), out PayloadPrelude pre, out DatagramReader reader).Should().BeTrue();
            pre.Wanted.Should().Be(u.GetProperty("wanted").GetBoolean());
            pre.Recommended.Should().Be(OptInt(u.GetProperty("recommended")));
            pre.Failed.Should().Be(u.GetProperty("failed").GetBoolean());
            pre.Capability.Should().Be(OptInt(u.GetProperty("capability")));
            var got = new List<string>();
            while (reader.TryNext(out var g))
            {
                got.Add(Convert.ToHexString(g).ToLowerInvariant());
            }

            got.Should().Equal(u.GetProperty("datagrams").EnumerateArray().Select(x => x.GetString()!));
        }
    }

    [Fact]
    public void A_Payload_Whose_Datagram_Exactly_Fills_The_Room_Round_Trips()
    {
        int n = 0;
        foreach (var c in Vectors.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("tag").GetString() == "exact_fill"))
        {
            c.GetProperty("packed").GetString()!.Length.Should().Be(c.GetProperty("room").GetInt32() * 2);
            n++;
        }

        n.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void A_Flipped_Bit_Fails_The_Check()
    {
        var bad = Vectors.GetProperty("bad");
        bad.GetArrayLength().Should().BeGreaterThanOrEqualTo(30);
        foreach (var c in bad.EnumerateArray())
        {
            int info = c.GetProperty("info_bit_count").GetInt32();
            var bits = LinkVectorFile.Bits(c.GetProperty("info_bits").GetString()!, info);
            PayloadCodec.TryDecode(bits, c.GetProperty("sender").GetInt32(), c.GetProperty("dest").GetInt32(), c.GetProperty("burst").GetInt32(), new byte[(info - 32) / 8], out _).Should().BeFalse();
        }
    }

    [Fact]
    public void Odd_Payloads_Unpack_As_The_Prototype_Does()
    {
        foreach (var c in Vectors.GetProperty("odd").EnumerateArray())
        {
            byte[] p = Convert.FromHexString(c.GetProperty("payload").GetString()!);
            PayloadCodec.TryUnpack(p, c.GetProperty("everyone").GetBoolean(), out PayloadPrelude pre, out DatagramReader reader).Should().BeTrue();
            pre.Wanted.Should().Be(c.GetProperty("wanted").GetBoolean());
            pre.Recommended.Should().Be(OptInt(c.GetProperty("recommended")));
            pre.Failed.Should().Be(c.GetProperty("failed").GetBoolean());
            pre.Capability.Should().Be(OptInt(c.GetProperty("capability")));
            var got = new List<string>();
            while (reader.TryNext(out var g))
            {
                got.Add(Convert.ToHexString(g).ToLowerInvariant());
            }

            got.Should().Equal(c.GetProperty("datagrams").EnumerateArray().Select(x => x.GetString()!));
        }
    }

    [Fact]
    public void Capacity_And_Symbols_For_Match_The_Prototype()
    {
        var loads = LinkVectorFile.Load("formats.json").GetProperty("loads").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        foreach (var f in LinkVectorFile.Load("formats.json").GetProperty("formats").EnumerateArray())
        {
            int number = f.GetProperty("number").GetInt32();
            int symbols = 4;
            foreach (var cap in f.GetProperty("capacity").EnumerateArray())
            {
                PayloadCodec.Capacity(number, symbols).Should().Be(cap.GetInt32(), "format {0} at {1}", number, symbols);
                symbols += 4;
            }

            int i = 0;
            foreach (var expected in f.GetProperty("symbols_for").EnumerateArray())
            {
                PayloadCodec.SymbolsFor(number, loads[i]).Should().Be(OptInt(expected), "format {0} load {1}", number, loads[i]);
                i++;
            }
        }
    }

    [Fact]
    public void Datagrams_Of_Several_Sizes_Round_Trip_Through_The_Bits()
    {
        var rng = new Random(11);
        foreach (int fmt in new[] { 192, 24, 5 * 32 + 20, 2 * 32 + 30 })
        {
            int symbols = PayloadCodec.SymbolsFor(fmt, 130) ?? 252;
            int info = StandardFormats.InfoBits(fmt, symbols);
            for (int count = 0; count <= 3; count++)
            {
                var made = new ReadOnlyMemory<byte>[count];
                for (int i = 0; i < count; i++)
                {
                    var b = new byte[rng.Next(1, 40)];
                    rng.NextBytes(b);
                    made[i] = b;
                }

                var room = new byte[(info - 32) / 8];
                int n = PayloadCodec.Pack(new PayloadPrelude(true, 40, false, 0x0123), made, room);
                n.Should().BeGreaterThan(0);
                var bits = new byte[info];
                PayloadCodec.Encode(room.AsSpan(0, n), info, 5, 6, 7, bits);
                var back = new byte[room.Length];
                PayloadCodec.TryDecode(bits, 5, 6, 7, back, out _).Should().BeTrue();
                PayloadCodec.TryUnpack(back, false, out var pre, out var reader).Should().BeTrue();
                pre.Should().Be(new PayloadPrelude(true, 40, false, 0x0123));
                int seen = 0;
                while (reader.TryNext(out var g))
                {
                    g.ToArray().Should().Equal(made[seen].ToArray());
                    seen++;
                }

                seen.Should().Be(count);
                bits[3] ^= 1;
                PayloadCodec.TryDecode(bits, 5, 6, 7, back, out _).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void The_Wrong_Burst_Number_Does_Not_Unscramble()
    {
        int info = StandardFormats.InfoBits(24, 8);
        var bits = new byte[info];
        PayloadCodec.Encode(new byte[] { 0, 0, 0, 1, 0x55 }, info, 1, 2, 3, bits);
        var back = new byte[(info - 32) / 8];
        PayloadCodec.TryDecode(bits, 1, 2, 3, back, out _).Should().BeTrue();
        back[4].Should().Be(0x55);
        PayloadCodec.TryDecode(bits, 1, 2, 4, back, out _).Should().BeTrue();
        back[4].Should().NotBe(0x55);
    }

    [Fact]
    public void Packing_Reports_When_It_Does_Not_Fit()
    {
        var one = new ReadOnlyMemory<byte>[] { new byte[10] };
        PayloadCodec.Pack(null, one, new byte[11]).Should().Be(-1);
        PayloadCodec.Pack(null, one, new byte[12]).Should().Be(12);
        PayloadCodec.Pack(new PayloadPrelude(false, null, false, 1), [], new byte[3]).Should().Be(-1);
    }
}
