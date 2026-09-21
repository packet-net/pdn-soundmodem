using M0LTE.Radio.Audio;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Kiss;

public class KissTcpServerTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly KissTcpServer _server;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(20));
    private Task? _transmitter;

    public KissTcpServerTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 100;
        _server = new KissTcpServer(_channel, port: 0);
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync();
        _cancellation.Dispose();
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _server.LocalPort);
        return client;
    }

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[25];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(2).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static async Task<KissFrame> ReadFrameAsync(TcpClient client, TimeSpan timeout)
    {
        var frames = new List<KissFrame>();
        var decoder = new KissDecoder(frames.Add);
        var buffer = new byte[4096];
        using var cancellation = new CancellationTokenSource(timeout);
        NetworkStream stream = client.GetStream();
        while (frames.Count == 0)
        {
            int got = await stream.ReadAsync(buffer, cancellation.Token);
            if (got == 0)
            {
                throw new IOException("connection closed");
            }

            decoder.Push(buffer.AsSpan(0, got));
        }

        return frames[0];
    }

    private static byte[] ReceiverReadyFrame(int receiveSequence) =>
        [.. Convert.FromHexString("8E846E9EB08CE48E846EA4888E65"), (byte)(0x01 | (receiveSequence << 5))];

    private async Task QueueAckmodeReceiverReadyRunAsync(NetworkStream stream, byte[][] ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            await stream.WriteAsync(KissCodec.Encode(new KissFrame(
                0, KissCommand.AckModeData, [.. ids[i], .. ReceiverReadyFrame(i)])));
        }

        // The socket decoder calls EnqueueTransmit synchronously, in wire order. Seeing this
        // trailing parameter applied proves all three requests are queued, not merely written.
        await stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.TxDelay, [15])));
        await WaitUntilAsync(() => _channel.Csma.TxDelayMilliseconds == 150);
    }

    private sealed class KissResponseReader
    {
        private readonly NetworkStream _stream;
        private readonly KissDecoder _decoder;
        private readonly List<KissFrame> _frames = [];
        private readonly byte[] _buffer = new byte[4096];

        public KissResponseReader(NetworkStream stream)
        {
            _stream = stream;
            _decoder = new KissDecoder(_frames.Add);
        }

        public IReadOnlyList<KissFrame> Frames => _frames;

        public async Task ReadUntilAsync(Func<IReadOnlyList<KissFrame>, bool> complete)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!complete(_frames))
            {
                int got = await _stream.ReadAsync(_buffer, cancellation.Token);
                if (got == 0)
                {
                    throw new IOException("connection closed");
                }

                // Retain every response in a read, and any partial frame across later reads.
                // ReadFrameAsync returns only the first and would hide extra or duplicate ACKs.
                _decoder.Push(_buffer.AsSpan(0, got));
            }
        }

        public async Task ReadThroughMarkerAsync(byte[] id)
        {
            await _stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.AckModeData, id)));
            await ReadUntilAsync(frames => frames.Any(frame =>
                frame.Command == KissCommand.AckModeData && frame.Payload.SequenceEqual(id)));
        }
    }

    [Fact]
    public async Task A_Frame_Over_The_Ports_Cap_Is_Dropped_And_Reported_With_The_Host_And_The_Cap()
    {
        // The report is the point: without it the host sees its frame vanish and reads a bad
        // link, which is what the old silent 2 KiB cap did to a whole throughput campaign.
        await using var server = new KissTcpServer(_channel, port: 0, maxFrameBytes: 100);
        var reported = new List<KissOversizeEvent>();
        server.FrameOversize += reported.Add;
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.LocalPort);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.Data, new byte[300])));
        await client.GetStream().FlushAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (reported.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        reported.Should().ContainSingle();
        reported[0].MaxFrameBytes.Should().Be(100);
        reported[0].Remote.Should().NotBeNull("the journal line names the host");
        server.MaxFrameBytes.Should().Be(100);
        client.Connected.Should().BeTrue("an oversize frame costs the frame, not the session");
    }

    [Fact]
    public async Task A_Kiss_Data_Frame_Is_Transmitted_As_Audio()
    {
        byte[] frame = SampleFrame();
        using TcpClient client = await ConnectAsync();

        await client.GetStream().WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.Data, frame)));

        // Wait until the transmitter has finished producing audio (count stops growing).
        await WaitUntilAsync(() => _output.WrittenCount > 2000);
        int settled;
        do
        {
            settled = _output.WrittenCount;
            await Task.Delay(100);
        }
        while (_output.WrittenCount != settled);
        var received = new List<byte[]>();
        var rxChannel = new SoundModemChannel(SampleRate);
        rxChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        rxChannel.FrameReceived += (_, decoded) => received.Add(decoded);
        rxChannel.ProcessReceive([.. _output.Snapshot(), .. new float[SampleRate / 2]]);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public async Task Received_Frames_Broadcast_To_Every_Client()
    {
        using TcpClient first = await ConnectAsync();
        using TcpClient second = await ConnectAsync();
        await Task.Delay(100); // both connections accepted

        byte[] frame = SampleFrame();
        float[] audio = new Afsk1200Modem(SampleRate, _ => { }).Modulate(frame, txDelayMilliseconds: 150);
        _channel.ProcessReceive([.. audio, .. new float[SampleRate / 2]]);

        var timeout = TimeSpan.FromSeconds(5);
        (await ReadFrameAsync(first, timeout)).Payload.Should().Equal(frame);
        (await ReadFrameAsync(second, timeout)).Payload.Should().Equal(frame);
    }

    [Fact]
    public async Task Ackmode_Frames_Echo_Their_Id_After_Transmission()
    {
        byte[] frame = SampleFrame();
        using TcpClient client = await ConnectAsync();

        byte[] payload = [0xBE, 0xEF, .. frame];
        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.AckModeData, payload)));

        KissFrame ack = await ReadFrameAsync(client, TimeSpan.FromSeconds(10));
        ack.Command.Should().Be(KissCommand.AckModeData);
        ack.Payload.Should().Equal([0xBE, 0xEF]);
        _output.WrittenCount.Should().BeGreaterThan(0, "the ack only comes after the audio went out");
    }

    [Fact]
    public async Task Compacted_Ackmode_ReceiverReady_Requests_Echo_Every_Id_And_Transmit_Only_The_Newest()
    {
        bool inhibited = true;
        _channel.TransmitInhibit = () => Volatile.Read(ref inhibited);
        var transmitted = new ConcurrentQueue<(int Port, byte[] Frame)>();
        _channel.FrameTransmitted += (port, frame) => transmitted.Enqueue((port, frame));
        byte[][] ids = [[0xBE, 0xEF], [0xC0, 0xDB], [0x12, 0x34]];
        byte[] heldMarker = [0xFE, 0x00];
        byte[] completedMarker = [0xFE, 0x01];
        using TcpClient client = await ConnectAsync();
        NetworkStream stream = client.GetStream();
        var responses = new KissResponseReader(stream);

        await QueueAckmodeReceiverReadyRunAsync(stream, ids);

        // An empty ACKMODE request is answered without transmitting. Its response fences the
        // socket's send queue while the channel is still held, without a negative sleep.
        await responses.ReadThroughMarkerAsync(heldMarker);
        responses.Frames.Should().ContainSingle().Which.Payload.Should().Equal(heldMarker);
        transmitted.Should().BeEmpty();
        _output.WrittenCount.Should().Be(0, "no request may transmit or earn an ACK while held");

        Volatile.Write(ref inhibited, false);
        await responses.ReadUntilAsync(frames => frames.Count >= ids.Length + 1);
        await responses.ReadThroughMarkerAsync(completedMarker);

        responses.Frames.Should().OnlyContain(frame =>
            frame.Port == 0 && frame.Command == KissCommand.AckModeData && frame.Payload.Length == 2);
        responses.Frames.Select(frame => Convert.ToHexString(frame.Payload)).Should().BeEquivalentTo(
            new[] { heldMarker, ids[0], ids[1], ids[2], completedMarker }.Select(id => Convert.ToHexString(id)),
            "every original request is owed exactly one ACK, regardless of completion order");
        transmitted.Should().ContainSingle().Which.Port.Should().Be(0);
        transmitted.Single().Frame.Should().Equal(ReceiverReadyFrame(2));
        _output.WrittenCount.Should().BeGreaterThan(0);

        var decoded = new List<byte[]>();
        var receiver = new SoundModemChannel(SampleRate);
        receiver.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        receiver.FrameReceived += (_, frame) => decoded.Add(frame);
        receiver.ProcessReceive([.. _output.Snapshot(), .. new float[SampleRate / 2]]);
        decoded.Should().ContainSingle().Which.Should().Equal(ReceiverReadyFrame(2));
    }

    [Fact]
    public async Task A_Rejected_Ackmode_ReceiverReady_Survivor_Rejects_Every_Request_Without_Success_Acks()
    {
        bool inhibited = true;
        _channel.TransmitInhibit = () => Volatile.Read(ref inhibited);
        _channel.Csma.TxTailMilliseconds = 0;
        var failure = new ArgumentException("modulation refused");
        var modulated = new ConcurrentQueue<byte[]>();
        _channel.TransmitTrimHz = (_, frame) =>
        {
            modulated.Enqueue(frame);
            throw failure;
        };
        var transmitted = new ConcurrentQueue<byte[]>();
        _channel.FrameTransmitted += (_, frame) => transmitted.Enqueue(frame);
        var rejected = new ConcurrentQueue<(int Port, byte[] Frame, Exception Reason)>();
        var allRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.TransmitRejected += (port, frame, reason) =>
        {
            rejected.Enqueue((port, frame, reason));
            if (rejected.Count >= 3)
            {
                allRejected.TrySetResult();
            }
        };
        byte[][] ids = [[0xBE, 0xEF], [0xC0, 0xDB], [0x12, 0x34]];
        byte[] heldMarker = [0xFE, 0x00];
        byte[] rejectedMarker = [0xFE, 0x02];
        using TcpClient client = await ConnectAsync();
        NetworkStream stream = client.GetStream();
        var responses = new KissResponseReader(stream);

        await QueueAckmodeReceiverReadyRunAsync(stream, ids);
        await responses.ReadThroughMarkerAsync(heldMarker);
        responses.Frames.Should().ContainSingle().Which.Payload.Should().Equal(heldMarker);
        modulated.Should().BeEmpty();
        rejected.Should().BeEmpty();
        transmitted.Should().BeEmpty();
        _output.WrittenCount.Should().Be(0);

        Volatile.Write(ref inhibited, false);
        await allRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Only send the marker once all three rejection callbacks have been observed. Keep
        // every response, including any unexpected successful ACK sharing the marker's read.
        await responses.ReadThroughMarkerAsync(rejectedMarker);
        responses.Frames.Should().OnlyContain(frame =>
            frame.Port == 0 && frame.Command == KissCommand.AckModeData);
        responses.Frames.Select(frame => Convert.ToHexString(frame.Payload)).Should().Equal(
            Convert.ToHexString(heldMarker), Convert.ToHexString(rejectedMarker));
        rejected.Should().HaveCount(3);
        for (int i = 0; i < ids.Length; i++)
        {
            byte[] expected = ReceiverReadyFrame(i);
            var rejection = rejected.Should().ContainSingle(item => item.Frame.SequenceEqual(expected)).Which;
            rejection.Port.Should().Be(0);
            rejection.Reason.Should().BeSameAs(failure);
        }

        modulated.Should().ContainSingle().Which.Should().Equal(ReceiverReadyFrame(2));
        transmitted.Should().BeEmpty("neither the survivor nor its suppressed requests reached the audio output");
        _output.WrittenCount.Should().Be(0);
    }

    [Fact]
    public async Task Kiss_Parameter_Commands_Update_Csma_Settings()
    {
        using TcpClient client = await ConnectAsync();
        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.TxDelay, [25])));
        await stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.Persistence, [128])));
        await stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.SlotTime, [7])));
        await stream.WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.TxTail, [3])));

        await WaitUntilAsync(() => _channel.Csma.TxTailMilliseconds == 30);
        _channel.Csma.TxDelayMilliseconds.Should().Be(250);
        _channel.Csma.Persistence.Should().Be(128);
        _channel.Csma.SlotTimeMilliseconds.Should().Be(70);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellation.Token);
        }
    }
}
