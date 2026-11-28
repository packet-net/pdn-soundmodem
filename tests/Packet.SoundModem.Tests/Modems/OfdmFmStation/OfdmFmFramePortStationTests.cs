using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>A station rig with a frame port on its channel, and the frames and events the channel reported, recorded.</summary>
internal sealed class PortRig : IAsyncDisposable
{
    public const int Sub = 0;
    private readonly Lock _gate = new();
    private readonly List<byte[]> _frames = [];
    private readonly List<FrameQuality> _qualities = [];
    private readonly List<byte[]> _transmitted = [];
    private readonly List<(byte[] Frame, Exception Reason)> _rejected = [];

    public PortRig(int seed, string? callsign)
    {
        Rig = new StationRig(seed: seed);
        Port = OfdmFmFramePort.Attach(Rig.Channel, Sub, Rig.Host);
        Rig.Channel.FrameReceived += (_, frame) =>
        {
            lock (_gate)
            {
                _frames.Add(frame);
            }

            Recorded?.Invoke();
        };
        Rig.Channel.FrameReceivedWithQuality += (_, _, quality) =>
        {
            lock (_gate)
            {
                _qualities.Add(quality);
            }

            Recorded?.Invoke();
        };
        Rig.Channel.FrameTransmitted += (_, frame) =>
        {
            lock (_gate)
            {
                _transmitted.Add(frame);
            }
        };
        Rig.Channel.TransmitRejected += (_, frame, reason) =>
        {
            lock (_gate)
            {
                _rejected.Add((frame, reason));
            }
        };
        if (callsign is not null)
        {
            Rig.Host.SetIdentity(callsign).Should().Be(IdentityResult.Accepted);
        }
    }

    public StationRig Rig { get; }

    public OfdmFmFramePort Port { get; }

    /// <summary>Raised after the channel's frame or quality report has been recorded here.</summary>
    public event Action? Recorded;

    /// <summary>
    /// Waits, without polling, until <paramref name="condition"/> holds, looked at again each time any of
    /// <paramref name="rigs"/> records a frame or a quality report.
    /// </summary>
    public static async Task WhenRecordedAsync(Func<bool> condition, string what, params PortRig[] rigs)
    {
        var changes = new ChangeSignal();
        void Pulse() => changes.Pulse();
        foreach (PortRig rig in rigs)
        {
            rig.Recorded += Pulse;
        }

        try
        {
            await changes.WhenAsync(
                condition, what,
                () => string.Join("; ", rigs.Select(r => $"{r.Frames.Length} frame(s), {r.Qualities.Length} quality report(s)")));
        }
        finally
        {
            foreach (PortRig rig in rigs)
            {
                rig.Recorded -= Pulse;
            }
        }
    }

    /// <summary>Waits, without polling, until the port has counted at least <paramref name="count"/> datagrams that are not AX.25.</summary>
    public async Task WhenNonAx25Async(int count, string what)
    {
        var changes = new ChangeSignal();
        void Pulse() => changes.Pulse();
        Port.NonAx25Counted += Pulse;
        try
        {
            await changes.WhenAsync(() => Port.NonAx25Datagrams >= count, what, () => $"{Port.NonAx25Datagrams} counted");
        }
        finally
        {
            Port.NonAx25Counted -= Pulse;
        }
    }

    /// <summary>Frames the channel handed to its listeners as received.</summary>
    public byte[][] Frames { get { lock (_gate) { return [.. _frames]; } } }

    public FrameQuality[] Qualities { get { lock (_gate) { return [.. _qualities]; } } }

    public byte[][] Transmitted { get { lock (_gate) { return [.. _transmitted]; } } }

    public (byte[] Frame, Exception Reason)[] Rejected { get { lock (_gate) { return [.. _rejected]; } } }

    /// <summary>Two stations on one test air, each with a port on its channel and a callsign given.</summary>
    public static (PortRig A, PortRig B) Pair()
    {
        var a = new PortRig(11, "G4ABC");
        var b = new PortRig(12, "M0XYZ");
        var timeline = new Lock();
        a.Rig.Air.Timeline = timeline;
        b.Rig.Air.Timeline = timeline;
        a.Rig.Air.Radiated = chunk => b.Rig.Channel.ProcessReceive(chunk);
        b.Rig.Air.Radiated = chunk => a.Rig.Channel.ProcessReceive(chunk);
        a.Rig.StartTransmitter();
        b.Rig.StartTransmitter();
        return (a, b);
    }

    public async ValueTask DisposeAsync()
    {
        // Every tag ends in Transmitted or Dropped: nothing may be left in the book.
        int left = Port.Outstanding;
        Port.Dispose();
        await Rig.DisposeAsync();
        left.Should().Be(0, "the port's book of outstanding frames is empty at the end of the test");
    }
}

/// <summary>
/// The frame port over real stations: two on a test air, each with a port on its channel. No test waits on time. The test feeds quiet blocks (<see cref="StationRig.PumpAsync(Func{bool}, string, StationRig[])"/>),
/// which waits out every keyup before it feeds the next block, so the virtual clock only advances inside a keyup the transmitter thread is running and the test is not racing it. The one other
/// thread is the port's delivery thread; it never touches the clock, and the tests wait for what it hands up with the hang guard.
/// </summary>
public class OfdmFmFramePortStationTests
{
    private static async Task Settled(Task task, string because) => await Eventually.CompletesAsync(task, because);

    private static Task Ended(Task task) => task.ContinueWith(_ => { }, TaskScheduler.Default);

    [Fact]
    public async Task A_UI_frame_sent_at_one_end_is_delivered_at_the_other_through_the_channel_byte_for_byte()
    {
        (PortRig a, PortRig b) = PortRig.Pair();
        await using PortRig ra = a;
        await using PortRig rb = b;
        byte[] beacon = FramePortFrames.Ui("BEACON", "G4ABC-1", 60);

        Task sent = a.Port.Transmit(beacon);
        sent.IsCompleted.Should().BeFalse("nothing has gone on air");
        await StationRig.PumpAsync(() => b.Rig.Received.Length >= 1, "the frame to reach the other station", a.Rig, b.Rig);
        await PortRig.WhenRecordedAsync(() => b.Frames.Length >= 1 && b.Qualities.Length >= 1, "the channel to report the frame", b);
        await Settled(sent, "the frame's task to complete");

        b.Frames.Should().ContainSingle().Which.Should().Equal(beacon);
        b.Rig.Received.Single().Addressing.Should().Be(Addressing.ToEveryone);
        b.Rig.Received.Single().FromKey.Should().Be(LinkAddress.FromAscii("G4ABC-1").Key);
        FrameQuality quality = b.Qualities.Single();
        quality.Mode.Should().Be("ofdm-fm");
        quality.FrameBytes.Should().Be(beacon.Length);
        quality.CrcValid.Should().BeTrue();
        a.Port.Mode.Should().Be("ofdm-fm");
        a.Rig.Transmitted.Should().ContainSingle();
    }

    [Fact]
    public async Task An_addressed_exchange_arrives_in_order_the_first_frame_to_everyone_and_the_rest_addressed()
    {
        (PortRig a, PortRig b) = PortRig.Pair();
        await using PortRig ra = a;
        await using PortRig rb = b;
        byte[] sabm = FramePortFrames.Sabm("M0XYZ-1", "G4ABC-1");
        byte[] ua = FramePortFrames.Ua("G4ABC-1", "M0XYZ-1");
        byte[] i0 = FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0, 40);
        byte[] i1 = FramePortFrames.I("M0XYZ-1", "G4ABC-1", 1, 0, 40);

        Task sabmSent = a.Port.Transmit(sabm);
        await StationRig.PumpAsync(() => b.Rig.Received.Length >= 1, "the SABM to arrive", a.Rig, b.Rig);
        await Settled(sabmSent, "the SABM's task");

        Task uaSent = b.Port.Transmit(ua);
        await StationRig.PumpAsync(() => a.Rig.Received.Length >= 1, "the UA to arrive", a.Rig, b.Rig);
        await Settled(uaSent, "the UA's task");

        Task first = a.Port.Transmit(i0);
        Task second = a.Port.Transmit(i1);
        await StationRig.PumpAsync(() => b.Rig.Received.Length >= 3, "both I frames to arrive", a.Rig, b.Rig);
        await Settled(first, "the first I frame's task");
        await Settled(second, "the second I frame's task");
        await PortRig.WhenRecordedAsync(() => b.Frames.Length >= 3 && a.Frames.Length >= 1, "the channels to report every frame", a, b);

        b.Frames.Should().HaveCount(3);
        b.Frames[0].Should().Equal(sabm);
        b.Frames[1].Should().Equal(i0);
        b.Frames[2].Should().Equal(i1);
        a.Frames.Should().ContainSingle().Which.Should().Equal(ua);

        Addressing[] how = [.. b.Rig.Received.Select(r => r.Addressing)];
        how[0].Should().Be(Addressing.ForAddressNotYetLocated, "at first contact the frame goes to everyone, for the address it is for");
        how[1..].Should().AllBeEquivalentTo(Addressing.ToThisStation, "once the other end is located, frames are addressed");
        a.Rig.Received.Single().Addressing.Should().Be(Addressing.ToThisStation);

        // Each frame says whom it is from: the station it was transmitted as. This is what tells a swapped pair of addresses from the right one, since the exchange is symmetrical.
        b.Rig.Received.Select(r => r.FromKey).Should().AllBeEquivalentTo(LinkAddress.FromAscii("G4ABC-1").Key);
        a.Rig.Received.Single().FromKey.Should().Be(LinkAddress.FromAscii("M0XYZ-1").Key);
    }

    [Fact]
    public async Task A_frame_cancelled_in_the_station_faults_its_task_with_the_reason()
    {
        (PortRig a, PortRig b) = PortRig.Pair();
        await using PortRig ra = a;
        await using PortRig rb = b;

        Task sent = a.Port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0));
        a.Rig.Host.Cancel(LinkAddress.FromAscii("M0XYZ-1")).Should().Be(1);

        Exception fault = await ThrownAsync(sent);
        fault.Message.Should().Be("frame cancelled before it was sent");
        a.Rig.Requests.Should().BeEmpty("nothing was built, so nothing could be keyed");
    }

    [Fact]
    public async Task With_no_callsign_the_task_faults_at_once_and_nothing_is_keyed()
    {
        await using var rig = new PortRig(1, callsign: null);

        Task sent = rig.Port.Transmit(FramePortFrames.Ui("BEACON", "G4ABC-1"));

        sent.IsFaulted.Should().BeTrue();
        (await ThrownAsync(sent)).Message.Should().Be("no callsign set, not sent");
        await rig.Rig.FeedAsync(200);
        rig.Rig.Air.KeyAttempts.Should().Be(0);
        rig.Rig.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_frame_that_does_not_read_is_refused_and_nothing_is_sent()
    {
        await using var rig = new PortRig(1, "G4ABC");

        Task sent = rig.Port.Transmit([1, 2, 3, 4]);

        (await ThrownAsync(sent)).Message.Should().Be("frame addresses unreadable, not sent");
        rig.Port.Outstanding.Should().Be(0);
        rig.Rig.Host.Snapshot().Should().BeEmpty("nothing reached the station");
    }

    [Fact]
    public async Task A_frame_too_large_for_one_burst_is_refused_in_one_line()
    {
        await using var rig = new PortRig(1, "G4ABC");

        Task sent = rig.Port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0, 4000));

        Exception fault = await ThrownAsync(sent);
        fault.Message.Should().StartWith("frame too large for one burst to M0XYZ-1 now").And.EndWith(", not sent");
        fault.Message.Should().NotContain("\n");
    }

    [Fact]
    public async Task A_full_queue_refuses_and_cancelling_it_faults_every_waiting_task()
    {
        await using var rig = new PortRig(1, "G4ABC");
        var waiting = new List<Task>();
        Task? refused = null;
        for (int n = 0; n < 1000 && refused is null; n++)
        {
            Task sent = rig.Port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", (byte)(n % 8), 0, 200));
            if (sent.IsFaulted)
            {
                refused = sent;
            }
            else
            {
                waiting.Add(sent);
            }
        }

        refused.Should().NotBeNull("the queue to one address holds a limit");
        (await ThrownAsync(refused!)).Message.Should().Be("queue to M0XYZ-1 full, not sent");
        rig.Port.Outstanding.Should().Be(waiting.Count);

        rig.Rig.Host.Cancel(LinkAddress.FromAscii("M0XYZ-1")).Should().Be(waiting.Count);

        foreach (Task task in waiting)
        {
            (await ThrownAsync(task)).Message.Should().Be("frame cancelled before it was sent");
        }
    }

    [Fact]
    public async Task A_datagram_of_another_protocol_is_counted_and_not_delivered_as_a_frame()
    {
        (PortRig a, PortRig b) = PortRig.Pair();
        await using PortRig ra = a;
        await using PortRig rb = b;

        a.Rig.Host.Send(new byte[30], LinkAddress.Everyone, LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.Ip, new HostTag(1_000_000)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(() => b.Rig.Received.Length >= 1, "the datagram to arrive", a.Rig, b.Rig);

        // The rig's handler runs before the port's, on the transmitting thread: wait for the port's own count.
        await b.WhenNonAx25Async(1, "the port to count the datagram");
        b.Port.NonAx25Datagrams.Should().Be(1);
        b.Frames.Should().BeEmpty("a datagram is only queued for delivery on the other branch, so nothing is in flight");
    }

    [Fact]
    public async Task Disposing_the_port_faults_what_is_still_waiting()
    {
        PortRig rig = new(1, "G4ABC");
        Task sent = rig.Port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0));
        rig.Port.Outstanding.Should().Be(1);

        rig.Port.Dispose();

        (await ThrownAsync(sent)).Message.Should().Be("frame port closed");
        rig.Port.Outstanding.Should().Be(0);
        (await ThrownAsync(rig.Port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0)))).Message.Should().Be("frame port closed");
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task The_channels_own_path_announces_the_frame_when_it_has_gone_and_a_refusal_when_it_has_not()
    {
        (PortRig a, PortRig b) = PortRig.Pair();
        await using PortRig ra = a;
        await using PortRig rb = b;
        await using var silent = new PortRig(3, callsign: null);
        byte[] frame = FramePortFrames.Ui("BEACON", "G4ABC-1", 30);

        Task sent = a.Rig.Channel.EnqueueTransmit(PortRig.Sub, frame);
        a.Transmitted.Should().BeEmpty("it has not gone");
        await StationRig.PumpAsync(() => b.Rig.Received.Length >= 1, "the frame to arrive", a.Rig, b.Rig);
        await Settled(sent, "the channel's task");

        a.Transmitted.Should().ContainSingle().Which.Should().Equal(frame);
        a.Rejected.Should().BeEmpty();

        Task refused = silent.Rig.Channel.EnqueueTransmit(PortRig.Sub, frame);
        Exception fault = await ThrownAsync(refused);
        fault.Message.Should().Be("no callsign set, not sent");
        silent.Rejected.Should().ContainSingle().Which.Reason.Message.Should().Be("no callsign set, not sent");
        silent.Transmitted.Should().BeEmpty();
    }

    private static async Task<Exception> ThrownAsync(Task task)
    {
        await Eventually.CompletesAsync(Ended(task), "the task to end");
        task.IsFaulted.Should().BeTrue("the task faults");
        return task.Exception!.InnerException!;
    }
}
