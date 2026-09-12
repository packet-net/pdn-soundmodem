namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Says, in a line, why a decoded frame carries no callsigns on the row an operator is looking at.
/// </summary>
/// <remarks>
/// <para>
/// A frame reaching the panel as "unattributed" has already passed Reed-Solomon and, on an
/// IL2P+CRC link, the trailing CRC - the bits are right and it is the <em>reading</em> of them
/// that failed. That distinction is the whole diagnosis, and until now the only way to get at it
/// was to pull the payload blob out of the frame log by hand and stare at it. A station that
/// notices something it cannot explain should write down what it noticed.
/// </para>
/// <para>
/// Deliberately a diagnostic and not a parser: <see cref="Ax25AddressParser"/> decides whether a
/// frame's bytes yield callsigns and <see cref="Modems.DecodeStanding"/> decides whether the
/// decode behind them supports naming a station. Nothing here changes either verdict. This only
/// explains them.
/// </para>
/// </remarks>
public static class Ax25AttributionNote
{
    /// <summary>The AX.25 address field: destination (7) + source (7).</summary>
    private const int AddressFieldLength = 14;

    /// <summary>
    /// A short explanation of why <paramref name="frame"/> has no readable addresses, or null
    /// when it has.
    /// </summary>
    public static string? For(ReadOnlySpan<byte> frame)
    {
        if (Ax25AddressParser.TryParse(frame, out _, out _))
        {
            return null;
        }

        if (frame.Length < AddressFieldLength + 1)
        {
            return $"{frame.Length} bytes - shorter than an AX.25 address field and control byte "
                + $"({AddressFieldLength + 1})";
        }

        for (int at = 0; at < AddressFieldLength; at++)
        {
            if (at % 7 == 6)
            {
                continue;   // the SSID byte, which carries no callsign character
            }

            char c = (char)(frame[at] >> 1);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ')
            {
                continue;
            }

            string field = at < 7 ? "destination" : "source";
            string shown = c is >= ' ' and <= '~' ? $" ('{c}')" : "";
            return $"byte {at} of the {field} callsign is 0x{frame[at]:X2} -> 0x{frame[at] >> 1:X2}"
                + $"{shown}, not a shifted callsign character";
        }

        // Every character is legal and the parse still failed, which leaves two shapes, and they
        // are not the same fault. A destination that begins with a space and then carries
        // characters is neither a callsign nor the blank field a beacon leaves, so the header did
        // not survive and the source half is not trustworthy either (see Ax25AddressParser).
        if (!Ax25AddressParser.IsBlank(frame[..7]))
        {
            return "the destination field is neither a callsign nor blank, so the address field "
                + "did not survive";
        }

        // And the other one: a source field that is all spaces, i.e. an empty source callsign. (A
        // blank destination alone does not unattribute a frame; a corrupt one does.)
        return "the address field holds legal characters but an empty source callsign";
    }

    /// <summary>
    /// The same, for a frame whose decode is also in question: why the row carries no callsigns,
    /// whether that is because the bytes would not read or because nothing checked the bytes that
    /// did.
    /// </summary>
    /// <remarks>
    /// The second case is the one this exists for. A frame read on Reed-Solomon alone and reached
    /// only after chase decoding flipped bits has nothing standing behind the header it names, and
    /// three quarters of that class on the live 40 m slot were payloads no station ever sent
    /// (docs/dev/false-decodes.md). Such a row is listed, badged and logged exactly as before and
    /// simply does not claim a station - and this says so, because "unattributed" on a frame whose
    /// address field read perfectly well would otherwise send somebody looking for a parse bug.
    /// </remarks>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="quality">What the decode of it established.</param>
    public static string? For(ReadOnlySpan<byte> frame, in Modems.FrameQuality quality)
    {
        if (quality.CallsignWorthShowing || !Ax25AddressParser.TryParse(frame, out _, out _))
        {
            return For(frame);
        }

        string chased = quality.ChasedBits is int bits
            ? $"chase decoding moved {bits} bit{(bits == 1 ? "" : "s")} to reach it"
            : "chase decoding moved bits to reach it";
        return $"callsign withheld: Reed-Solomon alone stood behind this reading and {chased}, "
            + "so nothing checked the header it names";
    }
}
