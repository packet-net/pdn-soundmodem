using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The payload code's decoder against the prototype's, on the same soft values (Vectors/decode_vectors.json).</summary>
public class LinkLdpcDecodeTests
{
    public static IEnumerable<object[]> BlockCases() => DecodeVectorFile.BlockData();

    public static IEnumerable<object[]> PayloadCases() => DecodeVectorFile.PayloadData();

    [Fact]
    public void The_Vectors_Cover_Every_Kind_Of_Block()
    {
        var blocks = DecodeVectorFile.Blocks.ToList();
        blocks.Count.Should().BeGreaterThanOrEqualTo(60);
        blocks.Select(b => b.GetProperty("infoBits").GetInt32() + "/" + b.GetProperty("codedBits").GetInt32()).Distinct().Count().Should().BeGreaterThanOrEqualTo(15);
        blocks.Select(b => b.GetProperty("lifting").GetInt32()).Distinct().Count().Should().BeGreaterThanOrEqualTo(6);
        blocks.Count(b => b.GetProperty("iterations").GetInt32() == 0).Should().BeGreaterThanOrEqualTo(3);
        blocks.Count(b => b.GetProperty("converged").GetBoolean() && b.GetProperty("iterations").GetInt32() is >= 1 and <= 4).Should().BeGreaterThanOrEqualTo(15);
        blocks.Count(b => b.GetProperty("converged").GetBoolean() && b.GetProperty("iterations").GetInt32() >= 10).Should().BeGreaterThanOrEqualTo(15);
        blocks.Count(b => !b.GetProperty("converged").GetBoolean() && b.GetProperty("iterations").GetInt32() == 30).Should().BeGreaterThanOrEqualTo(15);
        DecodeVectorFile.Payloads.Count().Should().BeGreaterThanOrEqualTo(8);
        DecodeVectorFile.Payloads.Count(p => !p.GetProperty("ok").GetBoolean()).Should().BeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [MemberData(nameof(BlockCases))]
    public void A_Block_Decodes_As_The_Prototype_Does(string name)
    {
        JsonElement c = DecodeVectorFile.Block(name);
        int k = c.GetProperty("infoBits").GetInt32();
        int e = c.GetProperty("codedBits").GetInt32();
        float[] soft = DecodeVectorFile.Soft(c);
        var block = new LinkLdpcCodeBlock(k, e);
        var scratch = new float[block.DecodeScratchLength];

        var info = new byte[k];
        bool ok = block.Decode(soft, info, scratch, out int iterations);
        ok.Should().Be(c.GetProperty("converged").GetBoolean(), "converged");
        iterations.Should().Be(c.GetProperty("iterations").GetInt32(), "iterations");
        info.AsSpan().CommonPrefixLength(CodeVectorFile.Bits(c.GetProperty("info").GetString()!, k)).Should().Be(k, "first differing information bit");

        var codeword = new byte[block.CodewordLength];
        bool ok2 = block.DecodeCodeword(soft, codeword, scratch, out int iterations2);
        ok2.Should().Be(ok);
        iterations2.Should().Be(iterations);
        Convert.ToHexStringLower(SHA256.HashData(codeword)).Should().Be(c.GetProperty("codewordSha256").GetString(), "hard decision of the codeword");
    }

    [Theory]
    [MemberData(nameof(PayloadCases))]
    public void A_Payload_Decodes_As_The_Prototype_Does(string name)
    {
        JsonElement c = DecodeVectorFile.Payload(name);
        int k = c.GetProperty("infoBits").GetInt32();
        int e = c.GetProperty("codedBits").GetInt32();
        float[] soft = DecodeVectorFile.Soft(c);
        var code = new PayloadCode(k, e);
        var info = new byte[k];
        var guess = new byte[e];
        bool ok = code.Decode(soft, info, guess, new float[code.DecodeScratchLength], new byte[code.ScratchLength]);
        ok.Should().Be(c.GetProperty("ok").GetBoolean(), "every block satisfied");
        info.AsSpan().CommonPrefixLength(CodeVectorFile.Bits(c.GetProperty("info").GetString()!, k)).Should().Be(k, "first differing information bit");
        guess.AsSpan().CommonPrefixLength(CodeVectorFile.Bits(c.GetProperty("guess").GetString()!, e)).Should().Be(e, "first differing coded bit of the guess");
    }

    [Fact]
    public void Decoding_Allocates_Nothing_On_A_Second_Call()
    {
        JsonElement c = DecodeVectorFile.Block("shape4_hard");
        float[] soft = DecodeVectorFile.Soft(c);
        var block = new LinkLdpcCodeBlock(c.GetProperty("infoBits").GetInt32(), c.GetProperty("codedBits").GetInt32());
        var scratch = new float[block.DecodeScratchLength];
        var info = new byte[block.InfoBits];
        var codeword = new byte[block.CodewordLength];
        block.Decode(soft, info, scratch, out _);
        block.DecodeCodeword(soft, codeword, scratch, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        block.Decode(soft, info, scratch, out _);
        block.DecodeCodeword(soft, codeword, scratch, out _);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);

        JsonElement p = DecodeVectorFile.Payload("payload9");
        float[] psoft = DecodeVectorFile.Soft(p);
        var code = new PayloadCode(p.GetProperty("infoBits").GetInt32(), p.GetProperty("codedBits").GetInt32());
        var pinfo = new byte[code.InfoBits];
        var guess = new byte[code.CodedBits];
        var floats = new float[code.DecodeScratchLength];
        var bytes = new byte[code.ScratchLength];
        code.Decode(psoft, pinfo, guess, floats, bytes);
        before = GC.GetAllocatedBytesForCurrentThread();
        code.Decode(psoft, pinfo, guess, floats, bytes);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
    }

    [Fact]
    public void Wrong_Lengths_Are_Refused()
    {
        var block = new LinkLdpcCodeBlock(200, 500);
        var scratch = new float[block.DecodeScratchLength];
        var soft = new float[500];
        var info = new byte[200];
        Action a = () => block.Decode(new float[499], info, scratch, out _);
        a.Should().Throw<ArgumentException>();
        a = () => block.Decode(soft, new byte[199], scratch, out _);
        a.Should().Throw<ArgumentException>();
        a = () => block.Decode(soft, info, new float[block.DecodeScratchLength - 1], out _);
        a.Should().Throw<ArgumentException>();
        a = () => block.DecodeCodeword(soft, new byte[block.CodewordLength - 1], scratch, out _);
        a.Should().Throw<ArgumentException>();

        var code = new PayloadCode(1200, 2400);
        var floats = new float[code.DecodeScratchLength];
        var bytes = new byte[code.ScratchLength];
        var psoft = new float[2400];
        var pinfo = new byte[1200];
        var guess = new byte[2400];
        a = () => code.Decode(new float[2399], pinfo, guess, floats, bytes);
        a.Should().Throw<ArgumentException>();
        a = () => code.Decode(psoft, new byte[1199], guess, floats, bytes);
        a.Should().Throw<ArgumentException>();
        a = () => code.Decode(psoft, pinfo, new byte[2399], floats, bytes);
        a.Should().Throw<ArgumentException>();
        a = () => code.Decode(psoft, pinfo, guess, new float[code.DecodeScratchLength - 1], bytes);
        a.Should().Throw<ArgumentException>();
        a = () => code.Decode(psoft, pinfo, guess, floats, new byte[code.ScratchLength - 1]);
        a.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Noise_Free_Block_Whose_Unsent_Bits_Are_Zero_Decodes_In_Zero_Iterations()
    {
        var block = new LinkLdpcCodeBlock(384, 768);
        var info = new byte[384];
        var coded = new byte[768];
        block.Encode(info, coded);
        var soft = new float[768];
        for (int i = 0; i < soft.Length; i++)
        {
            soft[i] = coded[i] == 0 ? 8f : -8f;
        }

        var decoded = new byte[384];
        block.Decode(soft, decoded, new float[block.DecodeScratchLength], out int iterations).Should().BeTrue();
        iterations.Should().Be(0);
        decoded.Should().Equal(info);
    }
}
