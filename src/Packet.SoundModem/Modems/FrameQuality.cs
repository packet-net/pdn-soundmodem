namespace Packet.SoundModem.Modems;

/// <summary>
/// Per-frame receive diagnostics, delivered alongside every decoded frame via
/// <see cref="IModem.FrameDecoded"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately named for what is measured, not "BER": true bit-error rate is not
/// observable from a receiver. Errors inside a Reed-Solomon-corrected byte are invisible
/// (one flipped bit and eight flipped bits both cost one corrected symbol), and frames
/// with more damage than the code can repair never decode at all, so they report nothing.
/// What IS honest per frame: how many bytes FEC repaired. <see cref="CorrectedBytes"/>
/// over the frame's on-air length is a floor on the channel's byte error rate - zero on a
/// clean link, and any persistent non-zero value is a link that is quietly consuming its
/// error budget and will start dropping frames when conditions worsen. That early-warning
/// property is the operational point of surfacing this.
/// </para>
/// <para>
/// The NinoTNC exposes the same idea in aggregate (its GETALL counters for corrected vs
/// uncorrectable IL2P receives); this is the per-frame version.
/// </para>
/// </remarks>
/// <param name="Mode">The mode (and for multi-decoder banks, the branch) that decoded the
/// frame, e.g. <c>"qpsk2400-il2pc"</c> or <c>"afsk1200@+30Hz+6dB"</c>.</param>
/// <param name="FrameBytes">Decoded AX.25 frame length in bytes.</param>
/// <param name="CorrectedBytes">Bytes repaired by forward error correction (Reed-Solomon,
/// IL2P and FX.25 framings). <c>null</c> for unprotected framings (classic HDLC), where no
/// error count exists - an FCS pass only proves zero residual errors.</param>
/// <param name="CrcValid">IL2P trailing-CRC state: true/false when the link runs IL2P+CRC,
/// <c>null</c> where the framing carries no trailer (plain IL2P, HDLC, FX.25).</param>
/// <param name="FrequencyOffsetHz">For multi-decoder banks, the frequency offset of the
/// branch that decoded the frame; <c>null</c> for single decoders. A persistent non-zero
/// value means the far station is off-frequency by about that much.</param>
/// <param name="EmphasisDb">For multi-decoder banks, the input pre-emphasis (dB/octave) of
/// the winning branch; <c>null</c> for single decoders. Persistent non-zero = the far
/// station's TX audio is twisted.</param>
/// <param name="HeaderType">Which IL2P encapsulation the frame arrived in - Type 1 translated
/// (the AX.25 header compressed into IL2P's own) or Type 0 transparent (the whole AX.25 frame
/// in the payload); <c>null</c> for framings that are not IL2P. Surfaced because it is the
/// first question worth asking about a frame that decoded cleanly and then would not yield
/// callsigns: the two types put the address field in different places, so which one it was
/// decides whether the payload is unusual or the decode is.</param>
/// <param name="PlainIl2p">The frame was read as plain IL2P, with no trailing CRC behind it: it
/// is standing on Reed-Solomon alone. A fact about the <em>decode</em>, and the one a display
/// should badge - <see cref="CrcValid"/> is <c>null</c> here, but it is also null for HDLC and
/// FX.25 and for every frame of a mode that has no CRC to check, so "no CRC was checked" and
/// "no CRC existed" are not the same question and one flag cannot answer both. True both for a
/// frame the second plain reading of an IL2P+CRC link produced (see
/// <see cref="Il2pReceiver"/>) and for every frame of a link that runs plain IL2P as its own
/// framing, because the guarantee behind them is identical.</param>
/// <param name="MonitorOnly">The frame was <b>not</b> passed to the host: it reached
/// <see cref="IModem.FrameDecoded"/> and everything hanging off it - display, frame log,
/// journal, survey - but never the modem's constructor frame sink. A fact about what
/// <em>happened to</em> the frame rather than about the decode, which is why it is separate
/// from <see cref="PlainIl2p"/>. Set for a plain IL2P frame read by an IL2P+CRC link that was
/// not told to accept them (the default): the operator wants to see such a frame without
/// handing an RS-only frame to a host that asked for IL2P+CRC. Anything relaying frames onward
/// - the KISS server's quality sidecar included - must skip these, or it reports a frame its
/// peer never received.</param>
public readonly record struct FrameQuality(
    string Mode,
    int FrameBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? FrequencyOffsetHz = null,
    int? EmphasisDb = null,
    M0LTE.Il2p.Il2pHeaderType? HeaderType = null,
    bool PlainIl2p = false,
    bool MonitorOnly = false);
