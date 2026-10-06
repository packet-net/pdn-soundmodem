using System.Net;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Accepting requests from an <see cref="HttpListener"/>, and shutting it down, in a way that
/// cannot leave the accept loop waiting for ever.
/// </summary>
/// <remarks>
/// <para>The managed <see cref="HttpListener"/> on Linux completes a pending
/// <c>GetContextAsync</c> only at the very end of its own clean-up, after it has closed every
/// connection it still holds. Closing one flushes its response stream, and a connection whose
/// socket stream is already disposed (a WebSocket session that has just ended, say) makes that
/// flush throw <see cref="ObjectDisposedException"/> from inside <c>Stop()</c>. The throw skips
/// the rest of the clean-up, so the pending accept is never completed, and an accept loop
/// awaiting it never ends. Whatever disposes that loop then waits for it for ever.</para>
/// <para>That is what hung the monitor's uplink tests at their 30 s limit, about one class run
/// in four: a dump of one showed the listener stopped, its connections cleared and one accept
/// still queued, with the monitor's teardown parked on it, and every hang logged that throw. The
/// old code caught it and skipped <c>Close()</c> with it. A monitor being stopped by systemd
/// would hang the same way until it was killed.</para>
/// <para>So the accept is waited on together with the owner's stopping token, and the loop
/// ends when the token is cancelled whatever the listener does; and <c>Close()</c> is called even
/// when <c>Stop()</c> threw, because <c>Close()</c> is what finishes the clean-up.</para>
/// </remarks>
internal static class HttpListenerLifetime
{
    /// <summary>
    /// The next request, or <see cref="OperationCanceledException"/> as soon as
    /// <paramref name="stopping"/> is cancelled, whether or not the listener ever answers.
    /// </summary>
    internal static async Task<HttpListenerContext> AcceptAsync(
        HttpListener listener, CancellationToken stopping)
    {
        Task<HttpListenerContext> accept = listener.GetContextAsync();
        try
        {
            return await accept.WaitAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Abandon(accept);
            throw;
        }
    }

    /// <summary>
    /// Stops and closes the listener, each whatever the other did, and never throws for a peer
    /// that has already gone.
    /// </summary>
    internal static void Shut(HttpListener listener)
    {
        try
        {
            listener.Stop();
        }
        catch (Exception e) when (IsShutdownNoise(e))
        {
            // A connection whose peer has gone, failing to flush as it is closed. Close() below
            // is what finishes the job Stop() was doing when it threw.
        }

        try
        {
            listener.Close();
        }
        catch (Exception e) when (IsShutdownNoise(e))
        {
        }
    }

    /// <summary>
    /// What <see cref="HttpListener"/> throws while shutting down over connections whose peers
    /// have walked away: nothing anybody can act on, and nothing that should end a teardown.
    /// </summary>
    private static bool IsShutdownNoise(Exception e) =>
        e is ObjectDisposedException or InvalidOperationException or HttpListenerException
            or IOException;

    /// <summary>
    /// An accept nobody is waiting for any more. If it ever completes, the request it carries is
    /// refused and its fault is observed, so neither lingers.
    /// </summary>
    private static void Abandon(Task<HttpListenerContext> accept) =>
        _ = accept.ContinueWith(
            static spent =>
            {
                if (spent.IsCompletedSuccessfully)
                {
                    try
                    {
                        spent.Result.Response.Abort();
                    }
                    catch (Exception)
                    {
                        // Shutting down; the client will see its connection close.
                    }
                }
                else
                {
                    _ = spent.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
