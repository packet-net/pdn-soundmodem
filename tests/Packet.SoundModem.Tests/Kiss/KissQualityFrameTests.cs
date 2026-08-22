using System.Net.Sockets;
using System.Text;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Modems;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// The KISS quality sidecar (<see cref="KissTcpServer.EmitQualityFrames"/>) against a frame the
/// station read and did not pass on.
/// </summary>
/// <remarks>
/// The server subscribes to both of the channel's frame events: the host one sends the data frame,
/// the monitor one sends the JSON quality frame, and the sidecar's whole contract is that it
/// describes "the data frame it describes", sent just before it. A withheld frame raises only the
/// monitor event, so a server that gated only the data path would send a host a quality report for
/// a frame it never received - a small lie that a host with the extension on has no way to detect.
/// The fact travels on <see cref="FrameQuality.MonitorOnly"/> rather than being inferred from
/// whether the other event fired, because ordering between two synchronous events is not a
/// delivery signal, it just looks like one.
/// </remarks>
public class KissQualityFrameTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    /// <summary>The IL2P+CRC mode the 40 m station runs, and the CRC-less sibling its neighbour
    /// transmits with.</summary>
    private const string CrcMode = "bpsk300";
    private const string PlainMode = "bpsk300-nocrc";

    private SoundModemChannel _channel = null!;
    private KissTcpServer _server = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private void Start(bool acceptPlainIl2p)
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        _channel.AddModem(0, sink => ModemCatalog.Create(
            CrcMode, SampleRate, sink, new ModemOptions(AcceptPlainIl2p: acceptPlainIl2p)));
        _server = new KissTcpServer(_channel, port: 0) { EmitQualityFrames = true };
        _server.Start();
    }

    [Fact]
    public async Task A_Withheld_Frame_Sends_The_Host_Neither_A_Data_Frame_Nor_A_Quality_Frame()
    {
        Start(acceptPlainIl2p: false);
        using TcpClient client = await ConnectAsync();

        // The neighbour's plain frame first, then an ordinary IL2P+CRC one. The second is what
        // makes this a real assertion rather than a wait for nothing to happen: whatever the
        // withheld frame was going to send would have arrived ahead of it.
        _channel.ProcessReceive(Transmission(PlainMode));
        _channel.ProcessReceive(Transmission(CrcMode));

        List<KissFrame> frames = await ReadFramesAsync(client, 2, TimeSpan.FromSeconds(5));

        frames[0].Command.Should().Be(KissCommand.Data, "the first thing the host sees is the "
            + "ordinary frame, not anything about the withheld one");
        frames[0].Payload.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
        frames[1].Command.Should().Be(KissCommand.RxQuality);
        Json(frames[1]).Should().Contain("\"crc\":true", "the sidecar describes the frame above it");
    }

    [Fact]
    public async Task An_Accepted_Plain_Frame_Gets_Its_Data_Frame_And_Its_Sidecar()
    {
        Start(acceptPlainIl2p: true);
        using TcpClient client = await ConnectAsync();

        _channel.ProcessReceive(Transmission(PlainMode));

        List<KissFrame> frames = await ReadFramesAsync(client, 2, TimeSpan.FromSeconds(5));

        frames[0].Command.Should().Be(KissCommand.Data);
        frames[0].Payload.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
        frames[1].Command.Should().Be(KissCommand.RxQuality);
        Json(frames[1]).Should().NotContain("\"crc\"",
            "absent means null, and no CRC was checked on this one");
    }

    private static string Json(KissFrame frame) => Encoding.UTF8.GetString(frame.Payload);

    private async Task<TcpClient> ConnectAsync()
    {
        // A completed TCP handshake does NOT mean the server has registered the client: accept
        // runs on its own loop, so a frame injected in the gap reaches nobody and the read below
        // starves until its token trips, surfacing as a cancelled socket read. That gap used to be
        // covered by a Task.Delay(100), which is a guess about how busy the machine is, and on a
        // loaded CI runner 100ms is not always enough.
        //
        // Wait for the server's own ClientConnected event instead, which fires after the session
        // is in _clients. Subscribing BEFORE the connect means the signal cannot be missed. The
        // timeout only bounds a hang; it is not what decides the test.
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(KissClientEvent _) => registered.TrySetResult();
        _server.ClientConnected += OnConnected;
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", _server.LocalPort);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return client;
        }
        finally
        {
            _server.ClientConnected -= OnConnected;
        }
    }

    private static async Task<List<KissFrame>> ReadFramesAsync(
        TcpClient client, int count, TimeSpan timeout)
    {
        var frames = new List<KissFrame>();
        var decoder = new KissDecoder(frames.Add);
        var buffer = new byte[4096];
        using var cancellation = new CancellationTokenSource(timeout);
        NetworkStream stream = client.GetStream();
        while (frames.Count < count)
        {
            int got = await stream.ReadAsync(buffer, cancellation.Token);
            if (got == 0)
            {
                throw new IOException("connection closed");
            }

            decoder.Push(buffer.AsSpan(0, got));
        }

        return frames;
    }

    /// <summary>One burst of the GB7BPQ beacon in <paramref name="mode"/>, with silence either
    /// side - the trailing silence drops DCD, which is what releases a held plain frame.</summary>
    private static float[] Transmission(string mode)
    {
        float[] burst = ModemCatalog.Create(mode, SampleRate, static _ => { })
            .Modulate(Il2pReceiverTests.Gb7bpqBeacon(), txDelayMilliseconds: 300);
        int pad = SampleRate / 2;
        var audio = new float[pad + burst.Length + pad];
        burst.CopyTo(audio, pad);
        return audio;
    }
}
