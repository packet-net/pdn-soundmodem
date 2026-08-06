using System.Net.Sockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// The KISS control surface around SETHW and the ACKMODE hardening: waveform selection
/// reaching the modem (bare or behind the shift decorator), the confirmation echo, the
/// dedicated-port ack nibble, and the id-only ACKMODE frame. The daemon's KISS presentation
/// of an MS110D modem is the "driver" a remote host talks to, so its behaviours are pinned
/// here as a contract.
/// </summary>
public class KissHardwareControlTests : IAsyncLifetime
{
    private const int SampleRate = 48000;

    private readonly SoundModemChannel _channel;
    private readonly KissTcpServer _server;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
    private readonly List<KissHardwareEvent> _hardwareEvents = [];
    private Task? _transmitter;

    public KissHardwareControlTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Ms110dModem(SampleRate, sink));
        _channel.AddModem(1, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _server = new KissTcpServer(_channel, port: 0);
        _server.HardwareCommand += e =>
        {
            lock (_hardwareEvents)
            {
                _hardwareEvents.Add(e);
            }
        };
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

    private async Task<TcpClient> ConnectAsync(KissTcpServer server)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.LocalPort);
        return client;
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

    private async Task<KissHardwareEvent> WaitForHardwareEventAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (_hardwareEvents)
            {
                if (_hardwareEvents.Count > 0)
                {
                    return _hardwareEvents[^1];
                }
            }

            await Task.Delay(20, cancellation.Token);
        }
    }

    [Fact]
    public async Task Sethw_Switches_The_Tx_Waveform_And_Echoes_The_Applied_Frame()
    {
        using TcpClient client = await ConnectAsync(_server);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.SetHardware, [2])));

        KissFrame echo = await ReadFrameAsync(client, TimeSpan.FromSeconds(5));
        echo.Command.Should().Be(KissCommand.SetHardware);
        echo.Payload.Should().Equal([2], "the echo is the confirmation KISS never defined");
        _channel.Modems[0].Mode.Should().Be("ms110d-wn2");
        (await WaitForHardwareEventAsync()).Applied.Should().BeTrue();
    }

    [Fact]
    public async Task Sethw_Takes_An_Interleaver_Byte()
    {
        using TcpClient client = await ConnectAsync(_server);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.SetHardware, [4, 1])));

        (await ReadFrameAsync(client, TimeSpan.FromSeconds(5))).Payload.Should().Equal([4, 1]);
        _channel.Modems[0].Mode.Should().Be("ms110d-wn4");
        (await WaitForHardwareEventAsync()).Description.Should().Contain("long");
    }

    [Fact]
    public async Task An_Invalid_Waveform_Is_Ignored_Journalled_And_Not_Echoed()
    {
        using TcpClient client = await ConnectAsync(_server);

        // WN 9 is not a Phase A waveform. KISS has no error channel: no echo, mode unchanged,
        // and the journal event carries the refusal for the operator.
        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.SetHardware, [9])));

        KissHardwareEvent refusal = await WaitForHardwareEventAsync();
        refusal.Applied.Should().BeFalse();
        _channel.Modems[0].Mode.Should().Be("ms110d-wn6");
        await FluentActions.Awaiting(() => ReadFrameAsync(client, TimeSpan.FromMilliseconds(400)))
            .Should().ThrowAsync<OperationCanceledException>("a refused SETHW must stay silent");
    }

    [Fact]
    public async Task Sethw_On_A_Modem_Without_Hardware_Settings_Is_A_Journalled_No_Op()
    {
        using TcpClient client = await ConnectAsync(_server);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(1, KissCommand.SetHardware, [2])));

        KissHardwareEvent refusal = await WaitForHardwareEventAsync();
        refusal.Applied.Should().BeFalse();
        refusal.SubChannel.Should().Be(1);
    }

    [Fact]
    public async Task A_Dedicated_Ports_Ack_And_Sethw_Echo_Carry_Nibble_Zero()
    {
        // The dedicated port speaks nibble 0 in every direction - that is its entire reason to
        // exist (hosts that cannot set a nibble). Acks and echoes used to leak the real
        // sub-channel nibble, which such a host would mis-file or drop.
        await using var dedicated = new KissTcpServer(_channel, port: 0, subChannel: 0);
        dedicated.Start();
        using TcpClient client = await ConnectAsync(dedicated);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.AckModeData, [0xBE, 0xEF])));
        KissFrame ack = await ReadFrameAsync(client, TimeSpan.FromSeconds(5));
        ack.Command.Should().Be(KissCommand.AckModeData);
        ack.Port.Should().Be(0, "everything a dedicated port says is relabelled to nibble 0");

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.SetHardware, [3])));
        KissFrame echo = await ReadFrameAsync(client, TimeSpan.FromSeconds(5));
        echo.Port.Should().Be(0);
        _channel.Modems[0].Mode.Should().Be("ms110d-wn3");
    }

    [Fact]
    public async Task An_Id_Only_Ackmode_Frame_Is_Acked_Immediately_And_Transmits_Nothing()
    {
        using TcpClient client = await ConnectAsync(_server);

        await client.GetStream().WriteAsync(
            KissCodec.Encode(new KissFrame(0, KissCommand.AckModeData, [0x12, 0x34])));

        KissFrame ack = await ReadFrameAsync(client, TimeSpan.FromSeconds(5));
        ack.Payload.Should().Equal([0x12, 0x34],
            "transmission of nothing is trivially complete, and the host is owed its id back");
        _output.WrittenCount.Should().Be(0, "there was nothing to put on the air");
    }

    [Fact]
    public async Task Concurrent_Acks_And_Echoes_Never_Tear_A_Frame()
    {
        // A smoke for the per-connection write gate: id-only acks answer on the serve thread
        // while data acks answer on continuation threads; before the gate the two could
        // interleave mid-frame on the same socket and the client's decoder lost both. Send a
        // burst of both kinds and require every single answer back, well-formed.
        using TcpClient client = await ConnectAsync(_server);
        NetworkStream stream = client.GetStream();

        var frames = new List<KissFrame>();
        var decoder = new KissDecoder(frames.Add);
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!_cancellation.IsCancellationRequested)
            {
                int got = await stream.ReadAsync(buffer, _cancellation.Token);
                if (got == 0)
                {
                    return;
                }

                lock (frames)
                {
                    decoder.Push(buffer.AsSpan(0, got));
                }
            }
        });

        const int idOnly = 120;
        for (int i = 0; i < idOnly; i++)
        {
            await stream.WriteAsync(KissCodec.Encode(
                new KissFrame(0, KissCommand.AckModeData, [(byte)i, 0xAA])));
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (frames)
            {
                if (frames.Count >= idOnly)
                {
                    break;
                }
            }

            await Task.Delay(20, cancellation.Token);
        }

        lock (frames)
        {
            frames.Should().HaveCount(idOnly, "every ack must arrive intact - a torn frame vanishes");
            frames.Select(f => f.Payload[0]).Should().OnlyHaveUniqueItems();
        }
    }
}

/// <summary>
/// The typed runtime-waveform API and its path through the shift decorator - the surface an
/// in-process host (the pdn node's soundmodem transport) uses without KISS.
/// </summary>
public class Ms110dRuntimeWaveformTests
{
    [Fact]
    public void The_Typed_Switch_Changes_The_Next_Burst_And_Decodes()
    {
        var modem = new Ms110dModem(48000, _ => { });
        byte[] frame = new byte[40];
        new Random(9).NextBytes(frame);

        float[] wn6 = modem.Modulate(frame, 0);
        modem.SetTxWaveform(2);
        modem.Mode.Should().Be("ms110d-wn2");
        float[] wn2 = modem.Modulate(frame, 0);

        // WN2 is 600 bps against WN6's 3200: the same payload takes several times the air.
        wn2.Length.Should().BeGreaterThan(wn6.Length * 2,
            "the burst really is the slower waveform, not just a renamed mode string");

        var received = new List<byte[]>();
        var rx = new Ms110dModem(48000, received.Add);
        rx.Process(new float[4800]);
        rx.Process(wn2);
        rx.Process(new float[48000]);
        received.Should().ContainSingle().Which.Should().Equal(frame, "receive is autobaud");
    }

    [Fact]
    public void An_Invalid_Waveform_Throws_And_Changes_Nothing()
    {
        var modem = new Ms110dModem(48000, _ => { });

        FluentActions.Invoking(() => modem.SetTxWaveform(9))
            .Should().Throw<ArgumentException>();
        modem.Mode.Should().Be("ms110d-wn6", "a refused switch must leave the modem as it was");
    }

    [Fact]
    public void The_Byte_Path_And_The_Typed_Path_Work_Through_The_Shift_Decorator()
    {
        IModem wrapped = ModemCatalog.Create(
            "ms110d-wn6", 48000, _ => { }, new ModemOptions(CentreFrequencyHz: 3000));

        wrapped.Should().BeOfType<FrequencyShiftedModem>();
        ((IHardwareControllable)wrapped).TrySetHardware([2], out string outcome).Should().BeTrue();
        outcome.Should().Contain("ms110d-wn2");
        wrapped.Mode.Should().Be("ms110d-wn2", "the decorator forwards, it does not absorb");

        // The typed path for in-process hosts: unwrap and call the real thing.
        ((FrequencyShiftedModem)wrapped).Inner.Should().BeOfType<Ms110dModem>()
            .Which.As<Ms110dModem>().SetTxWaveform(4, Ms110dInterleaverKind.Long);
        wrapped.Mode.Should().Be("ms110d-wn4");
    }

    [Fact]
    public void A_Modem_With_No_Hardware_Settings_Refuses_Politely_Through_The_Decorator()
    {
        IModem wrapped = ModemCatalog.Create(
            "freedv-datac3", 48000, _ => { }, new ModemOptions(CentreFrequencyHz: 2500));

        ((IHardwareControllable)wrapped).TrySetHardware([2], out string outcome).Should().BeFalse();
        outcome.Should().Contain("no hardware settings");
    }
}
