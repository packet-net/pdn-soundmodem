namespace Packet.SoundModem.Ota;

/// <summary>One FM burst a pass transmitted — enough to regenerate its payload and channel and know
/// where it landed in the capture.</summary>
/// <param name="Mode">The FM-native <see cref="Packet.SoundModem.Modems.ModemCatalog"/> mode.</param>
/// <param name="Seed">Payload seed — and, offset, the channel realisation, so this row alone
/// reproduces the exact transmission.</param>
/// <param name="SnrDb">SNR the rig was asked to inject.</param>
/// <param name="Channel">Injected channel geometry.</param>
/// <param name="FrameBytes">AX.25 payload size, so the scorer regenerates the exact sent frame.</param>
/// <param name="StartSeconds">Where the active burst begins in the capture — after its noise
/// lead-in, derived from the actual key-up time (live) or the layout (rehearsal).</param>
/// <param name="BurstSeconds">Active modulated-burst length, the region the scorer measures signal
/// power over.</param>
internal sealed record FmCampaignBurst(
    string Mode,
    int Seed,
    double SnrDb,
    SimChannelKind Channel,
    int FrameBytes,
    double StartSeconds,
    double BurstSeconds);

/// <summary>
/// What an FM OTA pass did: everything the FM scorer needs to score the capture and everything
/// needed to interpret the numbers later — the FM counterpart of <see cref="OfdmCampaignManifest"/>.
/// </summary>
/// <param name="Name">Short label; ends up in filenames and the evidence log.</param>
/// <param name="Mode">The pass mode.</param>
/// <param name="DspRate">The mode's DSP audio rate — the rate the discriminator recovers to.</param>
/// <param name="TargetDeviationHz">The mode's target peak FM deviation; the drive is calibrated to it.</param>
/// <param name="OffsetHz">The FM carrier's offset within the capture baseband — the discriminator's
/// down-shift (0 when the RX is tuned to the carrier).</param>
/// <param name="CaptureRate">IQ sample rate of the capture the positions index into.</param>
/// <param name="Bursts">In transmit order.</param>
/// <param name="ModemRevision">Repository revision of the binary that ran.</param>
/// <param name="WrittenUtc">When this manifest was written.</param>
/// <param name="Radio">Radio address, or "none (rehearsal)".</param>
/// <param name="FrequencyMHz">Slice centre.</param>
/// <param name="RfPower">Radio power setting, or null when nothing was transmitted.</param>
/// <param name="PassAudioGain">The single DAX FM audio drive applied across the pass.</param>
/// <param name="MeasuredPeakDeviationHz">The peak deviation the calibration measured before the pass,
/// or null when it was not measured (a dry-run, or an uncalibrated pass).</param>
/// <param name="DialCorrectionHz">The session's measured dial correction.</param>
/// <param name="CapturePath">Capture file, if one was recorded.</param>
/// <param name="CaptureSha256">Its hash.</param>
/// <param name="CaptureSample0Utc">Timestamp of the capture's first sample — the timebase burst
/// positions are measured against.</param>
/// <param name="ReceiverHost">Which receiver.</param>
/// <param name="Notes">Why this pass exists — free text.</param>
internal sealed record FmCampaignManifest(
    string Name,
    string Mode,
    int DspRate,
    double TargetDeviationHz,
    double OffsetHz,
    int CaptureRate,
    IReadOnlyList<FmCampaignBurst> Bursts,
    string ModemRevision,
    DateTimeOffset WrittenUtc,
    string Radio,
    string FrequencyMHz,
    int? RfPower,
    double PassAudioGain,
    double? MeasuredPeakDeviationHz = null,
    double DialCorrectionHz = 0,
    string? CapturePath = null,
    string? CaptureSha256 = null,
    DateTimeOffset? CaptureSample0Utc = null,
    string? ReceiverHost = null,
    string? Notes = null);
