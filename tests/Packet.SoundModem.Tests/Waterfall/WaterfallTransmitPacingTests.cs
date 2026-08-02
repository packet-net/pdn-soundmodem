using System.Diagnostics;
using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// How our own transmissions are paced onto the waterfall.
/// </summary>
/// <remarks>
/// <para>The transmit path hands the waterfall a whole keyup in one call: the modulator produces
/// the burst as a single array long before the sound card has played a sample of it. Painted on
/// arrival, a two-second burst became sixty lines inside a few milliseconds followed by two
/// seconds of nothing — receive processing is gated off while transmitting, so there is no other
/// line source during a keyup. That is what "juddery, hangs at the start of TX, renders large
/// chunks at a time" looks like from the browser.</para>
/// <para>Receive audio has no such problem: it arrives from the sound card in real time. So the
/// transmit side is queued and released at the rate real time passes, which is the rate the audio
/// is actually leaving the radio.</para>
/// </remarks>
public class WaterfallTransmitPacingTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int LinesPerSecond = 30;

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 7);
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));

    public WaterfallTransmitPacingTests()
    {
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePort();
        _server = new WaterfallWebServer(
            _channel, _port, new WaterfallOptions { LinesPerSecond = LinesPerSecond });
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
    public async Task A_Keyup_Paints_Across_Its_Own_Duration_Rather_Than_In_One_Lurch()
    {
        using ClientWebSocket socket = await ConnectAsync();

        // A frame big enough to be over a second of AFSK1200 — long enough that "all at once"
        // and "spread over the burst" cannot be confused for one another.
        Task transmitting = TransmitAsync(new byte[220]);

        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 25);
        await transmitting;

        arrivals.Should().HaveCountGreaterThanOrEqualTo(25);

        // The bug, as an assertion: every line landing inside a few milliseconds.
        (arrivals[^1] - arrivals[0]).Should().BeGreaterThan(
            500, "the burst must paint across its own duration, not in a single instant chunk");

        // And no one gap may swallow most of the keyup — the "hangs at the start" half of the
        // symptom, which a spread-out first-to-last alone would not catch.
        long widestGap = 0;
        for (int i = 1; i < arrivals.Count; i++)
        {
            widestGap = Math.Max(widestGap, arrivals[i] - arrivals[i - 1]);
        }

        widestGap.Should().BeLessThan(
            250, "lines must keep coming through the keyup rather than stalling");
    }

    [Fact]
    public async Task Lines_Arrive_At_About_The_Display_Rate_Not_Faster()
    {
        using ClientWebSocket socket = await ConnectAsync();

        Task transmitting = TransmitAsync(new byte[220]);
        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 30);
        await transmitting;

        // 30 lines/s, so 30 lines is about a second. Generous either side: this is asserting that
        // the display is paced to the air at all, not measuring timer accuracy.
        double perLine = (arrivals[^1] - arrivals[0]) / (double)(arrivals.Count - 1);

        perLine.Should().BeInRange(
            1000.0 / LinesPerSecond * 0.5,
            1000.0 / LinesPerSecond * 3.0,
            "a transmitted second must take about a second of waterfall");
    }

    [Fact]
    public async Task Painting_Our_Own_Burst_Never_Holds_Up_The_Transmitter()
    {
        // The pacing must sit behind the event, not in front of it. A display that made the
        // transmit loop wait a second per second of audio would wreck channel timing outright.
        using ClientWebSocket socket = await ConnectAsync();

        var clock = Stopwatch.StartNew();
        await TransmitAsync(new byte[220]);
        clock.Stop();

        // FakeAudioOutput drains instantly, so this is the handover cost and nothing else.
        clock.ElapsedMilliseconds.Should().BeLessThan(
            700, "the transmitter must hand the burst over in an instant, not play it out");

        // And the display is still painting it afterwards — the point of doing it this way.
        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 10);
        arrivals.Should().HaveCountGreaterThanOrEqualTo(10);
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await ReceiveAsync(socket);   // the config message
        return socket;
    }

    /// <summary>Runs a real transmit through the transmitter loop the daemon uses.</summary>
    private async Task TransmitAsync(byte[] frame)
    {
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
        await _channel.EnqueueTransmit(0, frame).WaitAsync(TimeSpan.FromSeconds(20));
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Arrival times of transmitted (0x03) waterfall lines, in milliseconds.</summary>
    private async Task<List<long>> CollectTransmitLinesAsync(ClientWebSocket socket, int atLeast)
    {
        var arrivals = new List<long>();
        var clock = Stopwatch.StartNew();
        while (arrivals.Count < atLeast && clock.ElapsedMilliseconds < 20_000)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Binary && payload.Length > 0 && payload[0] == 0x03)
            {
                arrivals.Add(clock.ElapsedMilliseconds);
            }
        }

        return arrivals;
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, _cancellation.Token);
        return (result.MessageType, buffer[..result.Count]);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
