namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The 51-bit burst header: version 2, burst type 3, sender 6, destination 6, format 8, payload symbols in fours 6,
/// burst number 4, then a CRC-16 over those 35 bits. Bits are held one to a byte (0 or 1), the first bit sent first.
/// </summary>
public static class BurstHeaderCodec
{
    /// <summary>Bits in a header.</summary>
    public const int HeaderBits = 51;

    private const int CheckedBits = 35;

    /// <summary>Writes the header's 51 bits into <paramref name="bits"/>.</summary>
    /// <remarks>
    /// As the prototype: the version is 0, 1 or 2, the kind 0 to 7, sender and destination 0 to 63, the format 0 to
    /// 255 and the payload symbols a multiple of four, 0 to 252; anything else throws. The burst number is reduced
    /// to its low four bits.
    /// </remarks>
    public static void Write(in BurstHeader header, Span<byte> bits)
    {
        if (bits.Length < HeaderBits)
        {
            throw new ArgumentException("Need 51 bits of room.", nameof(bits));
        }

        int kind = (int)header.Kind;
        if (header.Version is < 0 or > 2 || kind is < 0 or > 7 || header.Sender is < 0 or > 63 || header.Destination is < 0 or > 63
            || header.Format is < 0 or > 255 || header.PayloadSymbols < 0 || header.PayloadSymbols % 4 != 0 || header.PayloadSymbols / 4 > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "A header field is out of range.");
        }

        int p = 0;
        Put(bits, ref p, header.Version, 2);
        Put(bits, ref p, kind, 3);
        Put(bits, ref p, header.Sender, 6);
        Put(bits, ref p, header.Destination, 6);
        Put(bits, ref p, header.Format, 8);
        Put(bits, ref p, header.PayloadSymbols / 4, 6);
        Put(bits, ref p, header.BurstNumber & 15, 4);
        uint crc = BitCrc.Crc16(bits[..CheckedBits]);
        Put(bits, ref p, (int)crc, 16);
    }

    /// <summary>Reads a header from its 51 bits.</summary>
    /// <remarks>
    /// Returns false when the check fails (or fewer than 51 bits are given). When the check passes and the version is
    /// not 0, returns true with only <see cref="BurstHeader.Version"/> set (the other fields are zero and mean nothing,
    /// as the prototype reads nothing more of such a burst); test <see cref="BurstHeader.IsKnownVersion"/>. For
    /// version 0 every field is returned as sent, and a burst type the code does not know (3 to 7) is returned as that
    /// number, with the sender and destination readable, as the prototype does.
    /// </remarks>
    public static bool TryRead(ReadOnlySpan<byte> bits, out BurstHeader header)
    {
        header = default;
        if (bits.Length < HeaderBits)
        {
            return false;
        }

        if (BitCrc.Crc16(bits[..CheckedBits]) != (uint)Take(bits, CheckedBits, 16))
        {
            return false;
        }

        int version = Take(bits, 0, 2);
        if (version != 0)
        {
            header = new BurstHeader(version, BurstKind.Data, 0, 0, 0, 0, 0);
            return true;
        }

        header = new BurstHeader(0, (BurstKind)Take(bits, 2, 3), Take(bits, 5, 6), Take(bits, 11, 6), Take(bits, 17, 8), 4 * Take(bits, 25, 6), Take(bits, 31, 4));
        return true;
    }

    private static void Put(Span<byte> bits, ref int p, int value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            bits[p++] = (byte)((value >> (width - 1 - i)) & 1);
        }
    }

    private static int Take(ReadOnlySpan<byte> bits, int from, int width)
    {
        int v = 0;
        for (int i = 0; i < width; i++)
        {
            v = (v << 1) | (bits[from + i] & 1);
        }

        return v;
    }
}
