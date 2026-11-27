using System.Reflection;
using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Modems.OfdmFmStation;
using OfdmStation = M0LTE.OfdmFm.Station;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The host's operations are thin calls into the station under its lock, and every station event a host needs is re-raised. Nothing here depends on elapsed time: the station's clock is the audio the test feeds,
/// and the one test that calls from another thread while audio flows decides its outcome by the events, not by how long anything took.
/// </summary>
public class OfdmFmStationHostOperationsTests
{
    private static readonly LinkAddress Me = LinkAddress.FromAscii("G4ABC-1");
    private static readonly LinkAddress Peer = LinkAddress.FromAscii("M0XYZ-1");

    private static byte[] Datagram(int seed, int length)
    {
        var d = new byte[length];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static SendResult Send(OfdmFmStationHost host, LinkAddress to, int tag, int length = 40, bool report = false, SendPriority priority = SendPriority.Ordinary) =>
        host.Send(Datagram(tag, length), to, Me, LinkProtocol.NoLayer3, new HostTag(tag), report, priority);

    [Fact]
    public async Task SetIdentity_passes_the_stations_answer_through()
    {
        await using var rig = new StationRig();

        rig.Host.SetIdentity("not a callsign").Should().Be(IdentityResult.Invalid);
        rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);
        rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted, "the same one again changes nothing");
        rig.Host.SetIdentity("M0XYZ").Should().Be(IdentityResult.Different);
    }

    [Fact]
    public async Task Send_passes_each_result_through_and_every_parameter_reaches_the_station()
    {
        await using var rig = new StationRig();

        Send(rig.Host, Peer, 1).Should().Be(SendResult.NoIdentity);
        rig.Host.SetIdentity("G4ABC");
        Send(rig.Host, Peer, 2).Should().Be(SendResult.Accepted);
        Send(rig.Host, Peer, 3, report: true, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        Send(rig.Host, Peer, 4, length: 60000).Should().Be(SendResult.TooLarge);
        Action empty = () => rig.Host.Send(ReadOnlyMemory<byte>.Empty, Peer, Me, LinkProtocol.NoLayer3, new HostTag(5));
        empty.Should().Throw<ArgumentException>("the station's own argument check");
    }

    [Fact]
    public async Task Cancel_by_tag_and_by_address_drop_what_is_queued_and_raise_Dropped_once_each()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        var dropped = new List<DroppedEvent>();
        rig.Host.Dropped += dropped.Add;
        Send(rig.Host, Peer, 7).Should().Be(SendResult.Accepted);
        Send(rig.Host, Peer, 8).Should().Be(SendResult.Accepted);
        Send(rig.Host, Peer, 9).Should().Be(SendResult.Accepted);

        rig.Host.Cancel(new HostTag(7)).Should().Be(1);
        dropped.Should().Equal([new DroppedEvent(new HostTag(7), DropReason.Cancelled)]);
        rig.Host.Cancel(new HostTag(7)).Should().Be(0, "it is gone");

        rig.Host.Cancel(Peer).Should().Be(2);
        dropped.Select(d => d.Tag.Value).Should().Equal(7, 8, 9);
        dropped.Should().OnlyContain(d => d.Reason == DropReason.Cancelled);
        rig.Host.Cancel(Peer).Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_lists_the_addresses_sent_to_as_the_station_knows_them()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        rig.Host.Snapshot().Should().BeEmpty();

        Send(rig.Host, Peer, 1).Should().Be(SendResult.Accepted);

        IReadOnlyList<LinkStateEvent> snapshot = rig.Host.Snapshot();
        LinkStateEvent only = snapshot.Should().ContainSingle().Subject;
        only.Address.Should().Be(Peer);
        only.Located.Should().BeFalse("nothing has been heard from it");
        rig.Host.StationForTests.Snapshot().Should().Equal(snapshot, "the host's answer is the station's");
    }

    [Fact]
    public async Task A_full_queue_raises_Congestion_once_and_the_result_is_QueueFull()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        var congestion = new List<CongestionEvent>();
        rig.Host.Congestion += congestion.Add;

        SendResult result = SendResult.Accepted;
        for (int i = 1; i < 20000 && result == SendResult.Accepted; i++)
        {
            result = Send(rig.Host, Peer, i);
        }

        result.Should().Be(SendResult.QueueFull);
        congestion.Should().Equal([new CongestionEvent(Peer, false)]);
    }

    [Fact]
    public async Task Every_event_the_station_raises_for_a_host_is_re_raised_once_with_the_stations_data()
    {
        await using var rig = new StationRig();
        OfdmFmStationHost host = rig.Host;
        LinkAddress address = Peer;

        Raise(host, nameof(OfdmStation.Received), e => host.Received += e, new ReceivedEvent(new byte[] { 1, 2, 3 }, 0xA1B2C3, 7, Addressing.ToEveryone, null, 12.5));
        Raise(host, nameof(OfdmStation.LinkState), e => host.LinkState += e, new LinkStateEvent(address, true, 3, 1200.0, 900.0, 200, default, 4.5, false));
        Raise(host, nameof(OfdmStation.PathReport), e => host.PathReport += e, new PathReportEvent(address, 2, true));
        Raise(host, nameof(OfdmStation.BurstNotDecoded), e => host.BurstNotDecoded += e, new BurstNotDecodedEvent(address, [new HostTag(4), new HostTag(5)]));
        Raise(host, nameof(OfdmStation.ChannelBusy), e => host.ChannelBusy += e, new ChannelBusyEvent(true));
        Raise(host, nameof(OfdmStation.Congestion), e => host.Congestion += e, new CongestionEvent(address, false));
        Raise(host, nameof(OfdmStation.ModemSent), e => host.ModemSent += e, new ModemSentEvent(ModemSentKind.Identification, 1));
        Raise(host, nameof(OfdmStation.Identified), e => host.Identified += e, new IdentifiedEvent("M0XYZ", 0));
        Raise(host, nameof(OfdmStation.Transmitted), e => host.Transmitted += e, new TransmittedEvent(new HostTag(1), address, 2, 3, 1200.0, 1.5, 6.0));
        Raise(host, nameof(OfdmStation.Dropped), e => host.Dropped += e, new DroppedEvent(new HostTag(1), DropReason.NoLongerFits));
    }

    [Fact]
    public void The_host_re_raises_every_station_event_except_the_ones_that_are_the_hosts_own_business()
    {
        string[] stations = [.. typeof(OfdmStation).GetEvents().Select(e => e.Name)];
        string[] hosts = [.. typeof(OfdmFmStationHost).GetEvents().Select(e => e.Name)];

        // The handshake and the work signal are the host's to answer, and the identification-due event is the Morse ident's trigger.
        string[] ownBusiness = [nameof(OfdmStation.TransmitRequest), nameof(OfdmStation.WithdrawRequest), nameof(OfdmStation.WorkAvailable), nameof(OfdmStation.IdentificationDue)];
        stations.Except(ownBusiness).Except(hosts).Should().BeEmpty("a station event a host needs has no counterpart on the host");
    }

    /// <summary>Makes the station raise <paramref name="name"/> with <paramref name="value"/> (through the event's own delegate, as the station does) and checks the host raised it once, with the same data.</summary>
    private static void Raise<T>(OfdmFmStationHost host, string name, Action<Action<T>> subscribe, T value)
    {
        var seen = new List<T>();
        subscribe(seen.Add);
        FieldInfo field = typeof(OfdmStation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"no field behind {name}");
        var raise = (Action<T>?)field.GetValue(host.StationForTests);
        raise.Should().NotBeNull($"the host subscribed to {name}");
        raise!(value);
        seen.Should().ContainSingle($"{name} is re-raised once").Which.Should().Be(value);
    }

    [Fact]
    public async Task An_operation_called_from_inside_an_event_handler_gets_the_stations_refusal_and_does_not_deadlock()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        Send(rig.Host, Peer, 1).Should().Be(SendResult.Accepted);
        var refusals = new List<Exception>();
        rig.Host.Dropped += _ =>
        {
            foreach (Action call in new Action[]
            {
                () => Send(rig.Host, Peer, 2),
                () => rig.Host.Cancel(new HostTag(9)),
                () => rig.Host.Cancel(Peer),
                () => rig.Host.Snapshot(),
            })
            {
                try
                {
                    call();
                }
                catch (InvalidOperationException refused)
                {
                    refusals.Add(refused);
                }
            }
        };

        await Eventually.CompletesAsync(Task.Run(() => rig.Host.Cancel(new HostTag(1))), "the cancel to return, not to deadlock on a call from its own handler");

        refusals.Should().HaveCount(4, "the station refuses each call made from inside its event");
        rig.Host.Snapshot().Should().NotBeNull("the host is still usable afterwards");
    }

    [Fact]
    public async Task Send_from_another_thread_while_audio_is_flowing_returns_its_result_and_every_datagram_goes()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        rig.StartTransmitter();
        const int Count = 8;

        // Another thread (a tool's command loop) calls Send as the test feeds audio on the receive thread.
        Task<SendResult[]> sender = Task.Run(() => Enumerable.Range(1, Count).Select(i => Send(rig.Host, LinkAddress.Everyone, i)).ToArray());
        await rig.PumpAsync(() => rig.Transmitted.Select(t => t.Tag.Value).Distinct().Count() >= Count, "every datagram to go");

        SendResult[] results = await Eventually.SignalAsync(sender, "the other thread's calls to return");
        results.Should().OnlyContain(r => r == SendResult.Accepted);
        rig.Transmitted.Select(t => t.Tag.Value).Distinct().Order().Should().Equal(Enumerable.Range(1, Count).Select(i => (long)i));
    }
}
