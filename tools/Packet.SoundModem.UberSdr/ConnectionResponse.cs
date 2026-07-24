using System.Text.Json.Serialization;

namespace Packet.SoundModem.UberSdr;

/// <summary>Deserialized <c>POST /connection</c> reply (see the protocol section of the OTA
/// capture-client plan).</summary>
public sealed class ConnectionResponse
{
    [JsonPropertyName("allowed")] public bool Allowed { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("client_ip")] public string? ClientIp { get; init; }
    [JsonPropertyName("session_timeout")] public int SessionTimeout { get; init; }
    [JsonPropertyName("max_session_time")] public int MaxSessionTime { get; init; }
    [JsonPropertyName("bypassed")] public bool Bypassed { get; init; }
    [JsonPropertyName("allowed_iq_modes")] public List<string>? AllowedIqModes { get; init; }
}
