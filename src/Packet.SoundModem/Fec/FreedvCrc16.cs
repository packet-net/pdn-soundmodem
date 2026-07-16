namespace Packet.SoundModem.Fec;

/// <summary>
/// The CRC-16 the FreeDV data frames carry — a port of codec2's <c>freedv_gen_crc16</c>
/// (<c>freedv_api.c</c>). It is CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF,
/// no input/output reflection, no final XOR. FreeDV computes it over the payload bytes and
/// appends it big-endian as the frame's last two bytes. LGPL-2.1 lineage — see PROVENANCE.md.
/// </summary>
internal static class FreedvCrc16
{
    /// <summary>Computes the CRC-16/CCITT-FALSE of <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte d in data)
        {
            byte x = (byte)((crc >> 8) ^ d);
            x ^= (byte)(x >> 4);
            crc = (ushort)((crc << 8) ^ (ushort)(x << 12) ^ (ushort)(x << 5) ^ x);
        }

        return crc;
    }
}
