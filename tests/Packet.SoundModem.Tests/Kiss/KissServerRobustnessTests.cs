using System.Net.Sockets;
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

    private async Task<TcpClient> ConnectAsync(int? receiveBufferBytes = null)
    {
        var client = new TcpClient();
        if (receiveBufferBytes is int bytes)
        {
            client.Client.ReceiveBufferSize = bytes;
        }

        await client.ConnectAsync("127.0.0.1", _server.LocalPort);
        return client;
    }

    [Fact]
    public async Task A_Host_That_Stops_Reading_Is_Dropped_And_Never_Stalls_The_Broadcast()
    {
        int accepted = 0;
        var disconnects = new List<KissClientEvent>();
        _server.ClientConnected += e => accepted = Math.Max(accepted, e.Clients);
        _server.ClientDisconnected += disconnects.Add;

        // The wedged host: socket open, receive window pinned small, and it never reads.
        using TcpClient wedged = await ConnectAsync(receiveBufferBytes: 8192);
        using TcpClient healthy = await ConnectAsync();
        await WaitUntilAsync(() => accepted == 2);

        // The healthy host reads continuously, counting the frames it decodes. Never
        // faults: the test's teardown closes the socket under it.
        int healthyGot = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                // maxFrame above the test's 64 KiB payloads: the decoder's 2 KiB default
                // exists for radio links, and this test's frames are deliberately huge.
                var decoder = new KissDecoder(_ => Interlocked.Increment(ref healthyGot), maxFrame: 70000);
                var buffer = new byte[65536];
                NetworkStream stream = healthy.GetStream();
                while (true)
                {
                    int got = await stream.ReadAsync(buffer);
                    if (got == 0)
                    {
                        return;
                    }

                    decoder.Push(buffer.AsSpan(0, got));
                }
            }
            catch (Exception)
            {
            }
        });

        // 200 large frames, paced so a reading host keeps up while the wedged one first
        // fills its kernel buffers and then its bounded send queue. Emitted off the test
        // thread so a regression (a blocking socket write on the receive path) fails this
        // test by timeout instead of hanging the run.
        byte[] big = new byte[65536];
        new Random(3).NextBytes(big);
        Task emitter = Task.Run(async () =>
        {
            float[] silence = new float[16];
            for (int i = 0; i < 200; i++)
            {
                _modem.Emit = big;
                _channel.ProcessReceive(silence);
                await Task.Delay(5);
            }

            _modem.Emit = null;
        });

        await emitter.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => Volatile.Read(ref healthyGot) >= 200);

        // The wedged host lost its session, with the reason on the disconnect - not the
        // teardown exception its dead socket produced.
        await WaitUntilAsync(() => disconnects.Count > 0);
        disconnects.Should().ContainSingle()
            .Which.Reason.Should().Contain("stopped reading");
    }

    [Fact]
    public async Task A_Data_Frame_For_A_Modemless_SubChannel_Is_Announced_As_Rejected()
    {
        var rejections = new List<(int SubChannel, Exception Reason)>();
        _channel.TransmitRejected += (subChannel, _, reason) => rejections.Add((subChannel, reason));

        using TcpClient client = await ConnectAsync();
        byte[] frame = new byte[25];
        new Random(2).NextBytes(frame);
        await client.GetStream().WriteAsync(KissCodec.Encode(new KissFrame(5, KissCommand.Data, frame)));

        await WaitUntilAsync(() => rejections.Count > 0);
        rejections.Should().ContainSingle();
        rejections[0].SubChannel.Should().Be(5);
        rejections[0].Reason.Message.Should().Contain("no modem on sub-channel 5");
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
