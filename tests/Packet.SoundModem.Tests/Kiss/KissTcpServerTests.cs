using M0LTE.Radio.Audio;
using System.Collections.Concurrent;
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
    private readonly CancellationTokenSource _cancellation = new();
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

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[25];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(2).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static byte[] ReceiverReadyFrame(int receiveSequence) =>
        [.. Convert.FromHexString("8E846E9EB08CE48E846EA4888E65"), (byte)(0x01 | (receiveSequence << 5))];

    /// <summary>Writes the three requests. The socket decoder calls EnqueueTransmit
    /// synchronously, in wire order, so the marker each caller reads through next is answered
    /// only once all three are queued, not merely written.</summary>
    private static async Task QueueAckmodeReceiverReadyRunAsync(KissTestClient client, byte[][] ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            await client.SendAsync(new KissFrame(
                0, KissCommand.AckModeData, [.. ids[i], .. ReceiverReadyFrame(i)]));
        }

        // This used to be the fence, waited for by polling the setting. The marker does that job
        // now; the TXDELAY stays so the bursts keep the 150 ms preamble they were checked with.
        await client.SendAsync(new KissFrame(0, KissCommand.TxDelay, [15]));
    }

    [Fact]
    public async Task A_Frame_Over_The_Ports_Cap_Is_Dropped_And_Reported_With_The_Host_And_The_Cap()
    {
        // The report is the point: without it the host sees its frame vanish and reads a bad
        // link, which is what the old silent 2 KiB cap did to a whole throughput campaign.
        await using var server = new KissTcpServer(_channel, port: 0, maxFrameBytes: 100);
        var reported = new ConcurrentQueue<KissOversizeEvent>();
        server.FrameOversize += reported.Enqueue;
        server.Start();
        using KissTestClient client = await KissTestClient.ConnectAsync(server);

        await client.SendAsync(new KissFrame(0, KissCommand.Data, new byte[300]));

        // The report is raised from the session's read loop as it decodes, and the fence's
        // answer is sent from the same loop after it has decoded everything before it, so once
        // the answer is in, the report has either been made or never will be.
        await client.FenceAsync();

        reported.Should().ContainSingle();
        reported.Single().MaxFrameBytes.Should().Be(100);
        reported.Single().Remote.Should().NotBeNull("the journal line names the host");
        server.MaxFrameBytes.Should().Be(100);
        client.Client.Connected.Should().BeTrue("an oversize frame costs the frame, not the session");
    }

    [Fact]
    public async Task A_Kiss_Data_Frame_Is_Transmitted_As_Audio()
    {
        byte[] frame = SampleFrame();
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);
        Task<byte[]> transmitted = KissTestWait.NextTransmissionAsync(_channel);

        await client.SendAsync(new KissFrame(0, KissCommand.Data, frame));

        // Raised once the frame's audio has been written; the trailing silence added below
        // stands in for whatever tail is still being written.
        await transmitted.Within("the frame to be transmitted");
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
        // Each returns once the server holds the session, not merely once the kernel has
        // finished the handshake: a frame received in between is broadcast to nobody.
        using KissTestClient first = await KissTestClient.ConnectAsync(_server);
        using KissTestClient second = await KissTestClient.ConnectAsync(_server);

        byte[] frame = SampleFrame();
        float[] audio = new Afsk1200Modem(SampleRate, _ => { }).Modulate(frame, txDelayMilliseconds: 150);
        _channel.ProcessReceive([.. audio, .. new float[SampleRate / 2]]);

        (await first.FenceAsync()).Should().ContainSingle().Which.Payload.Should().Equal(frame);
        (await second.FenceAsync()).Should().ContainSingle().Which.Payload.Should().Equal(frame);
    }

    [Fact]
    public async Task Ackmode_Frames_Echo_Their_Id_After_Transmission()
    {
        byte[] frame = SampleFrame();
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);

        byte[] payload = [0xBE, 0xEF, .. frame];
        await client.SendAsync(new KissFrame(0, KissCommand.AckModeData, payload));

        await client.ReadUntilAsync(frames => frames.Count > 0, "the ack");
        KissFrame ack = client.Frames[0];
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
        using KissTestClient responses = await KissTestClient.ConnectAsync(_server);

        await QueueAckmodeReceiverReadyRunAsync(responses, ids);

        // An empty ACKMODE request is answered without transmitting. Its response fences the
        // socket's send queue while the channel is still held, without a negative sleep.
        await responses.ReadThroughMarkerAsync(heldMarker);
        responses.Frames.Should().ContainSingle().Which.Payload.Should().Equal(heldMarker);
        transmitted.Should().BeEmpty();
        _output.WrittenCount.Should().Be(0, "no request may transmit or earn an ACK while held");

        Volatile.Write(ref inhibited, false);
        await responses.ReadUntilAsync(frames => frames.Count >= ids.Length + 1, "an ack for every request");
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
        using KissTestClient responses = await KissTestClient.ConnectAsync(_server);

        await QueueAckmodeReceiverReadyRunAsync(responses, ids);
        await responses.ReadThroughMarkerAsync(heldMarker);
        responses.Frames.Should().ContainSingle().Which.Payload.Should().Equal(heldMarker);
        modulated.Should().BeEmpty();
        rejected.Should().BeEmpty();
        transmitted.Should().BeEmpty();
        _output.WrittenCount.Should().Be(0);

        Volatile.Write(ref inhibited, false);
        await allRejected.Task.Within("all three requests to be rejected");

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
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);

        await client.SendAsync(new KissFrame(0, KissCommand.TxDelay, [25]));
        await client.SendAsync(new KissFrame(0, KissCommand.Persistence, [128]));
        await client.SendAsync(new KissFrame(0, KissCommand.SlotTime, [7]));
        await client.SendAsync(new KissFrame(0, KissCommand.TxTail, [3]));

        // Applied as each is decoded, on the loop that answers the fence after them.
        await client.FenceAsync();
        _channel.Csma.TxTailMilliseconds.Should().Be(30);
        _channel.Csma.TxDelayMilliseconds.Should().Be(250);
        _channel.Csma.Persistence.Should().Be(128);
        _channel.Csma.SlotTimeMilliseconds.Should().Be(70);
    }
}
