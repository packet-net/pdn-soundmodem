using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The page keep-alive (#411): every page socket is asked every
/// <see cref="WaterfallWebServer.KeepAlivePing"/> whether anybody is still there, and one that
/// has said nothing for <see cref="WaterfallWebServer.KeepAliveSilence"/> stops being counted as
/// a viewer.
/// </summary>
/// <remarks>
/// <para>Real sockets, on a fake clock. Real because the fault was in what the framework does
/// with a live connection whose far end has gone: the socket stays Open at both ends, the
/// framework's own keep-alive raises nothing, and behind a Cloudflare tunnel TCP never fails
/// either, because the server's peer is a healthy local <c>cloudflared</c>. A test against a
/// stubbed socket could not have caught that and did not.</para>
/// <para>Fake because the deadline is a minute, and a suite that spent a minute per case would
/// not be run. The clock is moved in steps of a third of the deadline where a page is meant to
/// survive, so that a scheduler which leaves an answer unread for a step or two changes
/// nothing.</para>
/// </remarks>
public class WaterfallKeepAliveTests
{
    private const int SampleRate = 12000;

    [Fact]
    public async Task A_Page_That_Stops_Answering_Is_Dropped_And_Stops_Being_Counted()
    {
        // The case #409 spent six hours on: a phone that went to sleep behind a tunnel. Nothing
        // about this socket is broken - it upgraded, it is Open at both ends, and the framework
        // is perfectly happy with it - and that is exactly why the count used to stay at 1 for
        // ever and the on-demand receiver never lingered.
        await using var h = Harness.Start();

        using ClientWebSocket page = await h.WatchAsync();
        h.Server.Viewers.Should().Be(1);

        h.Clock.Advance(WaterfallWebServer.KeepAliveSilence);

        // Waited for on the event rather than on the count: the count moves first, and the event
        // is the last thing the departure does, so waiting on it settles the journal line too.
        await h.Until(() => h.Counts.Length == 2, "the page that stopped answering is let go");
        h.Server.Viewers.Should().Be(0);

        page.State.Should().NotBe(WebSocketState.Closed,
            "nothing closed this socket politely; the server gave up on it");
        h.Counts.Should().Equal([1, 0], "the wrapper only ever sees the event, so it has to fire");
        h.Journal.Should().Equal(["page: viewer dropped, no reply for 60 s, 0 viewers"]);
    }

    /// <summary>
    /// What the page is sent, and how often: the shape its <c>onmessage</c> answers, pinned here
    /// so that a change to either side of it fails rather than quietly stops working.
    /// </summary>
    [Fact]
    public async Task Every_Page_Is_Asked_Whether_It_Is_Still_There()
    {
        await using var h = Harness.Start();

        using ClientWebSocket page = await h.WatchAsync();
        h.Clock.Advance(WaterfallWebServer.KeepAlivePing);

        using JsonDocument asked = await h.NextTextAsync(page);
        asked.RootElement.GetProperty("type").GetString().Should().Be("ping",
            "the page answers this and nothing else");
    }

    [Fact]
    public async Task A_Page_That_Answers_Is_Still_Watching_After_Several_Intervals()
    {
        // A monitor page left open overnight on purpose. A hundred seconds of clock, which is
        // five keep-alives and well past a deadline that has been reset all the way along.
        await using var h = Harness.Start();

        using ClientWebSocket page = await h.WatchAsync();

        await h.WatchingForAsync(page, WaterfallWebServer.KeepAlivePing * 5);

        h.Server.Viewers.Should().Be(1, "a page that answers is a page somebody is watching");
        h.Counts.Should().Equal([1], "nothing changed, so nothing was announced");
        h.Journal.Should().BeEmpty();
        page.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_Page_That_Reconnects_Counts_Once_As_Soon_As_The_Stale_Socket_Goes()
    {
        // The doubling in #409: a browser decided its own socket was dead and opened another,
        // while the server still held the first. Two sockets, one person. The keep-alive is what
        // bounds it - the abandoned one is gone within the deadline rather than never.
        await using var h = Harness.Start();

        ClientWebSocket abandoned = await h.WatchAsync();
        using ClientWebSocket reconnected = await h.WatchAsync();
        h.Server.Viewers.Should().Be(2, "for now one viewer really is holding two sockets");

        // The page that came back answers all the way to the deadline; the one the browser walked
        // away from cannot.
        await h.WatchingForAsync(reconnected, WaterfallWebServer.KeepAliveSilence);

        await h.Until(() => h.Counts.Length == 3, "the stale socket is dropped and the page is not");
        h.Server.Viewers.Should().Be(1);
        reconnected.State.Should().Be(WebSocketState.Open);
        h.Counts.Should().Equal([1, 2, 1]);
        h.Journal.Should().Equal(["page: viewer dropped, no reply for 60 s, 1 viewer"],
            "the line carries the count that is left, and says one viewer in the singular");

        abandoned.Dispose();
    }

    /// <summary>
    /// A page that says something of its own is a page that is there: the deadline is on silence,
    /// not on the keep-alive answer in particular.
    /// </summary>
    /// <remarks>
    /// It matters for the detached links window, which turns the waterfall off and is then sent
    /// nothing but keep-alives - and for any browser at all, since a viewer clicking Listen must
    /// not be a viewer the server is about to give up on.
    /// </remarks>
    [Fact]
    public async Task Anything_A_Page_Says_Keeps_Its_Place()
    {
        await using var h = Harness.Start();

        using ClientWebSocket page = await h.WatchAsync();

        await h.WatchingForAsync(
            page, WaterfallWebServer.KeepAliveSilence * 2, "{\"type\":\"spectrum\",\"on\":false}");

        h.Server.Viewers.Should().Be(1);
        h.Journal.Should().BeEmpty();
    }

    /// <summary>Everything one of these tests needs: a server on a fake clock, its journal, its
    /// viewer counts, and pages that can answer or go quiet.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<string> _journal = [];
        private readonly List<int> _counts = [];
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
        private readonly int _port;

        private Harness(SoundModemChannel channel, int port)
        {
            _port = port;
            Server = new WaterfallWebServer(channel, port, new WaterfallOptions
            {
                TimeProvider = Clock,
                Log = line => { lock (_journal) { _journal.Add(line); } },
            });
        }

        internal static Harness Start()
        {
            var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
            channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
            var h = new Harness(channel, FreePorts.Next());
            h.Server.ViewersChanged += n => { lock (h._counts) { h._counts.Add(n); } };
            h.Server.Start();
            return h;
        }

        internal FakeTimeProvider Clock { get; } = new();

        internal WaterfallWebServer Server { get; }

        internal string[] Journal
        {
            get { lock (_journal) { return [.. _journal]; } }
        }

        internal int[] Counts
        {
            get { lock (_counts) { return [.. _counts]; } }
        }

        /// <summary>A browser arriving: connected, and counted by the time this returns.</summary>
        internal async Task<ClientWebSocket> WatchAsync()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);

            // The config is the first thing every page is sent, and it is sent after the client
            // has been added to the list: reading it is how this knows the count has moved.
            await ReceiveAsync(socket);
            return socket;
        }

        /// <summary>
        /// Moves the clock on with the page answering as it goes, and hands back nothing: what a
        /// test wants to know afterwards is who is still counted.
        /// </summary>
        /// <remarks>
        /// <para>An answer every <see cref="WaterfallWebServer.KeepAlivePeriod"/>, which is twelve
        /// per deadline, rather than one per keep-alive: a fake clock and a real socket keep
        /// different time, and the jump happens in no time at all while the answer is still
        /// crossing loopback, so the server reads it some steps after the clock says it was
        /// sent.</para>
        /// <para>The millisecond is what makes twelve a margin rather than a hope. A send to
        /// loopback completes without ever yielding the thread, so without it this loop can run
        /// its whole span - two minutes of clock in a millisecond of wall time - before the server
        /// gets a turn at any of the answers, which is how it read as a page that had gone silent.
        /// Sleeping hands the CPU over between steps, so the answers are read as they are
        /// sent.</para>
        /// </remarks>
        /// <param name="socket">The page.</param>
        /// <param name="span">How long it stays open.</param>
        /// <param name="says">What it says; the page's own <c>pong</c> unless a test is asking
        /// what some other message does.</param>
        internal async Task WatchingForAsync(
            ClientWebSocket socket, TimeSpan span, string says = "{\"type\":\"pong\"}")
        {
            for (TimeSpan gone = TimeSpan.Zero;
                 gone < span;
                 gone += WaterfallWebServer.KeepAlivePeriod)
            {
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(says),
                    WebSocketMessageType.Text, true, _cancellation.Token);
                await Task.Delay(1, _cancellation.Token);
                TimeSpan step = span - gone;
                Clock.Advance(step < WaterfallWebServer.KeepAlivePeriod
                    ? step
                    : WaterfallWebServer.KeepAlivePeriod);
            }
        }

        /// <summary>The next message off a page's socket, as JSON.</summary>
        internal async Task<JsonDocument> NextTextAsync(ClientWebSocket socket)
        {
            while (true)
            {
                (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
                if (kind == WebSocketMessageType.Text)
                {
                    return JsonDocument.Parse(payload);
                }
            }
        }

        internal async Task Until(Func<bool> condition, string because)
        {
            while (!condition())
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, _cancellation.Token);
            }

            condition().Should().BeTrue(because);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _cancellation.Dispose();
        }

        private async Task<(WebSocketMessageType Kind, byte[] Payload)> ReceiveAsync(
            ClientWebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            int filled = 0;
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, filled, buffer.Length - filled),
                    _cancellation.Token);
                filled += result.Count;
                if (result.EndOfMessage)
                {
                    return (result.MessageType, buffer[..filled]);
                }
            }
        }
    }
}
