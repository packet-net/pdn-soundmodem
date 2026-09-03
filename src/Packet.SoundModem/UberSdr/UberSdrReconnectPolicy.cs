namespace Packet.SoundModem.UberSdr;

/// <summary>The receiver answered and said no for now - HTTP 429 on the stream upgrade.
/// Carries no blame: quota and rate limits are the receiver's to enforce, and the right
/// response is a long wait, not a restart.</summary>
internal sealed class UberSdrRefusedException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>How the last connection attempt or session ended, as far as pacing the next
/// attempt is concerned.</summary>
internal enum UberSdrReconnectOutcome
{
    /// <summary>The session delivered real audio before ending - the ordinary
    /// max-session-time rollover. Reconnect promptly.</summary>
    Healthy,

    /// <summary>The connect failed at the transport (unreachable, refused socket, DNS).
    /// Retry soon; a restart can genuinely help, so this outcome still feeds the
    /// give-up-and-restart clock.</summary>
    Transient,

    /// <summary>The receiver answered and said no for now - HTTP 429, or a daily listening
    /// quota shown as exhausted. Nothing on our side fixes this; only time does. Wait long,
    /// escalating to a cap sized for a limit that resets on a daily clock, and never treat
    /// it as a fault worth restarting the service over.</summary>
    Refused,

    /// <summary>The socket opened but the session died before delivering meaningful audio -
    /// an instance that accepts and immediately drops (full, restarting, quota enforcement
    /// at the stream rather than the door). Escalate; the fixed one-second breath this
    /// replaced hammered somebody else's receiver every second, indefinitely.</summary>
    ShortSession,
}

/// <summary>
/// Pacing for <see cref="UberSdrAudioInput"/>'s reconnect loop: consecutive non-healthy
/// outcomes escalate the delay exponentially to a per-class cap, and one healthy session
/// resets the ladder. Pure state machine, unit-tested apart from the socket machinery.
/// </summary>
internal sealed class UberSdrReconnectPolicy
{
    private static readonly TimeSpan HealthyBreath = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransientBase = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransientCap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefusedBase = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefusedCap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ShortSessionBase = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortSessionCap = TimeSpan.FromMinutes(5);

    private int _consecutive;

    /// <summary>Back to the foot of the ladder, as one healthy session does - for a caller that
    /// learns of health some other way than a session ending.</summary>
    public void Reset() => _consecutive = 0;

    /// <summary>Delay to wait after an attempt with the given outcome, before the next.</summary>
    public TimeSpan After(UberSdrReconnectOutcome outcome)
    {
        if (outcome == UberSdrReconnectOutcome.Healthy)
        {
            _consecutive = 0;

            // These are somebody else's receivers with a finite number of listener slots; even
            // the ordinary session rollover deserves a breath rather than an instant reconnect.
            return HealthyBreath;
        }

        int step = Math.Min(_consecutive, 20); // 2^20 s ≫ every cap; avoids overflow, nothing more
        _consecutive++;
        (TimeSpan baseDelay, TimeSpan cap) = outcome switch
        {
            UberSdrReconnectOutcome.Transient => (TransientBase, TransientCap),
            UberSdrReconnectOutcome.Refused => (RefusedBase, RefusedCap),
            _ => (ShortSessionBase, ShortSessionCap),
        };

        double seconds = baseDelay.TotalSeconds * Math.Pow(2, step);
        return seconds >= cap.TotalSeconds ? cap : TimeSpan.FromSeconds(seconds);
    }
}
