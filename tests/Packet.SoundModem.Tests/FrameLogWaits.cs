using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests;

/// <summary>Waiting for what a <see cref="FrameLog"/>'s writer thread has put on disk, without polling.</summary>
internal static class FrameLogWaits
{
    /// <summary>
    /// Waits until <paramref name="condition"/> holds, looked at again each time the writer says it has
    /// written a frame (<see cref="FrameLog.Written"/>). The condition is looked at once first, so frames
    /// written before the wait began are not waited for.
    /// </summary>
    public static async Task WhenWrittenAsync(this FrameLog log, Func<bool> condition, string what)
    {
        var changes = new ChangeSignal();
        void Pulse() => changes.Pulse();
        log.Written += Pulse;
        try
        {
            await changes.WhenAsync(condition, what, () => $"{log.Recent(1000).Count} frame(s) readable, {log.Dropped} dropped");
        }
        finally
        {
            log.Written -= Pulse;
        }
    }
}
