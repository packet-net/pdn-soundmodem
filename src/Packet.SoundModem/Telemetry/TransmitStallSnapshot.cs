namespace Packet.SoundModem.Telemetry;

/// <summary>
/// Transmit stalls since start (issue #748), as the metrics endpoint publishes them: the times the
/// transmit audio fell behind a self-pacing output's sample clock.
/// </summary>
/// <param name="Stalls">Times the transmit audio fell behind.</param>
/// <param name="Seconds">The sum of each stall's furthest-behind, in seconds: about the length of
/// the gaps they put on the air, to within one packet (5 ms) each.</param>
public readonly record struct TransmitStallSnapshot(long Stalls, double Seconds);
