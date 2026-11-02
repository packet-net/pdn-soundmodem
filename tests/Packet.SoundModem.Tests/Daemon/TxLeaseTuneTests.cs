using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Tests.Rig;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Issue #760: <c>"tune": true</c> on a transmit lease tunes the rig for the holder's modem,
/// against a fake rigctld and a fake clock.
/// </summary>
public sealed class TxLeaseTuneTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRigctld _fake = new();
    private readonly FakeTimeProvider _time = new(Noon);
    private readonly List<string> _said = [];
    private readonly List<RigControl> _rigs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (RigControl rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _fake.DisposeAsync();
    }

    private static List<ModemConfig> Modems() =>
    [
        new() { SubChannel = 0, Mode = "afsk1200", RfFrequency = 7_051_700 },
        new() { SubChannel = 3, Mode = "afsk1200", RfFrequency = 7_052_100 },
    ];

    private static RfPlan.Result Plan(List<ModemConfig> modems) =>
        BandPlanner.Plan(modems, "usb", null, 12000)!;

    private async Task<(SoundModemChannel Channel, RigControl Rig, TxLeaseTuning Tuning, List<ModemConfig> Modems, RfPlan.Result Plan)>
        Station(bool keysThroughRig = false)
    {
        var channel = new SoundModemChannel(12000, _time, randomSeed: 1);
        channel.AddModem(0, sink => new Afsk1200Modem(12000, sink));
        channel.AddModem(3, sink => new Afsk1200Modem(12000, sink));
        var rig = new RigControl(new RigControlOptions
        {
            Endpoint = _fake.Endpoint,
            Time = _time,
            KeysThroughRig = keysThroughRig,
            ReplyTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });
        rig.Journal += _said.Add;
        _rigs.Add(rig);
        (await rig.StartAsync(CancellationToken.None)).Should().BeTrue();
        channel.TransmitInhibit = () => rig.HoldsTransmitter;
        List<ModemConfig> modems = Modems();
        RfPlan.Result plan = Plan(modems);
        var tuning = new TxLeaseTuning(
            rig, channel.TransmitLease, sub => TxLeaseTuning.Resolve(sub, modems, plan, 0, null));
        return (channel, rig, tuning, modems, plan);
    }

    private int Tunes() => _fake.Sets.Count(c => c[0] is 'F' or 'M');

    private static (int Status, JsonObject Answer) Post(
        SoundModemChannel channel, RigControl? rig, TxLeaseTuning? tuning, string body) =>
        TxLeaseApi.Handle(channel, "POST", body, null, rig, tuning);

    [Fact]
    public async Task A_Take_That_Asks_Tunes_The_Rig_For_The_Holders_Modem_And_Release_Puts_It_Back()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, RfPlan.Result plan) = await Station();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""");

        status.Should().Be(200);
        double audio = plan.Modems.Single(p => p.Slot.SubChannel == 3).AudioCentreHz;
        long dial = (long)Math.Round(7_052_100 - audio);
        answer["tuned"]!.GetValue<bool>().Should().BeTrue();
        answer["dialHz"]!.GetValue<long>().Should().Be(dial);
        answer["mode"]!.GetValue<string>().Should().Be("USB");
        answer["passbandHz"]!.GetValue<int>().Should().BeGreaterThan(0);
        _fake.DialHz.Should().Be(dial);
        rig.HoldsTransmitter.Should().BeFalse("the holder has to be able to transmit inside its own tuning");

        JsonObject read = TxLeaseApi.Handle(channel, "GET", "", null, rig, tuning).Answer;
        read["tuned"]!.GetValue<bool>().Should().BeTrue();
        read["dialHz"]!.GetValue<long>().Should().Be(dial);

        Post(channel, rig, tuning, """{"release": true, "subChannel": 3}""").Status.Should().Be(200);

        _fake.DialHz.Should().Be(14_074_000);
        _said.Should().Contain(s => s.StartsWith("rig: tuned to ") && s.Contains("the transmit lease"));
        _said.Should().Contain(s => s.StartsWith("rig: put back to "));
        _said.Should().OnlyContain(s => s.All(c => c < 128), "journal lines are plain ASCII");
        TxLeaseApi.Handle(channel, "GET", "", null, rig, tuning).Answer["tuned"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_Renewal_Keeps_The_Tuning_Without_Retuning_It_Even_While_Keyed()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station(keysThroughRig: true);
        Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""").Status.Should().Be(200);
        int sets = Tunes();

        rig.Key();
        _time.Advance(TimeSpan.FromSeconds(30));
        (int status, JsonObject renewed) = Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""");
        rig.Unkey();

        status.Should().Be(200);
        renewed["renewed"]!.GetValue<bool>().Should().BeTrue();
        renewed["tuned"]!.GetValue<bool>().Should().BeTrue();
        Tunes().Should().Be(sets, "a renewal does not retune");

        // A renewal that does not repeat "tune" keeps the tuning alive too.
        for (int i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60}""").Status.Should().Be(200);
        }

        tuning.Held.Should().NotBeNull("renewals carry on past the window's first length");
        _fake.DialHz.Should().NotBe(14_074_000);
    }

    [Fact]
    public async Task An_Expired_Lease_Puts_The_Rig_Back()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        Post(channel, rig, tuning, """{"subChannel": 0, "seconds": 60, "tune": true}""").Status.Should().Be(200);
        _fake.DialHz.Should().NotBe(14_074_000);

        _time.Advance(TimeSpan.FromSeconds(61));

        channel.TransmitLease.Holder.Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
        rig.RestorePending.Should().BeFalse();
    }

    [Fact]
    public async Task Stopping_The_Station_Puts_The_Rig_Back()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        Post(channel, rig, tuning, """{"subChannel": 0, "seconds": 60, "tune": true}""").Status.Should().Be(200);

        await rig.DisposeAsync();

        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Rig_That_Refuses_Refuses_The_Take_With_A_500_And_Leaves_The_Rig_Alone()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        _fake.RefusesFrequency = true;

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""");

        status.Should().Be(500);
        answer["refused"]!.GetValue<string>().Should().Contain("could not be tuned");
        answer["tuned"]!.GetValue<bool>().Should().BeFalse();
        answer["tuneFailed"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Keyed_Rig_Refuses_The_Take_With_A_409_And_Is_Not_Tuned()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station(keysThroughRig: true);
        rig.Key();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""");

        status.Should().Be(409);
        answer["refused"]!.GetValue<string>().Should().Contain("keyed");
        answer["tuneFailed"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task Another_Sub_Channel_Asking_To_Tune_Does_Not_Move_The_Rig()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(200);
        int sets = _fake.Sets.Count;

        (int status, JsonObject other) = Post(channel, rig, tuning, """{"subChannel": 0, "tune": true}""");

        status.Should().Be(409);
        other.ContainsKey("tuneFailed").Should().BeFalse("it is the holder that refused, not the tuning");
        _fake.Sets.Count.Should().Be(sets);
        channel.TransmitLease.Holder.Should().Be(3);
    }

    [Fact]
    public async Task Without_A_Rig_Tune_Is_Accepted_And_Ignored_With_A_Note()
    {
        (SoundModemChannel channel, _, _, _, _) = await Station();

        (int status, JsonObject answer) = Post(channel, null, null, """{"subChannel": 3, "tune": true}""");

        status.Should().Be(200);
        answer["tuned"]!.GetValue<bool>().Should().BeFalse();
        answer["note"]!.GetValue<string>().Should().Contain("no \"rig\" section");
        channel.TransmitLease.Holder.Should().Be(3);
    }

    [Fact]
    public async Task A_Modem_Without_An_RfFrequency_Refuses_The_Tune_With_A_400()
    {
        (SoundModemChannel channel, RigControl rig, _, _, _) = await Station();
        var bare = new List<ModemConfig> { new() { SubChannel = 3, Mode = "afsk1200" } };
        var tuning = new TxLeaseTuning(rig, channel.TransmitLease, sub => TxLeaseTuning.Resolve(sub, bare, null, 0, null));

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""");

        status.Should().Be(400);
        answer["refused"]!.GetValue<string>().Should().Contain("rfFrequency");
        answer["tuneFailed"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Lease_Tuning_And_A_Receive_Window_Stay_Mutually_Exclusive()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();

        // A window open: the lease is refused and the rig is not touched.
        rig.Tune(new RigTuning(7_052_000, "USB", 3000), TimeSpan.FromSeconds(60), ReceiveWindowApi.Owner)
            .Granted.Should().BeTrue();
        _fake.Sets.ToList().Count.Should().BeGreaterThan(0);
        int sets = _fake.Sets.Count;
        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(409);
        _fake.Sets.Count.Should().Be(sets);
        channel.TransmitLease.Holder.Should().BeNull();
        rig.Release(ReceiveWindowApi.Owner);

        // A lease with a tuning held: the window is refused.
        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(200);
        (int status, _) = ReceiveWindowApi.Handle(
            rig, channel.TransmitLease, "POST", """{"dialHz": 7052000, "seconds": 60}""");
        status.Should().Be(409);
        tuning.Held.Should().NotBeNull();
    }
}
