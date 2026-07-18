namespace Packet.SoundModem.Modems;

/// <summary>
/// How a PSK demodulator recovers symbols. Both decode the same (differentially-encoded IL2P)
/// wire format — this selects only the receiver's detection method.
/// </summary>
public enum PskDetector
{
    /// <summary>Coherent detection: a <see cref="CostasLoop"/> recovers the carrier's
    /// absolute phase, then consecutive absolute symbols are differentially decoded — what
    /// the NinoTNC does. Its narrow tracking loop earns a small noise-margin edge but only
    /// pulls in a few Hz of carrier offset within a real signal's short (~150 ms) preamble;
    /// the opt-in for strong, on-frequency links. See <see cref="BpskMultiModem"/> to acquire
    /// off-frequency carriers coherently with a bank of stepped branches.</summary>
    Coherent,

    /// <summary>Differential detection: each symbol is multiplied by the conjugate of the
    /// one-symbol-delayed baseband, reading the phase change directly with no carrier
    /// recovery. Acquires instantly (decodes at zero preamble) and tolerates tens of Hz of
    /// carrier offset for a ~1–2 dB noise cost. <b>The default</b>: on real off-air HF traffic
    /// (measured against a NinoTNC on the GB7RDG 40 m channel) it matches and occasionally
    /// beats coherent, because real carriers arrive off-frequency with short preambles.</summary>
    Differential,
}
