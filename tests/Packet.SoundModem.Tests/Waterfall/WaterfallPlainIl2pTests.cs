using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// What the waterfall is told about a frame read as plain IL2P: that it was, and whether the host
/// got it. Real audio through a real <c>bpsk300</c> modem, so this is the GB7BPQ case arriving at a
/// browser rather than a hand-built quality struct.
/// </summary>
/// <remarks>
/// The panel is the one place an operator sees the mode name and the decode side by side, which is
/// exactly where "bpsk300-il2pc" and "nothing but Reed-Solomon checked this" contradict each other.
/// Its own two fields rather than the page inferring it from a null <c>crc</c>: <c>crc</c> is null
/// on our own transmissions, on the HDLC modes and on ARDOP too, so that inference would badge
/// three things to be right about one.
/// </remarks>
public class WaterfallPlainIl2pTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;

    public WaterfallPlainIl2pTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
        _port = FreePorts.Next();
        _server = new WaterfallWebServer(_channel, _port);
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    /// <summary>
    /// A fresh deadline for one await, rather than one deadline shared by the whole test.
    /// </summary>
    /// <remarks>
    /// A single test-lifetime budget has to cover the connect, the config message, the
    /// <em>synchronous</em> demodulation of a 300 baud burst through a nine-branch bank, and only
    /// then the receive it was actually written to bound. Running the whole suite in parallel on a
    /// loaded box, the work in front can eat most of it, and what surfaces is a
    /// <c>TaskCanceledException</c> out of a socket read that had seconds rather than the thirty
    /// it looks like it has. Measured here at roughly one failure per six full Release runs.
    /// Per-await budgets mean each wait is bounded by how long that wait takes.
    /// </remarks>
    private static CancellationTokenSource Budget() => new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_Withheld_Plain_Frame_Reaches_The_Browser_Marked_As_Both()
    {
        JsonDocument message = await FirstFrameAsync(Transmission("bpsk300-nocrc"));

        message.RootElement.GetProperty("from").GetString().Should().Be("GB7BPQ",
            "the operator's question was who that unreadable burst was, and this is the answer");
        message.RootElement.GetProperty("plain").GetBoolean().Should().BeTrue(
            "Reed-Solomon alone stood behind it, whatever the mode label says");
        message.RootElement.GetProperty("monitorOnly").GetBoolean().Should().BeTrue(
            "and by default it stops here rather than going to the host");
        message.RootElement.GetProperty("crc").ValueKind.Should().Be(JsonValueKind.Null);
        message.Dispose();
    }

    [Fact]
    public async Task An_Ordinary_Frame_Is_Marked_As_Neither()
    {
        JsonDocument message = await FirstFrameAsync(Transmission("bpsk300"));

        message.RootElement.GetProperty("crc").GetBoolean().Should().BeTrue();
        message.RootElement.GetProperty("plain").ValueKind.Should().Be(
            JsonValueKind.Null, "a CRC stood behind this one, and absent means no");
        message.RootElement.GetProperty("monitorOnly").ValueKind.Should().Be(JsonValueKind.Null);
        message.Dispose();
    }

    /// <summary>
    /// The operator's rule, over real audio: an RS-only reading is listed and is not a link.
    /// </summary>
    /// <remarks>
    /// Reed-Solomon alone stood behind these bytes, which is why the station keeps them from its
    /// host; the pair of callsigns in them is exactly as unverified as the rest of the frame. So
    /// the links pane - which says who is talking to whom, and is read as a statement about the
    /// channel - must not open a card for GB7BPQ on the strength of it, and the frames panel must
    /// go on listing it badged RS ONLY, because that is where an operator judges it for what it
    /// is. The whole burst has been fed by the time the row arrives, so an empty pane here is the
    /// pane the browser would see and not a race with the decoder.
    /// </remarks>
    [Fact]
    public async Task A_Withheld_Plain_Frame_Is_Listed_And_Makes_No_Link()
    {
        JsonDocument message = await FirstFrameAsync(Transmission("bpsk300-nocrc"));

        message.RootElement.GetProperty("from").GetString().Should().Be("GB7BPQ",
            "the frames panel lists it exactly as it did before");
        message.RootElement.GetProperty("monitorOnly").GetBoolean().Should().BeTrue();
        message.Dispose();

        _server.Links.Snapshot().Should().BeEmpty(
            "an unverified reading names no station as heard and opens no link");
    }

    /// <summary>
    /// And the same station saying the same thing with a CRC behind it still does, so what was
    /// removed is the unverified reading rather than the beacon.
    /// </summary>
    [Fact]
    public async Task A_Verified_Frame_From_The_Same_Station_Still_Makes_Its_Link()
    {
        JsonDocument message = await FirstFrameAsync(Transmission("bpsk300"));

        message.RootElement.GetProperty("crc").GetBoolean().Should().BeTrue();
        message.Dispose();

        _server.Links.Snapshot().Should().ContainSingle(
            "a frame something checked is evidence of who was on the channel")
            .Which.Id.Should().Be("0|BEACON<>GB7BPQ");
    }

    /// <summary>
    /// And the same frame after a restart: written to a real frame log, replayed out of it into
    /// a fresh server's opening backlog, and still marked as both.
    /// </summary>
    /// <remarks>
    /// The panel's backlog is the only thing a browser sees of a channel that was busy before the
    /// daemon was restarted. The badge is drawn from <c>plain</c>, so a backlog row that dropped
    /// it listed GB7BPQ as though something had checked the frame - the one reading the badge
    /// exists to prevent, and the one an operator gets for the whole quiet spell after a restart.
    /// Real audio into a real SQLite file and back out through a socket, so what is pinned is the
    /// whole path rather than either end of it.
    /// </remarks>
    [Fact]
    public async Task A_Withheld_Plain_Frame_Replayed_From_The_Log_Is_Still_Marked_As_Both()
    {
        string directory = Directory.CreateTempSubdirectory("pdnsm-rsonly").FullName;
        string path = Path.Combine(directory, "frames.db");
        try
        {
            // The station that heard it, wired the way StationFactory wires one: a real bpsk300
            // bank, and every frame it decodes written down with its quality.
            var heard = new SoundModemChannel(SampleRate, randomSeed: 7);
            heard.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
            await using (FrameLog writing = FrameLog.Open(path))
            {
                heard.FrameReceivedWithQuality += (sub, frame, quality) =>
                    writing.Record(sub, frame, quality, audioHz: 2150, rfHz: 7_051_600);
                heard.ProcessReceive(Transmission("bpsk300-nocrc"));
                for (int i = 0; i < 100 && writing.Recent(10).Count == 0; i++)
                {
                    await Task.Delay(20);
                }

                writing.Recent(10).Should().ContainSingle("the burst has to have been logged")
                    .Which.From.Should().Be("GB7BPQ");
            }

            // And the station that comes up next, on the file the one above left behind.
            await using FrameLog restarted = FrameLog.Open(path);
            int port = FreePorts.Next();
            await using var server = new WaterfallWebServer(
                new SoundModemChannel(SampleRate, randomSeed: 7), port,
                new WaterfallOptions { FrameHistory = restarted.Recent });
            server.Start();

            using var socket = new ClientWebSocket();
            using (CancellationTokenSource connecting = Budget())
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), connecting.Token);
            }

            await Receive(socket);   // config
            (_, byte[] payload) = await Receive(socket);
            using JsonDocument history = JsonDocument.Parse(payload);
            history.RootElement.GetProperty("type").GetString().Should().Be("history");
            JsonElement row = history.RootElement.GetProperty("frames")[0];

            row.GetProperty("from").GetString().Should().Be("GB7BPQ");
            row.GetProperty("plain").GetBoolean().Should().BeTrue(
                "the badge a live row carried is the badge the replayed row carries");
            row.GetProperty("monitorOnly").GetBoolean().Should().BeTrue(
                "and the tooltip still says the host was not given it");
            row.GetProperty("crc").ValueKind.Should().Be(JsonValueKind.Null);
            row.GetProperty("hist").GetBoolean().Should().BeTrue("it is still a backlog row");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<JsonDocument> FirstFrameAsync(float[] audio)
    {
        using var socket = new ClientWebSocket();
        using (CancellationTokenSource connecting = Budget())
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), connecting.Token);
        }

        await Receive(socket);   // config

        // Read while the audio is fed, the way a browser does, rather than pushing it all in and
        // only then looking. The server's per-client queue is bounded at about a second of
        // messages and drops the OLDEST when it overflows, because a display that has fallen
        // behind should show the newest spectrum and not a backlog. Four seconds of 300 baud
        // audio in one synchronous call puts something like 120 spectrum lines behind the frame
        // message and evicts it - the server behaving exactly as designed, and a test that has to
        // behave like a browser to see the thing it is asserting about. Measured before the fix:
        // about one failure per four full parallel runs, and none at all in 40 runs of this class
        // on its own, which is the signature of a queue that only overflows when the send loop is
        // competing for a thread.
        Task<JsonDocument> reader = Task.Run(() => ReadUntilFrameAsync(socket));
        for (int at = 0; at < audio.Length; at += SampleRate / 10)
        {
            _channel.ProcessReceive(audio.AsSpan(at, Math.Min(SampleRate / 10, audio.Length - at)));
            await Task.Delay(1);   // let the reader drain what that tenth of a second produced
        }

        return await reader;
    }

    private static async Task<JsonDocument> ReadUntilFrameAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetString() == "frame")
            {
                return message;
            }

            message.Dispose();
        }
    }

    private static async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var got = new List<byte>();
        WebSocketReceiveResult result;
        do
        {
            using CancellationTokenSource reading = Budget();
            result = await socket.ReceiveAsync(buffer, reading.Token);
            got.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        }
        while (!result.EndOfMessage);

        return (result.MessageType, [.. got]);
    }

    private static float[] Transmission(string mode)
    {
        float[] burst = ModemCatalog.Create(mode, SampleRate, static _ => { })
            .Modulate(Il2pReceiverTests.Gb7bpqBeacon(), txDelayMilliseconds: 300);
        int pad = SampleRate / 2;
        var audio = new float[pad + burst.Length + pad];
        burst.CopyTo(audio, pad);
        return audio;
    }

}
