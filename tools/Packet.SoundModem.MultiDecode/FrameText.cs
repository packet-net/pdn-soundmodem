using System.Text;

namespace Packet.SoundModem.MultiDecode;

/// <summary>
/// Renders a decoded frame for a person to read: the canonical hex-and-ASCII dump, and an AX.25
/// header line when the bytes are shaped like one.
/// </summary>
/// <remarks>
/// The header decode is best-effort and says so. A frame reaching here has already passed an FCS
/// or a Reed-Solomon check, so its bytes are almost certainly what was sent - but "what was sent"
/// is not always AX.25. An IL2P Type 0 frame carries an opaque payload, and a false HDLC opening
/// on noise can produce a short well-formed-by-luck frame. Both would decode into nonsense
/// callsigns if we insisted, so the address field is validated (shifted ASCII, plausible
/// characters) and the line is simply omitted when it does not hold up. The hex dump is always
/// printed, because it is the thing that is definitely true.
/// </remarks>
internal static class FrameText
{
    /// <summary>Two addresses plus a control byte: the shortest thing that can be AX.25.</summary>
    private const int MinimumAx25Length = 15;

    /// <summary>AX.25 2.2 allows two, and a frame claiming more is being read wrong.</summary>
    private const int MaxDigipeaters = 8;

    /// <summary>
    /// A one-line AX.25 header summary (<c>SRC&gt;DEST via A,B  UI  pid=F0</c>), or null when the
    /// frame is not AX.25-shaped.
    /// </summary>
    public static string? Ax25Header(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < MinimumAx25Length)
        {
            return null;
        }

        if (!TryCall(frame[..7], out string destination) || !TryCall(frame.Slice(7, 7), out string source))
        {
            return null;
        }

        var digipeaters = new List<string>();
        int position = 14;
        while ((frame[position - 1] & 0x01) == 0)
        {
            if (position + 7 > frame.Length || digipeaters.Count >= MaxDigipeaters)
            {
                return null; // address field never terminated: not an AX.25 frame after all
            }

            if (!TryCall(frame.Slice(position, 7), out string digipeater))
            {
                return null;
            }

            // Bit 7 of the SSID byte is the has-been-repeated flag; a '*' is how every monitor
            // since the 1980s shows which digipeater actually handled the frame.
            digipeaters.Add((frame[position + 6] & 0x80) != 0 ? digipeater + "*" : digipeater);
            position += 7;
        }

        if (position >= frame.Length)
        {
            return null; // addresses but no control byte
        }

        byte control = frame[position];
        string kind = ControlName(control, out bool carriesPid);
        string via = digipeaters.Count > 0 ? " via " + string.Join(',', digipeaters) : "";
        string pid = carriesPid && position + 1 < frame.Length
            ? $"  pid={frame[position + 1]:X2}"
            : "";

        return $"{source}>{destination}{via}  {kind}{pid}";
    }

    /// <summary>
    /// The frame's information field, if it has one, as printable text with control bytes shown as
    /// '.'. Null when the frame is not AX.25-shaped or carries no payload.
    /// </summary>
    public static string? InfoField(ReadOnlySpan<byte> frame)
    {
        if (Ax25Header(frame) is null)
        {
            return null;
        }

        int position = 14;
        while ((frame[position - 1] & 0x01) == 0)
        {
            position += 7;
        }

        ControlName(frame[position], out bool carriesPid);
        int start = position + (carriesPid ? 2 : 1);
        return start < frame.Length ? Printable(frame[start..]) : null;
    }

    /// <summary>Canonical hex dump, <c>hexdump -C</c> layout, indented by
    /// <paramref name="indent"/> spaces.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, int indent)
    {
        var text = new StringBuilder();
        string pad = new(' ', indent);

        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int length = Math.Min(16, data.Length - offset);
            ReadOnlySpan<byte> row = data.Slice(offset, length);

            text.Append(pad).Append(CultureInfoFree(offset)).Append(' ');
            for (int i = 0; i < 16; i++)
            {
                // The gap after eight bytes is what makes a long dump countable by eye.
                text.Append(i == 8 ? "  " : " ");
                text.Append(i < length ? row[i].ToString("x2") : "  ");
            }

            text.Append("  |").Append(Printable(row)).Append("|\n");
        }

        return text.ToString();
    }

    /// <summary>Bytes as text, with anything outside printable ASCII shown as '.'. Deliberately
    /// not Latin-1: this output goes to a terminal whose locale is not ours to assume, and the
    /// high bytes of an AX.25 payload are as often a decode artefact as they are accented text.
    /// The hex dump beside it is where the real byte values live.</summary>
    public static string Printable(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(data.Length);
        foreach (byte value in data)
        {
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
        }

        return text.ToString();
    }

    private static string CultureInfoFree(int offset) => offset.ToString("x4");

    /// <summary>Reads one shifted-ASCII AX.25 address, rejecting anything that is not one.</summary>
    private static bool TryCall(ReadOnlySpan<byte> address, out string call)
    {
        call = "";
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            // The low bit of every callsign byte is reserved and clear; a set one means we are
            // not looking at an address field.
            if ((address[i] & 0x01) != 0)
            {
                return false;
            }

            char c = (char)(address[i] >> 1);
            if (c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not ' ')
            {
                return false;
            }

            chars[i] = c;
        }

        string trimmed = new string(chars).TrimEnd();
        if (trimmed.Length == 0 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return false; // padding is trailing only; an interior space is not a callsign
        }

        int ssid = (address[6] >> 1) & 0x0F;
        call = ssid == 0 ? trimmed : $"{trimmed}-{ssid}";
        return true;
    }

    /// <summary>
    /// Names an AX.25 control byte. Modulo 8 is assumed: the frame arrived on its own with no
    /// link state behind it, and modulo 128 is not detectable from a single frame.
    /// </summary>
    private static string ControlName(byte control, out bool carriesPid)
    {
        carriesPid = false;

        if ((control & 0x01) == 0)
        {
            carriesPid = true;
            return $"I ns={(control >> 1) & 0x07} nr={(control >> 5) & 0x07}{PollFinal(control, 4)}";
        }

        if ((control & 0x03) == 0x01)
        {
            string name = ((control >> 2) & 0x03) switch
            {
                0 => "RR",
                1 => "RNR",
                2 => "REJ",
                _ => "SREJ",
            };
            return $"{name} nr={(control >> 5) & 0x07}{PollFinal(control, 4)}";
        }

        // U frame: the type lives in the bits either side of the P/F bit.
        byte type = (byte)(control & 0xEC);
        string unnumbered = type switch
        {
            0x6C => "SABME",
            0x2C => "SABM",
            0x40 => "DISC",
            0x0C => "DM",
            0x60 => "UA",
            0x84 => "FRMR",
            0x00 => "UI",
            0xAC => "XID",
            0xE0 => "TEST",
            _ => $"U({control:X2})",
        };

        carriesPid = unnumbered is "UI";
        return unnumbered + PollFinal(control, 4);
    }

    private static string PollFinal(byte control, int bit) =>
        ((control >> bit) & 1) != 0 ? " P/F" : "";
}
