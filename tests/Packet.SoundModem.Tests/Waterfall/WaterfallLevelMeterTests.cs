using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The input level message: what an operator's page is sent about the audio arriving from the
/// sound card, and the two things that must be true of it - that it is measured where it says it
/// is, and that a station nobody is watching does no work for it.
/// </summary>
/// <remarks>
/// <para>Tom, 2026-09-06, with the Mixer group open on the bench Pi: "Some assistance in setting
/// the capture level would be useful. No way for the user to know what good means." The slider
/// was bounded by the card's own dB range, which says what can be asked for and nothing about
/// what should be.</para>
/// <para>A <see cref="FakeTimeProvider"/> throughout, because the message is paced at five a
/// second and a test that waited for real intervals would be a test that fails on a loaded
/// runner. The audio is fed by hand for the same reason.</para>
/// </remarks>
public class WaterfallLevelMeterTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
    private readonly FakeTimeProvider _clock = new();
    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;

    public WaterfallLevelMeterTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePorts.Next();
        _server = new WaterfallWebServer(
            _channel, _port,
            new WaterfallOptions { TimeProvider = _clock, InputLevelMeter = true });
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

    /// <summary>One cycle-aligned sine block, so its peak and RMS are known exactly.</summary>
    private static float[] Tone(float amplitude, int samples = 1200)
    {
        var block = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            block[n] = amplitude * MathF.Sin(2 * MathF.PI * 100 * n / samples);
        }

        return block;
    }

    /// <summary>
    /// Feeds a block, ends the interval, and feeds another - which is when a reading is produced,
    /// because the meter is only asked whether the interval is over when audio arrives.
    /// </summary>
    private void FeedAcrossAnInterval(float[] block)
    {
        _channel.ProcessReceive(block);
        _clock.Advance(InputLevelMeter.DefaultInterval);
        _channel.ProcessReceive(block);
    }

    private async Task<ClientWebSocket> AttachAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);

        // The config message is sent from the connection's own task after the client is in the
        // list, so waiting for it is also how this test knows the server counts a viewer.
        using JsonDocument config = await NextAsync(socket, "config");
        return socket;
    }

    /// <summary>The next text message of a given type, ignoring everything else.</summary>
    private async Task<JsonDocument> NextAsync(
        ClientWebSocket socket, string type, CancellationToken? token = null)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer, token ?? _cancellation.Token);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            if (message.RootElement.TryGetProperty("type", out JsonElement kind)
                && kind.GetString() == type)
            {
                return message;
            }

            message.Dispose();
        }
    }

    /// <summary>
    /// The reading the page draws its bar from, measured on the audio the modems are hearing.
    /// </summary>
    [Fact]
    public async Task An_Open_Page_Is_Sent_The_Level_Of_What_The_Station_Is_Hearing()
    {
        using ClientWebSocket page = await AttachAsync();

        // 0.25 full scale: -12.04 dBFS peak, -15.05 dBFS RMS, and squarely inside the band this
        // repository has measured as good on real hardware.
        FeedAcrossAnInterval(Tone(0.25f));

        using JsonDocument level = await NextAsync(page, "level");

        level.RootElement.GetProperty("peak").GetDouble().Should().BeApproximately(-12.0, 0.1);
        level.RootElement.GetProperty("rms").GetDouble().Should().BeApproximately(-15.1, 0.1);
        level.RootElement.GetProperty("clip").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// A capture that has hit the end of the 16-bit scale says so, which is the one reading on
    /// the meter that has to be acted on rather than noticed.
    /// </summary>
    [Fact]
    public async Task A_Clipped_Capture_Is_Reported_As_Clipped()
    {
        using ClientWebSocket page = await AttachAsync();

        var block = new float[600];
        Array.Fill(block, Pcm16.ToFloat(short.MinValue));
        FeedAcrossAnInterval(block);

        using JsonDocument level = await NextAsync(page, "level");

        level.RootElement.GetProperty("clip").GetBoolean().Should().BeTrue();
        level.RootElement.GetProperty("peak").GetDouble().Should().BeApproximately(0, 0.05);
    }

    /// <summary>
    /// Nothing is measured while nobody is watching, and nothing measured then is kept.
    /// </summary>
    /// <remarks>
    /// The same rule the audio relay keeps, and the reason a station serving nobody pays nothing
    /// for this. The reset half matters as much as the skip: without it, the first reading a
    /// page is sent on arrival would be a peak from whenever the last page left, which on a
    /// station that clipped once overnight is a clip light on a browser opened at breakfast.
    /// </remarks>
    [Fact]
    public async Task Nothing_Is_Measured_While_Nobody_Is_Watching()
    {
        // Loud, with nobody attached. Two blocks and an interval between them, which is exactly
        // what would produce a reading if the meter were running.
        FeedAcrossAnInterval(Tone(1f));

        using ClientWebSocket page = await AttachAsync();

        FeedAcrossAnInterval(Tone(0.25f));

        using JsonDocument level = await NextAsync(page, "level");

        level.RootElement.GetProperty("peak").GetDouble().Should().BeApproximately(
            -12.0, 0.1, "the loud audio arrived while the page was closed and was never measured");
        level.RootElement.GetProperty("clip").GetBoolean().Should().BeFalse(
            "and a clip nobody was watching is not held over to the next viewer");
    }

    /// <summary>
    /// A server that was not asked for a meter never sends one, whatever audio it is given.
    /// </summary>
    /// <remarks>
    /// This is what keeps the meter off the public page, off a monitor's per-receiver pages and
    /// off a flex: or ubersdr: station: the daemon sets the option only for an operator's page on
    /// a station with a sound card of its own, so the message not arriving is the whole check.
    /// </remarks>
    [Fact]
    public async Task A_Server_With_No_Meter_Never_Sends_A_Level_Message()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var plain = new WaterfallWebServer(
            channel, port, new WaterfallOptions { TimeProvider = _clock });
        plain.Start();

        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using (JsonDocument config = await NextAsync(page, "config"))
        {
            config.RootElement.GetProperty("type").GetString().Should().Be("config");
        }

        float[] block = Tone(0.25f);
        channel.ProcessReceive(block);
        _clock.Advance(InputLevelMeter.DefaultInterval);
        channel.ProcessReceive(block);

        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Func<Task> waiting = async () =>
        {
            using JsonDocument _ = await NextAsync(page, "level", giveUp.Token);
        };

        await waiting.Should().ThrowAsync<OperationCanceledException>(
            "a page that was not offered a meter is never sent one");
    }
}
