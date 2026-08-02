using System.Text.Json.Serialization;

namespace Packet.SoundModem.UberSdr;

/// <summary>Deserialized <c>POST /connection</c> reply (see the protocol section of the OTA
/// capture-client plan).</summary>
public sealed class ConnectionResponse
{
    /// <summary>Whether this client may open a stream at all.</summary>
    [JsonPropertyName("allowed")] public bool Allowed { get; init; }

    /// <summary>Why not, when <see cref="Allowed"/> is false.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    /// <summary>The address the receiver sees us arriving from.</summary>
    [JsonPropertyName("client_ip")] public string? ClientIp { get; init; }

    /// <summary>Idle timeout in seconds.</summary>
    [JsonPropertyName("session_timeout")] public int SessionTimeout { get; init; }

    /// <summary>How long one session may last, in seconds, before the receiver closes it —
    /// 10800 (3 h) on the public instances. A long-running receiver has to expect that and
    /// reconnect.</summary>
    [JsonPropertyName("max_session_time")] public int MaxSessionTime { get; init; }

    /// <summary>True for an authenticated client exempt from the public limits, including the
    /// <see cref="AllowedIqModes"/> list.</summary>
    [JsonPropertyName("bypassed")] public bool Bypassed { get; init; }

    /// <summary>The IQ modes this client may ask for (<c>iq48</c>, sometimes also
    /// <c>iq96</c>); empty or absent means the receiver is not gating them.</summary>
    [JsonPropertyName("allowed_iq_modes")] public List<string>? AllowedIqModes { get; init; }
}
