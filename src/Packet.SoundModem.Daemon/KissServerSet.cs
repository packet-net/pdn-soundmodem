using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Owns the daemon's KISS listeners — the shared multiplexed port plus any per-modem ports —
/// so a single <c>await using</c> shuts all of them down.
/// </summary>
internal sealed class KissServerSet(IReadOnlyList<KissTcpServer> servers) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        foreach (KissTcpServer server in servers)
        {
            // One listener refusing to shut down cleanly must not strand the others.
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}
