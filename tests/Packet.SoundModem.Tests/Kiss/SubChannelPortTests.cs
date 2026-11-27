using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// A sub-channel port (a mode that is not an <see cref="IModem"/>) behind the channel and the KISS
/// server: transmit, announcements, ACKMODE, received frames, SETHW and the one-owner rule.
/// Everything waits on what the code raises; nothing waits on time.
/// </summary>
public class SubChannelPortTests : IAsyncLifetime
{
    private const int SampleRate = 48000;
    private const int Sub = 2;

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 7);
    private readonly FakePort _port = new("fake-mode");
    private readonly KissTcpServer _server;
    private readonly KissTcpServer _qualityServer;

    public SubChannelPortTests()
    {
        _channel.AddPort(Sub, _port);
        _server = new KissTcpServer(_channel, port: 0);
        _qualityServer = new KissTcpServer(_channel, port: 0) { EmitQualityFrames = true };
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        _qualityServer.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _qualityServer.DisposeAsync();
    }

    private static FrameQuality Quality(int bytes) =>
        new("fake-mode", FrameBytes: bytes, CorrectedBytes: null, CrcValid: true);

    [Fact]
    public async Task Kiss_Data_On_The_Sub_Channel_Reaches_The_Port()
    {
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);
        byte[] payload = [1, 2, 3, 4];

        await client.SendAsync(new KissFrame(Sub, KissCommand.Data, payload));

        (await _port.NextCall().Within("the port to be asked to transmit")).Should().Equal(payload);
    }

    [Fact]
    public async Task Frame_Transmitted_Fires_On_Completion_And_Not_Before()
    {
        var events = new ConcurrentQueue<string>();
        _channel.FrameTransmitted += (sub, frame) => events.Enqueue($"tx {sub} {frame.Length}");
        _channel.FrameTransmittedWithTrim += (_, _, trim) => events.Enqueue($"trim {trim}");
        _channel.FrameTransmittedWithReport += (_, _, report) => events.Enqueue($"report {report.TrimHz}");
        _channel.TransmitRejected += (_, _, _) => events.Enqueue("rejected");

        Task sent = _channel.EnqueueTransmit(Sub, [9, 9, 9]);
        await _port.NextCall().Within("the port to be asked to transmit");
        events.Should().BeEmpty("the frame has not gone on air yet");
        sent.IsCompleted.Should().BeFalse();

        _port.Complete(0);
        await sent.Within("the send to complete");

        events.Should().Equal($"tx {Sub} 3", "trim 0", "report 0");
    }

    [Fact]
    public async Task Transmit_Rejected_Fires_On_Fault_And_The_Task_Faults()
    {
        var events = new ConcurrentQueue<string>();
        var reasons = new ConcurrentQueue<Exception>();
        _channel.FrameTransmitted += (_, _) => events.Enqueue("tx");
        _channel.TransmitRejected += (sub, frame, reason) =>
        {
            events.Enqueue($"rejected {sub} {frame.Length}");
            reasons.Enqueue(reason);
        };
        var refusal = new InvalidOperationException("no air");

        Task sent = _channel.EnqueueTransmit(Sub, [1, 2]);
        await _port.NextCall().Within("the port to be asked to transmit");
        _port.Fail(0, refusal);

        Func<Task> act = async () => await sent.Within("the send to fault");
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(refusal);
        events.Should().Equal($"rejected {Sub} 2");
        reasons.Should().ContainSingle().Which.Should().BeSameAs(refusal);
    }

    [Fact]
    public async Task A_Port_That_Throws_Instead_Of_Returning_A_Task_Is_Rejected_The_Same_Way()
    {
        var rejected = new ConcurrentQueue<Exception>();
        _channel.TransmitRejected += (_, _, reason) => rejected.Enqueue(reason);
        _port.ThrowSynchronously = new InvalidOperationException("refused at once");

        Func<Task> act = async () => await _channel.EnqueueTransmit(Sub, [1]).Within("the send to fault");

        await act.Should().ThrowAsync<InvalidOperationException>();
        rejected.Should().ContainSingle();
    }

    [Fact]
    public async Task The_Port_Is_Handed_A_Copy_Of_The_Frame()
    {
        byte[] original = [5, 6, 7];

        Task sent = _channel.EnqueueTransmit(Sub, original);
        byte[] handed = await _port.NextCall().Within("the port to be asked to transmit");
        original[0] = 0xFF;
        _port.Complete(0);
        await sent.Within("the send to complete");

        handed.Should().Equal(5, 6, 7);
    }

    [Fact]
    public async Task An_Ackmode_Frame_Is_Answered_When_The_Port_Completes()
    {
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);
        byte[] ackId = [0xBE, 0xEF];

        await client.SendAsync(new KissFrame(Sub, KissCommand.AckModeData, [.. ackId, 1, 2, 3]));
        (await _port.NextCall().Within("the port to be asked to transmit")).Should().Equal(1, 2, 3);
        List<KissFrame> before = await client.FenceAsync();
        before.Should().BeEmpty("the frame has not gone on air, so there is nothing to answer");

        _port.Complete(0);

        await client.ReadUntilAsync(
            frames => frames.Any(f => f.Command == KissCommand.AckModeData && f.Payload.SequenceEqual(ackId)),
            "the ACKMODE answer");
    }

    [Fact]
    public async Task An_Ackmode_Frame_Is_Not_Answered_When_The_Port_Faults()
    {
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.TransmitRejected += (_, _, _) => rejected.TrySetResult();

        await client.SendAsync(new KissFrame(Sub, KissCommand.AckModeData, [0xBE, 0xEF, 1, 2, 3]));
        await _port.NextCall().Within("the port to be asked to transmit");
        _port.Fail(0, new InvalidOperationException("dropped"));
        await rejected.Task.Within("the frame to be rejected");

        // The fence is answered behind everything the server has queued for this session, so an
        // answer to the faulted frame would be in what it returns.
        (await client.FenceAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Received_Frame_Reaches_Every_Kiss_Client_With_Quality_Where_Enabled()
    {
        using KissTestClient plain = await KissTestClient.ConnectAsync(_server);
        using KissTestClient another = await KissTestClient.ConnectAsync(_server);
        using KissTestClient withQuality = await KissTestClient.ConnectAsync(_qualityServer);
        byte[] frame = [10, 20, 30];

        _channel.DeliverReceived(Sub, frame, Quality(frame.Length));

        foreach (KissTestClient client in new[] { plain, another })
        {
            List<KissFrame> got = await client.FenceAsync();
            got.Should().ContainSingle().Which.Should().Match<KissFrame>(
                f => f.Port == Sub && f.Command == KissCommand.Data && f.Payload.SequenceEqual(frame));
        }

        List<KissFrame> sidecar = await withQuality.FenceAsync();
        sidecar.Select(f => f.Command).Should().Equal(KissCommand.Data, KissCommand.RxQuality);
        sidecar[0].Payload.Should().Equal(frame);
        System.Text.Encoding.UTF8.GetString(sidecar[1].Payload).Should().Contain("\"mode\":\"fake-mode\"");
    }

    [Fact]
    public void A_Monitor_Only_Frame_Is_Not_Given_To_The_Host_But_Is_Still_Reported()
    {
        var host = new List<byte[]>();
        var monitor = new List<byte[]>();
        _channel.FrameReceived += (_, f) => host.Add(f);
        _channel.FrameReceivedWithQuality += (_, f, _) => monitor.Add(f);

        _channel.DeliverReceived(Sub, [1], Quality(1) with { MonitorOnly = true });

        host.Should().BeEmpty();
        monitor.Should().ContainSingle();
    }

    [Fact]
    public async Task A_Received_Frame_Is_Written_To_The_Frame_Log()
    {
        string dir = Directory.CreateTempSubdirectory("pdnsm-port").FullName;
        try
        {
            var journal = new StationJournal("", _ => { }, _ => { });
            StationFactory.TryOpenFrameLog(Path.Combine(dir, "frames.db"), [], _channel, journal, out FrameLog? log)
                .Should().BeTrue();
            byte[] frame = Packet.SoundModem.Waterfall.Ax25UiFrame.Build("M0LTE", "GB7RDG", [1, 2, 3, 4]);

            _channel.DeliverReceived(Sub, frame, Quality(frame.Length));
            await log!.DisposeAsync();

            var rows = log.Recent(10);
            rows.Should().ContainSingle().Which.Should().Match<Packet.SoundModem.Waterfall.LoggedFrame>(
                r => r.SubChannel == Sub && r.Mode == "fake-mode" && r.LengthBytes == frame.Length);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Sethw_Reaches_A_Port_That_Takes_It_And_Is_Echoed()
    {
        var controllable = new ControllablePort("hw-mode");
        _channel.AddPort(5, controllable);
        var applied = new TaskCompletionSource<KissHardwareEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.HardwareCommand += e => applied.TrySetResult(e);
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);

        await client.SendAsync(new KissFrame(5, KissCommand.SetHardware, [7, 8]));

        KissHardwareEvent seen = await applied.Task.Within("the hardware event");
        seen.Should().Be(new KissHardwareEvent(5, "set", true));
        controllable.Received.Should().Equal(7, 8);
        await client.ReadUntilAsync(
            frames => frames.Any(f => f.Port == 5 && f.Command == KissCommand.SetHardware), "the SETHW echo");
    }

    [Fact]
    public async Task Sethw_Is_Ignored_By_A_Port_That_Does_Not_Take_It()
    {
        var seenEvent = new TaskCompletionSource<KissHardwareEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.HardwareCommand += e => seenEvent.TrySetResult(e);
        using KissTestClient client = await KissTestClient.ConnectAsync(_server);

        await client.SendAsync(new KissFrame(Sub, KissCommand.SetHardware, [7]));

        KissHardwareEvent seen = await seenEvent.Task.Within("the hardware event");
        seen.Applied.Should().BeFalse();
        seen.SubChannel.Should().Be(Sub);
        (await client.FenceAsync()).Should().BeEmpty("a refused SETHW is not echoed");
    }

    [Fact]
    public void Adding_A_Port_Where_A_Port_Is_Throws()
    {
        Action act = () => _channel.AddPort(Sub, new FakePort("other"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*sub-channel 2 already has a port*");
    }

    [Fact]
    public void Adding_A_Port_Where_A_Modem_Is_Throws_And_The_Reverse()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));

        Action port = () => _channel.AddPort(0, new FakePort("x"));
        Action modem = () => _channel.AddModem(Sub, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));

        port.Should().Throw<InvalidOperationException>().WithMessage("*sub-channel 0 already has a modem*");
        modem.Should().Throw<InvalidOperationException>().WithMessage("*sub-channel 2 already has a port*");
        _channel.Modems.Should().ContainKey(0).And.NotContainKey(Sub);
    }

    [Fact]
    public void A_Port_Is_Not_Listed_As_A_Modem()
    {
        _channel.Modems.Should().BeEmpty();
        _channel.Ports.Should().ContainKey(Sub).WhoseValue.Should().BeSameAs(_port);
    }

    [Fact]
    public void Delivering_On_A_Sub_Channel_With_No_Port_Throws()
    {
        Action act = () => _channel.DeliverReceived(9, [1], Quality(1));

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>A port whose transmissions finish when the test says so.</summary>
    private class FakePort(string mode) : ISubChannelPort
    {
        private readonly List<TaskCompletionSource> _pending = [];
        private readonly ConcurrentQueue<byte[]> _calls = new();
        private readonly SemaphoreSlim _called = new(0);

        public string Mode { get; } = mode;

        public Exception? ThrowSynchronously { get; set; }

        public Task Transmit(byte[] frame)
        {
            if (ThrowSynchronously is { } failure)
            {
                throw failure;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
            {
                _pending.Add(done);
            }

            _calls.Enqueue(frame);
            _called.Release();
            return done.Task;
        }

        /// <summary>The frame of the next call to <see cref="Transmit"/>, once it has been made.</summary>
        public async Task<byte[]> NextCall()
        {
            await _called.WaitAsync();
            _calls.TryDequeue(out byte[]? frame).Should().BeTrue();
            return frame!;
        }

        public void Complete(int call)
        {
            lock (_pending)
            {
                _pending[call].SetResult();
            }
        }

        public void Fail(int call, Exception reason)
        {
            lock (_pending)
            {
                _pending[call].SetException(reason);
            }
        }
    }

    private sealed class ControllablePort(string mode) : FakePort(mode), IHardwareControllable
    {
        public byte[] Received { get; private set; } = [];

        public bool TrySetHardware(ReadOnlySpan<byte> payload, out string outcome)
        {
            Received = payload.ToArray();
            outcome = "set";
            return true;
        }
    }
}
