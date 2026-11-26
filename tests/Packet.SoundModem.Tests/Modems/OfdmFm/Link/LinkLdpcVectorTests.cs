using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>
/// The adaptive payload code's encoder against the prototype: every vector in code_vectors.json (made by
/// the prototype's proto/vectors/code_vectors.py from <c>fit(info, coded, "own").encode(bits)</c>) must be
/// matched bit for bit, every block plan must match the prototype's, and every encoded block must satisfy
/// its own parity checks.
/// </summary>
public class LinkLdpcVectorTests
{
    public static IEnumerable<object[]> Cases() => CodeVectorFile.CaseData();

    public static IEnumerable<object[]> Plans() => CodeVectorFile.PlanData();

    [Fact]
    public void The_Vector_File_Has_The_Cases_The_Port_Is_Pinned_To()
    {
        List<string> names = [.. CodeVectorFile.CaseNames()];
        names.Count.Should().BeGreaterThan(100);
        names.Count(n => n.StartsWith("format", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(40);
        foreach (int z in LinkLdpcTables.Liftings)
        {
            names.Should().Contain($"lift{z}_little_shortening").And.Contain($"lift{z}_much_shortening");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_Encoder_Matches_The_Prototype_Bit_For_Bit(string name)
    {
        JsonElement c = CodeVectorFile.Case(name);
        int infoBits = c.GetProperty("infoBits").GetInt32();
        int codedBits = c.GetProperty("codedBits").GetInt32();
        byte[] info = CodeVectorFile.Bits(c.GetProperty("info").GetString()!, infoBits);
        var coded = new byte[codedBits];
        if (c.GetProperty("direct").GetBoolean())
        {
            new LinkLdpcCodeBlock(infoBits, codedBits).Encode(info, coded);
        }
        else
        {
            PayloadCode.Get(infoBits, codedBits).Encode(info, coded);
        }

        if (c.TryGetProperty("coded", out JsonElement whole))
        {
            byte[] expected = CodeVectorFile.Bits(whole.GetString()!, codedBits);
            int at = coded.AsSpan().CommonPrefixLength(expected);
            at.Should().Be(codedBits, $"{name}: first differing coded bit is {at}");
        }
        else
        {
            Convert.ToHexString(SHA256.HashData(coded)).ToLowerInvariant().Should().Be(c.GetProperty("codedSha256").GetString());
            coded.AsSpan(0, 64).ToArray().Should().Equal(CodeVectorFile.Bits(c.GetProperty("codedHead").GetString()!, 64));
            coded.AsSpan(codedBits - 64).ToArray().Should().Equal(CodeVectorFile.Bits(c.GetProperty("codedTail").GetString()!, 64));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_Encoded_Block_Has_A_Zero_Syndrome(string name)
    {
        JsonElement c = CodeVectorFile.Case(name);
        int infoBits = c.GetProperty("infoBits").GetInt32();
        int codedBits = c.GetProperty("codedBits").GetInt32();
        byte[] info = CodeVectorFile.Bits(c.GetProperty("info").GetString()!, infoBits);
        IReadOnlyList<LinkLdpcCodeBlock> blocks = c.GetProperty("direct").GetBoolean()
            ? [new LinkLdpcCodeBlock(infoBits, codedBits)]
            : PayloadCode.Get(infoBits, codedBits).Blocks;
        int offset = 0;
        foreach (LinkLdpcCodeBlock b in blocks)
        {
            var word = new byte[b.CodewordLength];
            var work = new byte[b.CheckRows * b.LiftingSize];
            b.EncodeCodeword(info.AsSpan(offset, b.InfoBits), word, work);
            offset += b.InfoBits;
            b.SyndromeIsZero(word).Should().BeTrue($"{name}: block of {b.InfoBits} bits at lifting {b.LiftingSize}");
            foreach (int s in b.Shortened)
            {
                word[s].Should().Be(0, "a shortened bit is a known zero");
            }
        }

        offset.Should().Be(infoBits);
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void The_Block_Plans_Match_The_Prototypes(int index)
    {
        JsonElement p = CodeVectorFile.Plan(index);
        int k = p.GetProperty("infoBits").GetInt32();
        int e = p.GetProperty("codedBits").GetInt32();
        var b = new LinkLdpcCodeBlock(k, e);
        string at = $"block k={k} e={e}";
        b.LiftingSize.Should().Be(p.GetProperty("z").GetInt32(), $"{at}: lifting size");
        b.CheckRows.Should().Be(p.GetProperty("rows").GetInt32(), $"{at}: check rows kept");
        b.Shortened.ToArray().Should().Equal(CodeVectorFile.Ints(p.GetProperty("shortened")), $"{at}: shortened positions");
        b.TransmitOrder.ToArray().Should().Equal(CodeVectorFile.Ints(p.GetProperty("transmitOrder")), $"{at}: transmit order");
    }
}
