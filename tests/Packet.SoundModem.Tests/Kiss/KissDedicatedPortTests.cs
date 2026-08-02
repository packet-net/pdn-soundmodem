using System.Net.Sockets;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// A KISS server dedicated to one modem: the escape hatch for host software that hardcodes
/// KISS channel 0 and offers no way to set the port nibble. On the shared port such a host can
/// only ever reach sub-channel 0, however many modems are configured.
///
/// The two modems here are deliberately different framings at the same baud — AX.25 HDLC on
/// sub-channel 0, IL2P on sub-channel 1 — so which modem actually handled a frame is decidable
/// from the audio, rather than assumed from the code path.
/// </summary>
public class KissDedicatedPortTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int Hdlc = 0;   // afsk1200, AX.25 HDLC
    private const int Il2p = 1;   // afsk1200-il2p

    private readonly SoundModemChannel _channel;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
    private readonly List<KissTcpServer> _servers = [];
    private Task? _transmitter;

    private KissTcpServer _shared = null!;
    private KissTcpServer _hdlcOnly = null!;
    private KissTcpServer _il2pOnly = null!;

    public KissDedicatedPortTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(Hdlc, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _channel.AddModem(Il2p, sink => ModemCatalog.Create("afsk1200-il2p", SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
    }

    public ValueTask InitializeAsync()
    {
        _shared = Serve(subChannel: null);
        _hdlcOnly = Serve(subChannel: Hdlc);
        _il2pOnly = Serve(subChannel: Il2p);
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return ValueTask.CompletedTask;
    }

    private KissTcpServer Serve(int? subChannel)
    {
        var server = new KissTcpServer(_channel, port: 0, bind: null, subChannel: subChannel);
        server.Start();
        _servers.Add(server);
        return server;
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

        foreach (KissTcpServer server in _servers)
        {
            await server.DisposeAsync();
        }

        _cancellation.Dispose();
    }

    private static async Task<TcpClient> ConnectAsync(KissTcpServer server)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.LocalPort);
        return client;
    }

    private static byte[] SampleFrame(int seed = 2)
    {
        byte[] frame = new byte[25];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(16));
        return frame;
    }

    /// <summary>Audio carrying <paramref name="frame"/> as sent by <paramref name="mode"/>.</summary>
    private static async Task<float[]> ModulateAsync(string mode, byte[] frame)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        var output = new FakeAudioOutput(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(output, new NullPtt(), cancellation.Token);
        await channel.EnqueueTransmit(0, frame);
        await SettleAsync(output);
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return [.. output.Snapshot(), .. new float[SampleRate / 2]];
    }

    /// <summary>Waits until the transmitter has stopped producing samples.</summary>
    private static async Task SettleAsync(FakeAudioOutput output)
    {
        int settled;
        do
        {
            settled = output.WrittenCount;
            await Task.Delay(150);
        }
        while (output.WrittenCount != settled || output.WrittenCount == 0);
    }

    private static async Task<List<KissFrame>> DrainAsync(TcpClient client, TimeSpan window)
    {
        var frames = new List<KissFrame>();
        var decoder = new KissDecoder(frames.Add);
        var buffer = new byte[4096];
        using var cancellation = new CancellationTokenSource(window);
        NetworkStream stream = client.GetStream();
        try
        {
            while (true)
            {
                int got = await stream.ReadAsync(buffer, cancellation.Token);
                if (got == 0)
                {
                    break;
                }

                decoder.Push(buffer.AsSpan(0, got));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return frames;
    }

    [Fact]
    public async Task A_Dedicated_Port_Relabels_Its_Modem_To_Nibble_Zero()
    {
        // The whole point: a host that can only read channel 0 still sees sub-channel 1's traffic.
        byte[] frame = SampleFrame();
        float[] audio = await ModulateAsync("afsk1200-il2p", frame);
        using TcpClient dedicated = await ConnectAsync(_il2pOnly);
        using TcpClient shared = await ConnectAsync(_shared);

        _channel.ProcessReceive(audio);

        List<KissFrame> onDedicated = await DrainAsync(dedicated, TimeSpan.FromSeconds(2));
        List<KissFrame> onShared = await DrainAsync(shared, TimeSpan.FromSeconds(2));

        onDedicated.Should().ContainSingle().Which.Port.Should().Be(
            0, "a dedicated port presents its modem as channel 0");
        onDedicated[0].Payload.Should().Equal(frame);

        // The shared port is unchanged: it still reports where the frame really came from.
        onShared.Should().ContainSingle().Which.Port.Should().Be(Il2p);
    }

    [Fact]
    public async Task A_Dedicated_Port_Never_Shows_Another_Modems_Traffic()
    {
        byte[] frame = SampleFrame(5);
        float[] audio = await ModulateAsync("afsk1200", frame);   // decodes on sub-channel 0 only
        using TcpClient wrongModem = await ConnectAsync(_il2pOnly);
        using TcpClient rightModem = await ConnectAsync(_hdlcOnly);

        _channel.ProcessReceive(audio);

        List<KissFrame> onWrong = await DrainAsync(wrongModem, TimeSpan.FromSeconds(2));
        List<KissFrame> onRight = await DrainAsync(rightModem, TimeSpan.FromSeconds(2));

        onRight.Should().ContainSingle("the HDLC modem's port must carry the HDLC frame");
        onWrong.Should().BeEmpty("sub-channel 1's port must not leak sub-channel 0's traffic");
    }

    [Fact]
    public async Task A_Frame_Sent_To_A_Dedicated_Port_As_Channel_Zero_Goes_To_That_Modem()
    {
        // Sent as nibble 0 — what a host that cannot set the nibble would send — but it must
        // leave on sub-channel 1's modem. Decided by decoding the audio, not by inspection:
        // only an IL2P receiver can read it, so only the IL2P modem can have sent it.
        byte[] frame = SampleFrame(9);
        using TcpClient client = await ConnectAsync(_il2pOnly);

        await client.GetStream().WriteAsync(KissCodec.Encode(new KissFrame(0, KissCommand.Data, frame)));
        await SettleAsync(_output);

        float[] audio = [.. _output.Snapshot(), .. new float[SampleRate / 2]];
        Decode("afsk1200-il2p", audio).Should().ContainSingle()
            .Which.Should().Equal(frame, "the dedicated port must transmit on its own modem");
        Decode("afsk1200", audio).Should().BeEmpty(
            "if the HDLC modem had sent it, sub-channel 0 would have been used");
    }

    [Fact]
    public async Task The_Shared_Port_Still_Honours_The_Nibble_A_Client_Sends()
    {
        byte[] frame = SampleFrame(11);
        using TcpClient client = await ConnectAsync(_shared);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(Il2p, KissCommand.Data, frame)));
        await SettleAsync(_output);

        float[] audio = [.. _output.Snapshot(), .. new float[SampleRate / 2]];
        Decode("afsk1200-il2p", audio).Should().ContainSingle().Which.Should().Equal(frame);
    }

    private static List<byte[]> Decode(string mode, float[] audio)
    {
        var received = new List<byte[]>();
        var channel = new SoundModemChannel(SampleRate);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, SampleRate, sink));
        channel.FrameReceived += (_, decoded) => received.Add(decoded);
        channel.ProcessReceive(audio);
        return received;
    }
}
