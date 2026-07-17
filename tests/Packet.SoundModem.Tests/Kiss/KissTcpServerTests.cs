using M0LTE.Radio.Audio;
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

    public Task InitializeAsync()
    {
        _server.Start();
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
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
