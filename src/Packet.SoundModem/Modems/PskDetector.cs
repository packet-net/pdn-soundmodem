namespace Packet.SoundModem.Modems;

/// <summary>
/// How a PSK demodulator recovers symbols. Both decode the same (differentially-encoded IL2P)
/// wire format — this selects only the receiver's detection method.
/// </summary>
public enum PskDetector
{
    /// <summary>Coherent detection: a <see cref="CostasLoop"/> recovers the carrier's
    /// absolute phase, then consecutive absolute symbols are differentially decoded — what
    /// the NinoTNC does. Needs preamble to pull the loop in (like the NinoTNC), and pays back
    /// the differential-detection noise penalty. The default.</summary>
    Coherent,

    /// <summary>Differential detection: each symbol is multiplied by the conjugate of the
    /// one-symbol-delayed baseband, reading the phase change directly with no carrier
    /// recovery. Acquires instantly (decodes at zero preamble) at a ~1–2 dB noise cost. The
    /// opt-in for short-preamble links where acquisition speed matters more than margin.</summary>
    Differential,
}
