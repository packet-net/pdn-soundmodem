using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Rig control through rigctld, against <see cref="FakeRigctld"/>: connecting and reading the rig,
/// the band plan's dial, PTT, a rigctld that dies part way, and tuning windows that are always put
/// back and never overlap a transmission. Every window and every backoff runs on a fake clock; the
/// only real waiting is for the fake server's socket to answer, and nothing here is timed.
/// </summary>
public sealed class RigControlTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRigctld _fake = new();
    private readonly FakeTimeProvider _time = new(Noon);
    private readonly ConcurrentQueue<string> _said = new();
    private readonly ConcurrentQueue<string> _warned = new();
    private readonly List<RigControl> _rigs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (RigControl rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _fake.DisposeAsync();
    }

    private RigControl Rig(bool keysThroughRig = false, RigTuning? plan = null, RigctldEndpoint? endpoint = null)
    {
        var rig = new RigControl(new RigControlOptions
        {
            Endpoint = endpoint ?? _fake.Endpoint,
            Time = _time,
            Say = _said.Enqueue,
            Warn = _warned.Enqueue,
            KeysThroughRig = keysThroughRig,
            Plan = plan,
        });
        _rigs.Add(rig);
        return rig;
    }

    private async Task<RigControl> Started(bool keysThroughRig = false, RigTuning? plan = null)
    {
        RigControl rig = Rig(keysThroughRig, plan);
        (await rig.StartAsync(CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    /// <summary>
    /// Waits for something the rig does on its own thread, stepping the fake clock by
    /// <paramref name="step"/> between looks. Bounded by a count of looks, not by a time, and the
    /// bound is far beyond anything a working build needs.
    /// </summary>
    private async Task Eventually(Func<bool> condition, string what, TimeSpan step = default)
    {
        for (int look = 0; look < 2000; look++)
        {
            if (condition())
            {
                return;
            }

            if (step > TimeSpan.Zero)
            {
                _time.Advance(step);
            }

            await Task.Delay(5);
        }

        condition().Should().BeTrue(what);
    }

    [Fact]
    public async Task It_Connects_Reads_The_Rig_And_Says_What_It_Is_On()
    {
        RigControl rig = await Started();

        RigState state = rig.Snapshot();
        state.Connected.Should().BeTrue();
        state.Tuning.Should().Be(new RigTuning(14_074_000, "USB", 2400));
        _said.Should().Contain($"rig: rigctld at {_fake.Endpoint}: 14.074000 MHz USB (2400 Hz passband)");
        _fake.Sets.Should().BeEmpty("with no band plan and no rigctld PTT nothing is set");
    }

    [Fact]
    public async Task The_Band_Plan_Sets_The_Mode_And_Passband_Then_The_Dial()
    {
        await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        _fake.DialHz.Should().Be(7_049_450);
        _fake.Mode.Should().Be("USB");
        _fake.PassbandHz.Should().Be(2400);
        _fake.Sets.Should().Equal(["M USB 2400", "F 7049450"], "the mode goes first, as some rigs move the dial on a mode change");
        _said.Should().Contain("rig: setting the rig to 7.049450 MHz USB (2400 Hz passband) from the band plan");
        _warned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Passband_The_Rig_Refuses_Falls_Back_To_Its_Normal_Width_And_Says_So()
    {
        _fake.RefusesPassband = true;
        _fake.NormalPassbandHz = 2700;

        await Started(plan: new RigTuning(7_049_450, "PKTUSB", 2400));

        _fake.Sets.Should().Equal(["M PKTUSB 2400", "M PKTUSB 0", "F 7049450"]);
        _fake.Mode.Should().Be("PKTUSB");
        _warned.Should().ContainSingle(w => w.Contains("would not take a 2400 Hz passband in PKTUSB"));
    }

    [Fact]
    public async Task A_Passband_The_Rig_Sets_Differently_Is_Warned_About()
    {
        _fake.WidestPassbandHz = 1800;

        await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        _warned.Should().ContainSingle(w =>
            w.Contains("asked the rig for a 2400 Hz passband from the band plan, and it reports 1800 Hz"));
    }

    [Fact]
    public async Task A_Dial_The_Rig_Refuses_Is_A_Warning_And_The_Station_Carries_On()
    {
        _fake.RefusesFrequency = true;

        RigControl rig = await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        rig.Connected.Should().BeTrue();
        _warned.Should().ContainSingle(w => w.Contains("RPRT -11") && w.Contains("set the rig to 7.049450 MHz USB"));
    }

    [Fact]
    public async Task Nothing_Listening_Is_A_Warning_Not_A_Failure()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        RigControl rig = Rig(endpoint: new RigctldEndpoint("127.0.0.1", port));
        bool answered = await rig.StartAsync(CancellationToken.None);

        answered.Should().BeFalse();
        rig.Connected.Should().BeFalse();
        _warned.Should().ContainSingle(w => w.Contains($"cannot reach rigctld at 127.0.0.1:{port}"));
        rig.HoldsTransmitter.Should().BeFalse("a missing rigctld does not stop a station keyed by its own line");
    }

    [Fact]
    public async Task A_Rigctld_That_Was_Not_Ready_Is_Retried_And_The_Plan_Applied_When_It_Answers()
    {
        _fake.Accepting = false;
        RigControl rig = Rig(plan: new RigTuning(7_049_450, "USB", 2400));

        (await rig.StartAsync(CancellationToken.None)).Should().BeFalse();
        _fake.DialHz.Should().Be(14_074_000);

        _fake.Accepting = true;
        await Eventually(() => rig.Connected, "the backoff should bring the connection up", TimeSpan.FromSeconds(1));

        _fake.DialHz.Should().Be(7_049_450);
        _said.Should().Contain(
            $"rig: rigctld at {_fake.Endpoint}: 14.074000 MHz USB (2400 Hz passband)",
            "a connection that hung up before answering never counted as one");
    }

    [Fact]
    public async Task A_Reconnect_That_Finds_The_Rig_Elsewhere_Puts_It_Back_On_The_Band_Plan()
    {
        RigControl rig = await Started(plan: new RigTuning(7_049_450, "USB", 2400));

        // A rig that was power-cycled while rigctld was restarted.
        _fake.DialHz = 145_000_000;
        _fake.Mode = "FM";
        _fake.Kill();

        await Eventually(() => _fake.DialHz == 7_049_450, "the reconnect should restore the plan", TimeSpan.FromSeconds(5));
        _fake.Mode.Should().Be("USB");
        _said.Should().Contain("rig: the rig is not where the band plan puts it; setting it back to 7.049450 MHz USB (2400 Hz passband)");
        rig.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task Ptt_Through_Rigctld_Keys_With_T_1_And_Unkeys_With_T_0()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();

        ptt.Key();
        _fake.Ptt.Should().BeTrue();
        rig.Snapshot().Keyed.Should().BeTrue();

        ptt.Unkey();
        _fake.Ptt.Should().BeFalse();
        rig.Snapshot().Keyed.Should().BeFalse();

        // The connection opened with an unkey, the fail-safe, before any keyup of ours.
        _fake.Sets.Should().Equal(["T 0", "T 1", "T 0"]);
    }

    [Fact]
    public async Task Rigctld_Dying_Mid_Keyup_Is_Unkeyed_As_Soon_As_It_Answers_Again()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        ptt.Key();
        _fake.Ptt.Should().BeTrue();

        _fake.Kill();
        Action unkey = ptt.Unkey;

        unkey.Should().Throw<IOException>();
        rig.Snapshot().Keyed.Should().BeFalse("the station's own idea of keyed is cleared whatever the command does");

        // The radio is still keyed (rigctld never got the T 0) until a new connection's first act.
        await Eventually(() => !_fake.Ptt && _fake.Connections == 2, "the reconnect should unkey the radio");
        _fake.Commands.First(c => c.Connection == 2).Command.Should().Be("T 0");
        _warned.Should().Contain(w => w.Contains("lost rigctld") && w.Contains("may still be keyed"));
        _said.Should().Contain("rig: unkeyed the radio, which had been keyed when rigctld went away");
    }

    [Fact]
    public async Task A_Keyup_With_Rigctld_Gone_Fails_And_Nothing_Is_Left_Keyed()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        _fake.Accepting = false;
        _fake.Kill();

        Action key = ptt.Key;

        key.Should().Throw<IOException>();
        rig.Snapshot().Keyed.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_Unkeys_A_Rigctld_Ptt()
    {
        RigControl rig = await Started(keysThroughRig: true);
        rig.KeyingPtt().Key();

        await rig.DisposeAsync();

        _fake.Ptt.Should().BeFalse();
        _fake.Sets[^1].Should().Be("T 0");
    }

    [Fact]
    public async Task A_Window_Tunes_The_Rig_And_Puts_It_Back_When_Its_Time_Runs_Out()
    {
        RigControl rig = await Started();

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "usb", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Tuned);
        _fake.DialHz.Should().Be(7_052_000);
        _fake.Mode.Should().Be("USB");
        rig.HoldsTransmitter.Should().BeTrue();
        result.Window!.Expires.Should().Be(Noon.AddSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(59));
        _fake.DialHz.Should().Be(7_052_000);

        _time.Advance(TimeSpan.FromSeconds(1));
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Mode.Should().Be("USB");
        _fake.PassbandHz.Should().Be(2400);
        rig.HoldsTransmitter.Should().BeFalse();
        _said.Should().Contain(s => s.Contains("has ended (its time ran out)"));
        _said.Should().Contain("rig: put back to 14.074000 MHz USB (2400 Hz passband); transmissions resume");
    }

    [Fact]
    public async Task Releasing_A_Window_Puts_The_Rig_Back_At_Once()
    {
        RigControl rig = await Started();
        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Window!.Dispose();

        _fake.DialHz.Should().Be(14_074_000);
        rig.Snapshot().Window.Should().BeNull();
        rig.Release("mailcast").Should().BeFalse("it has already gone");
    }

    [Fact]
    public async Task A_Window_Whose_Owner_Goes_Away_Is_Put_Back()
    {
        RigControl rig = await Started();
        using var owner = new CancellationTokenSource();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(300), "mailcast", owner.Token);

        await owner.CancelAsync();

        _fake.DialHz.Should().Be(14_074_000);
        rig.HoldsTransmitter.Should().BeFalse();
        _said.Should().Contain(s => s.Contains("has ended (mailcast went away)"));
    }

    [Fact]
    public async Task Shutdown_Puts_An_Open_Window_Back()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(300), "mailcast");

        await rig.DisposeAsync();

        _fake.DialHz.Should().Be(14_074_000);
        _said.Should().Contain(s => s.Contains("has ended (the station is stopping)"));
    }

    [Fact]
    public async Task A_Window_Is_Capped_At_Five_Minutes_And_Renewed_By_Its_Owner()
    {
        RigControl rig = await Started();

        RigTuneResult first = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromMinutes(20), "mailcast");
        first.Capped.Should().BeTrue();
        first.Window!.Expires.Should().Be(Noon.AddMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(4));
        RigTuneResult renewed = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromMinutes(5), "mailcast");
        renewed.Outcome.Should().Be(RigTuneOutcome.Renewed);
        renewed.Window.Should().BeSameAs(first.Window);

        _time.Advance(TimeSpan.FromMinutes(4));
        _fake.DialHz.Should().Be(7_052_000, "the renewal moved the end to 12:09");

        _time.Advance(TimeSpan.FromMinutes(1));
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Renewal_Somewhere_New_Retunes_And_Still_Restores_To_Where_It_Started()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        rig.Tune(new RigTuning(3_582_000, "LSB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Outcome.Should().Be(RigTuneOutcome.Renewed);
        _fake.DialHz.Should().Be(3_582_000);
        _fake.Mode.Should().Be("LSB");

        rig.Release("mailcast").Should().BeTrue();
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Mode.Should().Be("USB");
    }

    [Fact]
    public async Task Another_Owner_Is_Refused_While_A_Window_Is_Open()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        RigTuneResult other = rig.Tune(new RigTuning(10_147_000, "USB", 0), TimeSpan.FromSeconds(60), "the API");

        other.Outcome.Should().Be(RigTuneOutcome.Refused);
        other.Why.Should().Contain("already tuned to 7.052000 MHz USB").And.Contain("for mailcast");
        _fake.DialHz.Should().Be(7_052_000);
    }

    [Fact]
    public async Task The_Rig_Is_Never_Retuned_While_The_Transmitter_Is_Keyed()
    {
        RigControl rig = await Started();
        IPttControl ptt = rig.Guard(new NullPtt());
        ptt.Key();

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Refused);
        result.Why.Should().Contain("keyed");
        _fake.DialHz.Should().Be(14_074_000);
        _fake.Sets.Should().BeEmpty();

        ptt.Unkey();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast")
            .Outcome.Should().Be(RigTuneOutcome.Tuned);
    }

    [Fact]
    public async Task A_Keyup_Is_Refused_While_A_Window_Is_Open_And_Allowed_Once_It_Is_Put_Back()
    {
        RigControl rig = await Started(keysThroughRig: true);
        IPttControl ptt = rig.KeyingPtt();
        RigTuneResult window = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Action key = ptt.Key;

        key.Should().Throw<InvalidOperationException>().WithMessage("*tuned to 7.052000 MHz USB (normal passband) for mailcast*");
        _fake.Ptt.Should().BeFalse();

        window.Window!.Dispose();
        ptt.Key();
        _fake.Ptt.Should().BeTrue();
        ptt.Unkey();
    }

    [Fact]
    public async Task A_Guarded_Line_Is_Refused_During_A_Window_Without_Touching_The_Line()
    {
        RigControl rig = await Started();
        var line = new CountingPtt();
        IPttControl ptt = rig.Guard(line);
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Action key = ptt.Key;

        key.Should().Throw<InvalidOperationException>();
        line.Keys.Should().Be(0);
        rig.Guard(ptt).Should().BeSameAs(ptt, "guarding twice would be one lock taken twice for nothing");
    }

    [Fact]
    public async Task A_Transmission_The_Inhibit_Does_Not_Hold_Is_Still_Refused_At_The_Key_During_A_Window()
    {
        // ARDOP's own bursts own the channel's timing and so pass every inhibit; the PTT is the
        // backstop that keeps them off a retuned rig too.
        RigControl rig = await Started();
        var channel = new SoundModemChannel(12000, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        channel.Csma.Persistence = 255;
        var output = new Packet.SoundModem.Tests.Channel.FakeAudioOutput(12000);
        var line = new CountingPtt();
        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(output, rig.Guard(line), stop.Token);
        RigTuneResult window = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        Task during = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: true);
        Func<Task> waitDuring = () => during.WaitAsync(TimeSpan.FromSeconds(30));

        (await waitDuring.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*tuned to 7.052000 MHz*");
        line.Keys.Should().Be(0);
        output.WrittenCount.Should().Be(0);

        window.Window!.Dispose();
        await channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: true)
            .WaitAsync(TimeSpan.FromSeconds(30));
        line.Keys.Should().Be(1);

        await stop.CancelAsync();
        Func<Task> ended = () => transmitter;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Restore_That_Cannot_Reach_The_Rig_Is_Owed_And_Holds_The_Transmitter_Until_It_Is_Done()
    {
        RigControl rig = await Started();
        rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");
        _fake.Accepting = false;
        _fake.Kill();

        // The window ends with nobody to tell: the restore is owed, and the station stays off the air.
        _time.Advance(TimeSpan.FromSeconds(60));
        await Eventually(() => rig.Snapshot().RestoreOwed is not null, "the restore should be owed");
        rig.HoldsTransmitter.Should().BeTrue();
        _fake.DialHz.Should().Be(7_052_000);

        _fake.Accepting = true;
        await Eventually(() => !rig.HoldsTransmitter, "the reconnect should do the restore", TimeSpan.FromSeconds(1));
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Tune_With_Rigctld_Gone_Is_Refused_And_Says_Why()
    {
        RigControl rig = await Started();
        _fake.Accepting = false;
        _fake.Kill();
        await Eventually(() => !rig.Connected, "the poll should notice the connection has gone", TimeSpan.FromSeconds(5));

        RigTuneResult result = rig.Tune(new RigTuning(7_052_000, "USB", 0), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Refused);
        result.Why.Should().Contain("not connected");
    }

    [Theory]
    [InlineData(0, "USB", 0, "is not a dial frequency")]
    [InlineData(7_052_000, "DIGU", 0, "is not a mode")]
    [InlineData(7_052_000, "USB", -5, "is not one")]
    public async Task A_Tune_That_Makes_No_Sense_Is_Invalid(long dial, string mode, int passband, string why)
    {
        RigControl rig = await Started();

        RigTuneResult result = rig.Tune(new RigTuning(dial, mode, passband), TimeSpan.FromSeconds(60), "mailcast");

        result.Outcome.Should().Be(RigTuneOutcome.Invalid);
        result.Why.Should().Contain(why);
    }

    [Fact]
    public async Task The_Api_Tunes_Renews_Refuses_And_Releases()
    {
        RigControl rig = await Started();

        (int status, JsonObject answer) = RigApi.Handle(rig, "/api/rig", "GET", "");
        status.Should().Be(200);
        answer["connected"]!.GetValue<bool>().Should().BeTrue();
        answer["dialHz"]!.GetValue<long>().Should().Be(14_074_000);
        answer["window"].Should().BeNull();

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 90}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeFalse();
        answer["transmitHeld"]!.GetValue<bool>().Should().BeTrue();
        answer["window"]!["expires"]!.GetValue<string>().Should().Be("2026-10-05T12:01:30Z");
        answer["window"]!["restoreTo"]!["dialHz"]!.GetValue<long>().Should().Be(14_074_000);

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 600}""");
        status.Should().Be(200);
        answer["renewed"]!.GetValue<bool>().Should().BeTrue();
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["seconds"]!.GetValue<double>().Should().Be(300);

        (status, answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"release": true}""");
        status.Should().Be(200);
        answer["released"]!.GetValue<bool>().Should().BeTrue();
        answer["window"].Should().BeNull();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task The_Api_Answers_409_While_Keyed_And_400_For_A_Bad_Request()
    {
        RigControl rig = await Started();
        IPttControl ptt = rig.Guard(new NullPtt());
        ptt.Key();

        (int status, JsonObject answer) = RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB"}""");
        status.Should().Be(409);
        answer["refused"]!.GetValue<string>().Should().Contain("keyed");
        answer["keyed"]!.GetValue<bool>().Should().BeTrue();
        ptt.Unkey();

        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000.5, "mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": "7052000", "mode": "USB"}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", """{"dialHz": 7052000, "mode": "USB", "seconds": 0}""").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig/tune", "POST", "not json").Status.Should().Be(400);
        RigApi.Handle(rig, "/api/rig", "POST", "").Status.Should().Be(405);
        _fake.Sets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("127.0.0.1:4532", "127.0.0.1", 4532)]
    [InlineData("localhost", "localhost", 4532)]
    [InlineData("rig.lan:4533", "rig.lan", 4533)]
    [InlineData("[::1]:4534", "::1", 4534)]
    [InlineData("[::1]", "::1", 4532)]
    public void An_Endpoint_Is_Read_As_Host_And_Port(string text, string host, int port)
    {
        RigctldEndpoint.TryParse(text, out _).Should().Be(new RigctldEndpoint(host, port));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":4532")]
    [InlineData("host:0")]
    [InlineData("host:port")]
    [InlineData("::1:4532")]
    [InlineData("[::1")]
    public void An_Endpoint_That_Is_Not_One_Is_Refused(string text)
    {
        RigctldEndpoint.TryParse(text, out string why).Should().BeNull();
        why.Should().NotBeEmpty();
    }

    private sealed class CountingPtt : IPttControl
    {
        internal int Keys { get; private set; }

        public void Key() => Keys++;

        public void Unkey()
        {
        }
    }
}
