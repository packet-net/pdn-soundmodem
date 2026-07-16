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
/// over the frame's on-air length is a floor on the channel's byte error rate — zero on a
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
/// error count exists — an FCS pass only proves zero residual errors.</param>
/// <param name="CrcValid">IL2P trailing-CRC state: true/false when the link runs IL2P+CRC,
/// <c>null</c> where the framing carries no trailer (plain IL2P, HDLC, FX.25).</param>
/// <param name="FrequencyOffsetHz">For multi-decoder banks, the frequency offset of the
/// branch that decoded the frame; <c>null</c> for single decoders. A persistent non-zero
/// value means the far station is off-frequency by about that much.</param>
/// <param name="EmphasisDb">For multi-decoder banks, the input pre-emphasis (dB/octave) of
/// the winning branch; <c>null</c> for single decoders. Persistent non-zero = the far
/// station's TX audio is twisted.</param>
public readonly record struct FrameQuality(
    string Mode,
    int FrameBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? FrequencyOffsetHz = null,
    int? EmphasisDb = null);
