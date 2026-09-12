using System.Text;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Display-grade AX.25 address extraction for the waterfall's per-burst attribution: source
/// and destination callsigns (with SSID) plus the digipeater path, straight off the shifted
/// address field. This is a label maker, not a protocol codec - nothing on the wire path
/// depends on it, and frames it cannot read are simply shown unattributed. (The full AX.25
/// codec lives in packet.net; the daemon deliberately keeps its shipped dependency set to
/// the M0LTE.* packages, and fourteen shifted bytes do not justify another one.)
/// </summary>
public static class Ax25AddressParser
{
    /// <summary>Parses the source and destination callsigns from a raw AX.25 frame
    /// (no flags/FCS, as delivered by the modems). Returns true when the frame yields a
    /// readable source callsign - who sent it is what attribution means - <em>and</em> the
    /// destination field either reads as a callsign or is genuinely blank; false when it
    /// does not.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="source">e.g. <c>"M0LTE-9"</c> (SSID 0 omits the suffix).</param>
    /// <param name="destination">Destination in the same form; <c>""</c> where that field is
    /// blank.</param>
    public static bool TryParse(
        ReadOnlySpan<byte> frame,
        out string source,
        out string destination)
    {
        source = "";
        destination = "";
        // Minimum frame: destination(7) + source(7) + control(1).
        if (frame.Length < 15)
        {
            return false;
        }

        // The destination has to be one of exactly two things, and telling them apart is the
        // whole reason this field is read at all.
        //
        // BLANK is a sender who left the field empty: all six callsign bytes are shifted spaces,
        // which is 0x40 on the wire (' ' << 1). PD4R-12's beacons arrive exactly like that -
        // 40 40 40 40 40 40 E0, pinned as Ax25AddressParserTests.Pd4rBeaconHex - and requiring
        // both addresses filed a perfectly readable sender under "unattributed" with both frame
        // log columns null. A blank destination costs nothing, and the source attributes the
        // frame on its own.
        //
        // CORRUPT is anything else that will not read as a callsign: GB&)>W, off the live 40 m
        // channel, where the frame it was a damaged copy of said GB7RDG. That is not an empty
        // field, it is seven bytes of header that did not survive, and the other seven are no
        // more trustworthy for having landed on plausible letters. Reed-Solomon mis-correction of
        // an IL2P header walks a callsign a character or two off a real one and hands back
        // 16WBPQ where GB7BPQ transmitted; the destination half is free corroboration that
        // catches it, and reading only the source threw that evidence away. Measured on this
        // station's own log (docs/dev/false-decodes.md): over a week, requiring this cost zero
        // labels on CRC-verified frames and withdrew one in five from the class that was
        // fabricating them.
        //
        // Do NOT simplify this back to reading the source alone. The two cases look alike in a
        // diff and are opposites on the air.
        if (!TryReadAddress(frame[..7], out destination) && !IsBlank(frame[..7]))
        {
            return false;
        }

        return TryReadAddress(frame.Slice(7, 7), out source);
    }

    /// <summary>
    /// Whether an address field is blank - every one of its six callsign bytes a shifted space -
    /// as opposed to unreadable.
    /// </summary>
    /// <remarks>
    /// Judged with the same shift <see cref="TryReadAddress"/> reads characters with, so that
    /// there is one view of what is in a byte rather than two that can disagree. The seventh byte
    /// is not looked at: it carries the SSID, the command/response bit, two reserved bits and the
    /// end-of-field bit, none of which say anything about whether a callsign is present.
    /// </remarks>
    internal static bool IsBlank(ReadOnlySpan<byte> field)
    {
        for (int n = 0; n < 6; n++)
        {
            if ((char)(field[n] >> 1) != ' ')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadAddress(ReadOnlySpan<byte> field, out string address)
    {
        address = "";
        var callsign = new StringBuilder(9);
        for (int n = 0; n < 6; n++)
        {
            char c = (char)(field[n] >> 1);
            if (c == ' ')
            {
                break;
            }

            // Callsigns are upper-case alphanumerics; anything else means these bytes are
            // not a shifted AX.25 address (an IL2P payload, a corrupt decode, not AX.25).
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return false;
            }

            callsign.Append(c);
        }

        if (callsign.Length == 0)
        {
            return false;
        }

        int ssid = (field[6] >> 1) & 0x0F;
        if (ssid != 0)
        {
            callsign.Append('-').Append(ssid);
        }

        address = callsign.ToString();
        return true;
    }
}
