using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The local channel audio stream (issue #584): loopback only, no key, refused from anything
/// that declares an <c>Origin</c>, the channel's own rate, keyed blocks marked rather than
/// missing, a slow reader's own loss marked with a gap, and several readers at once.
/// </summary>
public class ChannelAudioStreamTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public ChannelAudioStreamTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 11);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePorts.Next();
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

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Is_Loopback_Address_Accepts_Only_Loopback(string address, bool expected) =>
        ChannelAudioStream.IsLoopbackAddress(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void Is_Loopback_Address_Refuses_No_Address_At_All() =>
        ChannelAudioStream.IsLoopbackAddress(null).Should().BeFalse("no remote address is not trusted as loopback");

    [Fact]
    public async Task Refuses_A_Websocket_Upgrade_That_Declares_An_Origin()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://example.com");

        Func<Task> connect = () => socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);

        await connect.Should().ThrowAsync<WebSocketException>(
            "a browser sets Origin itself and script cannot remove it, so this is exactly "
            + "the one thing that must never be served");
    }

    [Fact]
    public async Task Accepts_A_Loopback_Connection_With_No_Origin_And_Says_The_Rate()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);

        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Text);
        using JsonDocument hello = JsonDocument.Parse(payload);
        hello.RootElement.GetProperty("type").GetString().Should().Be("hello");
        hello.RootElement.GetProperty("rateHz").GetInt32().Should().Be(SampleRate);
    }

    [Fact]
    public async Task Streams_Received_Audio_As_Float32_Blocks_With_A_Sample_Counter()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await Receive(socket); // hello

        float[] tone = Tone(SampleRate / 10, 1800);
        _channel.ProcessReceive(tone);

        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Binary);
        payload[0].Should().Be((byte)0x01, "the audio-block message kind");
        payload[1].Should().Be((byte)0, "neither transmitted nor gap on a first, ordinary block");
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12, 4));
        count.Should().Be(tone.Length);
        for (int i = 0; i < count; i++)
        {
            float sample = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(16 + (i * 4), 4));
            sample.Should().BeApproximately(tone[i], 0.0001f, $"sample {i} must come through unchanged");
        }
    }

    [Fact]
    public async Task Marks_A_Block_Skipped_For_A_Keyup_As_Transmitted_Silence()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await Receive(socket); // hello

        _channel.Csma.Persistence = 255; // never defer: the keyup below is the test's own
        var recorder = new RecordingOutput(SampleRate);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.TransmittingChanged += transmitting =>
        {
            if (transmitting)
            {
                // Feed a receive block while the channel is keyed: ProcessReceive is still
                // called on this path in the real daemon (the capture loop never stops just
                // because PTT is up), so this is exactly the block ProcessReceive's own gate
                // would otherwise drop with nothing to show for it.
                _channel.ProcessReceive(new float[240]);
            }
            else
            {
                ended.TrySetResult();
            }
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task completion = _channel.EnqueueTransmit(0, Frame());
        Task transmitter = _channel.RunTransmitterAsync(recorder, recorder, cancellation.Token);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        byte[]? keyedBlock = null;
        for (int i = 0; i < 50 && keyedBlock is null; i++)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Binary && (payload[1] & 0x01) != 0)
            {
                keyedBlock = payload;
            }
        }

        keyedBlock.Should().NotBeNull("a block skipped for the keyup must still be marked, not missing");
        int count = BinaryPrimitives.ReadInt32LittleEndian(keyedBlock.AsSpan(12, 4));
        count.Should().Be(240);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.ReadSingleLittleEndian(keyedBlock.AsSpan(16 + (i * 4), 4)).Should().Be(
                0f, "a keyed block carries silence, never our own transmitted audio");
        }
    }

    [Fact]
    public async Task Several_Readers_Each_Get_The_Same_Audio()
    {
        using var first = new ClientWebSocket();
        using var second = new ClientWebSocket();
        await first.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await second.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await Receive(first);  // hello
        await Receive(second); // hello

        float[] tone = Tone(480, 1200);
        _channel.ProcessReceive(tone);

        (_, byte[] a) = await Receive(first);
        (_, byte[] b) = await Receive(second);
        a.Should().Equal(b, "every connected reader hears the same block");
    }

    [Fact]
    public async Task A_Disconnecting_Client_Is_Dropped_From_The_Fan_Out()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await Receive(socket); // hello
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        socket.Dispose();

        await WaitUntil(() => CountInternalClients() == 0, "the dropped client must be removed, not leaked");
    }

    [Fact]
    public async Task Asking_For_A_Band_Fires_The_Hook_And_Clearing_It_Fires_Null()
    {
        var seen = new List<(int LowHz, int HighHz)?>();
        _server.ReceiveBandRequested = requested => seen.Add(requested);

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await Receive(socket); // hello

        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes("""{"name":"test","band":{"lowHz":1000,"highHz":2000}}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);

        await WaitUntil(() => seen.Count >= 1, "the identify message must trigger the band hook");
        seen[^1].Should().Be((1000, 2000));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        socket.Dispose();

        await WaitUntil(() => seen.Count >= 2 && seen[^1] is null,
            "no connection asking for a band any more must fire null");
    }

    [Fact]
    public void A_Queue_Marks_The_Next_Block_With_A_Gap_After_Dropping_The_Oldest()
    {
        var queue = new ChannelAudioStream.AudioQueue(2);
        queue.Enqueue(0, new float[] { 1f }, transmitted: false);
        queue.Enqueue(1, new float[] { 2f }, transmitted: false);
        queue.Enqueue(2, new float[] { 3f }, transmitted: false); // queue full: drops index 0

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ChannelAudioStream.QueuedBlock first = Dequeue(queue, cancellation.Token);
        first.SampleIndex.Should().Be(1, "the oldest block was dropped to make room");
        first.Gap.Should().BeTrue("the block right after a drop carries the gap flag");

        ChannelAudioStream.QueuedBlock second = Dequeue(queue, cancellation.Token);
        second.SampleIndex.Should().Be(2);
        second.Gap.Should().BeFalse("only the block right after the drop is marked, not every block after it");
    }

    private static ChannelAudioStream.QueuedBlock Dequeue(ChannelAudioStream.AudioQueue queue, CancellationToken cancellation) =>
        queue.DequeueAsync(cancellation).AsTask().GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("expected a block");

    private int CountInternalClients()
    {
        // No public API for this beyond serving sockets - read by testing the band hook's own
        // visible side effect would overreach into unrelated behaviour, so this reaches the
        // one internal counter that exists purely to make this assertion possible.
        return _server.GetType()
            .GetField("_channelAudioStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_server) is ChannelAudioStream stream
            ? stream.ClientCount
            : -1;
    }

    private static float[] Tone(int samples, double hz)
    {
        var tone = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            tone[n] = 0.25f * MathF.Sin(2 * MathF.PI * (float)hz * n / SampleRate);
        }

        return tone;
    }

    private static byte[] Frame()
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        return frame;
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().Should().BeTrue(because);
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

    private sealed class RecordingOutput(int sampleRate) : IAudioOutput, IPttControl
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
        }

        public void Drain()
        {
        }

        public void Key()
        {
        }

        public void Unkey()
        {
        }
    }
}
