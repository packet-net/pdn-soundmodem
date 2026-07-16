using System.Numerics;
using Packet.SoundModem.Pocsag;

namespace Packet.SoundModem.Tests.Pocsag;

public class PocsagCodewordTests
{
    /// <summary>
    /// The two published protocol constants are themselves BCH codewords; reproducing
    /// them from their own data bits pins the whole layout — generator polynomial,
    /// check-bit placement (bits 10..1) and even parity (bit 0) — against the spec.
    /// (This is also the discriminating vector: DAPNET UniPager's generator.rs runs its
    /// division one step too far and does NOT reproduce the frame-sync word.)
    /// </summary>
    [Theory]
    [InlineData(PocsagCodeword.FrameSync)]
    [InlineData(PocsagCodeword.Idle)]
    public void The_Published_Protocol_Words_Are_Reproduced_From_Their_Data_Bits(uint word)
    {
        PocsagCodeword.Encode(word >> 11).Should().Be(word);
    }

    [Fact]
    public void Every_Encoded_Word_Has_Even_Parity()
    {
        var random = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            uint word = PocsagCodeword.Encode((uint)random.Next(1 << 21));
            (BitOperations.PopCount(word) & 1).Should().Be(0);
        }
    }

    [Fact]
    public void Valid_Words_Pass_With_Zero_Errors()
    {
        uint word = PocsagCodeword.Encode(0x12345);
        PocsagCodeword.TryCorrect(ref word, out int errors).Should().BeTrue();
        errors.Should().Be(0);
        PocsagCodeword.Data(word).Should().Be(0x12345u);
    }

    [Fact]
    public void Every_Single_Bit_Error_Is_Corrected()
    {
        uint word = PocsagCodeword.Encode(0x0F5A3);
        for (int bit = 0; bit < 32; bit++)
        {
            uint corrupted = word ^ (1u << bit);
            PocsagCodeword.TryCorrect(ref corrupted, out int errors)
                .Should().BeTrue($"bit {bit} flipped");
            errors.Should().Be(1);
            corrupted.Should().Be(word);
        }
    }

    [Fact]
    public void Every_Double_Bit_Error_Is_Corrected()
    {
        uint word = PocsagCodeword.Encode(0x1ABCD);
        for (int i = 0; i < 32; i++)
        {
            for (int j = i + 1; j < 32; j++)
            {
                uint corrupted = word ^ (1u << i) ^ (1u << j);
                PocsagCodeword.TryCorrect(ref corrupted, out int errors)
                    .Should().BeTrue($"bits {i} and {j} flipped");
                errors.Should().Be(2);
                corrupted.Should().Be(word);
            }
        }
    }

    /// <summary>
    /// With the overall parity bit the code's minimum distance is 6, so every 3-bit
    /// error is detected (never miscorrected into a different word). Exhaustive over
    /// all C(32,3) = 4960 patterns.
    /// </summary>
    [Fact]
    public void Every_Triple_Bit_Error_Is_Rejected()
    {
        uint word = PocsagCodeword.Encode(0x15555);
        for (int i = 0; i < 32; i++)
        {
            for (int j = i + 1; j < 32; j++)
            {
                for (int k = j + 1; k < 32; k++)
                {
                    uint corrupted = word ^ (1u << i) ^ (1u << j) ^ (1u << k);
                    PocsagCodeword.TryCorrect(ref corrupted, out _)
                        .Should().BeFalse($"bits {i}, {j} and {k} flipped");
                }
            }
        }
    }

    [Fact]
    public void Encode_Rejects_More_Than_21_Data_Bits()
    {
        Action act = () => PocsagCodeword.Encode(0x200000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
