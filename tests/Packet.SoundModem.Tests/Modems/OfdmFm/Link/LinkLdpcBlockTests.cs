using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>Properties of the code family and its encoder that the vectors do not state directly.</summary>
public class LinkLdpcBlockTests
{
    private static byte[] RandomBits(int n, int seed)
    {
        var rng = new Random(seed);
        var bits = new byte[n];
        for (int i = 0; i < n; i++)
        {
            bits[i] = (byte)rng.Next(2);
        }

        return bits;
    }

    [Fact]
    public void The_Tables_Have_The_Shape_The_Prototype_Reads()
    {
        LinkLdpcTables.Kb.Should().Be(24);
        LinkLdpcTables.Punctured.Should().Be(2);
        LinkLdpcTables.CoreRows.Should().Be(3);
        LinkLdpcTables.Mb.Should().Be(98);
        LinkLdpcTables.Nb.Should().Be(122);
        LinkLdpcTables.Liftings.Should().Equal(Enumerable.Range(1, 22).Select(i => i * 16));
        LinkLdpcTables.MaxInfoBitsPerBlock.Should().Be(8448);
    }

    [Theory]
    [InlineData(24, 136)]
    [InlineData(1000, 2000)]
    [InlineData(1000, 5000)]
    [InlineData(3000, 3500)]
    [InlineData(8448, 16000)]
    [InlineData(100, 1)]
    public void The_Dense_Core_Inverse_Gives_The_Same_Codeword_As_The_Dual_Diagonal_Peel(int k, int e)
    {
        var peeled = new LinkLdpcCodeBlock(k, e, structured: true);
        var dense = new LinkLdpcCodeBlock(k, e, structured: false);
        peeled.EncoderKind.Should().Be("sum");
        dense.EncoderKind.Should().Be("dense");
        byte[] info = RandomBits(k, k + e);
        var a = new byte[e];
        var b = new byte[e];
        peeled.Encode(info, a);
        dense.Encode(info, b);
        b.Should().Equal(a);
        var word = new byte[dense.CodewordLength];
        dense.EncodeCodeword(info, word, new byte[dense.CheckRows * dense.LiftingSize]);
        dense.SyndromeIsZero(word).Should().BeTrue();
    }

    [Fact]
    public void A_Flipped_Bit_Breaks_The_Syndrome()
    {
        var block = new LinkLdpcCodeBlock(1000, 2000);
        var word = new byte[block.CodewordLength];
        block.EncodeCodeword(RandomBits(1000, 1), word, new byte[block.CheckRows * block.LiftingSize]);
        block.SyndromeIsZero(word).Should().BeTrue();
        word[block.CodewordLength - 1] ^= 1;
        block.SyndromeIsZero(word).Should().BeFalse();
    }

    [Fact]
    public void Encoding_Allocates_Nothing_Once_The_Plan_Is_Built()
    {
        var code = new PayloadCode(20000, 30000);
        byte[] info = RandomBits(20000, 5);
        var coded = new byte[30000];
        var scratch = new byte[code.ScratchLength];
        code.Encode(info, coded, scratch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        code.Encode(info, coded, scratch);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
    }

    [Fact]
    public void The_Convenience_Overload_Gives_The_Same_Bits_As_Caller_Supplied_Scratch()
    {
        var code = PayloadCode.Get(9000, 12000);
        byte[] info = RandomBits(9000, 6);
        var a = new byte[12000];
        var b = new byte[12000];
        code.Encode(info, a);
        code.Encode(info, b, new byte[code.ScratchLength]);
        b.Should().Equal(a);
    }

    [Fact]
    public void A_Payload_Is_Split_Into_The_Fewest_Blocks_Larger_Information_Blocks_First()
    {
        var code = new PayloadCode(8449, 9000);
        code.Blocks.Select(b => b.InfoBits).Should().Equal(4225, 4224);
        code.Blocks.Select(b => b.CodedBits).Should().Equal(4500, 4500);
        new PayloadCode(8448, 8448).Blocks.Should().HaveCount(1);
    }

    [Fact]
    public void The_Payload_Plan_Cache_Is_Bounded_And_Returns_The_Same_Instance_For_A_Repeat()
    {
        for (int i = 0; i < 100; i++)
        {
            PayloadCode.Get(1000 + i, 3000);
            PayloadCode.CachedCount.Should().BeLessThanOrEqualTo(PayloadCode.CacheCapacity);
        }

        PayloadCode.CachedCount.Should().BeLessThanOrEqualTo(32);
        PayloadCode last = PayloadCode.Get(5000, 7000);
        PayloadCode.Get(5000, 7000).Should().BeSameAs(last);
    }

    [Fact]
    public void A_Payload_With_More_Information_Than_Coded_Bits_Is_Refused()
    {
        Action act = () => _ = new PayloadCode(100, 99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Building_And_Encoding_The_Largest_Plan_Is_Timed()
    {
        // The largest block (lifting 352, every check row kept), built fresh, peeled and dense; then the largest
        // burst the format list allows (780416 information bits in 839160 coded, 93 blocks) encoded.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var block = new LinkLdpcCodeBlock(8448, 60000, structured: true);
        long peeled = clock.ElapsedMilliseconds;
        clock.Restart();
        var dense = new LinkLdpcCodeBlock(8448, 60000, structured: false);
        long denseMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var code = new PayloadCode(780416, 839160);
        long payload = clock.ElapsedMilliseconds;
        byte[] info = RandomBits(780416, 7);
        var coded = new byte[839160];
        var scratch = new byte[code.ScratchLength];
        code.Encode(info, coded, scratch);
        clock.Restart();
        code.Encode(info, coded, scratch);
        long encode = clock.ElapsedMilliseconds;
        var typical = new PayloadCode(110000, 138528);
        Console.WriteLine($"Expected bytes: payload plan largest {code.PlanBytes}, typical {typical.PlanBytes}, shared structure (lifting 352, 98 rows) {block.StructureBytes}");
        Console.WriteLine($"Expected timing: block plan ({block.CheckRows} rows, lifting {block.LiftingSize}) peeled {peeled} ms, dense {denseMs} ms; "
            + $"largest payload plan ({code.Blocks.Count} blocks) {payload} ms; encode {encode} ms");
        dense.EncoderKind.Should().Be("dense");
        peeled.Should().BeLessThan(20000);
        denseMs.Should().BeLessThan(20000);
        encode.Should().BeLessThan(20000);
    }
}
