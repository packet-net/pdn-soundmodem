using System.Text;
using Packet.SoundModem.Fec;

namespace Packet.SoundModem.Tests.Fec;

/// <summary>The FreeDV framing primitives (CRC-16, golden-prime interleaver), validated
/// against standard check values and their algebraic properties.</summary>
public class FreedvFramingTests
{
    [Fact]
    public void Crc16_Matches_The_Ccitt_False_Check_Value()
    {
        // "123456789" → 0x29B1 is the canonical CRC-16/CCITT-FALSE check value.
        FreedvCrc16.Compute(Encoding.ASCII.GetBytes("123456789")).Should().Be(0x29B1);
    }

    [Fact]
    public void Crc16_Of_Empty_Is_The_Init_Value()
    {
        FreedvCrc16.Compute(ReadOnlySpan<byte>.Empty).Should().Be(0xFFFF);
    }

    [Theory]
    [InlineData(10, 7)]     // floor(10/1.62)=6 → next_prime → 7
    [InlineData(100, 67)]   // floor(100/1.62)=61 → next_prime(61)=67 (strictly above)
    public void Interleaver_B_Is_The_Golden_Prime(int n, int expectedB)
    {
        GpInterleaver.ChooseB(n).Should().Be(expectedB);
    }

    [Theory]
    [InlineData(96)]     // datac14 coded symbols
    [InlineData(128)]    // datac0 coded symbols
    [InlineData(1024)]   // datac3
    public void Interleaver_Is_A_Bijection_And_Round_Trips(int n)
    {
        var frame = new int[n];
        for (int i = 0; i < n; i++)
        {
            frame[i] = i;
        }

        var interleaved = new int[n];
        GpInterleaver.Interleave<int>(frame, interleaved, n);

        // A permutation: every original element appears exactly once.
        interleaved.Should().BeEquivalentTo(frame, "the interleave must be a bijection");

        var back = new int[n];
        GpInterleaver.Deinterleave<int>(interleaved, back, n);
        back.Should().Equal(frame, "deinterleave inverts interleave");
    }
}
