using System.Text;

namespace Packet.SoundModem.Kiss;

/// <summary>
/// Reads the address field and control byte of an AX.25 frame: which station a frame was heard
/// from, which it is for next, and whether it is a UI frame or an expedited one. Shared by
/// <see cref="PolyglotRouter"/> and anything else that must read a header the same way.
/// </summary>
/// <remarks>
/// A header that does not read (too short, an address that is not a callsign, no control byte
/// after the last address) yields null or false from every member, never a guess.
/// </remarks>
internal static class Ax25Header
{
    /// <summary>Most addresses an AX.25 header carries: destination, source, eight digipeaters.</summary>
    private const int MaxAddresses = 10;

    /// <summary>The station whose transmitter a received frame came from, or null when the
    /// header does not read.</summary>
    internal static string? HeardStation(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count))
        {
            return null;
        }

        // Latest repeater first: the last digipeater with H set is the one on the air.
        for (int n = count - 1; n >= 2; n--)
        {
            if ((frame[(n * 7) + 6] & 0x80) != 0)
            {
                return ReadStation(frame.Slice(n * 7, 7));
            }
        }

        return ReadStation(frame.Slice(7, 7));
    }

    /// <summary>The station a frame to be sent must reach first, or null when the header does
    /// not read.</summary>
    internal static string? NextHop(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count))
        {
            return null;
        }

        for (int n = 2; n < count; n++)
        {
            if ((frame[(n * 7) + 6] & 0x80) == 0)
            {
                return ReadStation(frame.Slice(n * 7, 7));
            }
        }

        return ReadStation(frame[..7]);
    }

    /// <summary>True for a UI frame (control 0x03, P/F masked) with no digipeater still to repeat
    /// it, so it is for everyone. False for any other frame, a UI frame with a digipeater still
    /// to go, and a header that does not read.</summary>
    internal static bool IsUiWithNoDigipeaterPending(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count) || (frame[count * 7] & 0xEF) != 0x03)
        {
            return false;
        }

        for (int n = 2; n < count; n++)
        {
            if ((frame[(n * 7) + 6] & 0x80) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for a frame that should not queue behind data: every supervisory frame (RR,
    /// RNR, REJ, SREJ, at modulo 8 or 128) and the unnumbered UA and DM (P/F masked). False for
    /// everything else (I, UI, SABM, SABME, DISC, FRMR, XID, TEST) and for a header that does
    /// not read.</summary>
    internal static bool IsExpedited(ReadOnlySpan<byte> frame)
    {
        if (!TryReadPath(frame, out int count))
        {
            return false;
        }

        byte control = frame[count * 7];
        if ((control & 0x03) == 0x01)
        {
            return true;
        }

        return (control & 0xEF) is 0x63 or 0x0F;
    }

    /// <summary>Counts the address fields: they run until one has the extension bit set. False
    /// for anything that cannot be an AX.25 header, and when any address in it does not read as a
    /// callsign, since a header with one damaged address says nothing trustworthy about any. On
    /// success the control byte is at <c>count * 7</c>.</summary>
    internal static bool TryReadPath(ReadOnlySpan<byte> frame, out int count)
    {
        count = 0;
        while (count < MaxAddresses && (count + 1) * 7 <= frame.Length)
        {
            ReadOnlySpan<byte> field = frame.Slice(count * 7, 7);
            if (ReadStation(field) is null)
            {
                return false;
            }

            count++;
            if ((field[6] & 0x01) != 0)
            {
                // A control byte must follow the last address.
                return count >= 2 && count * 7 < frame.Length;
            }
        }

        return false;
    }

    /// <summary>One address field as text: upper case <c>CALL-SSID</c>, SSID 0 written bare. Null
    /// when the field is not a callsign.</summary>
    internal static string? ReadStation(ReadOnlySpan<byte> field)
    {
        var callsign = new StringBuilder(9);
        bool ended = false;
        for (int n = 0; n < 6; n++)
        {
            char c = (char)(field[n] >> 1);
            if (c == ' ')
            {
                ended = true;
                continue;
            }

            // Letters and digits only, and no letter after the padding starts.
            if (ended || c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return null;
            }

            callsign.Append(c);
        }

        if (callsign.Length == 0)
        {
            return null;
        }

        int ssid = (field[6] >> 1) & 0x0F;
        return ssid == 0 ? callsign.ToString() : callsign.Append('-').Append(ssid).ToString();
    }
}
