namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The two checks the burst uses, over bits held one to a byte (0 or 1), most significant bit first, nothing
/// reflected and nothing inverted at the end: CRC-16 with polynomial 0x1021 for the header and CRC-32 with
/// polynomial 0x04C11DB7 for the payload, both started at all ones.
/// </summary>
internal static class BitCrc
{
    /// <summary>The CRC-16 of the bits, as a number whose top bit is the first check bit sent.</summary>
    public static uint Crc16(ReadOnlySpan<byte> bits) => Run(bits, 16, 0x1021u, 0xFFFFu);

    /// <summary>The CRC-32 of the bits, as a number whose top bit is the first check bit sent.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bits) => Run(bits, 32, 0x04C11DB7u, 0xFFFFFFFFu);

    private static uint Run(ReadOnlySpan<byte> bits, int width, uint poly, uint init)
    {
        uint mask = width == 32 ? 0xFFFFFFFFu : (1u << width) - 1;
        uint reg = init;
        foreach (byte b in bits)
        {
            uint top = (reg >> (width - 1)) & 1;
            reg = (reg << 1) & mask;
            if ((top ^ (uint)(b & 1)) != 0)
            {
                reg ^= poly;
            }
        }

        return reg;
    }
}
