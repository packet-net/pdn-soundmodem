using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// Two receivers' pages on one port, each under its own path prefix - the shape a site that
/// offers a list of web receivers has to have, because fifty receivers is one hostname and one
/// tunnel, not fifty listeners behind a proxy.
/// </summary>
public class WaterfallRouterTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const string ReadingBase = "/r/reading/";
    private const string DalgetyBase = "/r/m9psy-1/";

    private readonly SoundModemChannel _readingChannel = new(SampleRate, randomSeed: 7);
    private readonly SoundModemChannel _dalgetyChannel = new(SampleRate, randomSeed: 7);
    private readonly WaterfallWebServer _reading;
    private readonly WaterfallWebServer _dalgety;
    private readonly WaterfallRouter _router;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public WaterfallRouterTests()
    {
        _readingChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _dalgetyChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));

        // Titles rather than ports are what tells the two apart from a browser: they share the one
        // port, which is the point.
        _reading = WaterfallWebServer.Routed(
            _readingChannel, new WaterfallOptions { Public = true, Title = "Reading" });
        _dalgety = WaterfallWebServer.Routed(
            _dalgetyChannel, new WaterfallOptions { Public = true, Title = "Dalgety Bay" });

        _port = FreePort();
        _router = new WaterfallRouter(_port);
    }

    public ValueTask InitializeAsync()
    {
        // Each server still starts itself, and does so before it is registered: the band probe and
        // the channel subscriptions are the station's own work either way, only the listening
        // moved out, and a server registered before it has started could be asked for a page it
        // has nothing to say on.
        _reading.Start();
        _dalgety.Start();
        _router.Add(ReadingBase, _reading);
        _router.Add(DalgetyBase, _dalgety);
        _router.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _router.DisposeAsync();
        await _reading.DisposeAsync();
        await _dalgety.DisposeAsync();
        _cancellation.Dispose();
    }

    [Fact]
    public async Task Two_Servers_Behind_One_Listener_Answer_On_Their_Own_Prefixes()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        // Every route the page needs, under each base: the page itself, the copy it is served as
        // when a browser navigates to it by name, and the torn-off links window.
        foreach (string path in (string[])
                 [
                     ReadingBase, ReadingBase + "index.html", ReadingBase + "links",
                     DalgetyBase, DalgetyBase + "index.html", DalgetyBase + "links",
                 ])
        {
            (await http.GetStringAsync(path, _cancellation.Token)).Should()
                .Contain("<!doctype html>", "{0} serves the page", path);
        }

        // Each server's own channel, so each its own config message.
        using ClientWebSocket reading = await ConnectAsync(ReadingBase + "ws");
        using ClientWebSocket dalgety = await ConnectAsync(DalgetyBase + "ws");
        using JsonDocument readingConfig = await NextOfTypeAsync(reading, "config");
        using JsonDocument dalgetyConfig = await NextOfTypeAsync(dalgety, "config");
        readingConfig.RootElement.GetProperty("title").GetString().Should().Be("Reading");
        dalgetyConfig.RootElement.GetProperty("title").GetString().Should().Be("Dalgety Bay");

        // And its own frames. Both are driven before either is read, so a frame arriving on the
        // wrong socket would have to be waited out rather than raced past.
        _reading.ReportFrame(
            subChannel: 0, mode: "afsk1200", from: "G4EYR", to: "GB7RDG",
            lengthBytes: 22, snrDb: 14.0, decodedOk: true);
        _dalgety.ReportFrame(
            subChannel: 0, mode: "afsk1200", from: "M9PSY-1", to: "GB7RDG",
            lengthBytes: 22, snrDb: 14.0, decodedOk: true);

        using JsonDocument readingFrame = await NextOfTypeAsync(reading, "frame");
        using JsonDocument dalgetyFrame = await NextOfTypeAsync(dalgety, "frame");
        readingFrame.RootElement.GetProperty("from").GetString().Should().Be("G4EYR");
        dalgetyFrame.RootElement.GetProperty("from").GetString().Should()
            .Be("M9PSY-1", "one receiver's traffic is nothing to do with another's");
    }

    [Fact]
    public async Task A_Router_Answers_404_Outside_Every_Prefix()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        foreach (string path in (string[])
                 [
                     // Nothing is registered here.
                     "/r/nobody/", "/r/nobody/links", "/nope",
                     // The front page is Phase 3's, and nothing has claimed it yet.
                     "/", "/index.html",
                     // A receiver's own routes are its own: served under its base, nowhere else.
                     "/links", "/survey/20260804-151909-862hz-unclaimed.wav", "/metrics",
                 ])
        {
            HttpResponseMessage response = await http.GetAsync(path, _cancellation.Token);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} belongs to nobody", path);
        }

        // A socket upgrade to a prefix nothing is served under is refused rather than accepted by
        // whichever server happened to be asked: a server takes an upgrade at any path under its
        // own base, and this is not under one.
        using var socket = new ClientWebSocket();
        Func<Task> connect = async () => await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}/r/nobody/ws"), _cancellation.Token);

        await connect.Should().ThrowAsync<WebSocketException>();
    }

    [Fact]
    public async Task A_Socket_Upgrade_Reaches_The_Server_Its_Prefix_Names()
    {
        using ClientWebSocket socket = await ConnectAsync(DalgetyBase + "ws");
        using JsonDocument config = await NextOfTypeAsync(socket, "config");

        config.RootElement.GetProperty("title").GetString().Should().Be("Dalgety Bay");

        // The decisive half: the viewer is counted against the receiver whose page it is watching,
        // which is what an on-demand session is opened and dropped on. The count is taken as the
        // socket is added to the broadcast list, before the config that has just been read.
        _dalgety.Viewers.Should().Be(1);
        _reading.Viewers.Should().Be(0, "nobody is watching Reading");
    }

    [Fact]
    public async Task The_Front_Door_Answers_Everything_No_Prefix_Claims()
    {
        int asked = 0;
        _router.FrontDoor = async context =>
        {
            Interlocked.Increment(ref asked);
            if (context.Request.Url?.AbsolutePath is not ("/" or "/api/instances"))
            {
                return false;
            }

            byte[] body = Encoding.UTF8.GetBytes("the receivers");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _cancellation.Token);
            context.Response.Close();
            return true;
        };

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        (await http.GetStringAsync("/", _cancellation.Token)).Should().Be("the receivers");
        (await http.GetStringAsync("/api/instances", _cancellation.Token)).Should().Be("the receivers");

        // What it declines is still a 404, and what a prefix claims never reaches it at all.
        (await http.GetAsync("/nope", _cancellation.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetStringAsync(ReadingBase, _cancellation.Token)).Should().Contain("<!doctype html>");
        asked.Should().Be(3, "a receiver's own page is the receiver's, not the front door's");
    }

    [Fact]
    public async Task A_Prefix_Without_Its_Trailing_Slash_Redirects_To_It()
    {
        // The page hangs its socket, its survey links and its links window off its own path, so a
        // visitor who types /r/reading would get a page reaching for /r/ws and connecting to
        // nothing. The redirect is what makes the address bar's obvious spelling work.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}"),
        };

        HttpResponseMessage response = await http.GetAsync("/r/reading", _cancellation.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be(ReadingBase);

        // Whatever was after the "?" is the page's, and is still there afterwards.
        HttpResponseMessage queried =
            await http.GetAsync("/r/reading?tab=links", _cancellation.Token);

        queried.StatusCode.Should().Be(HttpStatusCode.Found);
        queried.Headers.Location!.ToString().Should().Be($"{ReadingBase}?tab=links");

        // A method nobody typed into an address bar is not redirected: it is a 404, as it would
        // have been under the base itself.
        HttpResponseMessage posted = await http.PostAsync(
            "/r/reading", new StringContent(""), _cancellation.Token);

        posted.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A handler installed under a receiver's API sees the path relative to that receiver, so one
    /// written for a station that is its own site works unchanged under a prefix.
    /// </summary>
    /// <remarks>
    /// The daemon's config API matches on "/api/config" exactly. Handed the request's own path it
    /// would see "/r/reading/api/config", match nothing and 404 - which is a station that answers
    /// its own API everywhere except where it is actually served.
    /// </remarks>
    [Fact]
    public async Task An_Api_Handler_Is_Given_The_Path_Under_Its_Own_Base()
    {
        string? seen = null;
        _reading.ApiHandler = async (context, path) =>
        {
            seen = path;
            byte[] body = Encoding.UTF8.GetBytes("{}");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _cancellation.Token);
            context.Response.Close();
            return true;
        };

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        (await http.GetStringAsync($"{ReadingBase}api/config", _cancellation.Token)).Should().Be("{}");

        seen.Should().Be("/api/config", "the handler matches on its own routes, not on where it is served");
    }

    /// <summary>The longest registered prefix a path is under is the one that gets it.</summary>
    /// <remarks>
    /// Two things at once: a base is not a bare string prefix, so /r/ab/ is nothing to do with
    /// /r/a/ despite starting with the same four characters; and a base nested inside another
    /// gets its own requests rather than its parent's, whichever order they were registered in.
    /// </remarks>
    [Fact]
    public async Task The_Longest_Matching_Prefix_Gets_The_Request()
    {
        await using var nested = new Receivers(3);
        int port = FreePort();
        await using var router = new WaterfallRouter(port);

        // Registered inside-out, so that "the one registered first" and "the longest" cannot be
        // the same answer.
        router.Add("/r/a/b/", nested[0]);
        router.Add("/r/a/", nested[1]);
        router.Add("/r/ab/", nested[2]);
        router.Start();

        foreach ((string path, string title) in ((string, string)[])
                 [("/r/a/b/ws", "receiver 0"), ("/r/a/ws", "receiver 1"), ("/r/ab/ws", "receiver 2")])
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{path}"), _cancellation.Token);
            using JsonDocument config = await NextOfTypeAsync(socket, "config");
            config.RootElement.GetProperty("title").GetString().Should().Be(title, "{0} is its own", path);
        }
    }

    [Fact]
    public async Task A_Registered_Server_Is_Told_Where_It_Is_Served_From()
    {
        // A station journals a URL for somebody to paste, and on a routed server the only thing
        // that knows the port is the router.
        _reading.Port.Should().Be(_port);
        _reading.Url.Should().Be($"http://127.0.0.1:{_port}{ReadingBase}");
    }

    [Fact]
    public async Task A_Removed_Prefix_Stops_Being_Served_And_Its_Server_Forgets_Where_It_Was()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        (await http.GetAsync(ReadingBase, _cancellation.Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        _router.Remove(ReadingBase).Should().BeTrue();

        (await http.GetAsync(ReadingBase, _cancellation.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _reading.Url.Should().BeEmpty("a URL it no longer answers on is worse than none at all");
        _reading.Port.Should().Be(0);
        _router.Remove(ReadingBase).Should().BeFalse("it has already gone");

        // The other receiver is untouched, which is the whole point of taking one out.
        (await http.GetAsync(DalgetyBase, _cancellation.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A receiver picked while the site is up is served from the moment it is registered - which
    /// is the only way stations can be created on demand rather than all fifty at start-up.
    /// </summary>
    [Fact]
    public async Task A_Server_Added_After_The_Router_Started_Is_Served_Straight_Away()
    {
        const string LateBase = "/r/g4eyr/";
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        (await http.GetAsync(LateBase, _cancellation.Token)).StatusCode.Should()
            .Be(HttpStatusCode.NotFound, "nobody has picked this receiver yet");

        await using var late = new Receivers(1);
        _router.Add(LateBase, late[0]);

        (await http.GetStringAsync(LateBase, _cancellation.Token)).Should().Contain("<!doctype html>");
        _router.Remove(LateBase);
    }

    /// <summary>
    /// A handful of started routed servers, each with its own channel, disposed together.
    /// </summary>
    private sealed class Receivers : IAsyncDisposable
    {
        private readonly WaterfallWebServer[] _servers;

        public Receivers(int count)
        {
            _servers = new WaterfallWebServer[count];
            for (int n = 0; n < count; n++)
            {
                var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
                channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
                _servers[n] = WaterfallWebServer.Routed(
                    channel, new WaterfallOptions { Public = true, Title = $"receiver {n}" });
                _servers[n].Start();
            }
        }

        public WaterfallWebServer this[int at] => _servers[at];

        public async ValueTask DisposeAsync()
        {
            foreach (WaterfallWebServer server in _servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(string path)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{path}"), _cancellation.Token);
        return socket;
    }

    /// <summary>
    /// The next message of a kind, so that a handshake growing another opening message does not
    /// break every test that reads past the config.
    /// </summary>
    private async Task<JsonDocument> NextOfTypeAsync(ClientWebSocket socket, string type)
    {
        while (true)
        {
            var buffer = new byte[64 * 1024];
            int filled = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, filled, buffer.Length - filled), _cancellation.Token);
                filled += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(buffer[..filled]);
            if (message.RootElement.GetProperty("type").GetString() == type)
            {
                return message;
            }

            message.Dispose();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
