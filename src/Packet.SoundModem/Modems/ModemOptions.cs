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
/// <param name="OffsetPairs">Frequency-diversity bank width for the <c>bpsk*</c> and
/// <c>afsk300*</c> modes: <c>2·OffsetPairs+1</c> stepped branches (0 collapses to a plain
/// single modem). Ignored by non-bank modes. Null ⇒ 4 (bpsk) / 5 (afsk300).</param>
/// <param name="OffsetStepHz">Hz step between diversity branches for the <c>bpsk*</c> and
/// <c>afsk300*</c> modes. Null ⇒ the mode's default (bpsk baud/40; afsk300 35 Hz).</param>
/// <param name="Detector">PSK detection method for <c>bpsk*</c>/<c>qpsk*</c>. Null ⇒ the
/// per-family default from <see cref="ModemCatalog.DefaultDetectorFor"/> (BPSK differential,
/// QPSK coherent).</param>
/// <param name="AcceptPlainIl2p">Also deliver frames that arrive as plain IL2P, with no trailing
/// CRC, on a mode whose link runs IL2P+CRC - for a neighbour (a BPQ32 node, say) that sends the
/// CRC-less variant. Null ⇒ false, the interop ground truth: IL2P+CRC modes accept IL2P+CRC and
/// nothing else. Only the IL2P+CRC modes have anything to switch on, so <see langword="true"/>
/// for any other mode throws - see <see cref="ModemCatalog.RunsIl2pCrc"/> and, for what the
/// tolerance costs, <see cref="Il2pReceiver"/>.</param>
public readonly record struct ModemOptions(
    double? CentreFrequencyHz = null,
    int? OffsetPairs = null,
    double? OffsetStepHz = null,
    PskDetector? Detector = null,
    bool? AcceptPlainIl2p = null);
