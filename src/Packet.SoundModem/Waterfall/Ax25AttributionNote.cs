namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Says, in a line, why a decoded frame yielded no callsigns.
/// </summary>
/// <remarks>
/// <para>
/// A frame reaching the panel as "unattributed" has already passed Reed-Solomon and, on an
/// IL2P+CRC link, the trailing CRC — the bits are right and it is the <em>reading</em> of them
/// that failed. That distinction is the whole diagnosis, and until now the only way to get at it
/// was to pull the payload blob out of the frame log by hand and stare at it. A station that
/// notices something it cannot explain should write down what it noticed.
/// </para>
/// <para>
/// Deliberately a diagnostic and not a parser: <see cref="Ax25AddressParser"/> decides
/// whether a frame is attributable and nothing here changes that verdict. This only explains it.
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

        // Every character is legal but the parse still failed — the remaining way that happens is
        // a field that is all spaces, i.e. an empty callsign.
        return "the address field holds legal characters but an empty callsign";
    }
}
