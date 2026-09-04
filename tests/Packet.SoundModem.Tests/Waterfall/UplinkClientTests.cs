using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The station's side of the uplink: one outbound socket to a public monitor site, what goes up
/// it, what comes back, and what happens when the site is down or says no.
/// </summary>
/// <remarks>
/// <para>Phase 2 of <c>docs/uplink-plan.md</c>. Every test here drives a real in-process WebSocket
/// server standing in for the monitor rather than a mock, because the things worth being sure of
/// are the framing, the handshake ordering and the keepalive, and a mock would agree with whatever
/// this client did.</para>
/// <para>The clock is a <see cref="FakeTimeProvider"/>, so the reconnect ladder is measured rather
/// than waited out: a test that took fifteen real minutes to prove the refusal backoff would not
/// be run.</para>
/// </remarks>
public class UplinkClientTests
{
    private const int ChannelRate = 12000;
    private const int PublishedRate = 12000;

    /// <summary>Samples in one published 40 ms block, as the client computes it.</summary>
    private const int BlockSamples = PublishedRate * 40 / 1000;

    /// <summary>Bytes of one audio message: the 4-byte header plus s16 samples.</summary>
    private const int AudioMessageBytes = 4 + (BlockSamples * 2);

    /// <summary>A token that is long enough to be one; the site issues 43 base64 characters.</summary>
    private const string Token = "pdnsm_0123456789012345678901234567890123456789";

    /// <summary>How long a test waits for something the network has to do before it gives up.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A monitor, as far as a station can tell: it answers the upgrade, reads the token off the
    /// header, says <c>welcome</c>, sends <c>demand</c> when the test says somebody arrived, and
    /// writes down every byte it is sent.
    /// </summary>
    private sealed class StubMonitor : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<(WebSocketMessageType Kind, byte[] Payload)> _received = [];
        private readonly Lock _gate = new();
        private WebSocket? _socket;
        private Task? _accept;

        public StubMonitor()
        {
            Port = FreePorts.Next();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _accept = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>Where a station is told to publish to.</summary>
        public string Url => $"ws://127.0.0.1:{Port}/uplink";

        /// <summary>Upgrade requests seen, refused ones included.</summary>
        public int Attempts;

        /// <summary>Upgrades actually accepted.</summary>
        public int Accepted;

        /// <summary>Answer every upgrade with this status instead of accepting it.</summary>
        public HttpStatusCode? Refuse { get; set; }

        /// <summary>Accept the upgrade and drop it at once, as a site restarting would.</summary>
        public bool DropAtOnce { get; set; }

        /// <summary>Welcome the station and then drop it, as a site that keeps falling over would.</summary>
        public bool DropAfterWelcome { get; set; }

        /// <summary>Whether to complete the handshake. False leaves the station waiting.</summary>
        public bool SendWelcome { get; set; } = true;

        /// <summary>The credential the last upgrade presented.</summary>
        public string? Authorization { get; private set; }

        public IReadOnlyList<(WebSocketMessageType Kind, byte[] Payload)> Received
        {
            get { lock (_gate) { return [.. _received]; } }
        }

        /// <summary>Everything sent after the hello, in bytes on the wire.</summary>
        public long BytesAfterHello
        {
            get { lock (_gate) { return _received.Skip(1).Sum(m => (long)m.Payload.Length); } }
        }

        public IEnumerable<JsonElement> TextMessages => Received
            .Where(m => m.Kind == WebSocketMessageType.Text)
            .Select(m => JsonDocument.Parse(m.Payload).RootElement);

        public IEnumerable<JsonElement> TextMessagesOfType(string type) => TextMessages
            .Where(m => m.TryGetProperty("type", out JsonElement t) && t.GetString() == type);

        public IReadOnlyList<byte[]> AudioMessages =>
            [.. Received.Where(m => m.Kind == WebSocketMessageType.Binary).Select(m => m.Payload)];

        /// <summary>Says how many people are watching, as a real monitor does on every change.</summary>
        public async Task DemandAsync(int viewers)
        {
            WebSocket socket = _socket
                ?? throw new InvalidOperationException("no station is connected");
            await socket.SendAsync(
                Encoding.UTF8.GetBytes($"{{\"type\":\"demand\",\"viewers\":{viewers}}}"),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }

        /// <summary>Sends anything at all, for the "everything but demand is dropped" test.</summary>
        public async Task SendAsync(string text)
        {
            WebSocket socket = _socket
                ?? throw new InvalidOperationException("no station is connected");
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(text),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref Attempts);
                Authorization = context.Request.Headers["Authorization"];
                if (Refuse is { } status)
                {
                    context.Response.StatusCode = (int)status;
                    context.Response.Close();
                    continue;
                }

                HttpListenerWebSocketContext upgrade = await context.AcceptWebSocketAsync(null);
                Interlocked.Increment(ref Accepted);
                _socket = upgrade.WebSocket;
                if (DropAtOnce)
                {
                    upgrade.WebSocket.Abort();
                    continue;
                }

                await ReadAsync(upgrade.WebSocket);
            }
        }

        private async Task ReadAsync(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            bool welcomed = false;
            try
            {
                while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
                {
                    var message = new List<byte>();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, _stopping.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                    }
                    while (!result.EndOfMessage);

                    lock (_gate)
                    {
                        _received.Add((result.MessageType, [.. message]));
                    }

                    // The hello, and then the handshake this station is waiting on.
                    if (!welcomed && SendWelcome)
                    {
                        welcomed = true;
                        await socket.SendAsync(
                            Encoding.UTF8.GetBytes(
                                "{\"type\":\"welcome\",\"slug\":\"gb7rdg-2\","
                                + "\"url\":\"https://monitor.example/r/gb7rdg-2/\"}"),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            _stopping.Token);

                        // A real monitor says how many people are watching straight away, and it
                        // is nearly always nobody.
                        await DemandAsync(0);
                        if (DropAfterWelcome)
                        {
                            socket.Abort();
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A station going away is the ordinary end of a session here.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Close();
            if (_accept is { } accept)
            {
                try
                {
                    await accept;
                }
                catch (Exception)
                {
                    // Shutting down.
                }
            }

            _accept = null;
            _stopping.Dispose();
        }
    }

    /// <summary>A station's waterfall server, with one declared band so the hello has one.</summary>
    private static WaterfallWebServer StationServer(FakeTimeProvider clock, int rate = ChannelRate)
    {
        var channel = new SoundModemChannel(rate, randomSeed: 7);
        WaterfallWebServer server = WaterfallWebServer.Routed(channel, new WaterfallOptions
        {
            TimeProvider = clock,
            DialFrequencyHz = 7047500,
            Sideband = "usb",
            DeclaredBands = [new DeclaredBand(0, "afsk1200", 1700, 1200)],
        });
        server.Start();
        return server;
    }

    private static UplinkSettings SettingsFor(
        string url, int channelRate = ChannelRate, int audioRate = PublishedRate) => new()
        {
            Url = url,
            Token = Token,
            Callsign = "GB7RDG-2",
            Operator = "Tom M0LTE",
            Location = "Reading, England",
            Radio = "IC-7300 into a doublet at 10 m",
            Site = "https://gb7rdg.example/",
            ChannelRate = channelRate,
            AudioRate = audioRate,
            DialHz = 7047500,
            Sideband = "usb",
        };

    /// <summary>Waits for something the network has to get around to, or fails the test.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }

    /// <summary>
    /// Waits for something that is behind a wait on the clock, moving the clock on a second at a
    /// time until it happens.
    /// </summary>
    /// <remarks>
    /// A single jump would not do: the client records the wait it is about to take and then takes
    /// it, so a test that jumped between those two lines would move the clock past a delay that
    /// had not been registered yet and then wait for ever. Advancing repeatedly cannot miss it.
    /// </remarks>
    private static async Task UntilAdvancing(
        FakeTimeProvider clock, Func<bool> condition, string what, int stepSeconds = 1)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            clock.Advance(TimeSpan.FromSeconds(stepSeconds));
            await Task.Delay(5);
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }

    /// <summary>One block of received audio at the channel rate, with recognisable content.</summary>
    private static float[] Tone(int samples, float scale = 0.25f)
    {
        var block = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            block[i] = scale * MathF.Sin(2 * MathF.PI * 1000 * i / ChannelRate);
        }

        return block;
    }

    private static RelayedFrame AFrame(string from = "M0LTE") => new()
    {
        SubChannel = 0,
        Mode = "afsk1200",
        From = from,
        To = "GB7RDG",
        LengthBytes = 32,
        SnrDb = 14.5,
        CrcValid = true,
        At = DateTimeOffset.UnixEpoch,
        Raw = [0x01, 0x02, 0x03],
    };

    /// <summary>
    /// A station with no <c>publish</c> block opens no socket, because there is no client to open
    /// one: the relay seam is null and the site never hears from it.
    /// </summary>
    /// <remarks>
    /// This is the claim the whole project rests on and it is why the wiring in <c>Program.cs</c>
    /// is inside one <c>if</c>: the station below is a complete one, hearing audio and listing
    /// frames, sitting next to a monitor that is running and would accept it.
    /// </remarks>
    [Fact]
    public async Task A_Station_With_No_Publish_Block_Opens_No_Socket_At_All()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);

        server.Relay.Should().BeNull("a station is not publishing until it is given a publish block");
        for (int block = 0; block < 25; block++)
        {
            server.ReportFrame(0, "afsk1200", "M0LTE", "GB7RDG", 32, 12.0, true);
        }

        await Task.Delay(250);

        monitor.Attempts.Should().Be(0, "nothing on this station has anywhere to dial out to");
        monitor.Received.Should().BeEmpty();
    }

    /// <summary>The hello is first on the wire, and it says what this station is.</summary>
    [Fact]
    public async Task The_Uplink_Sends_Its_Hello_Before_Anything_Else()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);

        // Frames offered before the socket is even open: none of them may overtake the hello.
        client.Frame(AFrame());
        client.Start();
        client.Frame(AFrame());

        await Until(() => monitor.Received.Count >= 1, "the hello");

        (WebSocketMessageType kind, byte[] payload) = monitor.Received[0];
        kind.Should().Be(WebSocketMessageType.Text);
        JsonElement hello = JsonDocument.Parse(payload).RootElement;
        hello.GetProperty("type").GetString().Should().Be("hello");
        hello.GetProperty("protocol").GetInt32().Should().Be(1);
        hello.GetProperty("callsign").GetString().Should().Be("GB7RDG-2");
        hello.GetProperty("operator").GetString().Should().Be("Tom M0LTE");
        hello.GetProperty("audioRate").GetInt32().Should().Be(PublishedRate);
        hello.GetProperty("blockSamples").GetInt32().Should().Be(BlockSamples);
        hello.GetProperty("dialHz").GetDouble().Should().Be(7047500);
        hello.GetProperty("sideband").GetString().Should().Be("usb");
        hello.GetProperty("frames").GetString().Should().Be("always");

        // The bands come off this station's own waterfall server, because a monitor runs no
        // modems for a relayed station and has no other way to draw the overlays.
        JsonElement modem = hello.GetProperty("modems").EnumerateArray().Single();
        modem.GetProperty("sub").GetInt32().Should().Be(0);
        modem.GetProperty("mode").GetString().Should().Be("afsk1200");
        modem.GetProperty("centreHz").GetDouble().Should().Be(1700);

        monitor.Authorization.Should().Be($"Bearer {Token}",
            "a header, not a query parameter: a query parameter is written to every log between "
            + "here and there");
    }

    /// <summary>
    /// Nothing at all crosses the wire until the monitor says somebody is watching - counted in
    /// bytes, over ten seconds of this station's audio (criterion 4 of 6.2).
    /// </summary>
    /// <remarks>
    /// Ten seconds of audio rather than ten seconds of waiting: the audio is what would be sent,
    /// and 250 blocks of it is what ten seconds of a real station produces. The clock is advanced
    /// by the same ten seconds so the keepalive and the demand watchdog live through it too.
    /// </remarks>
    [Fact]
    public async Task The_Uplink_Sends_No_Audio_Until_A_Viewer_Arrives()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();

        await Until(() => client.Publishing, "the welcome");
        client.Wanted.Should().BeFalse("the monitor has said nobody is watching");

        for (int block = 0; block < 250; block++)
        {
            client.Audio(Tone(ChannelRate * 40 / 1000), transmitted: false);
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        await Task.Delay(250);
        monitor.BytesAfterHello.Should().Be(0,
            "ten seconds of a station's audio, and not a byte of it is anybody's business until "
            + "somebody opens the page");

        await monitor.DemandAsync(1);
        await Until(() => client.Wanted, "the demand");

        for (int block = 0; block < 5; block++)
        {
            client.Audio(Tone(ChannelRate * 40 / 1000), transmitted: false);
        }

        await Until(() => monitor.AudioMessages.Count >= 5, "audio for the viewer");
        monitor.AudioMessages.Should().AllSatisfy(m =>
        {
            m.Length.Should().Be(AudioMessageBytes,
                "the length is fixed by the rate the hello declared");
            m[0].Should().Be((byte)0x02);
            m[1].Should().Be((byte)0, "this is audio the station heard");
        });
    }

    /// <summary>The audio stops with the last viewer, and no half-block outlives them.</summary>
    [Fact]
    public async Task The_Uplink_Stops_Sending_Audio_When_The_Last_Viewer_Leaves()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        var journal = new List<string>();
        await using var client = new UplinkClient(
            server,
            SettingsFor(monitor.Url),
            clock,
            line => { lock (journal) { journal.Add(line); } });
        client.Start();
        await Until(() => client.Publishing, "the welcome");

        await monitor.DemandAsync(2);
        await Until(() => client.Wanted, "the demand");
        for (int block = 0; block < 4; block++)
        {
            client.Audio(Tone(ChannelRate * 40 / 1000), transmitted: false);
        }

        await Until(() => monitor.AudioMessages.Count >= 4, "audio for the viewers");

        await monitor.DemandAsync(0);
        await Until(() => !client.Wanted, "the last viewer leaving");

        int sent = monitor.AudioMessages.Count;
        for (int block = 0; block < 50; block++)
        {
            client.Audio(Tone(ChannelRate * 40 / 1000), transmitted: false);
        }

        await Task.Delay(250);
        monitor.AudioMessages.Should().HaveCount(sent, "nobody is watching any more");

        // Said in pairs. A viewer who arrives and leaves inside a minute used to leave "1
        // watching, sending audio" as the last word on the subject, so the journal read as
        // though the station were still sending.
        string[] said;
        lock (journal)
        {
            said = [.. journal];
        }

        said.Should().ContainSingle(l => l.Contains("watching, sending audio"));
        said.Should().ContainSingle(l => l.Contains("nobody watching, audio stopped"));
    }

    /// <summary>
    /// Decoded frames go up whether or not anybody is watching, which is decision 3 and is what
    /// makes a quiet band look alive to somebody arriving an hour later.
    /// </summary>
    [Fact]
    public async Task The_Uplink_Sends_Frames_Whether_Or_Not_Anybody_Is_Watching()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");
        client.Wanted.Should().BeFalse("nobody is watching, and that is the point of this test");

        client.Frame(AFrame());
        await Until(() => monitor.TextMessagesOfType("frame").Any(), "the frame");

        JsonElement frame = monitor.TextMessagesOfType("frame").Single();
        frame.GetProperty("sub").GetInt32().Should().Be(0);
        frame.GetProperty("mode").GetString().Should().Be("afsk1200");
        frame.GetProperty("from").GetString().Should().Be("M0LTE");
        frame.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.GetProperty("lenBytes").GetInt32().Should().Be(32);
        frame.GetProperty("snrDb").GetDouble().Should().Be(14.5);
        frame.GetProperty("crc").GetBoolean().Should().BeTrue();
        frame.GetProperty("at").GetString().Should().StartWith("1970-01-01T00:00:00");

        // The AX.25 bytes, because the monitor folds its own links panel out of them rather than
        // being sent a summary of one.
        Convert.FromBase64String(frame.GetProperty("raw").GetString()!)
            .Should().Equal([0x01, 0x02, 0x03]);

        // And no audio came with them.
        monitor.AudioMessages.Should().BeEmpty();
    }

    /// <summary>
    /// A planned stop says goodbye first, so the site's journal reads "GB7RDG-2 is shutting down"
    /// rather than "connection closed" (4.2).
    /// </summary>
    [Fact]
    public async Task A_Planned_Stop_Says_Goodbye_Before_The_Socket_Goes()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");

        await client.DisposeAsync();

        await Until(() => monitor.TextMessagesOfType("bye").Any(), "the goodbye");
        monitor.TextMessagesOfType("bye").Single()
            .GetProperty("reason").GetString().Should().Contain("shutting down");
        client.Publishing.Should().BeFalse();
    }

    /// <summary>The station's own transmissions are part of what a viewer hears, flagged as ours.</summary>
    [Fact]
    public async Task The_Uplink_Sends_Its_Own_Transmitted_Audio_Flagged_As_Ours()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");
        await monitor.DemandAsync(1);
        await Until(() => client.Wanted, "the demand");

        client.Audio(Tone(BlockSamples), transmitted: false);
        client.Audio(Tone(BlockSamples), transmitted: true);
        await Until(() => monitor.AudioMessages.Count >= 2, "both blocks");

        // A block is never half one and half the other, so the kind is a property of the block.
        monitor.AudioMessages[0][1].Should().Be((byte)0, "audio the station heard");
        monitor.AudioMessages[1][1].Should().Be((byte)1, "the station's own transmission");
        monitor.AudioMessages.Should().AllSatisfy(m => m.Length.Should().Be(AudioMessageBytes));
    }

    /// <summary>A 48 kHz station publishes at the rate its config asked for, decimated not resampled.</summary>
    [Fact]
    public async Task Forty_Eight_Kilohertz_Audio_Is_Decimated_To_The_Published_Rate()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock, rate: 48000);
        await using var client = new UplinkClient(
            server, SettingsFor(monitor.Url, channelRate: 48000, audioRate: 12000), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");

        JsonElement hello = monitor.TextMessagesOfType("hello").Single();
        hello.GetProperty("audioRate").GetInt32().Should().Be(12000);
        hello.GetProperty("blockSamples").GetInt32().Should().Be(480);

        await monitor.DemandAsync(1);
        await Until(() => client.Wanted, "the demand");

        // One second of 48 kHz audio is one second of 12 kHz audio, which is 25 blocks of 480.
        var second = new float[48000];
        for (int i = 0; i < second.Length; i++)
        {
            second[i] = 0.2f * MathF.Sin(2 * MathF.PI * 1000 * i / 48000f);
        }

        client.Audio(second, transmitted: false);
        await Until(() => monitor.AudioMessages.Count >= 25, "a second of decimated audio");

        await Task.Delay(100);
        monitor.AudioMessages.Should().HaveCount(25,
            "48 kHz in at 4:1 is 12 kHz out, and a second of it is 25 blocks and not 100");
        monitor.AudioMessages.Should().AllSatisfy(m => m.Length.Should().Be(4 + (480 * 2)));
    }

    /// <summary>
    /// A site that cannot be reached is retried on the transport ladder - 1 s doubling to a
    /// 30-second cap - for ever, and the station is not told about it.
    /// </summary>
    [Fact]
    public async Task A_Dropped_Uplink_Retries_On_The_Transport_Ladder()
    {
        var clock = new FakeTimeProvider();
        await using WaterfallWebServer server = StationServer(clock);

        // A port nothing is listening on: the connect fails at the transport, which is the
        // outage every home station will actually see.
        int dead = FreePorts.Next();
        await using var client = new UplinkClient(
            server, SettingsFor($"ws://127.0.0.1:{dead}/uplink"), clock);
        client.Start();

        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        ];

        for (int step = 0; step < expected.Length; step++)
        {
            int taken = step + 1;
            await Until(() => client.RetryWaits.Count >= taken, $"failure {taken}");
            client.RetryWaits[step].Should().Be(expected[step]);
            await UntilAdvancing(
                clock, () => client.ConnectAttempts > taken, $"attempt {taken + 1}");
        }

        client.ConnectAttempts.Should().BeGreaterThanOrEqualTo(expected.Length,
            "it keeps trying, for ever, because a station that quietly stopped publishing would "
            + "not be found out until somebody looked at the site");
        client.RunTask!.IsFaulted.Should().BeFalse();
    }

    /// <summary>
    /// A token the site will not accept is a mistake somebody has to fix, not a condition that
    /// clears itself: an hour between complaints, and quarter-hours between attempts.
    /// </summary>
    [Fact]
    public async Task A_Refused_Token_Backs_Off_To_Quarter_Hours_And_Says_So_Once()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor { Refuse = HttpStatusCode.Unauthorized };
        await using WaterfallWebServer server = StationServer(clock);
        var journal = new List<string>();
        await using var client = new UplinkClient(
            server,
            SettingsFor(monitor.Url),
            clock,
            line => { lock (journal) { journal.Add(line); } });
        client.Start();

        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240),
            TimeSpan.FromSeconds(480), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15),
        ];

        for (int step = 0; step < expected.Length; step++)
        {
            int taken = step + 1;
            await Until(() => client.RetryWaits.Count >= taken, $"refusal {taken}");
            client.RetryWaits[step].Should().Be(expected[step]);
            await UntilAdvancing(
                clock, () => monitor.Attempts > taken, $"attempt {taken + 1}");
        }

        // Six refusals over about half an hour, and one line about it.
        string[] said;
        lock (journal)
        {
            said = [.. journal];
        }

        said.Where(l => l.Contains("token")).Should().ContainSingle(
            "a refused token is said once an hour, not once a minute: it is somebody's mistake to "
            + "fix and saying it every attempt would bury everything else in the journal");
        said.Should().Contain(l => l.StartsWith("publish: ", StringComparison.Ordinal));
        said.Should().Contain(l => l.Contains("the station is unaffected"));
        said.Should().AllSatisfy(
            l => l.Should().MatchRegex("^[\\x20-\\x7e]*$", "journal lines are plain ASCII"));
    }

    /// <summary>
    /// The site being down or hostile costs the station nothing: no fault, no exit code, no
    /// stopped receive path, and a loop that is still going.
    /// </summary>
    [Fact]
    public async Task A_Dropped_Uplink_Leaves_The_Station_Running_And_The_Exit_Code_Alone()
    {
        int exitCodeBefore = Environment.ExitCode;
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor { DropAtOnce = true };
        var channel = new SoundModemChannel(ChannelRate, randomSeed: 7);
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel, new WaterfallOptions
        {
            TimeProvider = clock,
            DeclaredBands = [new DeclaredBand(0, "afsk1200", 1700, 1200)],
        });
        server.Start();
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        server.Relay = client;
        client.Start();

        // A station hearing its band all the way through a flapping uplink.
        await Until(() => client.ConnectAttempts >= 1, "the first attempt");
        for (int block = 0; block < 50; block++)
        {
            channel.ProcessReceive(Tone(ChannelRate / 30));
            server.ReportFrame(0, "afsk1200", "M0LTE", "GB7RDG", 32, 12.0, true);
            if (block % 10 == 0)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
            }
        }

        await UntilAdvancing(clock, () => client.ConnectAttempts >= 2, "a reconnect");

        client.RunTask!.IsFaulted.Should().BeFalse("the uplink is a courtesy and swallows its own");
        client.RunTask.IsCompleted.Should().BeFalse("it retries for ever rather than giving up");
        Environment.ExitCode.Should().Be(exitCodeBefore, "the uplink never touches the exit code");
        typeof(UplinkClient).GetEvents().Should().BeEmpty(
            "there is nothing here to subscribe to, so there is no way for this to fault a station");
        server.Bands.Should().ContainSingle("the station is still the station it was");
    }

    /// <summary>
    /// The whole inbound surface of a published station is <c>demand</c>. Everything else is
    /// counted and dropped, including the shapes that would matter if anything read them.
    /// </summary>
    [Fact]
    public async Task An_Uplink_Ignores_Every_Message_Type_But_Demand()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");

        int ignoredBefore = client.IgnoredMessages;
        int upgradesBefore = monitor.Accepted;
        string[] mischief =
        [
            // The page's own protocol, which this is not.
            "{\"type\":\"config\",\"sampleRate\":48000}",
            "{\"type\":\"audio\",\"on\":true}",

            // A transmit request, in every shape somebody might reach for.
            "{\"type\":\"transmit\",\"frame\":\"AAECAw==\"}",
            "{\"type\":\"kiss\",\"port\":0,\"data\":\"wADCAA==\"}",
            "{\"type\":\"restart\"}",
            "{\"type\":\"welcome\",\"slug\":\"somebody-else\"}",

            // Fields that are not the JSON type they should be. These used to throw
            // InvalidOperationException out of the reader and tear the session down, which is the
            // opposite of "counted and dropped" and would have put every station on the site into
            // a permanent connect-and-drop loop the first time a monitor serialised a number as a
            // string. Counted, not applied: the first sets no viewers and the second is not even
            // a message type.
            "{\"type\":\"demand\",\"viewers\":\"2\"}",
            "{\"type\":1}",
            "{\"type\":\"welcome\",\"url\":7}",

            // And things that are not messages at all.
            "not json at all",
            "{}",
            "[]",

            // Last, so the viewer count below is this one's doing.
            "{\"type\":\"demand\",\"viewers\":1,\"transmit\":true}",
        ];

        foreach (string message in mischief)
        {
            await monitor.SendAsync(message);
        }

        await Until(
            () => client.IgnoredMessages >= ignoredBefore + mischief.Length - 2,
            "every message but the two demands to be dropped");

        // The one with a "viewers" in it did what a demand does and nothing else: no transmit
        // happened because there is nothing here that could make one happen.
        await Until(() => client.Viewers == 1, "the one demand among them");
        client.Wanted.Should().BeTrue();

        // And not one of them cost the session, which is the whole promise of 4.6 layer 2: a
        // monitor that grows a message type, or types a field wrongly, does not take the site's
        // stations off the air.
        client.Publishing.Should().BeTrue();
        monitor.Accepted.Should().Be(upgradesBefore, "nothing above caused a reconnect");

        // A second welcome changed nothing about the session it arrived in.
        monitor.TextMessagesOfType("hello").Should().ContainSingle(
            "nothing above made this station say hello again");
    }

    /// <summary>
    /// The two kinds of audio arrive on two threads at once at the tail of a key-up, and no block
    /// may come out holding any of the other kind's samples.
    /// </summary>
    /// <remarks>
    /// <para>The review's finding, and the reason it mattered: a mixed block is the right length,
    /// so 4.2's length check at the far end cannot see it. What a listener would get is the
    /// station's key-up and the band spliced together, and what the waterfall would draw is the
    /// broadband haze the server's own gate exists to prevent, reproduced in somebody else's
    /// browser.</para>
    /// <para>48 kHz decimated to 12 kHz on purpose, so the per-kind scratch buffer is on the path
    /// rather than skipped. The two kinds are held at opposite signs, so a sample of the wrong one
    /// is unmistakable. The first block seen of each kind is skipped and only that one: its
    /// decimator is filling from silence, and after it each kind's input is a constant, so every
    /// later block is flat.</para>
    /// </remarks>
    [Fact]
    public async Task Two_Threads_Offering_Both_Kinds_At_Once_Never_Mix_Them_In_One_Block()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock, rate: 48000);
        await using var client = new UplinkClient(
            server, SettingsFor(monitor.Url, channelRate: 48000, audioRate: 12000), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");
        await monitor.DemandAsync(1);
        await Until(() => client.Wanted, "the demand");

        const int calls = 200;
        var heard = new float[2400];
        var ours = new float[2400];
        Array.Fill(heard, 0.8f);
        Array.Fill(ours, -0.8f);

        void Offer(float[] block, bool transmitted)
        {
            for (int i = 0; i < calls; i++)
            {
                client.Audio(block, transmitted);
            }
        }

        var received = new Thread(() => Offer(heard, transmitted: false));
        var transmitted = new Thread(() => Offer(ours, transmitted: true));
        received.Start();
        transmitted.Start();
        received.Join();
        transmitted.Join();

        await Until(() => monitor.AudioMessages.Count > 20, "blocks to inspect");
        await Task.Delay(250);

        IReadOnlyList<byte[]> blocks = monitor.AudioMessages;
        blocks.Should().HaveCountGreaterThan(20, "there has to be something to inspect");
        blocks.Should().AllSatisfy(
            b => b.Length.Should().Be(4 + (480 * 2)),
            "a mixed block would be the right length too, which is why length is not the test");

        var seen = new bool[2];
        var mixed = new List<string>();
        foreach (byte[] block in blocks)
        {
            int kind = block[1];
            kind.Should().BeInRange(0, 1);
            if (!seen[kind])
            {
                seen[kind] = true;   // the decimator filling from silence
                continue;
            }

            for (int i = 0; i < 480; i++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(block.AsSpan(4 + (i * 2)));
                bool wrong = kind == 0 ? sample < 0 : sample > 0;
                if (wrong)
                {
                    mixed.Add($"kind {kind} block, sample {i} = {sample}");
                    break;
                }
            }
        }

        mixed.Should().BeEmpty(
            "one block is never half heard and half transmitted (4.2), whichever threads the "
            + "station's audio loop and its display pacer happen to be on");
        seen.Should().AllSatisfy(k => k.Should().BeTrue("both kinds must have reached the site"));
    }

    /// <summary>
    /// A clock that hands the test the repeating timers created on it, so a watchdog tick can be
    /// fired by hand at the moment the thing it cancels has gone.
    /// </summary>
    /// <remarks>
    /// Only repeating timers are kept. <c>Task.Delay</c> on a <see cref="TimeProvider"/> makes a
    /// one-shot timer through the same method, and the reconnect ladder is full of them.
    /// </remarks>
    private sealed class TimerCatcher(FakeTimeProvider inner) : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _repeating = [];

        public IReadOnlyList<(TimerCallback Callback, object? State)> Repeating
        {
            get { lock (_repeating) { return [.. _repeating]; } }
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
            {
                lock (_repeating) { _repeating.Add((callback, state)); }
            }

            return inner.CreateTimer(callback, state, dueTime, period);
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
    }

    /// <summary>
    /// The silence watchdog cannot throw, whatever has been torn down under it. An exception out
    /// of a timer callback has no caller to catch it and ends the process, which would be decision
    /// 8 broken by the one part of this class that was outside a try.
    /// </summary>
    /// <remarks>
    /// Driven rather than raced: the tick is captured as it is created and then fired by hand
    /// after the session it belongs to has ended and disposed the source it cancels, which is
    /// exactly the ordering a real tick lands in when a session ends under it. A plain
    /// <c>Dispose</c> on an <c>ITimer</c> does not wait for a callback already running, so this is
    /// reachable however carefully the disposal is ordered.
    /// </remarks>
    [Fact]
    public async Task The_Silence_Watchdog_Cannot_Throw_When_The_Session_Ends_Under_It()
    {
        var clock = new FakeTimeProvider();
        var catcher = new TimerCatcher(clock);
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);
        var client = new UplinkClient(server, SettingsFor(monitor.Url), catcher);
        client.Start();
        await Until(() => client.Publishing, "the welcome");
        await Until(() => catcher.Repeating.Count >= 1, "the watchdog to be armed");

        (TimerCallback tick, object? state) = catcher.Repeating[0];

        // The session ends and takes its cancellation source with it.
        await client.DisposeAsync();

        // And now the tick that was already in flight lands, with the silence condition true so
        // that it takes its cancelling branch rather than returning early.
        clock.Advance(TimeSpan.FromSeconds(60));
        Action landing = () => tick(state);

        landing.Should().NotThrow(
            "a timer callback is the one place in this class with no caller to catch for it, and "
            + "an unhandled exception there aborts the daemon: decision 8 says a website cannot "
            + "stop a node passing traffic");
    }

    /// <summary>
    /// The sentence the station's status chip has now reaches the site on every session, not just
    /// the next time it changes.
    /// </summary>
    /// <remarks>
    /// A Flex station publishes its frequency reference long before the uplink exists, and
    /// <c>SetRadioStatus</c> fires only on a change, so without this a relayed station's chip
    /// would be empty until the radio said something new, and empty again after every reconnect.
    /// </remarks>
    [Fact]
    public async Task The_Current_Radio_Sentence_Reaches_The_Site_On_Every_Session()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor();
        await using WaterfallWebServer server = StationServer(clock);

        // Before the uplink exists at all, as a Flex station's reference is.
        server.SetRadioStatus("reference: GPSDO locked");

        await using var client = new UplinkClient(server, SettingsFor(monitor.Url), clock);
        client.Start();
        await Until(() => client.Publishing, "the welcome");

        await Until(() => monitor.TextMessagesOfType("radio").Any(), "the status sentence");
        monitor.TextMessagesOfType("radio").First()
            .GetProperty("status").GetString().Should().Be("reference: GPSDO locked");
    }

    /// <summary>
    /// A site that welcomes a station and then drops it, over and over, gets one journal line
    /// every fifteen minutes and nothing louder.
    /// </summary>
    /// <remarks>
    /// Decision 8's standard. The welcome used to clear the "said it once" flag, so a flapping
    /// site wrote a line per cycle - about 2700 a day - with the rate limit never once applying.
    /// </remarks>
    [Fact]
    public async Task A_Site_That_Welcomes_And_Drops_Over_And_Over_Says_So_Once()
    {
        var clock = new FakeTimeProvider();
        await using var monitor = new StubMonitor { DropAfterWelcome = true };
        await using WaterfallWebServer server = StationServer(clock);
        var journal = new List<string>();
        await using var client = new UplinkClient(
            server,
            SettingsFor(monitor.Url),
            clock,
            line => { lock (journal) { journal.Add(line); } });
        client.Start();

        await UntilAdvancing(clock, () => client.ConnectAttempts >= 4, "four flaps", stepSeconds: 5);

        string[] said;
        lock (journal)
        {
            said = [.. journal];
        }

        said.Where(l => l.Contains("Retrying in")).Should().ContainSingle(
            "four sessions, each welcomed and dropped, inside fifteen minutes of the clock");
        client.ConnectAttempts.Should().BeGreaterThanOrEqualTo(4, "it kept trying regardless");
    }

    /// <summary>The record of the ladder is capped, so a site that is down for a year costs nothing.</summary>
    [Fact]
    public async Task The_Reconnect_Ladder_Is_Not_Remembered_For_Ever()
    {
        var clock = new FakeTimeProvider();
        await using WaterfallWebServer server = StationServer(clock);
        int dead = FreePorts.Next();
        await using var client = new UplinkClient(
            server, SettingsFor($"ws://127.0.0.1:{dead}/uplink"), clock);
        client.Start();

        await UntilAdvancing(
            clock, () => client.RetryCount > 70, "seventy failures", stepSeconds: 60);

        client.RetryWaits.Should().HaveCount(64, "the first 64 are kept and the rest counted");
        client.RetryCount.Should().BeGreaterThan(70);
        client.RunTask!.IsFaulted.Should().BeFalse();
    }

    /// <summary>
    /// The uplink holds nothing that can act on the station, by reflection over its fields, so a
    /// later change that adds one fails the build rather than being noticed in review or not.
    /// </summary>
    /// <remarks>
    /// <para>Section 4.6 of the plan, layer 3. The list is exhaustive rather than a search for
    /// known-bad types: a new field of any kind has to be added here deliberately, and the
    /// question "can this act on the radio" gets asked at that moment.</para>
    /// <para><see cref="WaterfallWebServer"/> is on the list and is the only object here with any
    /// connection to the station at all. It is read for one thing, its measured bands, and its own
    /// public surface has no way to transmit: no <c>EnqueueTransmit</c>, no modem, no PTT.</para>
    /// </remarks>
    [Fact]
    public void An_Uplink_Client_Holds_Nothing_That_Can_Transmit()
    {
        FieldInfo[] fields = typeof(UplinkClient)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        string[] forbidden =
        [
            "SoundModemChannel", "IModem", "IPttControl", "KissTcpServer", "ConfigApi",
            "Station", "IAudioOutput", "IAudioInput", "Process", "FileStream",
        ];

        foreach (FieldInfo field in fields)
        {
            forbidden.Should().NotContain(
                field.FieldType.Name.TrimEnd('[', ']', '?'),
                "{0} would give the uplink a way to act on the station", field.Name);
        }

        string[] held = [.. fields.Select(f => f.FieldType.Name).Distinct().Order()];
        held.Should().BeEquivalentTo(
            [
                "Action`1", "Boolean", "CancellationTokenSource", "Channel`1", "Decimator[]",
                "Int16[][]", "Int32", "Int32[]", "Int64", "List`1", "Lock", "Single[][]", "Task",
                "TimeProvider", "UberSdrReconnectPolicy", "UplinkSettings", "Uri",
                "WaterfallWebServer",
            ],
            "the uplink holds a socket, some buffers, a clock and what it is publishing, and "
            + "nothing that could transmit, retune, reconfigure or restart anything. Adding a "
            + "field means adding it here, and asking that question while you do");
    }
}
