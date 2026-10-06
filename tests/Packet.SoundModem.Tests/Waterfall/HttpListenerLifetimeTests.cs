using System.Net;
using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// A site's listener is taken down without its accept loop waiting for ever, whatever state the
/// browsers it was serving left it in.
/// </summary>
/// <remarks>
/// The managed <see cref="HttpListener"/> completes a pending accept only at the end of its own
/// clean-up, and a connection whose stream is already disposed throws out of the middle of that
/// clean-up. The monitor's teardown used to wait on that accept, and hung the uplink tests at
/// their 30 s limit about one class run in four. The throw needs a WebSocket session to end at
/// just the wrong moment, so what is pinned here is the half of the cure that does not depend on
/// it: the accept loop's wait ends when its owner stops, whatever state the listener is in.
/// </remarks>
public class HttpListenerLifetimeTests
{
    /// <summary>A safety net, not a budget: everything here is over in milliseconds.</summary>
    private const int TestTimeoutMs = 30_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Accept_Ends_When_Its_Owner_Stops_Though_The_Listener_Never_Answers()
    {
        int port = FreePorts.Next();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stopping = new CancellationTokenSource();

        Task<HttpListenerContext> accept = HttpListenerLifetime.AcceptAsync(listener, stopping.Token);
        accept.IsCompleted.Should().BeFalse("nobody has asked for anything");

        // The listener is left exactly as it is: still listening, with the accept still queued
        // inside it, which is the state a throw out of Stop() used to leave it in for good.
        await stopping.CancelAsync();
        Func<Task> waiting = () => accept;
        await waiting.Should().ThrowAsync<OperationCanceledException>(
            "the loop's owner stopping is enough on its own to end the wait");
        listener.IsListening.Should().BeTrue("nothing here depended on the listener answering");

        HttpListenerLifetime.Shut(listener);
        listener.IsListening.Should().BeFalse();
    }
}
