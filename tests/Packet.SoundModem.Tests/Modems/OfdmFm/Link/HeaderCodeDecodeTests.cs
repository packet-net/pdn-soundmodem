using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The header code's decoder against the prototype's, on the same soft values (Vectors/decode_vectors.json).</summary>
public class HeaderCodeDecodeTests
{
    public static IEnumerable<object[]> Openings() =>
        Enum.GetValues<FormatOpening>().Select(o => new object[] { o });

    [Theory]
    [MemberData(nameof(Openings))]
    public void Every_Vector_Decodes_As_The_Prototype_Does(FormatOpening opening)
    {
        JsonElement e = DecodeVectorFile.Opening(opening.ToString());
        e.GetProperty("codedBits").GetInt32().Should().Be(HeaderCode.CodedBits(opening));
        int count = 0;
        int zero = 0;
        int easy = 0;
        int hard = 0;
        int fail = 0;
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        foreach (JsonElement c in e.GetProperty("cases").EnumerateArray())
        {
            float[] soft = DecodeVectorFile.Soft(c);
            string label = $"{opening} {c.GetProperty("name").GetString()}";
            bool ok = HeaderCode.TryDecode(opening, soft, bits, out int iterations);
            ok.Should().Be(c.GetProperty("converged").GetBoolean(), label + ": converged");
            bits.AsSpan().CommonPrefixLength(CodeVectorFile.Bits(c.GetProperty("bits").GetString()!, BurstHeaderCodec.HeaderBits))
                .Should().Be(BurstHeaderCodec.HeaderBits, label + ": first differing bit");
            iterations.Should().Be(c.GetProperty("iterations").GetInt32(), label + ": iterations");
            if (iterations == 0)
            {
                zero++;
            }
            else if (ok && iterations <= 4)
            {
                easy++;
            }
            else if (ok && iterations >= 10)
            {
                hard++;
            }
            else if (!ok)
            {
                fail++;
                iterations.Should().Be(30);
            }

            count++;
        }

        count.Should().Be(40);
        easy.Should().BeGreaterThanOrEqualTo(5);
        hard.Should().BeGreaterThanOrEqualTo(5);
        fail.Should().BeGreaterThanOrEqualTo(5);
        (zero + easy + hard + fail).Should().BeLessThanOrEqualTo(40);
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Decoding_Allocates_Nothing_After_First_Use(FormatOpening opening)
    {
        JsonElement c = DecodeVectorFile.Opening(opening.ToString()).GetProperty("cases").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "hard");
        float[] soft = DecodeVectorFile.Soft(c);
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        var scratch = new float[HeaderCode.DecodeScratchLength(opening)];
        HeaderCode.TryDecode(opening, soft, bits);
        HeaderCode.TryDecode(opening, soft, bits, scratch, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HeaderCode.TryDecode(opening, soft, bits);
        HeaderCode.TryDecode(opening, soft, bits, scratch, out _);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Wrong_Lengths_Are_Refused(FormatOpening opening)
    {
        int n = HeaderCode.CodedBits(opening);
        var bits = new byte[BurstHeaderCodec.HeaderBits];
        Action a = () => HeaderCode.TryDecode(opening, new float[n - 1], bits);
        a.Should().Throw<ArgumentException>();
        a = () => HeaderCode.TryDecode(opening, new float[n], new byte[BurstHeaderCodec.HeaderBits - 1]);
        a.Should().Throw<ArgumentException>();
        a = () => HeaderCode.TryDecode(opening, new float[n], bits, new float[HeaderCode.DecodeScratchLength(opening) - 1], out _);
        a.Should().Throw<ArgumentException>();
    }
}
