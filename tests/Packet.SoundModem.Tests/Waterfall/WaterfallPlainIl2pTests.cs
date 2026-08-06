using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using Packet.SoundModem.Channel;
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
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public WaterfallPlainIl2pTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
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

    private async Task<JsonDocument> FirstFrameAsync(float[] audio)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _channel.ProcessReceive(audio);

        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                var message = JsonDocument.Parse(payload);
                if (message.RootElement.GetProperty("type").GetString() == "frame")
                {
                    return message;
                }

                message.Dispose();
            }
        }
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var got = new List<byte>();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, _cancellation.Token);
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

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
