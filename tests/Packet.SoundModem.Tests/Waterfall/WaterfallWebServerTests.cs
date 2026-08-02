using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class WaterfallWebServerTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public WaterfallWebServerTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePort();
        _server = new WaterfallWebServer(_channel, _port);
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _cancellation.Dispose();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static byte[] TestFrame()
    {
        var frame = new byte[24];
        Write(frame, 0, "GB7RDG", 0, last: false);
        Write(frame, 7, "M0LTE", 9, last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        Encoding.ASCII.GetBytes("hi there").CopyTo(frame, 16);
        return frame;

        static void Write(byte[] frame, int at, string call, int ssid, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
        }
    }

    [Fact]
    public async Task Serves_the_embedded_page_over_http()
    {
        using var http = new HttpClient();

        string page = await http.GetStringAsync($"http://127.0.0.1:{_port}/", _cancellation.Token);

        page.Should().Contain("<!doctype html>").And.Contain("waterfall");
    }

    [Fact]
    public async Task Unknown_paths_return_404()
    {
        using var http = new HttpClient();

        HttpResponseMessage response =
            await http.GetAsync($"http://127.0.0.1:{_port}/nope", _cancellation.Token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Measures_the_modem_band_around_its_audio_centre()
    {
        _server.Bands.Should().HaveCount(1);
        ModemBand band = _server.Bands[0];
        band.Mode.Should().Be("afsk1200");
        // Bell 202 at the 1700 Hz default centre: the 99 % OBW must bracket both tones.
        band.LowHz.Should().BeLessThan(1200);
        band.HighHz.Should().BeGreaterThan(2200);
        band.HighHz.Should().BeLessThan(4000);
        band.CentreHz.Should().BeApproximately(1700, 400);
    }

    [Fact]
    public async Task Our_Own_Transmission_Is_Drawn_But_Marked_As_Ours()
    {
        // Receive processing is gated off while transmitting, so without this the display simply
        // stops for the length of every keyup and its time axis stops meaning anything. Drawn —
        // but under its own line type, because a burst of your own must not read as a strong
        // station.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        // Transmit for real, so this exercises the path the transmitter loop actually takes.
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        using var transmitCancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), transmitCancel.Token);
        await _channel.EnqueueTransmit(0, TestFrame()).WaitAsync(TimeSpan.FromSeconds(15));

        byte[]? txLine = null;
        for (int i = 0; i < 20 && txLine is null; i++)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Binary && payload[0] == 0x03)
            {
                txLine = payload;
            }
        }

        txLine.Should().NotBeNull("a transmission must still produce waterfall lines");
        txLine!.Length.Should().Be(5 + 1024, "a transmitted line is shaped like any other");

        await transmitCancel.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Receive_Audio_Is_Sent_Only_To_A_Browser_That_Asked_For_It()
    {
        // Opening the page to look at a waterfall must not quietly start pulling ~24 KB/s, and
        // several viewers must not each cost that unless they each asked.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var tone = new float[SampleRate];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = 0.3f * MathF.Sin(2 * MathF.PI * 1700 * n / SampleRate);
        }

        _channel.ProcessReceive(tone);
        var seenBefore = new List<byte>();
        for (int i = 0; i < 5; i++)
        {
            (_, byte[] payload) = await Receive(socket);
            seenBefore.Add(payload[0]);
        }

        seenBefore.Should().NotContain((byte)0x02, "audio is off until the browser asks");

        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes("""{"type":"audio","on":true}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);

        // Keep feeding and reading until audio appears: the request is applied by the server's
        // receive loop asynchronously, and the per-client queue drops oldest when it backs up,
        // so a single shot can race either way.
        byte[]? audio = null;
        for (int round = 0; round < 20 && audio is null; round++)
        {
            await Task.Delay(50);
            _channel.ProcessReceive(tone);
            for (int i = 0; i < 20 && audio is null; i++)
            {
                (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
                if (kind == WebSocketMessageType.Binary && payload[0] == 0x02)
                {
                    audio = payload;
                }
            }
        }

        audio.Should().NotBeNull("audio must flow once asked for");
        // [0x02][pad×3][s16 LE mono], 40 ms at the channel rate.
        (audio!.Length - WaterfallWebServer.AudioHeaderBytes).Should().Be(SampleRate * 40 / 1000 * 2);
        // The header must keep the samples on a 2-byte boundary: a browser views them as an
        // Int16Array, which throws outright on an unaligned offset. A 1-byte header did.
        (WaterfallWebServer.AudioHeaderBytes % 2).Should().Be(
            0, "an unaligned payload cannot be read by the client at all");
    }

    [Fact]
    public async Task Streams_config_lines_and_frame_events_over_the_websocket()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);

        // 1: the config message arrives first
        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Text);
        using JsonDocument config = JsonDocument.Parse(payload);
        config.RootElement.GetProperty("type").GetString().Should().Be("config");
        config.RootElement.GetProperty("sampleRate").GetInt32().Should().Be(SampleRate);
        config.RootElement.GetProperty("linesPerSecond").GetInt32().Should().Be(30);
        config.RootElement.GetProperty("modems").GetArrayLength().Should().Be(1);

        // 2: audio produces binary spectrum lines [0x01][u32 index][bins]
        var tone = new float[SampleRate];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = 0.3f * MathF.Sin(2 * MathF.PI * 1700 * n / SampleRate);
        }

        _channel.ProcessReceive(tone);
        (kind, payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Binary);
        payload[0].Should().Be(0x01);
        payload.Length.Should().Be(5 + 1024);

        // 3: a decoded frame produces an attributed frame event
        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        _channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        _channel.ProcessReceive(new float[SampleRate / 4]);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (kind, payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            frame = JsonDocument.Parse(payload);
        }

        frame.RootElement.GetProperty("type").GetString().Should().Be("frame");
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE-9");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(0);
        frame.RootElement.GetProperty("mode").GetString().Should().Be("afsk1200");
        frame.RootElement.GetProperty("snrDb").GetDouble().Should().BeGreaterThan(3);
        frame.RootElement.GetProperty("burstLines").GetInt32().Should().BeGreaterThan(5);
        frame.Dispose();
    }

    [Fact]
    public async Task Transmit_Metering_Reaches_The_Page_And_Clears_On_Key_Up()
    {
        // Forward power and SWR while keyed. Cleared on key-up rather than left showing the last
        // reading, which would otherwise look like a station transmitting into a dummy load
        // forever.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // config

        _server.SetTransmitStatus("TX 29.4 W · SWR 1.2");
        JsonDocument keyed = await NextTextAsync(socket);
        keyed.RootElement.GetProperty("type").GetString().Should().Be("tx");
        keyed.RootElement.GetProperty("status").GetString().Should().Be("TX 29.4 W · SWR 1.2");
        keyed.Dispose();

        _server.SetTransmitStatus(null);
        JsonDocument unkeyed = await NextTextAsync(socket);
        unkeyed.RootElement.GetProperty("type").GetString().Should().Be("tx");
        unkeyed.RootElement.GetProperty("status").ValueKind.Should().Be(
            JsonValueKind.Null, "an unkeyed transmitter has nothing to report");
        unkeyed.Dispose();
    }

    [Fact]
    public async Task An_Unchanged_Transmit_Reading_Is_Not_Rebroadcast()
    {
        // The meters update many times a second; only changes are worth a message.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);

        _server.SetTransmitStatus("TX 29.4 W");
        (await NextTextAsync(socket)).Dispose();

        _server.SetTransmitStatus("TX 29.4 W");
        _server.SetTransmitStatus("TX 30.1 W");

        JsonDocument next = await NextTextAsync(socket);
        next.RootElement.GetProperty("status").GetString().Should()
            .Be("TX 30.1 W", "the repeat must not have been sent");
        next.Dispose();
    }

    private async Task<JsonDocument> NextTextAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                return JsonDocument.Parse(payload);
            }
        }
    }

    [Fact]
    public async Task A_Frame_From_A_Demodulator_Outside_The_Channel_Is_Listed_Like_Any_Other()
    {
        // ARDOP demodulates inside its virtual TNC, so its frames never raise the channel event
        // every other modem's do. Before this the ARDOP band was drawn and its bursts painted,
        // and the decode panel stayed empty for it forever.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // the config message

        _server.ReportFrame(
            subChannel: 2, mode: "ConReq500M", from: "M0LTE", to: "GB7RDG",
            lengthBytes: 22, snrDb: 14.0, decodedOk: true);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("type").GetString().Should().Be("frame");
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(2);
        frame.RootElement.GetProperty("mode").GetString().Should()
            .Be("ConReq500M", "with ARDOP the frame type is most of what the entry says");
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("lenBytes").GetInt32().Should().Be(22);
        frame.RootElement.GetProperty("snrDb").GetDouble().Should()
            .Be(14.0, "the demodulator's own report beats anything the band tracker can infer");
        frame.RootElement.GetProperty("crc").GetBoolean().Should().BeTrue();
        frame.Dispose();
    }

    [Fact]
    public async Task Reporting_A_Frame_Before_The_Server_Starts_Is_Harmless()
    {
        // Start order is the daemon's business, not a reason to crash a receive thread.
        await using var unstarted = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 3), FreePort());

        Action report = () => unstarted.ReportFrame(2, "IDFrame", "M0LTE", null, 0, null, true);

        report.Should().NotThrow();
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        int filled = 0;
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, filled, buffer.Length - filled), _cancellation.Token);
            filled += result.Count;
            if (result.EndOfMessage)
            {
                return (result.MessageType, buffer[..filled]);
            }
        }
    }
}
