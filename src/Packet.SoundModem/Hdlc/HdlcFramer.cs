using M0LTE.Fec;

namespace Packet.SoundModem.Hdlc;

/// <summary>
/// Builds the transmit-side HDLC bit stream for an AX.25 frame: opening flags (the
/// TXDELAY preamble), the frame with its CRC-16/X-25 FCS appended (low byte first),
/// zero-stuffing after five consecutive ones, and closing flags. Output bits are
/// logical HDLC bits — NRZI encoding is applied downstream by the modulator chain.
/// </summary>
public static class HdlcFramer
{
    /// <summary>
    /// Frames <paramref name="frame"/> (an AX.25 frame without FCS) as logical bits.
    /// </summary>
    /// <param name="frame">Frame content; the FCS is computed and appended here.</param>
    /// <param name="openingFlags">Flags sent before the frame (TXDELAY fill).</param>
    /// <param name="closingFlags">Flags sent after the frame; at least one is required
    /// to close it.</param>
    public static byte[] FrameBits(ReadOnlySpan<byte> frame, int openingFlags, int closingFlags = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(openingFlags, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(closingFlags, 1);
        if (frame.IsEmpty)
        {
            throw new ArgumentException("cannot frame an empty frame", nameof(frame));
        }

        ushort fcs = Crc16X25.Compute(frame);

        // Worst case: every content bit stuffed (×1.2) plus flags.
        var bits = new List<byte>((openingFlags + closingFlags) * 8 + (frame.Length + 2) * 10);

        for (int i = 0; i < openingFlags; i++)
        {
            AppendFlag(bits);
        }

        int onesRun = 0;
        foreach (byte value in frame)
        {
            AppendStuffed(bits, value, ref onesRun);
        }

        AppendStuffed(bits, (byte)(fcs & 0xFF), ref onesRun);
        AppendStuffed(bits, (byte)(fcs >> 8), ref onesRun);

        for (int i = 0; i < closingFlags; i++)
        {
            AppendFlag(bits);
        }

        return [.. bits];
    }

    private static void AppendFlag(List<byte> bits)
    {
        // 0x7E = 01111110, transmitted LSB first; never stuffed.
        bits.Add(0);
        for (int i = 0; i < 6; i++)
        {
            bits.Add(1);
        }

        bits.Add(0);
    }

    private static void AppendStuffed(List<byte> bits, byte value, ref int onesRun)
    {
        for (int i = 0; i < 8; i++)
        {
            int bit = (value >> i) & 1;
            bits.Add((byte)bit);
            if (bit == 1)
            {
                if (++onesRun == 5)
                {
                    bits.Add(0);
                    onesRun = 0;
                }
            }
            else
            {
                onesRun = 0;
            }
        }
    }
}
