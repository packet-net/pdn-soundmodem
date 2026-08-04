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
    /// (no flags/FCS, as delivered by the modems). Returns false when the bytes do not
    /// look like an AX.25 address field.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="source">e.g. <c>"M0LTE-9"</c> (SSID 0 omits the suffix).</param>
    /// <param name="destination">Destination in the same form.</param>
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

        return TryReadAddress(frame[..7], out destination)
            && TryReadAddress(frame.Slice(7, 7), out source);
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
