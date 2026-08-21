namespace Packet.SoundModem.Modems;

/// <summary>
/// Optional, mode-specific knobs for <see cref="ModemCatalog.Create"/>. Every field left
/// <see langword="null"/> selects that mode's documented default - the same defaults the
/// daemon and every other consumer get - so <c>default(ModemOptions)</c> is always valid.
/// </summary>
/// <param name="CentreFrequencyHz">Audio-centre frequency for the variable-centre families
/// (afsk tone-pair, bpsk/qpsk carrier). Must be <see langword="null"/> for the fixed-centre
/// modes (fsk*/c4fsk*/freedv-*/ms110d-*); supplying one for those throws - see
/// <see cref="ModemCatalog.AcceptsCentreFrequency"/>.</param>
/// <param name="OffsetPairs">Frequency-diversity bank width for the <c>bpsk*</c>, <c>qpsk*</c>
/// and <c>afsk300*</c> modes: <c>2·OffsetPairs+1</c> stepped branches (0 collapses to a plain
/// single modem). Ignored by non-bank modes. Null ⇒ 4 (bpsk, qpsk600/2400) / 0 (qpsk3600, an
/// FM mode whose audio tones arrive on frequency) / 5 (afsk300).</param>
/// <param name="OffsetStepHz">Hz step between diversity branches for the <c>bpsk*</c>,
/// <c>qpsk*</c> and <c>afsk300*</c> modes. Null ⇒ the mode's default (psk baud/40; afsk300
/// 35 Hz).</param>
/// <param name="Detector">PSK detection method for <c>bpsk*</c>/<c>qpsk*</c>. Null ⇒ the
/// per-family default from <see cref="ModemCatalog.DefaultDetectorFor"/> (differential for
/// both families).</param>
/// <param name="AcceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC, to
/// the modem's frame sink - for a neighbour (a BPQ32 node, say) that sends the CRC-less variant.
/// This decides <em>delivery</em>, not decoding: an IL2P+CRC mode reads such frames whatever this
/// says, and reports every one of them through <see cref="IModem.FrameDecoded"/> marked
/// <see cref="FrameQuality.PlainIl2p"/>, so a display shows the neighbour either way. Null ⇒
/// false, which leaves them <see cref="FrameQuality.MonitorOnly"/>: seen, and not handed to a
/// host that asked for IL2P+CRC. Only the IL2P+CRC modes have anything to release, so
/// <see langword="true"/> for any other mode throws - see
/// <see cref="ModemCatalog.RunsIl2pCrc"/> and, for what the tolerance costs,
/// <see cref="Il2pReceiver"/>.</param>
/// <param name="SecondDetector">Ensemble decode-any for the <c>bpsk*</c> and <c>qpsk*</c>
/// banks (rx-roadmap workstream 2): every diversity position gets a second branch under this
/// detector and the bank delivers the union of what both decode, at double the bank CPU.
/// Only the PSK banks have the machinery; supplying it for any other mode throws, so a
/// measurement that asks for an ensemble can never silently not get one (and the MLSE stage
/// is BPSK-only, so a qpsk bank refuses that one). Null ⇒ the single-detector bank.</param>
public readonly record struct ModemOptions(
    double? CentreFrequencyHz = null,
    int? OffsetPairs = null,
    double? OffsetStepHz = null,
    PskDetector? Detector = null,
    bool? AcceptPlainIl2p = null,
    PskDetector? SecondDetector = null);
