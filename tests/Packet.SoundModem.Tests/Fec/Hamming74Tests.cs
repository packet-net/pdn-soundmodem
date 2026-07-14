using Packet.SoundModem.Fec;

namespace Packet.SoundModem.Tests.Fec;

public class Hamming74Tests
{
    [Fact]
    public void Encode_Matches_The_Spec_Example()
    {
        // Spec: "4-bit value 0x7 encodes as 7-bit value 0x47".
        Hamming74.Encode(0x7).Should().Be(0x47);
    }

    [Fact]
    public void Decode_Matches_The_Spec_Example()
    {
        // Spec: "7-bit value 0x6 decodes as 4-bit value 0xE".
        Hamming74.Decode(0x6).Should().Be(0xE);
    }

    [Fact]
    public void Decode_Inverts_Encode_For_All_Nibbles()
    {
        for (int v = 0; v < 16; v++)
        {
            Hamming74.Decode(Hamming74.Encode(v)).Should().Be((byte)v);
        }
    }

    [Fact]
    public void Any_Single_Bit_Error_Is_Corrected()
    {
        for (int v = 0; v < 16; v++)
        {
            byte codeword = Hamming74.Encode(v);
            for (int bit = 0; bit < 7; bit++)
            {
                byte corrupted = (byte)(codeword ^ (1 << bit));
                Hamming74.Decode(corrupted).Should().Be((byte)v, $"value {v:X} with bit {bit} flipped");
            }
        }
    }

    [Fact]
    public void The_Padded_Msb_Is_Ignored()
    {
        Hamming74.Decode(0x80 | 0x47).Should().Be(0x7);
    }
}
