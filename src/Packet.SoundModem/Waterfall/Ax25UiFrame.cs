namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Builds the one AX.25 frame shape everything here ever needs to make: a two-address UI
/// frame (control 0x03, PID 0xF0). The companion to <see cref="Ax25AddressParser"/> and
/// held to the same standard: probe/test-grade, not a protocol codec - no digipeater path,
/// no I/S frames, no PID choices. (The full AX.25 codec lives in packet.net; see the
/// parser's remarks for why the daemon does not take that dependency.) Before this, the
/// fourteen shifted address bytes were hand-rolled at every site that needed a frame - the
/// band probe, both bench tools, and half a dozen test fixtures - each a fresh chance to
/// get a shift or a reserved bit wrong.
/// </summary>
public static class Ax25UiFrame
{
    /// <summary>
    /// A UI frame <c>source&gt;destination</c> carrying <paramref name="payload"/>.
    /// Callsigns take the same <c>CALL</c> or <c>CALL-SSID</c> forms the parser produces,
    /// so a built frame round-trips through <see cref="Ax25AddressParser.TryParse"/>.
    /// Command framing per convention: the destination's C bit set, the source's clear.
    /// </summary>
    /// <param name="source">Sending callsign, up to 6 characters plus an optional -SSID (0-15).</param>
    /// <param name="destination">Destination callsign in the same form.</param>
    /// <param name="payload">Information field, appended after control and PID.</param>
    public static byte[] Build(string source, string destination, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var frame = new byte[16 + payload.Length];
        WriteAddress(frame.AsSpan(0, 7), destination, command: true, last: false);
        WriteAddress(frame.AsSpan(7, 7), source, command: false, last: true);
        frame[14] = 0x03;   // UI
        frame[15] = 0xF0;   // no layer 3
        payload.CopyTo(frame.AsSpan(16));
        return frame;
    }

    private static void WriteAddress(Span<byte> field, string address, bool command, bool last)
    {
        int dash = address.IndexOf('-');
        ReadOnlySpan<char> call = dash < 0 ? address : address.AsSpan(0, dash);
        int ssid = dash < 0 ? 0 : int.Parse(address.AsSpan(dash + 1));
        if (call.Length is 0 or > 6 || ssid is < 0 or > 15)
        {
            throw new ArgumentException($"'{address}' is not CALL or CALL-SSID (SSID 0-15)");
        }

        for (int i = 0; i < 6; i++)
        {
            field[i] = (byte)((i < call.Length ? char.ToUpperInvariant(call[i]) : ' ') << 1);
        }

        field[6] = (byte)(((command ? 1 : 0) << 7) | 0x60 | (ssid << 1) | (last ? 1 : 0));
    }
}
