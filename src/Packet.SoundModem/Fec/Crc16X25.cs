namespace Packet.SoundModem.Fec;

/// <summary>
/// CRC-16/X-25 — the AX.25 frame check sequence: reflected polynomial 0x1021
/// (0x8408 reversed form), initial value 0xFFFF, final XOR 0xFFFF.
/// IL2P's optional trailing CRC (spec draft v0.6 § Optional Trailing CRC) specifies
/// "CRC-16-CCITT … calculated in the same manner as for an AX.25 frame", i.e. exactly
/// this variant — verified against the spec's S-frame example (CRC 0xF0DB).
/// </summary>
public static class Crc16X25
{
    /// <summary>Computes the CRC over <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            }
        }

        return (ushort)(crc ^ 0xFFFF);
    }
}
