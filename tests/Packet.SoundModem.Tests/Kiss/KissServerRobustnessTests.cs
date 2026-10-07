using System.Collections.Concurrent;
using System.Net;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// The failure modes a KISS server must survive without taking the station with it: a host
/// that keeps its socket open and stops reading (the receive path must never block on it),
/// and a frame addressed to a sub-channel nothing transmits on (which must be announced,
/// not silently discarded).
/// </summary>
public class KissServerRobustnessTests : IAsyncDisposable
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly KissTcpServer _server;
    private readonly EmittingModem _modem;

    public KissServerRobustnessTests()
    {
        EmittingModem? created = null;
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => created = new EmittingModem(sink));
        _modem = created!;
        _server = new KissTcpServer(_channel, port: 0);
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task A_Host_That_Stops_Reading_Is_Dropped_And_Never_Stalls_The_Broadcast()
    {
        var disconnects = new ConcurrentQueue<KissClientEvent>();
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.ClientDisconnected += e =>
        {
            disconnects.Enqueue(e);
            dropped.TrySetResult();
        };

        // The wedged host: socket open, receive window pinned small, and it never reads. The
        // healthy one decodes frames above the 64 KiB payloads this test sends; the decoder's
        // default exists for radio links, and these frames are deliberately huge.
        using KissTestClient wedged = await KissTestClient.ConnectAsync(_server, receiveBufferBytes: 8192);
        using KissTestClient healthy = await KissTestClient.ConnectAsync(_server, maxFrame: 70000);

        // 200 frames of 64 KiB, 12.8 MiB: more than the kernel will hold for the wedged host (its
        // send buffer is capped by tcp_wmem, 4 MiB by default) plus the 1 MiB it may have queued,
        // so its session has to be dropped. Each frame goes out only once the healthy host has
        // read the one before, so the healthy host is never more than one frame behind and can
        // never be the one dropped, however slowly this machine runs the server's write loop.
        // It used to be paced at 5 ms instead, and a write loop held up for a few tens of
        // milliseconds let the healthy host's queue pass the same 1 MiB budget.
        //
        // Each broadcast runs off the test thread, so a regression that makes it block on the
        // wedged socket fails this test by name instead of hanging the run.
        const int Frames = 200;
        byte[] big = new byte[65536];
        new Random(3).NextBytes(big);
        _modem.Emit = big;
        float[] silence = new float[16];
        for (int i = 0; i < Frames; i++)
        {
            await Task.Run(() => _channel.ProcessReceive(silence))
                .Within($"broadcast {i + 1} to return while a host is not reading");
            int expected = i + 1;
            await healthy.ReadUntilAsync(frames => frames.Count >= expected, $"frame {expected} on the healthy host");
        }

        _modem.Emit = null;
        healthy.Frames.Should().HaveCount(Frames).And.OnlyContain(frame => frame.Payload.SequenceEqual(big));

        // The wedged host lost its session, with the reason on the disconnect - not the
        // teardown exception its dead socket produced.
        await dropped.Task.Within("the wedged host to be dropped");
        KissClientEvent disconnect = disconnects.Should().ContainSingle().Which;
        disconnect.Reason.Should().Contain("stopped reading");
        ((IPEndPoint)disconnect.Remote!).Port.Should().Be(wedged.LocalPort);
    }

    [Fact]
    public async Task A_Data_Frame_For_A_Modemless_SubChannel_Is_Announced_As_Rejected()
    {
        var rejections = new ConcurrentQueue<(int SubChannel, Exception Reason)>();
        _channel.TransmitRejected += (subChannel, _, reason) => rejections.Enqueue((subChannel, reason));

        using KissTestClient client = await KissTestClient.ConnectAsync(_server);
        byte[] frame = new byte[25];
        new Random(2).NextBytes(frame);
        await client.SendAsync(new KissFrame(5, KissCommand.Data, frame));

        // Rejected as the frame is decoded, on the read loop that answers the fence after it.
        await client.FenceAsync();
        (int SubChannel, Exception Reason) rejection = rejections.Should().ContainSingle().Which;
        rejection.SubChannel.Should().Be(5);
        rejection.Reason.Message.Should().Contain("no modem on sub-channel 5");
    }

    /// <summary>A modem that emits <see cref="Emit"/> to its frame sink on every
    /// <see cref="Process"/> call - the broadcast path with no DSP in the way.</summary>
    private sealed class EmittingModem(Action<byte[]> sink) : IModem
    {
        public string Mode => "stub";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public byte[]? Emit { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
            if (Emit is { } frame)
            {
                sink(frame);
            }
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[16];

        public void ResetCarrierState()
        {
        }
    }
}
