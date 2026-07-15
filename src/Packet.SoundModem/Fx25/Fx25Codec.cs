using Packet.SoundModem.Fec;
using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Fx25;

/// <summary>
/// FX.25 (Stensat draft, 2006): wraps a normally bit-stuffed HDLC/AX.25 frame in a
/// Reed-Solomon code block preceded by a 64-bit correlation tag, transparently — legacy
/// AX.25 receivers still decode the embedded frame; FX.25 receivers can repair errors
/// first. Constants (tag values, block formats, RS fcr=1) match the spec as reproduced
/// by Dire Wolf, the de-facto interop reference.
/// </summary>
public static class Fx25Codec
{
    /// <summary>One correlation-tag block format.</summary>
    /// <param name="Tag">64-bit correlation tag value (transmitted least-significant
    /// byte and bit first).</param>
    /// <param name="RadioDataBytes">Data bytes actually transmitted.</param>
    /// <param name="RsDataBytes">RS block data size (255 − parity); the gap between this
    /// and <paramref name="RadioDataBytes"/> is implicit zero padding.</param>
    /// <param name="ParityBytes">RS check bytes (16/32/64).</param>
    public readonly record struct TagFormat(ulong Tag, int RadioDataBytes, int RsDataBytes, int ParityBytes);

    /// <summary>Tag numbers 0x01–0x0B (index 0 = tag 0x01).</summary>
    public static readonly TagFormat[] Formats =
    [
        new(0xB74DB7DF8A532F3E, 239, 239, 16),
        new(0x26FF60A600CC8FDE, 128, 239, 16),
        new(0xC7DC0508F3D9B09E, 64, 239, 16),
        new(0x8F056EB4369660EE, 32, 239, 16),
        new(0x6E260B1AC5835FAE, 223, 223, 32),
        new(0xFF94DC634F1CFF4E, 128, 223, 32),
        new(0x1EB7B9CDBC09C00E, 64, 223, 32),
        new(0xDBF869BD2DBB1776, 32, 223, 32),
        new(0x3ADB0C13DEAE2836, 191, 191, 64),
        new(0xAB69DB6A543188D6, 128, 191, 64),
        new(0x4A4ABEC4A724B796, 64, 191, 64),
    ];

    private static readonly ReedSolomon Rs16 = new(16, firstConsecutiveRoot: 1);
    private static readonly ReedSolomon Rs32 = new(32, firstConsecutiveRoot: 1);
    private static readonly ReedSolomon Rs64 = new(64, firstConsecutiveRoot: 1);

    internal static ReedSolomon RsFor(int parityBytes) => parityBytes switch
    {
        16 => Rs16,
        32 => Rs32,
        64 => Rs64,
        _ => throw new ArgumentOutOfRangeException(nameof(parityBytes)),
    };

    /// <summary>Finds a tag matching <paramref name="accumulator"/> within 8 bit errors
    /// (Dire Wolf's CLOSE_ENOUGH), or -1.</summary>
    public static int FindTag(ulong accumulator)
    {
        for (int i = 0; i < Formats.Length; i++)
        {
            if (System.Numerics.BitOperations.PopCount(accumulator ^ Formats[i].Tag) <= 8)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Encodes an AX.25 frame (no flags/FCS) as FX.25 logical bits: correlation tag,
    /// then the flag-delimited bit-stuffed frame padded with rotating flag fill, then RS
    /// check bytes. All bytes are emitted least-significant bit first; NRZI is applied
    /// downstream by the modulator, exactly as for plain HDLC.
    /// </summary>
    /// <param name="ax25Frame">Frame content; FCS is appended here.</param>
    /// <param name="checkBytes">Requested FEC strength: 16, 32 or 64. The smallest
    /// transmitted-size format of that strength which fits is picked.</param>
    /// <exception cref="ArgumentException">The stuffed frame does not fit any format of
    /// the requested strength.</exception>
    public static byte[] EncodeBits(ReadOnlySpan<byte> ax25Frame, int checkBytes = 16)
    {
        byte[] stuffed = StuffToBytes(ax25Frame);

        TagFormat? chosen = null;
        foreach (TagFormat format in Formats)
        {
            if (format.ParityBytes == checkBytes && format.RadioDataBytes >= stuffed.Length
                && (chosen is null || format.RadioDataBytes < chosen.Value.RadioDataBytes))
            {
                chosen = format;
            }
        }

        if (chosen is not TagFormat picked)
        {
            throw new ArgumentException(
                $"stuffed frame of {stuffed.Length} bytes fits no FX.25 format with {checkBytes} check bytes");
        }

        // Fill between the stuffed frame and the transmitted size with the rotating flag
        // pattern (a continuous 01111110... bit stream, as Dire Wolf's stuff_it does),
        // then zero-pad the RS block's untransmitted remainder.
        var data = new byte[picked.RsDataBytes];
        stuffed.CopyTo(data, 0);
        FillRotatingFlags(data.AsSpan(stuffed.Length, picked.RadioDataBytes - stuffed.Length));

        var parity = new byte[picked.ParityBytes];
        RsFor(picked.ParityBytes).Encode(data, parity);

        var bits = new byte[(8 + picked.RadioDataBytes + picked.ParityBytes) * 8];
        int position = 0;
        for (int k = 0; k < 8; k++)
        {
            AppendByteLsbFirst(bits, ref position, (byte)(picked.Tag >> (k * 8)));
        }

        for (int i = 0; i < picked.RadioDataBytes; i++)
        {
            AppendByteLsbFirst(bits, ref position, data[i]);
        }

        foreach (byte b in parity)
        {
            AppendByteLsbFirst(bits, ref position, b);
        }

        return bits;
    }

    /// <summary>Builds the flag-delimited, bit-stuffed byte image of the frame (opening
    /// flag, stuffed frame + FCS, closing flag; final partial byte completed by the
    /// caller's flag fill).</summary>
    private static byte[] StuffToBytes(ReadOnlySpan<byte> ax25Frame)
    {
        byte[] frameBits = HdlcFramer.FrameBits(ax25Frame, openingFlags: 1, closingFlags: 1);
        var bytes = new byte[(frameBits.Length + 7) / 8 + 1];
        int bitPosition = 0;
        foreach (byte bit in frameBits)
        {
            if (bit != 0)
            {
                bytes[bitPosition >> 3] |= (byte)(1 << (bitPosition & 7)); // LSB first
            }

            bitPosition++;
        }

        // Complete any trailing partial byte with flag-pattern bits so the embedded
        // stream remains a valid HDLC idle sequence (0x7E has zeros at both ends, so no
        // seven-ones run can arise at any alignment).
        for (; (bitPosition & 7) != 0; bitPosition++)
        {
            if (((0x7E >> (bitPosition & 7)) & 1) != 0)
            {
                bytes[bitPosition >> 3] |= (byte)(1 << (bitPosition & 7));
            }
        }

        return bytes[..(bitPosition >> 3)];
    }

    private static void FillRotatingFlags(Span<byte> destination)
    {
        // Continuous 0x7E bit pattern, LSB-first within each byte — byte value is 0x7E
        // again when byte-aligned, which it is here.
        destination.Fill(0x7E);
    }

    private static void AppendByteLsbFirst(byte[] bits, ref int position, byte value)
    {
        for (int k = 0; k < 8; k++)
        {
            bits[position++] = (byte)((value >> k) & 1);
        }
    }
}
