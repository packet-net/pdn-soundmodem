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

    /// <summary>Seconds of listening this address has used today, where the instance meters
    /// it (the public instances allow 10800 s — 3 h — per address per day).</summary>
    [JsonPropertyName("daily_time_used_secs")] public int DailyTimeUsedSecs { get; init; }

    /// <summary>Seconds of listening left today: −1 (or absent) when the instance does not
    /// meter, 0 when the daily allowance is spent — the refusal that time, not
    /// configuration, fixes.</summary>
    [JsonPropertyName("daily_time_remaining_secs")] public int DailyTimeRemainingSecs { get; init; } = -1;

    /// <summary>True when this reply is a refusal that only time can lift: the address's
    /// daily listening allowance is exhausted. A client seeing this should wait patiently
    /// and retry, not fail its own start-up over it.</summary>
    [JsonIgnore] public bool RefusedForNow => !Allowed && DailyTimeRemainingSecs == 0;
}
