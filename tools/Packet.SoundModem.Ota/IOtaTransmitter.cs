namespace Packet.SoundModem.Ota;

/// <summary>
/// The transmit-and-identify surface a ladder pass drives, common to both routes -
/// <see cref="FlexIqTransmitter"/> (waveform IQ, the bench/instrument path) and
/// <see cref="FlexDaxTransmitter"/> (DAX audio, the deployment path). The route selector picks
/// one at bring-up and the pass loop holds it through this seam, so the capture/manifest
/// orchestration is written once and is identical whichever route transmitted.
/// </summary>
/// <remarks>The <em>payload</em> differs by route - interleaved complex IQ on the waveform path,
/// mono real audio on the DAX path - so the per-point payload preparation is still route-specific
/// in the caller. Everything after that (preflight, identification, keyed transmission and the
/// safety envelope) is the same shape, which is what this interface captures.</remarks>
internal interface IOtaTransmitter : IAsyncDisposable
{
    /// <summary>Transmits one modulated burst (IQ on the waveform route, mono audio on DAX) as a
    /// single keyed transmission with the meter interlock live throughout.</summary>
    Task<TransmitReport> TransmitAsync(float[] samples, CancellationToken cancellation = default);

    /// <summary>Pre-flight: a short constant-envelope tone while watching SWR, the session's SWR
    /// measurement and the check that stands between a mis-cabled rig and full power.</summary>
    Task<TransmitReport> PreflightAsync(
        double seconds = 2.0, double toneHz = 1000, double amplitude = 0.9,
        bool requireSwrReading = true, CancellationToken cancellation = default);

    /// <summary>Identifies if one is owed - a no-op almost always, and the one time it is not is
    /// the time it would otherwise have been forgotten.</summary>
    Task EnsureIdentifiedAsync(CancellationToken cancellation = default);

    /// <summary>Whether an identification is owed right now, so a pass can put it in a gap rather
    /// than discover it mid-burst.</summary>
    bool IdentificationDue { get; }
}
