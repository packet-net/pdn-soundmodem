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
        new() { SubChannel = 0, Mode = "afsk1200", RfFrequency = 7_049_900 },
        new() { SubChannel = 3, Mode = "afsk1200", RfFrequency = 7_053_800, TransmitOnlyUnderLease = true },
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
        Dictionary<int, RfPlan.Result> alone = BandPlanner.PlanLeaseOnly(modems, "usb", 12000);
        channel.RequireTunedLease(3, () => rig.WindowOwner == TxLeaseTuning.Owner);
        channel.LeaseTuningOpen = () => rig.WindowOwner == TxLeaseTuning.Owner;
        var tuning = new TxLeaseTuning(
            rig, channel.TransmitLease, sub => TxLeaseTuning.Resolve(sub, modems, plan, alone, 0, null))
        {
            KeyedWait = TimeSpan.FromMilliseconds(100),
        };
        return (channel, rig, tuning, modems, plan);
    }

    private int Tunes() => _fake.Sets.Count(c => c[0] is 'F' or 'M');

    private static (int Status, JsonObject Answer) Post(
        SoundModemChannel channel, RigControl? rig, TxLeaseTuning? tuning, string body) =>
        TxLeaseApi.Handle(channel, "POST", body, null, rig, tuning);

    [Fact]
    public async Task A_Take_That_Asks_Tunes_The_Rig_For_The_Holders_Modem_And_Release_Puts_It_Back()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, List<ModemConfig> modems, _) = await Station();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""");

        status.Should().Be(200);
        double audio = modems.Single(m => m.SubChannel == 3).Frequency!.Value;
        long dial = (long)Math.Round(7_053_800 - audio);
        (dial + audio).Should().BeApproximately(7_053_800, 0.5, "the modem's rfFrequency lands at its audio centre");
        answer["tuned"]!.GetValue<bool>().Should().BeTrue();
        answer["dialHz"]!.GetValue<long>().Should().Be(dial);
        answer["audioCentreHz"]!.GetValue<double>().Should().BeApproximately(audio, 0.1);
        answer["mode"]!.GetValue<string>().Should().Be("USB");
        answer["passbandHz"]!.GetValue<int>().Should().BeGreaterThan(0);
        _fake.DialHz.Should().Be(dial);
        rig.HoldsTransmitter.Should().BeFalse("the holder has to be able to transmit inside its own tuning");

        JsonObject read = TxLeaseApi.Handle(channel, "GET", "", null, rig, tuning).Answer;
        read["tuned"]!.GetValue<bool>().Should().BeTrue();
        read["dialHz"]!.GetValue<long>().Should().Be(dial);
        read["audioCentreHz"]!.GetValue<double>().Should().BeApproximately(audio, 0.1);

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

        for (int i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""").Status.Should().Be(200);
        }

        tuning.Held.Should().NotBeNull("renewals carry on past the window's first length");
        _fake.DialHz.Should().NotBe(14_074_000);
    }

    [Fact]
    public async Task An_Expired_Lease_Puts_The_Rig_Back()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""").Status.Should().Be(200);
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
        Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60, "tune": true}""").Status.Should().Be(200);

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
    public async Task An_Everyday_Modem_Asking_To_Tune_Is_Told_It_Is_Already_On_Its_Dial()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        int tunes = Tunes();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 0, "tune": true}""");

        status.Should().Be(200);
        answer["tuned"]!.GetValue<bool>().Should().BeFalse();
        answer["note"]!.GetValue<string>().Should().Contain("everyday dial");
        answer.ContainsKey("tuneFailed").Should().BeFalse();
        Tunes().Should().Be(tunes);
        channel.TransmitLease.Holder.Should().Be(0);
    }

    [Fact]
    public async Task A_Lease_Only_Modem_Cannot_Take_A_Lease_Without_Tune()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 60}""");

        status.Should().Be(409);
        answer["tuneFailed"]!.GetValue<bool>().Should().BeTrue();
        answer["refused"]!.GetValue<string>().Should().Contain("\"tune\": true");
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public async Task A_Tuned_Lease_Is_Capped_So_Its_Window_Outlives_The_Closing_Ident()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();

        JsonObject answer = Post(channel, rig, tuning, """{"subChannel": 3, "seconds": 3600, "tune": true}""").Answer;

        answer["seconds"]!.GetValue<double>().Should().Be(240);
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Every_Source_Attributed_To_A_Lease_Only_Sub_Channel_Is_Held_To_The_Tuned_Lease()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();
        var testTone = new object();
        channel.TransmitLease.Attribute(testTone, 3);
        var identifier = new Packet.SoundModem.Ident.StationIdentifier(
            "G8ABC", null, 1000, 25, TimeSpan.FromMinutes(10), 12000);
        identifier.NoteTransmission();
        IdentTransmission.Register(channel, 3, identifier);

        // No lease: the test tone (and a probe, queued the same way) and the ident are refused.
        channel.EnqueueTransmit(_ => new float[100], source: testTone).IsFaulted.Should().BeTrue();
        channel.TunedLeaseAdmits(testTone).Should().BeFalse();
        IdentTransmission.ShouldSend(channel, identifier).Should().BeFalse("not tuned, so no ident");

        // A lease with no tuning is no better.
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.TunedLeaseAdmits(testTone).Should().BeFalse();
        channel.TransmitLease.Release(3);

        // Tuned: they are admitted.
        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(200);
        channel.TunedLeaseAdmits(testTone).Should().BeTrue();
        IdentTransmission.ShouldSend(channel, identifier).Should().BeTrue();
        Task closing = channel.EnqueueTransmit(_ => new float[100], source: testTone);
        closing.IsFaulted.Should().BeFalse();

        // Restored: what is still queued is refused, not sent on the everyday dial.
        Post(channel, rig, tuning, """{"release": true, "subChannel": 3}""").Status.Should().Be(200);
        channel.TunedLeaseAdmits(testTone).Should().BeFalse();
        IdentTransmission.ShouldSend(channel, identifier).Should().BeFalse("after the restore");
        channel.EnqueueTransmit(_ => new float[100], source: testTone).IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_Transmits_While_The_Lease_Tuning_Is_Open_And_Nobody_Holds_The_Lease()
    {
        (SoundModemChannel channel, RigControl rig, _, _, _) = await Station();

        rig.Tune(new RigTuning(7_052_000, "USB", 3000), TimeSpan.FromSeconds(60), TxLeaseTuning.Owner, holdsTransmitter: false)
            .Granted.Should().BeTrue();

        Task everyday = channel.EnqueueTransmit(0, new byte[20]);
        everyday.IsFaulted.Should().BeTrue("an everyday modem must not key on the lease's dial");
        everyday.Exception!.InnerException!.Message.Should().Contain("being tuned for a transmit lease");
    }

    [Fact]
    public async Task A_Keyed_Rig_Is_Waited_For_Then_The_Lease_Is_Given_Back_When_It_Still_Cannot_Tune()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station(keysThroughRig: true);
        rig.Key();

        (int status, JsonObject answer) = Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""");

        status.Should().Be(409);
        answer["tuneFailed"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull("a lease that could not be tuned is not kept");
        rig.Unkey();
        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(200);
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

    [Fact]
    public void The_Band_Plan_Leaves_Out_A_Lease_Only_Modem_And_Plans_It_Alone()
    {
        List<ModemConfig> modems = Modems();
        RfPlan.Result everyday = Plan(modems);
        Dictionary<int, RfPlan.Result> alone = BandPlanner.PlanLeaseOnly(modems, "usb", 12000);

        everyday.Modems.Select(m => m.Slot.SubChannel).Should().Equal(0);
        RfPlan.Result onlyOrdinary = BandPlanner.Plan(
            [new ModemConfig { SubChannel = 0, Mode = "afsk1200", RfFrequency = 7_049_900 }], "usb", null, 12000)!;
        everyday.DialHz.Should().Be(onlyOrdinary.DialHz, "the flagged modem does not move the everyday dial");

        double centre = modems.Single(m => m.SubChannel == 3).Frequency!.Value;
        alone.Should().ContainKey(3);
        alone[3].DialHz.Should().Be(7_053_800 - centre);
        alone[3].Modems.Single().AudioCentreHz.Should().Be(centre);
        centre.Should().BeInRange(300, 2700);
    }

    [Fact]
    public async Task Frames_For_A_Lease_Only_Modem_Are_Refused_Unless_Its_Lease_Is_Tuned()
    {
        (SoundModemChannel channel, RigControl rig, TxLeaseTuning tuning, _, _) = await Station();

        Task untuned = channel.EnqueueTransmit(3, new byte[20]);
        untuned.IsFaulted.Should().BeTrue("no lease");
        untuned.Exception!.InnerException!.Message.Should().Contain("tuned transmit lease");

        // A lease taken without tuning is no use to it either.
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.EnqueueTransmit(3, new byte[20]).IsFaulted.Should().BeTrue("a lease with no tuning");
        channel.TransmitLease.Release(3);

        Post(channel, rig, tuning, """{"subChannel": 3, "tune": true}""").Status.Should().Be(200);
        Task tuned = channel.EnqueueTransmit(3, new byte[20]);
        tuned.IsFaulted.Should().BeFalse("it holds a tuned lease");

        // The everyday modem is an ordinary one, refused only by the lease as ever.
        channel.EnqueueTransmit(0, new byte[20]).IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task A_Lease_Only_Modem_Hears_Nothing_For_The_Station_Outside_Its_Tuning()
    {
        (SoundModemChannel channel, _, _, _, _) = await Station();

        channel.CarrierDetect.Should().BeFalse();
        channel.ChannelBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"device": "flex:10.45.0.76", "modems": [{"subChannel": 0, "mode": "afsk1200", "rfFrequency": 7049900}, {"subChannel": 3, "mode": "afsk1200", "rfFrequency": 7053800, "transmitOnlyUnderLease": true}]}""", "needs a \"rig\" section")]
    [InlineData("""{"device": "null", "modems": [{"subChannel": 0, "mode": "afsk1200", "rfFrequency": 7049900}, {"subChannel": 3, "mode": "afsk1200", "rfFrequency": 7053800, "transmitOnlyUnderLease": true}]}""", "needs a \"rig\" section")]
    [InlineData("""{"device": "null", "rig": {}, "modems": [{"subChannel": 0, "mode": "afsk1200", "transmitOnlyUnderLease": true}]}""", "needs an \"rfFrequency\"")]
    public void The_Flag_Is_Refused_Without_A_Rig_Or_An_Rf_Frequency(string json, string why)
    {
        string dir = Directory.CreateTempSubdirectory("pdnsm-lo").FullName;
        try
        {
            string path = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(path, json);
            DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
            config.Should().BeNull();
            error.Should().Contain(why);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Flag_Loads_With_A_Rig()
    {
        string dir = Directory.CreateTempSubdirectory("pdnsm-lo").FullName;
        try
        {
            string path = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(path, """{"device": "null", "rig": {}, "modems": [{"subChannel": 0, "mode": "afsk1200", "rfFrequency": 7049900}, {"subChannel": 3, "mode": "afsk1200", "rfFrequency": 7053800, "transmitOnlyUnderLease": true}]}""");
            DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
            error.Should().BeEmpty();
            config!.Modems.Single(m => m.SubChannel == 3).TransmitOnlyUnderLease.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_Lease_Only_Modems_Ident_Is_Placed_Against_The_Tuned_Dial()
    {
        List<ModemConfig> modems = Modems();
        RfPlan.Result everyday = Plan(modems);
        Dictionary<int, RfPlan.Result> alone = BandPlanner.PlanLeaseOnly(modems, "usb", 12000);

        RfPlan.Result? plan = TxLeaseTuning.PlanFor(3, everyday, alone);

        plan.Should().BeSameAs(alone[3]);
        TxLeaseTuning.PlanFor(0, everyday, alone).Should().BeSameAs(everyday);
        // An ident at rf 7053.8 MHz is the audio centre against the tuned dial, which the
        // everyday dial would put elsewhere.
        double tone = 7_053_800 - plan!.DialHz;
        tone.Should().Be(modems.Single(m => m.SubChannel == 3).Frequency);
        (7_053_800 - everyday.DialHz).Should().NotBe(tone);
    }
}
