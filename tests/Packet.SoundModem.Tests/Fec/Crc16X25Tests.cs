using Packet.SoundModem.Fec;

namespace Packet.SoundModem.Tests.Fec;

public class Crc16X25Tests
{
    [Fact]
    public void Matches_The_Standard_Check_Value()
    {
        // The canonical CRC-16/X-25 check input.
        Crc16X25.Compute("123456789"u8).Should().Be(0x906E);
    }

    [Fact]
    public void Matches_The_Il2p_Spec_S_Frame_Example()
    {
        // IL2P spec draft v0.6, example encoded packets: the S-frame's trailing CRC
        // decodes to 0xF0DB (Hamming codewords 7F 00 1D 2B).
        byte[] sFrame = Convert.FromHexString("968264888AAEE4969668908A946F81");
        Crc16X25.Compute(sFrame).Should().Be(0xF0DB);
    }

    [Fact]
    public void Empty_Input_Yields_The_Init_Xor_Value()
    {
        Crc16X25.Compute([]).Should().Be(0x0000);
    }
}
