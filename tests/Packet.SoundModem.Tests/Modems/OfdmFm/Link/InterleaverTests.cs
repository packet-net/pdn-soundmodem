using System.Security.Cryptography;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class InterleaverTests
{
    [Fact]
    public void Sixteen_Bits_Give_The_Documented_Permutation()
    {
        Interleaver.Permutation(16).ToArray().Should().Equal(9, 3, 10, 4, 5, 6, 7, 15, 11, 2, 1, 12, 13, 14, 0, 8);
    }

    [Fact]
    public void Small_Permutations_Match_The_Prototype_Entry_For_Entry()
    {
        var small = LinkVectorFile.Load("symbol_vectors.json").GetProperty("interleaver").GetProperty("small");
        foreach (var prop in small.EnumerateObject())
        {
            int e = int.Parse(prop.Name);
            int[] expected = prop.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            expected.Length.Should().Be(e);
            Interleaver.Permutation(e).ToArray().Should().Equal(expected, $"E = {e}");
        }
    }

    [Theory]
    [InlineData(138528)]
    [InlineData(839160)]
    public void Large_Permutations_Match_The_Prototypes_Hash_And_Ends(int e)
    {
        var v = LinkVectorFile.Load("symbol_vectors.json").GetProperty("interleaver").GetProperty("big").GetProperty(e.ToString());
        int[] p = Interleaver.Permutation(e).ToArray();
        p.Length.Should().Be(e);
        var bytes = new byte[4 * e];
        for (int i = 0; i < e; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 * i), p[i]);
        }

        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(v.GetProperty("sha256").GetString());
        p[..16].Should().Equal(v.GetProperty("first").EnumerateArray().Select(x => x.GetInt32()));
        p[^16..].Should().Equal(v.GetProperty("last").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Interleave_Places_Coded_Bit_K_At_P_Of_K_And_Deinterleave_Undoes_It()
    {
        const int E = 1000;
        var rng = new Random(837);
        var coded = new byte[E];
        rng.NextBytes(coded);
        var tx = new byte[E];
        Interleaver.Interleave<byte>(coded, tx);
        var p = Interleaver.Permutation(E);
        for (int k = 0; k < E; k++)
        {
            tx[p[k]].Should().Be(coded[k]);
        }

        var back = new byte[E];
        Interleaver.Deinterleave<byte>(tx, back);
        back.Should().Equal(coded);
    }

    [Fact]
    public void Soft_Values_Deinterleave_The_Same_Way()
    {
        const int E = 77;
        var coded = new float[E];
        for (int i = 0; i < E; i++)
        {
            coded[i] = i * 0.5f - 10;
        }

        var tx = new float[E];
        var back = new float[E];
        Interleaver.Interleave<float>(coded, tx);
        Interleaver.Deinterleave<float>(tx, back);
        back.Should().Equal(coded);
    }

    [Fact]
    public void The_Cache_Holds_At_Most_Four_Permutations()
    {
        Interleaver.ClearCache();
        for (int e = 100; e < 110; e++)
        {
            Interleaver.Permutation(e);
            Interleaver.CachedCount.Should().BeLessThanOrEqualTo(Interleaver.CacheSize);
        }

        Interleaver.CachedCount.Should().Be(Interleaver.CacheSize);
    }

    [Fact]
    public void A_Repeated_Size_Is_Not_Rebuilt_And_Allocates_Nothing()
    {
        const int E = 5000;
        var coded = new byte[E];
        var tx = new byte[E];
        Interleaver.Interleave<byte>(coded, tx);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            Interleaver.Interleave<byte>(coded, tx);
            Interleaver.Deinterleave<byte>(tx, coded);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void Sizes_Outside_The_Definition_Are_Refused()
    {
        foreach (int e in new[] { 0, -1 })
        {
            Action act = () => Interleaver.Permutation(e);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
