using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The one-line-per-frame record of what the station heard and sent, as it appears in the journal.
/// </summary>
/// <remarks>
/// <para>This is the only view of a running station most operators ever have: the waterfall needs a
/// browser and the frame log needs SQL, but <c>journalctl -u pdn-soundmodem -f</c> is what someone
/// watches while they get a station working. It used to say <c>rx[0] 42 bytes</c> — which answers
/// neither "who is that" nor "did it decode cleanly" — and said nothing at all about transmissions
/// except when one was dropped, so a journal recorded every failure to transmit and no successes.
/// </para>
/// <para>Formatted as pure functions rather than inline interpolation so the exact text is pinned by
/// tests: these lines end up in other people's grep pipelines and bug reports, and a station's
/// output is an interface like any other.</para>
/// </remarks>
internal static class ActivityLog
{
    /// <summary>A received frame: who sent it, how it decoded, and how far off frequency it was.</summary>
    internal static string Received(int subChannel, ReadOnlySpan<byte> frame, FrameQuality quality)
    {
        var text = new System.Text.StringBuilder();
        text.Append($"rx[{subChannel}] {quality.Mode} ");
        text.Append(Addresses(frame));
        text.Append($" {quality.FrameBytes} bytes");

        // CRC first, because it is the difference between "a frame" and "probably a frame". A mode
        // with no CRC to check says nothing rather than implying a pass it never made.
        if (quality.CrcValid is bool crc)
        {
            text.Append(crc ? "  crc ok" : "  CRC BAD");
        }

        // Corrections are the headroom reading: zero means the FEC was not needed, a rising count
        // means the link is being carried by it and is closer to the edge than the frame suggests.
        if (quality.CorrectedBytes is int corrected)
        {
            text.Append($"  fec {corrected}");
        }

        if (quality.FrequencyOffsetHz is double offset)
        {
            text.Append($"  {offset:+0;-0} Hz");
        }

        if (quality.EmphasisDb is int emphasis and not 0)
        {
            text.Append($"  emph {emphasis:+0;-0} dB");
        }

        // A frame that decoded cleanly and then would not yield callsigns is the one line here
        // that used to raise a question instead of answering one — "(no ax25 header)" and
        // nothing more. It has already passed Reed-Solomon and, on an IL2P+CRC link, the CRC, so
        // the bits are right and the reading of them is not; which encapsulation carried it is
        // the first thing worth knowing, because Type 1 and Type 0 put the address field in
        // different places. Said here as well as in a survey capture, because the survey is
        // optional, budgeted, and may drop this one.
        if (Ax25AttributionNote.For(frame) is { } note)
        {
            if (quality.HeaderType is { } headerType)
            {
                text.Append($"  il2p {headerType}");
            }

            text.Append($"  [{note}]");
        }

        return text.ToString();
    }

    /// <summary>A frame this station transmitted, logged once it has actually gone out.</summary>
    internal static string Transmitted(int subChannel, string mode, ReadOnlySpan<byte> frame) =>
        $"tx[{subChannel}] {mode} {Addresses(frame)} {frame.Length} bytes";

    /// <summary>A frame that never went out, and why.</summary>
    internal static string Dropped(int subChannel, ReadOnlySpan<byte> frame, Exception reason) =>
        $"tx[{subChannel}] DROPPED {Addresses(frame)} {frame.Length} bytes: {reason.Message}";

    /// <summary>A host attached to a KISS port.</summary>
    internal static string ClientConnected(int port, int? dedicatedSubChannel, KissClientEvent e) =>
        $"kiss[{port}] {Host(e.Remote)} connected - {Clients(e.Clients)}{Serving(dedicatedSubChannel)}";

    /// <summary>A host's KISS session ended; the reason is given where it was not a clean close.</summary>
    internal static string ClientDisconnected(int port, int? dedicatedSubChannel, KissClientEvent e) =>
        $"kiss[{port}] {Host(e.Remote)} disconnected"
        + (e.Reason is { Length: > 0 } why ? $": {why}" : "")
        + $" - {Clients(e.Clients)}{Serving(dedicatedSubChannel)}";

    private static string Host(System.Net.EndPoint? remote) => remote?.ToString() ?? "(unknown host)";

    private static string Clients(int count) => count == 1 ? "1 client" : $"{count} clients";

    /// <summary>Which modems that port reaches — the thing a host operator gets wrong.</summary>
    private static string Serving(int? dedicatedSubChannel) =>
        dedicatedSubChannel is int sub ? $" (modem {sub} only)" : " (all modems)";

    /// <summary>
    /// <c>SOURCE&gt;DEST</c> where the frame is AX.25, else a marker. Not every mode carries AX.25
    /// addresses — a KISS host may send anything — and printing a mangled callsign would be worse
    /// than admitting there is not one.
    /// </summary>
    private static string Addresses(ReadOnlySpan<byte> frame) =>
        Ax25AddressParser.TryParse(frame, out string source, out string destination)
            ? $"{source}>{destination}"
            : "(no ax25 header)";
}
