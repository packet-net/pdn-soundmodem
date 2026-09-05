using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The transmitter test's half of the page: what the operator's page is offered, what a public
/// page is not, and that a click reaches the station with the operator's own numbers on it.
/// </summary>
/// <remarks>
/// The absence is the assertion that matters. A public page is not sent the block that describes
/// the control, so a visitor's page does not contain it at all - the hiding the public flavour
/// does to the dial and the level sliders is a second line of defence here, not the first one.
/// </remarks>
public class WaterfallTxTestTests : IAsyncLifetime
{
    private const int Rate = 12000;

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
    private readonly List<TxTestRequest> _asked = [];
    private int _stops;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private TxTestControl Control() => new()
    {
        DefaultSeconds = 5,
        MaxSeconds = 30,
        Presets = [.. TestTone.BesselNullTonesHz.Select(TxTestPreset.For)],
        Start = _asked.Add,
        Stop = () => _stops++,
    };

    private static SoundModemChannel Channel()
    {
        var channel = new SoundModemChannel(Rate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", Rate, sink));
        return channel;
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

    private async Task<ClientWebSocket> OpenAsync(int port)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        return socket;
    }

    [Fact]
    public async Task The_Operators_Page_Is_Offered_The_Control_With_Its_Presets()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            Channel(), port, new WaterfallOptions { TxTest = Control() });
        server.Start();

        using ClientWebSocket socket = await OpenAsync(port);
        (_, byte[] payload) = await Receive(socket);
        using JsonDocument config = JsonDocument.Parse(payload);

        JsonElement test = config.RootElement.GetProperty("txTest");
        test.GetProperty("defaultSeconds").GetDouble().Should().Be(5);
        test.GetProperty("maxSeconds").GetDouble().Should().Be(30);
        test.GetProperty("lowToneHz").GetDouble().Should().Be(700);
        test.GetProperty("highToneHz").GetDouble().Should().Be(1900);

        // The four FM presets, each carrying the deviation its null calibrates, so the page can
        // put the number the operator is aiming at beside the tone rather than making them
        // multiply by 2.405 in their head.
        JsonElement presets = test.GetProperty("presets");
        presets.GetArrayLength().Should().Be(4);
        presets.EnumerateArray().Select(p => p.GetProperty("toneHz").GetDouble())
            .Should().Equal([500, 999, 1248, 2079]);
        presets.EnumerateArray().Select(p => p.GetProperty("deviationHz").GetDouble())
            .Should().Equal([1202, 2403, 3001, 5000]);
    }

    [Fact]
    public async Task A_Page_With_No_Control_Installed_Is_Not_Told_About_One()
    {
        // Every public page, every relayed one, and every station without a transmitter: the
        // field is null rather than an object with a refusal in it, so there is nothing for a
        // page to draw and nothing for one to send.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            Channel(), port, new WaterfallOptions { Public = true });
        server.Start();

        using ClientWebSocket socket = await OpenAsync(port);
        (_, byte[] payload) = await Receive(socket);
        using JsonDocument config = JsonDocument.Parse(payload);

        config.RootElement.GetProperty("publicMonitor").GetBoolean().Should().BeTrue();
        config.RootElement.GetProperty("txTest").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_Request_From_A_Page_With_No_Control_Installed_Does_Nothing_At_All()
    {
        // The message a page could send by hand, or an old tab could send after the control was
        // taken away. There is nobody to refuse on its behalf, so it is dropped.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            Channel(), port, new WaterfallOptions { Public = true });
        server.Start();

        using ClientWebSocket socket = await OpenAsync(port);
        await Receive(socket);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"txtest","twoTone":true,"seconds":5}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);

        await Task.Delay(200, _cancellation.Token);
        _asked.Should().BeEmpty();
        _stops.Should().Be(0);
        socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_Click_Arrives_As_The_Operator_Sent_It_And_Is_Not_Second_Guessed()
    {
        // The page asks for what it asked for; every rule about it - the cap, the tone range,
        // whether this station may transmit at all - belongs to the daemon, so that a page and
        // the --two-tone switch cannot end up with two different sets of them.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            Channel(), port, new WaterfallOptions { TxTest = Control() });
        server.Start();

        using ClientWebSocket socket = await OpenAsync(port);
        await Receive(socket);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"txtest","twoTone":false,"toneHz":999,"seconds":600}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);
        while (_asked.Count == 0)
        {
            await Task.Delay(20, _cancellation.Token);
        }

        _asked.Should().ContainSingle().Which.Should().Be(new TxTestRequest(false, 999, 600));

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"txtest","stop":true}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);
        while (_stops == 0)
        {
            await Task.Delay(20, _cancellation.Token);
        }

        _stops.Should().Be(1);
        _asked.Should().ContainSingle("a stop is not a second test");
    }

    [Fact]
    public async Task A_Test_Transmission_Is_Listed_In_The_Frames_Panel_Like_Any_Other_Keyup()
    {
        // So that a burst of tones is not an unexplained signal - here, and on the public monitor
        // of a station that publishes to one, which is where somebody else would otherwise see a
        // transmission nothing accounts for.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(Channel(), port);
        server.Start();

        using ClientWebSocket socket = await OpenAsync(port);
        await Receive(socket);

        const string what = "tx test: two-tone 700+1900 Hz, 5.0 s, peak level 0.80 - done, 5.0 s on air";
        server.ReportTestTransmission(0, what, what.Length);

        JsonElement frame = default;
        for (int i = 0; i < 40 && frame.ValueKind == JsonValueKind.Undefined; i++)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            using JsonDocument message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetString() == "frame")
            {
                frame = message.RootElement.Clone();
            }
        }

        frame.ValueKind.Should().Be(JsonValueKind.Object, "the panel is told about the keyup");
        frame.GetProperty("tx").GetBoolean().Should().BeTrue("it was a transmission");
        frame.GetProperty("mode").GetString().Should().Be(WaterfallWebServer.TestTransmissionMode);
        frame.GetProperty("why").GetString().Should().Be(what, "and the row says what it was");
        frame.GetProperty("from").ValueKind.Should().Be(
            JsonValueKind.Null, "a tone burst carries no callsign, and inventing one would be a lie");
    }

    [Fact]
    public async Task Every_Open_Page_Hears_What_Became_Of_A_Test()
    {
        // Two tabs on one station must not disagree about whether it is transmitting.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            Channel(), port, new WaterfallOptions { TxTest = Control() });
        server.Start();

        using ClientWebSocket first = await OpenAsync(port);
        using ClientWebSocket second = await OpenAsync(port);
        await Receive(first);
        await Receive(second);

        server.ReportTxTest(new TxTestStatus("running", "two-tone 700+1900 Hz, 5.0 s"));

        foreach (ClientWebSocket socket in new[] { first, second })
        {
            JsonElement status = default;
            for (int i = 0; i < 40 && status.ValueKind == JsonValueKind.Undefined; i++)
            {
                (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
                if (kind != WebSocketMessageType.Text)
                {
                    continue;
                }

                using JsonDocument message = JsonDocument.Parse(payload);
                if (message.RootElement.GetProperty("type").GetString() == "txtest")
                {
                    status = message.RootElement.Clone();
                }
            }

            status.GetProperty("state").GetString().Should().Be("running");
            status.GetProperty("text").GetString().Should().Contain("700+1900");
        }
    }

    [Fact]
    public async Task A_Control_Installed_After_Start_Up_Reaches_The_Next_Page()
    {
        // Which is how the daemon installs it: whether the station can key at all is settled by
        // opening the sound card and the PTT line, long after the page is being served.
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(Channel(), port);
        server.Start();

        using (ClientWebSocket before = await OpenAsync(port))
        {
            (_, byte[] payload) = await Receive(before);
            using JsonDocument config = JsonDocument.Parse(payload);
            config.RootElement.GetProperty("txTest").ValueKind.Should().Be(JsonValueKind.Null);
        }

        server.SetTxTest(Control());

        using ClientWebSocket after = await OpenAsync(port);
        (_, byte[] later) = await Receive(after);
        using JsonDocument offered = JsonDocument.Parse(later);
        offered.RootElement.GetProperty("txTest").GetProperty("maxSeconds").GetDouble()
            .Should().Be(30);
    }
}
