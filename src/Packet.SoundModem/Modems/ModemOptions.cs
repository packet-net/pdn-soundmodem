namespace Packet.SoundModem.Modems;

/// <summary>
/// Optional, mode-specific knobs for <see cref="ModemCatalog.Create"/>. Every field left
/// <see langword="null"/> selects that mode's documented default — the same defaults the
/// daemon and every other consumer get — so <c>default(ModemOptions)</c> is always valid.
/// </summary>
/// <param name="CentreFrequencyHz">Audio-centre frequency for the variable-centre families
/// (afsk tone-pair, bpsk/qpsk carrier). Must be <see langword="null"/> for the fixed-centre
/// modes (fsk*/c4fsk*/freedv-*/ms110d-*); supplying one for those throws — see
/// <see cref="ModemCatalog.AcceptsCentreFrequency"/>.</param>
/// <param name="OffsetPairs">Frequency-diversity bank width for the <c>bpsk*</c> modes:
/// <c>2·OffsetPairs+1</c> stepped branches (0 collapses to a plain single modem). Ignored by
/// non-bank modes. Null ⇒ 4.</param>
/// <param name="OffsetStepHz">Hz step between diversity branches for the <c>bpsk*</c> modes.
/// Null ⇒ the mode's baud-derived default (baud/40).</param>
/// <param name="Detector">PSK detection method for <c>bpsk*</c>/<c>qpsk*</c>. Null ⇒ the
/// per-family default from <see cref="ModemCatalog.DefaultDetectorFor"/> (BPSK differential,
/// QPSK coherent).</param>
public readonly record struct ModemOptions(
    double? CentreFrequencyHz = null,
    int? OffsetPairs = null,
    double? OffsetStepHz = null,
    PskDetector? Detector = null);
