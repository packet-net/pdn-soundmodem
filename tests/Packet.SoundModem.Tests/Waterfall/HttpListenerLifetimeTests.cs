using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// A site's listener is taken down without its accept loop waiting for ever, whatever state the
/// browsers it was serving left it in.
/// </summary>
/// <remarks>
/// <para>The managed <see cref="HttpListener"/> completes a pending accept only at the end of its own
/// clean-up, and a connection whose stream is already disposed throws out of the middle of that
/// clean-up. The monitor's teardown used to wait on that accept, and hung the uplink tests at
/// their 30 s limit about one class run in four. The throw needs a WebSocket session to end at
/// just the wrong moment, so what is pinned here is the half of the cure that does not depend on
/// it: the accept loop's wait ends when its owner stops, whatever state the listener is in.
/// </para>
/// <para>The other thing pinned is that taking a listener down releases its port once and never
/// binds it again. The managed listener's <c>Close()</c> on a listener that is stopped or was
/// never started re-registers an endpoint for its port on the way to unregistering it, which
/// binds the port; the old <c>Stop()</c>-then-<c>Close()</c> teardown did exactly that, and threw
/// "Address already in use" out of a dispose whenever something else had been given the port in
/// the moment between the two. These hold the port with another socket first, so the second bind
/// fails every time rather than one run in a thousand, and look at what the teardown
/// swallowed.</para>
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

    [Fact(Timeout = TestTimeoutMs)]
    public void A_Listener_That_Never_Started_Is_Shut_Without_Binding_Its_Port()
    {
        // A server whose Start() was never reached (or threw before the listener started) is
        // still disposed, and its listener with it.
        int port = FreePorts.Next();
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            var swallowed = new List<Exception>();

            HttpListenerLifetime.Shut(listener, swallowed.Add);

            swallowed.Should().BeEmpty("a listener that never listened has no port to give back");
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Port_Released_By_A_Stopped_Listener_Is_Left_To_Whoever_Has_It_Now()
    {
        int port = FreePorts.Next();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        listener.Stop();

        // Somebody else is handed the port the moment it is free, as a bind to port 0 elsewhere
        // on the box can be.
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        try
        {
            var swallowed = new List<Exception>();
            HttpListenerLifetime.Shut(listener, swallowed.Add);

            swallowed.Should().BeEmpty("the teardown must not try to bind the port again");
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using Socket accepted = await holder.AcceptSocketAsync();
            accepted.Connected.Should().BeTrue("the port is still the new owner's");
        }
        finally
        {
            holder.Stop();
        }
    }
}
