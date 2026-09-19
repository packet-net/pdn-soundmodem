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
/// watches while they get a station working. It used to say <c>rx[0] 42 bytes</c> - which answers
/// neither "who is that" nor "did it decode cleanly" - and said nothing at all about transmissions
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

        // Same question, the other answer: there was no CRC to check because the frame came in as
        // plain IL2P, so Reed-Solomon is the whole of what stands behind it. Said out loud
        // because the mode column next to it says -il2pc and an operator reading that line has
        // every reason to assume the frame was checked. Whether it also went to the host is the
        // second half of the fact and is worth as much: a row nobody's node ever saw is a very
        // different thing from a delivery, and nothing else in the journal says which it was.
        if (quality.PlainIl2p)
        {
            text.Append(quality.MonitorOnly
                ? "  plain il2p (rs only, not passed to host)"
                : "  plain il2p (rs only)");
        }

        // Corrections are the headroom reading: zero means the FEC was not needed, a rising count
        // means the link is being carried by it and is closer to the edge than the frame suggests.
        if (quality.CorrectedBytes is int corrected)
        {
            text.Append($"  fec {corrected}");
        }

        // The strength of the burst it rode in on - the first question of nearly every
        // receive investigation, now answered by the line itself. Band-tracker dB, not the
        // sim ladder's 3 kHz reference (see FrameQuality.SnrDb).
        if (quality.SnrDb is double snr)
        {
            text.Append($"  snr {snr:0.0} dB");
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
        // that used to raise a question instead of answering one - "(no ax25 header)" and
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
    /// <param name="trimHz">
    /// How far the burst was shifted off the nominal centre to suit the station it was addressed
    /// to; 0 for the usual case. Said in the journal as well as the panel because a station
    /// transmitting somewhere other than its own band plan is exactly the sort of thing that
    /// looks like a fault to whoever reads the log next, and a line that explains itself is
    /// cheaper than the question.
    /// </param>
    internal static string Transmitted(
        int subChannel, string mode, ReadOnlySpan<byte> frame, double trimHz = 0) =>
        $"tx[{subChannel}] {mode} {Addresses(frame)} {frame.Length} bytes"
        + (trimHz == 0 ? "" : $"  shifted {trimHz:+0.0;-0.0} Hz to suit them");

    /// <summary>A frame that never went out, and why.</summary>
    internal static string Dropped(int subChannel, ReadOnlySpan<byte> frame, Exception reason) =>
        $"tx[{subChannel}] DROPPED {Addresses(frame)} {frame.Length} bytes: {reason.Message}";

    /// <summary>
    /// A frame ARDOP decoded, on the terms ARDOP carries: its own frame type in place of a mode
    /// string, its callsigns stated rather than parsed - ARDOP frames are not AX.25, so
    /// <see cref="Ax25AddressParser"/> would print "(no ax25 header)" on every one of them - and
    /// its own quality and signal-to-noise figures rather than the packet modems' FEC count and
    /// frequency offset, which ARDOP has neither of.
    /// </summary>
    /// <remarks>
    /// Nothing ARDOP hears or sends reached the journal before this: it demodulates inside the
    /// virtual TNC, never raises the channel event <see cref="StationFactory.JournalReceivedFrames"/>
    /// listens to, and until now the frames panel and the frame log knew about a receive that
    /// journalctl said nothing about at all (issue #478).
    /// </remarks>
    /// <param name="subChannel">The sub-channel ARDOP is configured on.</param>
    /// <param name="frameName">The ARDOP frame type, e.g. <c>IDFrame</c> or <c>ConReq500M</c>.</param>
    /// <param name="from">The station stated in the frame, where it carries one.</param>
    /// <param name="to">The station it was addressed to, where the frame type carries one.</param>
    /// <param name="lengthBytes">The frame's payload length.</param>
    /// <param name="decodedOk">Whether it decoded cleanly (RS/CRC verified where the type carries
    /// them); null is not a state ARDOP reports here.</param>
    /// <param name="quality">ARDOP's own 0-100 constellation quality, measured for every frame it
    /// decodes.</param>
    /// <param name="snDb">
    /// ARDOP's own reported signal-to-noise in dB, only on the Ping and PingAck rows it is
    /// actually computed for; null on everything else, rather than the 0 dB the underlying figure
    /// carries when it was never measured (issue #479).
    /// </param>
    internal static string ArdopReceived(
        int subChannel, string frameName, string? from, string? to, int lengthBytes,
        bool? decodedOk, int quality, double? snDb)
    {
        var text = new System.Text.StringBuilder();
        text.Append($"rx[{subChannel}] ardop {frameName} ");
        text.Append(ArdopAddresses(from, to));
        text.Append($" {lengthBytes} bytes");

        if (decodedOk is bool ok)
        {
            text.Append(ok ? "  crc ok" : "  CRC BAD");
        }

        text.Append($"  q {quality}");

        if (snDb is double sn)
        {
            text.Append($"  sn {sn:+0.0;-0.0} dB");
        }

        return text.ToString();
    }

    /// <summary>A frame this station sent over ARDOP, logged once it has actually gone out - the
    /// ARDOP counterpart of <see cref="Transmitted"/>.</summary>
    /// <remarks>
    /// No quality or signal-to-noise: those are receive measurements, and ARDOP's own transmitted-
    /// frame type carries neither - a station cannot measure its own burst, the same reason the
    /// AX.25 <see cref="Transmitted"/> line carries no SNR or FEC count either.
    /// </remarks>
    internal static string ArdopTransmitted(
        int subChannel, string frameName, string? from, string? to, int lengthBytes) =>
        $"tx[{subChannel}] ardop {frameName} {ArdopAddresses(from, to)} {lengthBytes} bytes";

    /// <summary>
    /// <c>SOURCE&gt;DEST</c> as ARDOP states it rather than parses it, or a marker that is
    /// honestly not a callsign where it named neither. ARDOP's connect handshake, Ping and ID
    /// frames carry both or one of the pair in clear; a data frame belonging to someone else's
    /// session carries neither.
    /// </summary>
    private static string ArdopAddresses(string? from, string? to) =>
        from is null ? "(no callsign)" : $"{from}>{to ?? "?"}";

    /// <summary>A host attached to a KISS port.</summary>
    internal static string ClientConnected(int port, int? dedicatedSubChannel, KissClientEvent e) =>
        $"kiss[{port}] {Host(e.Remote)} connected - {Clients(e.Clients)}{Serving(dedicatedSubChannel)}";

    /// <summary>A host's KISS session ended; the reason is given where it was not a clean close.</summary>
    internal static string ClientDisconnected(int port, int? dedicatedSubChannel, KissClientEvent e) =>
        $"kiss[{port}] {Host(e.Remote)} disconnected"
        + (e.Reason is { Length: > 0 } why ? $": {why}" : "")
        + $" - {Clients(e.Clients)}{Serving(dedicatedSubChannel)}";

    /// <summary>A host's frame was longer than the port allows and was dropped, with the fix.</summary>
    internal static string FrameOversize(int port, KissOversizeEvent e) =>
        $"kiss[{port}] {Host(e.Remote)} sent a frame over {e.MaxFrameBytes} bytes; dropped. "
        + "Raise \"kissMaxFrameBytes\" in the config if the modem it is for can carry it.";

    private static string Host(System.Net.EndPoint? remote) => remote?.ToString() ?? "(unknown host)";

    private static string Clients(int count) => count == 1 ? "1 client" : $"{count} clients";

    /// <summary>Which modems that port reaches - the thing a host operator gets wrong.</summary>
    private static string Serving(int? dedicatedSubChannel) =>
        dedicatedSubChannel is int sub ? $" (modem {sub} only)" : " (all modems)";

    /// <summary>
    /// <c>SOURCE&gt;DEST</c> where the frame is AX.25, else a marker. Not every mode carries AX.25
    /// addresses - a KISS host may send anything - and printing a mangled callsign would be worse
    /// than admitting there is not one. A blank destination field prints as <c>?</c>: the source
    /// attributes the frame on its own (the PD4R-12 beacon shape).
    /// </summary>
    private static string Addresses(ReadOnlySpan<byte> frame) =>
        Ax25AddressParser.TryParse(frame, out string source, out string destination)
            ? $"{source}>{(destination.Length > 0 ? destination : "?")}"
            : "(no ax25 header)";
}
