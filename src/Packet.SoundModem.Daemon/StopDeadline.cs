using System.Globalization;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// How long a stop may take before the daemon ends itself. The shipped units allow 400 s
/// (<c>TimeoutStopSec=400</c>), which a mailcast "after" hook can need; a daemon with no hooks
/// keeps the 90 s systemd's default gave it before, so a wedged stop is cut off as soon as it
/// always was.
/// </summary>
internal static class StopDeadline
{
    /// <summary>The stop time without hooks: systemd's default <c>TimeoutStopSec</c>.</summary>
    internal static readonly TimeSpan WithoutHooks = TimeSpan.FromSeconds(90);

    private static readonly Lock Gate = new();
    private static ITimer? _timer;

    /// <summary>
    /// The longest a stop of a station with <paramref name="mailcast"/> may take, or null to
    /// leave it to the unit: a station with hooks waits for them.
    /// </summary>
    internal static TimeSpan? For(MailcastConfig? mailcast) =>
        mailcast?.Hooks is { } hooks && (hooks.Before is not null || hooks.After is not null) ? null : WithoutHooks;

    /// <summary>
    /// Called as the stop begins: after <paramref name="limit"/> (if there is one), says so on
    /// stderr and exits 1, as systemd's SIGKILL would have ended it. Only the first call counts.
    /// </summary>
    internal static void Arm(TimeSpan? limit)
    {
        if (limit is not { } after)
        {
            return;
        }

        lock (Gate)
        {
            _timer ??= TimeProvider.System.CreateTimer(
                _ =>
                {
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"stopping took longer than {after.TotalSeconds:F0} s; exiting now"));
                    Environment.Exit(1);
                },
                null, after, Timeout.InfiniteTimeSpan);
        }
    }
}
