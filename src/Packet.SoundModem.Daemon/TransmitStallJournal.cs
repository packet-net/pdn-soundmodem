using System.Globalization;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The journal's words for a keyup whose transmit audio fell behind the radio (issue #748).
/// </summary>
/// <remarks>
/// One line per keyup, however many times it stalled: a contended machine stalls in bursts, and a
/// line per stall would bury the journal when it most needs reading. The keyup is named by when it
/// started, to the tenth of a second, which is what lines it up with the frame log's tx rows and
/// with a recording made somewhere else. The advice rides on the first line of a run only, as
/// <see cref="XrunWatch"/> does it; after that the stalls speak for themselves. ASCII, like every
/// printed line. Thread-safe: the daemon writes each line from the thread pool.
/// </remarks>
internal sealed class TransmitStallJournal
{
    private int _advised;

    /// <summary>The line for <paramref name="report"/>.</summary>
    internal string Line(TransmitStallReport report)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string keyup = string.Create(
            c, $"keyup from {report.KeyedAt.UtcDateTime:HH:mm:ss.f} UTC ({report.Length.TotalSeconds:F1} s)");
        string behind = string.Create(
            c, $"fell up to {report.Worst.TotalMilliseconds:F0} ms behind {report.Stalls} time{(report.Stalls == 1 ? "" : "s")}");
        string line = $"tx: {keyup}: DAX audio {behind}, so the radio likely sent a gap";
        return Interlocked.Exchange(ref _advised, 1) == 0 ? line + "; give this host more CPU" : line;
    }
}
