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
/// with a recording made somewhere else. ASCII, like every printed line.
/// </remarks>
internal static class TransmitStallJournal
{
    /// <summary>The line for <paramref name="report"/>.</summary>
    internal static string Line(TransmitStallReport report)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string keyup = string.Create(
            c, $"keyup from {report.KeyedAt.UtcDateTime:HH:mm:ss.f} UTC ({report.Length.TotalSeconds:F1} s)");
        string behind = string.Create(
            c, $"fell up to {report.Worst.TotalMilliseconds:F0} ms behind {report.Stalls} time{(report.Stalls == 1 ? "" : "s")}");
        return $"tx: {keyup}: DAX audio {behind}, so the radio sent a gap; give this host more CPU";
    }
}
