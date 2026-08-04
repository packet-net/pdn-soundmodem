namespace Packet.SoundModem.Ota;

/// <summary>One SSB audio-carrier burst a pass transmitted - enough to regenerate its payload, its
/// channel realisation, and know where it landed in the capture.</summary>
/// <param name="Mode">The <c>afsk300*</c>/<c>bpsk*</c>/<c>qpsk600</c>/<c>qpsk2400</c> mode.</param>
/// <param name="Seed">Payload seed - and, offset, the channel realisation, so this row alone
/// reproduces the exact transmission.</param>
/// <param name="FrameBytes">AX.25 frame size the seed fills - the scorer regenerates the same
/// bytes to compare against.</param>
/// <param name="SnrDb">SNR the rig was asked to inject.</param>
/// <param name="Channel">Injected channel geometry.</param>
/// <param name="StartSeconds">Where the active burst begins in the capture - after its noise
/// lead-in, derived from the actual key-up time (live) or the layout (rehearsal).</param>
/// <param name="BurstSeconds">Active modulated-burst length, the region the scorer measures signal
/// power over.</param>
internal sealed record SsbCampaignBurst(
    string Mode,
    int Seed,
    int FrameBytes,
    double SnrDb,
    SimChannelKind Channel,
    double StartSeconds,
    double BurstSeconds);

/// <summary>
/// What an SSB audio-carrier pass did: everything the SSB scorer needs to score the capture and
/// everything needed to interpret the numbers later - the audio-carrier counterpart of
/// <see cref="CampaignManifest"/> and <see cref="OfdmCampaignManifest"/>.
/// </summary>
/// <remarks>The modem revision above all: the demodulator changes, so a BER without one is a number
/// with no meaning attached. The scorer reads the offset (the capture's SSB dial), the per-burst
/// positions and the seeds straight out of this file, so a rehearsal and a live pass are scored by
/// exactly the same code.</remarks>
/// <param name="Name">Short label; ends up in filenames and the evidence log.</param>
/// <param name="Mode">The pass mode (also carried per-burst for a future mixed-mode pass).</param>
/// <param name="OffsetHz">The transmit dial offset - the scorer's IQ→audio down-shift (0 for the DAX
/// route, the software SSB offset e.g. 2000 Hz for the IQ route).</param>
/// <param name="CaptureRate">IQ sample rate of the capture the positions index into.</param>
/// <param name="Bursts">In transmit order.</param>
/// <param name="ModemRevision">Repository revision of the binary that ran.</param>
/// <param name="WrittenUtc">When this manifest was written.</param>
/// <param name="Radio">Radio address, or "none (rehearsal)".</param>
/// <param name="FrequencyMHz">Waveform slice centre.</param>
/// <param name="RfPower">Radio power setting, or null when nothing was transmitted.</param>
/// <param name="PassGain">The single pass gain that reached the radio: the DAX audio gain on the DAX
/// route, the IQ gain on the waveform route.</param>
/// <param name="DialCorrectionHz">The session's measured dial correction.</param>
/// <param name="CapturePath">Capture file, if one was recorded.</param>
/// <param name="CaptureSha256">Its hash.</param>
/// <param name="CaptureSample0Utc">Timestamp of the capture's first sample - the timebase burst
/// positions are measured against.</param>
/// <param name="ReceiverHost">Which receiver.</param>
/// <param name="Notes">Why this pass exists - free text.</param>
internal sealed record SsbCampaignManifest(
    string Name,
    string Mode,
    double OffsetHz,
    int CaptureRate,
    IReadOnlyList<SsbCampaignBurst> Bursts,
    string ModemRevision,
    DateTimeOffset WrittenUtc,
    string Radio,
    string FrequencyMHz,
    int? RfPower,
    double PassGain,
    double DialCorrectionHz,
    string? CapturePath = null,
    string? CaptureSha256 = null,
    DateTimeOffset? CaptureSample0Utc = null,
    string? ReceiverHost = null,
    string? Notes = null);
