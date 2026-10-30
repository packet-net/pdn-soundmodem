namespace Packet.SoundModem.Telemetry;

/// <summary>
/// Transmit stalls since start (issue #748), as the metrics endpoint publishes them: the times the
/// transmit audio fell behind a self-pacing output's sample clock.
/// </summary>
/// <param name="Stalls">Times the transmit audio fell behind.</param>
/// <param name="Seconds">The sum of each stall's furthest-behind, in seconds: an upper bound on the
/// airtime lost to them.</param>
public readonly record struct TransmitStallSnapshot(long Stalls, double Seconds);
