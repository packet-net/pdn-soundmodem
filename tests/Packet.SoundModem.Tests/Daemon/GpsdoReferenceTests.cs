using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The GPSDO's 10 MHz reference (issue #789): the <c>gpsdo</c> section, the verdict a display's
/// messages add up to, the journal's one line per change, the reconnect loop's quiet, and the
/// <c>reference</c> object on <c>/api/txlease</c>.
/// </summary>
/// <remarks>
/// Nothing here touches a broker or the network. The messages are fed to
/// <see cref="GpsdoReference.Receive"/> as the MQTT feed would pass them on, the connection is a
/// fake session delegate, and every clock is a <see cref="FakeTimeProvider"/>.
/// </remarks>
public sealed class GpsdoReferenceTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A real <c>gpsdo/state</c> from the display on the LAN, 2026-11-08, verbatim but for
    /// the fields each test sets. Its alarms are <c>unlocked</c> alone and its time-interval error
    /// 35 ns, so after a good report it is holdover (issue #807); <see cref="Faulty"/> is the same
    /// report with the 10 MHz gone, which is unreliable whatever came before.</summary>
    private static string State(string state, bool? referenceOk, string alarms = "\"unlocked\"") =>
        "{\"state\":\"" + state + "\""
        + (referenceOk is bool ok ? ",\"reference_ok\":" + (ok ? "true" : "false") : "")
        + ",\"locked\":false,\"locked_s\":0,\"lock_losses\":0,\"gpsdo_restarts\":0,\"gps_restarts\":0,"
        + "\"tic_ns\":44,\"dac\":30556,\"dac_near_limit\":false,\"ocxo_c\":42.4,\"diff_ns\":-4,\"tic_error_ns\":35.0,"
        + "\"tc_s\":300,\"ambient_c\":30.2,\"earliest_lock_s\":182,\"gpsdo_age_s\":0,"
        + "\"firmware\":\"Rev. 3.4 paulv mods  ID:0\",\"lock_counter\":1319,\"lock_target\":1500,"
        + "\"lock_test_tic_ns\":35,\"lock_test_freq_ns\":0,\"missed_pps_total\":1,\"since_missed_pps_s\":2106,"
        + "\"gps_seen\":true,\"fix\":\"3D\",\"sats_used\":12,\"sats_in_view\":22,\"hdop\":0.75,\"gps_age_s\":0,"
        + "\"alarms\":[" + alarms + "]}";

    /// <summary><see cref="State"/> with the <c>no_10mhz</c> alarm: a real fault, never holdover.</summary>
    private static string Faulty(string state) => State(state, false, "\"unlocked\",\"no_10mhz\"");

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-gpsdo").FullName;
    private readonly FakeTimeProvider _time = new(Noon);
    private readonly List<string> _lines = [];
    private readonly List<string> _errors = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GpsdoReference Reference() =>
        new("gpsdo", new StationJournal("", _lines.Add, _errors.Add), _time);

    private IEnumerable<string> Journal => _lines.Concat(_errors);

    private (DaemonConfig? Config, string Error) Load(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        return (config, error);
    }

    // ---------------------------------------------------------------- config

    [Fact]
    public void No_Gpsdo_Section_Watches_Nothing()
    {
        (DaemonConfig? config, string error) = Load("""{"device": "null"}""");

        error.Should().BeEmpty();
        config!.Gpsdo.Should().BeNull();
    }

    [Fact]
    public void A_Section_With_Only_A_Host_Takes_The_Defaults()
    {
        (DaemonConfig? config, string error) = Load("""{"device": "null", "gpsdo": {"host": "10.45.0.70"}}""");

        error.Should().BeEmpty();
        config!.Gpsdo!.Host.Should().Be("10.45.0.70");
        config.Gpsdo.Port.Should().Be(1883);
        config.Gpsdo.Username.Should().BeNull();
        config.Gpsdo.Password.Should().BeNull();
        config.Gpsdo.TopicPrefix.Should().Be("gpsdo");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Every_Key_Loads_And_A_Trailing_Slash_On_The_Prefix_Is_Dropped()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "gpsdo": {"host": "broker.lan", "port": 8883, "username": "u", "password": "p", "topicPrefix": "shack/gpsdo/"}}""");

        error.Should().BeEmpty();
        config!.Gpsdo!.Port.Should().Be(8883);
        config.Gpsdo.Username.Should().Be("u");
        config.Gpsdo.Password.Should().Be("p");
        config.Gpsdo.TopicPrefix.Should().Be("shack/gpsdo");
    }

    [Theory]
    [InlineData("""{"device": "null", "gpsdo": {}}""", "\"gpsdo\" has no \"host\"")]
    [InlineData("""{"device": "null", "gpsdo": {"host": "b", "port": 0}}""", "\"gpsdo\".\"port\" is 0")]
    [InlineData("""{"device": "null", "gpsdo": {"host": "b", "topicPrefix": "gpsdo/#"}}""", "\"gpsdo\".\"topicPrefix\"")]
    [InlineData("""{"device": "null", "gpsdo": {"host": "b", "topicPrefix": "/"}}""", "\"gpsdo\".\"topicPrefix\"")]
    [InlineData("""{"device": "null", "gpsdo": {"host": "b", "password": "p"}}""", "\"gpsdo\" has a \"password\" but no \"username\"")]
    public void A_Section_That_Cannot_Work_Is_Refused_At_Load(string json, string expected)
    {
        (DaemonConfig? config, string error) = Load(json);

        config.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void The_Password_Is_A_Secret_Kept_Only_While_The_Broker_Is_The_Same()
    {
        ConfigApi.IsSecret("gpsdo.password").Should().BeTrue();

        const string running = """{"device": "null", "gpsdo": {"host": "a.lan", "port": 1883, "username": "u", "password": "pw"}}""";
        string served = ConfigApi.Redact(running);
        served.Should().NotContain("pw");

        ConfigApi.RestoreHidden(served, running, out _).Should().NotBeNull();
        ConfigApi.RestoreHidden(served.Replace("a.lan", "evil.example", StringComparison.Ordinal), running, out string why)
            .Should().BeNull("a moved broker would be sent the password nobody was shown");
        why.Should().Contain("gpsdo.password");
        ConfigApi.RestoreHidden(served.Replace("1883", "1884", StringComparison.Ordinal), running, out _)
            .Should().BeNull("another port is another broker");
        ConfigApi.RestoreHidden(served.Replace("\"u\"", "\"v\"", StringComparison.Ordinal), running, out _)
            .Should().BeNull("another username is another account");
    }

    // ---------------------------------------------------------------- verdict

    [Fact]
    public void Before_Any_Message_The_Reference_Is_Unknown()
    {
        using GpsdoReference reference = Reference();

        reference.Current.Should().Be(new ReferenceVerdict(ReferenceState.Unknown, "no message yet", Noon));
        reference.Current.Chip.Should().Be("10 MHz unknown");
    }

    [Fact]
    public void A_Fresh_Report_With_Reference_Ok_Is_Ok_And_One_Without_Is_Unreliable()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);

        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);
        reference.Current.Detail.Should().Be("LOCKED");
        reference.Current.Chip.Should().Be("10 MHz OK");

        reference.Receive("gpsdo/state", Faulty("GPS LOST"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Chip.Should().Be("10 MHz unreliable: GPS LOST");
    }

    [Fact]
    public void A_Retained_State_Beside_An_Online_Status_Counts_At_Once()
    {
        // A restart just before a mailcast slot: the broker hands over the retained status and
        // state, and the head end's next look at /api/txlease must not read "unknown".
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: true);

        reference.Current.State.Should().Be(ReferenceState.Ok);

        _time.Advance(TimeSpan.FromSeconds(31));
        reference.Current.State.Should().Be(ReferenceState.Unknown, "it ages from when it arrived, like any report");
    }

    [Fact]
    public void A_Retained_State_With_No_Status_Waits_For_One()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: true);

        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Current.Detail.Should().Be("waiting for a fresh report");

        _time.Advance(TimeSpan.FromSeconds(2));
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Current.State.Should().Be(ReferenceState.Ok, "the status vouches for the state that came first");

        _time.Advance(TimeSpan.FromSeconds(29));
        reference.Current.State.Should().Be(ReferenceState.Unknown, "and it is aged from when the state came, not the status");
    }

    [Fact]
    public void A_Retained_State_Beside_An_Offline_Status_Is_Unknown()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "offline", retained: true);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: true);

        reference.Current.Detail.Should().Be("display offline");
    }

    [Fact]
    public void A_Report_Ages_On_The_Monotonic_Clock_Whatever_The_Wall_Clock_Does()
    {
        var clock = new SteppedClock(Noon);
        using var reference = new GpsdoReference("gpsdo", new StationJournal("", _lines.Add, _errors.Add), clock);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);

        clock.Step = TimeSpan.FromHours(-1);
        clock.Advance(TimeSpan.FromSeconds(31));

        reference.Current.State.Should().Be(ReferenceState.Unknown, "a wall clock stepped back does not make a report younger");
        reference.Current.Detail.Should().Be("last report 31 s old");
    }

    /// <summary>A clock whose wall time can be stepped without its monotonic time moving.</summary>
    private sealed class SteppedClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        internal TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Step;

        // The monotonic clock is the unstepped one.
        public override long GetTimestamp() => base.GetUtcNow().UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void Verdicts_Reach_The_Page_In_The_Order_They_Were_Reached()
    {
        // The timer and the MQTT thread both evaluate. Whatever interleaving they get, the last
        // thing the page was told must be the verdict as it stands, or the chip is wrong until
        // the next change, which can be hours.
        using GpsdoReference reference = Reference();
        ReferenceVerdict? last = null;
        reference.Changed += v => last = v;

        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            reference.Receive("gpsdo/state", i % 2 == 0 ? State("LOCKED", true) : Faulty("GPS LOST"), retained: false);
            if (i % 7 == 0)
            {
                reference.Evaluate();
            }
        });

        reference.Evaluate();
        ReferenceVerdict now = reference.Current;
        last.Should().NotBeNull();
        (last!.State, last.Detail).Should().Be((now.State, now.Detail));
    }

    [Fact]
    public async Task A_Tick_Skips_Rather_Than_Waits_While_A_Publication_Is_Stuck()
    {
        // A journal write or page update that hangs must not have a tick a second queue up
        // behind it for ever; the tick returns at once and the next one tries again.
        using GpsdoReference reference = Reference();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        reference.Changed += _ =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        Task stuck = Task.Run(() => reference.Receive("gpsdo/state", State("LOCKED", true), retained: false));
        inside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        Task<bool> tick = Task.Run(reference.Tick);
        await Eventually.SignalAsync(tick, "the tick to return rather than block");
        (await tick).Should().BeFalse("it skipped");

        release.Set();
        await Eventually.SignalAsync(stuck, "stuck to complete");
        reference.Tick().Should().BeTrue("once the lock is free the tick runs");
    }

    [Fact]
    public void A_Subscriber_That_Throws_Cannot_Take_The_Timer_Down_And_Is_Said_Once()
    {
        using GpsdoReference reference = Reference();
        reference.Changed += _ => throw new InvalidOperationException("page gone");

        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        _errors.Where(e => e.StartsWith("gpsdo: showing the reference failed", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain("page gone");
    }

    [Fact]
    public void An_Absent_Reference_Ok_Is_Unknown_Not_False()
    {
        using GpsdoReference reference = Reference();

        reference.Receive("gpsdo/state", State("NO DATA", null), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Current.Detail.Should().Be("NO DATA, reference_ok not reported");
    }

    [Fact]
    public void The_Display_Going_Offline_Makes_It_Unknown_Whatever_It_Said_Last()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);

        reference.Receive("gpsdo/status", "offline", retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Current.Detail.Should().Be("display offline");

        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok, "its last report is still fresh");
    }

    [Fact]
    public void A_Report_More_Than_30_Seconds_Old_Turns_Unknown_On_The_Clock_Alone()
    {
        using GpsdoReference reference = Reference();
        var seen = new List<ReferenceVerdict>();
        reference.Changed += seen.Add;
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);

        _time.Advance(TimeSpan.FromSeconds(30));
        reference.Current.State.Should().Be(ReferenceState.Ok, "30 s is the limit, not past it");

        // No message: the timer alone notices, and says so once.
        _time.Advance(TimeSpan.FromSeconds(1));
        seen.Should().HaveCount(2);
        seen[^1].State.Should().Be(ReferenceState.Unknown);
        seen[^1].Detail.Should().Be("no report for over 30 s");
        seen[^1].Since.Should().Be(Noon.AddSeconds(31));

        // The API's wording carries the age as of the moment it is asked.
        _time.Advance(TimeSpan.FromSeconds(16));
        reference.Current.Detail.Should().Be("last report 47 s old");
        seen.Should().HaveCount(2, "the age ticking up is not a change worth telling the page");
    }

    [Fact]
    public void A_Stale_Report_With_The_Broker_Gone_Says_So()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        reference.BrokerReachable(false);
        reference.Current.State.Should().Be(ReferenceState.Ok, "a fresh report stands until it is stale");

        _time.Advance(TimeSpan.FromSeconds(40));

        reference.Current.Detail.Should().Be("MQTT broker unreachable, last report 40 s old");
    }

    [Fact]
    public void Other_Topics_And_A_Payload_That_Is_Not_Json_Are_Ignored_With_One_Line()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);

        reference.Receive("gpsdo/other", State("GPS LOST", false), retained: false);
        reference.Receive("gpsdo/state", "not json", retained: false);
        reference.Receive("gpsdo/state", "[1, 2]", retained: false);

        reference.Current.State.Should().Be(ReferenceState.Ok);
        _errors.Should().ContainSingle().Which.Should().Be(
            "gpsdo: ignoring a gpsdo/state message that is not a JSON object: not json");
    }

    // ---------------------------------------------------------------- journal

    [Fact]
    public void Each_Change_Of_Verdict_Is_One_Journal_Line_Saying_How_Long_The_Last_One_Lasted()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);
        _time.Advance(TimeSpan.FromSeconds(10));
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);

        // Three hours twelve of fresh LOCKED reports, then the GPS goes.
        for (int i = 0; i < (3 * 360) + 72; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        }

        reference.Receive("gpsdo/state", Faulty("GPS LOST"), retained: false);
        reference.Receive("gpsdo/state", Faulty("GPS LOST"), retained: false);
        _time.Advance(TimeSpan.FromSeconds(5));
        reference.Receive("gpsdo/status", "offline", retained: false);

        Journal.Should().Equal(
            "10 MHz reference OK: LOCKED (was unknown for 10s)",
            "10 MHz reference unreliable: GPS LOST (was OK for 3h12m)",
            "10 MHz reference unknown: display offline (was unreliable for 5s)");
        _lines.Should().ContainSingle("only the return to OK is an ordinary line; the rest are problems");
    }

    [Theory]
    [InlineData(12, "12s")]
    [InlineData(303, "5m03s")]
    [InlineData((3 * 3600) + (12 * 60) + 40, "3h12m")]
    public void Spans_Read_The_Way_The_Journal_Says_Them(int seconds, string expected) =>
        GpsdoReference.Span(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    // ---------------------------------------------------------------- short spells (60 s grace)

    /// <summary>A state as the display sends it, with <c>locked</c> and the alarms set.</summary>
    private static string Live(string state, bool referenceOk, bool locked, params string[] alarms) =>
        "{\"state\":\"" + state + "\",\"reference_ok\":" + (referenceOk ? "true" : "false")
        + ",\"locked\":" + (locked ? "true" : "false") + ",\"locked_s\":182879,\"lock_losses\":0,"
        + "\"alarms\":[" + string.Join(",", alarms.Select(a => "\"" + a + "\"")) + "]}";

    /// <summary>The display reporting <paramref name="state"/> every 10 s for <paramref
    /// name="seconds"/>, as it does, so nothing goes stale.</summary>
    private void Reporting(GpsdoReference reference, string state, int seconds)
    {
        for (int i = 0; i < seconds / 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            reference.Receive("gpsdo/state", state, retained: false);
        }

        _time.Advance(TimeSpan.FromSeconds(seconds % 10));
    }

    private GpsdoReference Watching(List<ReferenceVerdict>? seen = null)
    {
        GpsdoReference reference = Reference();
        if (seen is not null)
        {
            reference.Changed += seen.Add;
        }

        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        return reference;
    }

    [Fact]
    public void A_Gps_Lost_Blip_Under_60_Seconds_With_The_Gpsdo_Locked_Stays_Ok_With_No_Journal_Line()
    {
        var seen = new List<ReferenceVerdict>();
        using GpsdoReference reference = Watching(seen);

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);
        _time.Advance(TimeSpan.FromSeconds(12));
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Ok);
        reference.Current.Detail.Should().Be("GPS LOST for 12 s, GPSDO still locked, holding");
        reference.Current.Chip.Should().Be("10 MHz OK, holding");
        TxLeaseApi.Handle(Channel(), "GET", "", null, reference: reference).Answer["reference"]!["state"]!
            .GetValue<string>().Should().Be("ok", "the head end must not hold or stop a slot for it");

        _time.Advance(TimeSpan.FromSeconds(8));
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);

        reference.Current.Should().Be(new ReferenceVerdict(ReferenceState.Ok, "LOCKED", Noon));
        Journal.Should().Equal(["10 MHz reference OK: LOCKED (was unknown for 0s)"], "a held spell writes nothing");
        seen.Select(v => v.Detail).Should().Equal(
            "no state report yet", "LOCKED", "GPS LOST, GPSDO still locked, holding", "LOCKED");
    }

    [Fact]
    public void A_Spell_Reaching_60_Seconds_Turns_Unreliable_On_The_Clock_Alone()
    {
        var seen = new List<ReferenceVerdict>();
        using GpsdoReference reference = Watching(seen);
        Reporting(reference, Live("LOCKED", true, true), 300);

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        Reporting(reference, Live("GPS LOST", false, true, "reference_suspect"), 50);

        // 50 s in. No more messages: the 1 s tick alone notices the 60 s pass.
        _time.Advance(TimeSpan.FromSeconds(9));
        reference.Current.State.Should().Be(ReferenceState.Ok);
        _time.Advance(TimeSpan.FromSeconds(1));

        seen[^1].State.Should().Be(ReferenceState.Unreliable);
        seen[^1].Detail.Should().Be("GPS LOST for over 60 s");
        seen[^1].Since.Should().Be(Noon.AddMinutes(6));
        reference.Current.Chip.Should().Be("10 MHz unreliable: GPS LOST for over 60 s");
        Journal.Should().Equal(
            "10 MHz reference OK: LOCKED (was unknown for 0s)",
            "10 MHz reference unreliable: GPS LOST for over 60 s (was OK for 6m00s)");

        _time.Advance(TimeSpan.FromSeconds(5));
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        _lines.Last().Should().Be("10 MHz reference OK: LOCKED (was unreliable for 5s)");
    }

    [Theory]
    [InlineData("UNLOCKED")]
    [InlineData("HOLD")]
    [InlineData("WARM UP")]
    [InlineData("NO DATA")]
    public void The_Gpsdo_Saying_It_Is_Not_Locked_With_No_Time_Interval_Error_To_Vouch_For_It_Is_Unreliable_At_Once(string state)
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live(state, false, false, "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be(state);
        Journal.Last().Should().Be($"10 MHz reference unreliable: {state} (was OK for 0s)");
    }

    [Fact]
    public void A_Report_Without_Locked_Is_Unreliable_At_Once_As_Before()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", """{"state":"GPS LOST","reference_ok":false}""", retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable, "with no word on the lock there is nothing to hold on");
    }

    [Fact]
    public void No_Pps_Is_Held_Although_The_Display_Says_Not_Locked_Once_The_Report_Lines_Stop()
    {
        // While the PPS is missing the GPSDO prints no report line, so after 3 s the display's
        // "locked" goes false with the "no_pps" alarm; the GPSDO itself has not lost lock.
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("NO PPS", false, true, "no_pps", "unlocked"), retained: false);
        _time.Advance(TimeSpan.FromSeconds(4));
        reference.Receive("gpsdo/state", Live("NO PPS", false, false, "no_pps", "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Ok);
        reference.Current.Detail.Should().Be("NO PPS for 4 s, holding");

        Reporting(reference, Live("NO PPS", false, false, "no_pps", "unlocked"), 56);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("NO PPS for over 60 s");
    }

    [Fact]
    public void A_Spell_Is_Continuous_Across_Changes_Of_Text_And_Ends_Only_On_Reference_Ok()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        Reporting(reference, Live("GPS LOST", false, true, "reference_suspect"), 30);
        reference.Receive("gpsdo/state", Live("NO PPS", false, true, "no_pps"), retained: false);
        Reporting(reference, Live("NO PPS", false, true, "no_pps"), 29);
        reference.Current.State.Should().Be(ReferenceState.Ok);
        reference.Current.Detail.Should().Be("NO PPS for 59 s, holding");

        _time.Advance(TimeSpan.FromSeconds(1));
        reference.Current.State.Should().Be(ReferenceState.Unreliable, "one spell of 60 s, whatever it was called");
    }

    [Fact]
    public void A_New_Spell_After_Recovery_Starts_Its_Own_60_Seconds()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        Reporting(reference, Live("GPS LOST", false, true, "reference_suspect"), 50);
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        _time.Advance(TimeSpan.FromSeconds(1));
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        Reporting(reference, Live("GPS LOST", false, true, "reference_suspect"), 50);

        reference.Current.State.Should().Be(ReferenceState.Ok);
        reference.Current.Detail.Should().Be("GPS LOST for 50 s, GPSDO still locked, holding");
        Journal.Should().ContainSingle("two short spells are no lines at all");
    }

    [Fact]
    public void A_Spell_That_Went_Unreliable_For_An_Unlock_Is_Not_Held_Again_Until_It_Ends()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("UNLOCKED", false, false, "unlocked"), retained: false);
        _time.Advance(TimeSpan.FromSeconds(10));
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("GPS LOST");

        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok, "a new spell, with the GPSDO locked");
    }

    [Fact]
    public void A_Held_Spell_Still_Goes_Unknown_When_Reports_Stop_And_Is_Still_Counted_After()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Current.Detail.Should().Be("last report 31 s old");

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok, "31 s into the spell");
        _time.Advance(TimeSpan.FromSeconds(29));
        reference.Current.State.Should().Be(ReferenceState.Unreliable, "the spell did not end while nobody was reporting");

        reference.Receive("gpsdo/status", "offline", retained: false);
        reference.Current.Detail.Should().Be("display offline");
    }

    [Theory]
    // The 10 MHz itself missing is the GPSDO's fault, not the GPS's, whatever "locked" says.
    [InlineData("NO 10 MHz", "no_10mhz")]
    [InlineData("GPS LOST", "reference_suspect,no_10mhz")]
    [InlineData("GPS LOST", "reference_suspect,dac_near_limit")]
    [InlineData("GPS LOST", "reference_suspect,something_new")]
    // No alarm at all: nothing says the GPS is to blame.
    [InlineData("GPS LOST", "")]
    public void A_Spell_With_Any_Cause_But_The_Gps_Is_Unreliable_At_Once(string state, string alarms)
    {
        using GpsdoReference reference = Watching();

        reference.Receive(
            "gpsdo/state", Live(state, false, true, alarms.Split(',', StringSplitOptions.RemoveEmptyEntries)), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be(state);
    }

    [Theory]
    [InlineData("gps_silent")]
    [InlineData("gps_no_fix")]
    [InlineData("gps_few_sats")]
    [InlineData("gps_spoofing")]
    [InlineData("gps_signal_loss")]
    [InlineData("reference_suspect")]
    public void Each_Gps_Side_Alarm_Is_Held(string alarm)
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, alarm, "reference_suspect"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Ok);
    }

    [Fact]
    public void A_No_10_Mhz_Report_Inside_A_Held_Spell_Ends_The_Hold()
    {
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        Reporting(reference, Live("GPS LOST", false, true, "reference_suspect"), 10);

        reference.Receive("gpsdo/state", Live("NO 10 MHz", false, true, "no_10mhz"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable, "the same spell, so not held again");
    }

    [Fact]
    public void No_Pps_From_A_Gpsdo_Never_Seen_Ok_Is_Unreliable_At_Once()
    {
        // The display just booted, or a cold GPSDO after a power cut: NO PPS with "locked" false
        // and no good report before it.
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: false);

        reference.Receive("gpsdo/state", Live("NO PPS", false, false, "no_pps", "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Theory]
    [InlineData("UNLOCKED")]
    [InlineData("HOLD")]
    [InlineData("WARM UP")]
    public void No_Pps_After_The_Gpsdo_Said_It_Was_Not_Locked_Is_Not_Held(string state)
    {
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", Live(state, false, false, "unlocked"), retained: false);

        reference.Receive("gpsdo/state", Live("NO PPS", false, false, "no_pps", "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void No_Pps_After_A_Gap_In_Reports_Is_Not_Held()
    {
        // The last good report is too old to vouch that the spell has only just begun.
        using GpsdoReference reference = Watching();
        _time.Advance(TimeSpan.FromSeconds(40));

        reference.Receive("gpsdo/state", Live("NO PPS", false, false, "no_pps", "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void The_Gpsdo_Going_Silent_With_No_Pps_Lingering_Is_Unreliable_At_Once()
    {
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", Live("NO PPS", false, true, "no_pps", "unlocked"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);

        _time.Advance(TimeSpan.FromSeconds(4));
        reference.Receive("gpsdo/state", Live("NO DATA", false, false, "gpsdo_silent", "no_pps", "unlocked"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("NO DATA");
    }

    [Fact]
    public void A_Spell_First_Seen_In_A_Retained_Report_Is_Not_Held()
    {
        // A restart mid-spell: the broker's retained report cannot say how long the GPS has been gone.
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: true);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable, "still the spell it joined part way");

        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok, "a spell seen to start is held");
    }

    [Fact]
    public void A_Spell_First_Seen_Live_With_No_Report_Before_It_Is_Not_Held()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: false);

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void A_Retained_Bad_Report_After_A_Reconnect_Is_Not_Held()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", Live("GPS LOST", false, true, "reference_suspect"), retained: true);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    // ---------------------------------------------------------------- holdover (issue #807)

    /// <summary>The display's <c>gpsdo/state</c> at M0LTE's station on 2026-11-14 while the
    /// reference was called unreliable and the 14:00 mailcast slot was aborted, verbatim from the
    /// issue: one missed PPS, and the firmware's 25 minutes back to <c>LOCKED</c>.</summary>
    private const string Relocking =
        """{"state":"UNLOCKED","reference_ok":false,"locked":false,"locked_s":0,"lock_losses":"""
        + """7,"tic_ns":-27,"tic_error_ns":-33.0,"dac":30140,"dac_near_limit":false,"ocxo_c":"""
        + """44.2,"tc_s":300,"lock_counter":1124,"lock_target":1500,"missed_pps_total":844,"since_missed_pps_s":"""
        + """69,"damaged_lines":242,"fix":"3D","sats_used":11,"hdop":0.86,"alarms":["unlocked"]}""";

    /// <summary>The same station's display during <c>NO PPS</c>, 2026-11-14, verbatim: the GPS
    /// has gone too, but the OCXO is 3 ns off.</summary>
    private const string NoPps =
        """{"state":"NO PPS","reference_ok":false,"locked":false,"locked_s":0,"lock_losses":"""
        + """7,"gpsdo_restarts":0,"gps_restarts":0,"tic_ns":-3,"dac":30186,"dac_near_limit":false,"ocxo_c":"""
        + """44.2,"diff_ns":5,"tic_error_ns":-3.0,"tc_s":300,"ambient_c":34.1,"gpsdo_age_s":1,"firmware":"Rev. 3.4 paulv mods  ID:"""
        + """0","missed_pps_since_display":478,"damaged_lines":243,"gps_position_learned":true,"gps_seen":true,"antenna":"DONTKNOW","antenna_text":"not checked","fix":"none","sats_used":"""
        + """0,"sats_in_view":22,"hdop":99.99,"gps_age_s":0,"alarms":["no_pps","unlocked","gps_no_fix","gps_few_sats","gps_signal_loss"]}""";

    /// <summary><see cref="Relocking"/> with one field's value replaced.</summary>
    private static string RelockingWith(string field, string value)
    {
        JsonObject report = JsonNode.Parse(Relocking)!.AsObject();
        report[field] = JsonNode.Parse(value);
        return report.ToJsonString();
    }

    /// <summary><see cref="Relocking"/> with one more field.</summary>
    private static string RelockingAnd(string field, string value) => RelockingWith(field, value);

    [Fact]
    public void The_Report_That_Cost_The_1400_Slot_Is_Holdover_And_Fine_To_Transmit_On()
    {
        using GpsdoReference reference = Watching();
        Reporting(reference, Live("LOCKED", true, true), 110 * 60);

        reference.Receive("gpsdo/state", Relocking, retained: false);

        reference.Current.State.Should().Be(ReferenceState.Holdover);
        reference.Current.Detail.Should().Be("UNLOCKED, relocking 1124/1500 s, TIE -33 ns");
        reference.Current.Chip.Should().Be("10 MHz OK, holdover: UNLOCKED, relocking 1124/1500 s, TIE -33 ns");
        JsonNode lease = TxLeaseApi.Handle(Channel(), "GET", "", null, reference: reference).Answer["reference"]!;
        lease["state"]!.GetValue<string>().Should().Be("holdover");
        lease["detail"]!.GetValue<string>().Should().Be("UNLOCKED, relocking 1124/1500 s, TIE -33 ns");
        Journal.Last().Should().Be("10 MHz reference in holdover: UNLOCKED, relocking 1124/1500 s, TIE -33 ns (was OK for 1h50m)");

        // The firmware's 25 minutes back to LOCKED, a report every 10 s: still holdover, and not
        // a line or a page update for the counter or the TIE moving.
        var seen = new List<ReferenceVerdict>();
        reference.Changed += seen.Add;
        for (int counter = 1134; counter < 1500; counter += 10)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            string report = RelockingWith("lock_counter", counter.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("-33", counter % 20 == 0 ? "-31" : "-35", StringComparison.Ordinal);
            reference.Receive("gpsdo/state", report, retained: false);
            reference.Current.State.Should().Be(ReferenceState.Holdover);
        }

        seen.Should().BeEmpty();
        _time.Advance(TimeSpan.FromSeconds(4));
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);
        _lines.Last().Should().Be("10 MHz reference OK: LOCKED (was in holdover for 6m14s)");
    }

    [Fact]
    public void No_Pps_Past_60_Seconds_Is_Holdover_Not_Unreliable()
    {
        var seen = new List<ReferenceVerdict>();
        using GpsdoReference reference = Watching(seen);

        reference.Receive("gpsdo/state", NoPps, retained: false);
        Reporting(reference, NoPps, 50);
        reference.Current.Detail.Should().Be("NO PPS for 50 s, holding", "the first 60 s are held as before");

        _time.Advance(TimeSpan.FromSeconds(10));
        reference.Current.State.Should().Be(ReferenceState.Holdover, "the 1 s clock alone moves it on");
        reference.Current.Detail.Should().Be("NO PPS, TIE -3 ns");
        seen[^1].Chip.Should().Be("10 MHz OK, holdover: NO PPS");
        Journal.Last().Should().Be("10 MHz reference in holdover: NO PPS, TIE -3 ns (was OK for 1m00s)");
    }

    [Fact]
    public void A_Spell_From_No_Pps_Into_Unlocked_Is_One_Holdover()
    {
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", NoPps, retained: false);
        Reporting(reference, NoPps, 70);
        reference.Receive("gpsdo/state", Relocking, retained: false);

        reference.Current.State.Should().Be(ReferenceState.Holdover);
        Journal.Where(l => l.Contains("holdover", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Fact]
    public void Holdover_Lasts_Up_To_Its_Limit_From_The_Last_Good_Report_Then_Is_Unreliable()
    {
        using var reference = new GpsdoReference(
            "gpsdo", new StationJournal("", _lines.Add, _errors.Add), _time, holdover: TimeSpan.FromMinutes(30));
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        reference.Receive("gpsdo/state", Relocking, retained: false);

        Reporting(reference, Relocking, (29 * 60) + 59);
        reference.Current.State.Should().Be(ReferenceState.Holdover);
        _time.Advance(TimeSpan.FromSeconds(1));

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("UNLOCKED, in holdover for over 30m00s");
        _errors.Last().Should().Be(
            "10 MHz reference unreliable: UNLOCKED, in holdover for over 30m00s (was in holdover for 30m00s)");
    }

    [Fact]
    public void The_Default_Holdover_Limit_Is_Six_Hours()
    {
        GpsdoReference.DefaultHoldover.Should().Be(TimeSpan.FromHours(6));
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", Relocking, retained: false);

        Reporting(reference, Relocking, (6 * 3600) - 10);
        reference.Current.State.Should().Be(ReferenceState.Holdover);
        Reporting(reference, Relocking, 10);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void A_Holdover_Limit_Of_Zero_Is_The_Old_Behaviour()
    {
        using var reference = new GpsdoReference(
            "gpsdo", new StationJournal("", _lines.Add, _errors.Add), _time, holdover: TimeSpan.Zero);
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);

        reference.Receive("gpsdo/state", Relocking, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("UNLOCKED");
    }

    [Theory]
    [InlineData("alarms", """["unlocked","no_10mhz"]""")]
    [InlineData("alarms", """["unlocked","gpsdo_silent"]""")]
    [InlineData("alarms", """["unlocked","dac_near_limit"]""")]
    [InlineData("alarms", """["unlocked","something_new"]""")]
    [InlineData("alarms", """["unlocked",7]""")]
    [InlineData("dac_near_limit", "true")]
    [InlineData("tic_error_ns", "1000.5")]
    [InlineData("tic_error_ns", "-4200.0")]
    [InlineData("tic_error_ns", "null")]
    [InlineData("warmup_remaining_s", "540")]
    public void A_Report_That_Does_Not_Show_A_Good_10_Mhz_Is_Unreliable_For_The_Rest_Of_The_Spell(string field, string value)
    {
        using GpsdoReference reference = Watching();
        string faulty = RelockingAnd(field, value);

        reference.Receive("gpsdo/state", Relocking, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Holdover);
        reference.Receive("gpsdo/state", faulty, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Detail.Should().Be("UNLOCKED");

        reference.Receive("gpsdo/state", Relocking, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable, "the same spell, so not holdover again");

        reference.Receive("gpsdo/state", Live("LOCKED", true, true), retained: false);
        reference.Receive("gpsdo/state", Relocking, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Holdover, "a new spell, seen to start from a good report");
    }

    [Fact]
    public void A_Time_Interval_Error_Of_Exactly_1_Microsecond_Is_Still_Holdover()
    {
        using GpsdoReference reference = Watching();

        reference.Receive("gpsdo/state", RelockingWith("tic_error_ns", "-1000.0"), retained: false);

        reference.Current.State.Should().Be(ReferenceState.Holdover);
        reference.Current.Detail.Should().Be("UNLOCKED, relocking 1124/1500 s, TIE -1000 ns");
    }

    [Fact]
    public void A_Gpsdo_Restart_Since_The_Last_Good_Report_Is_Not_Holdover()
    {
        // The OCXO may have gone cold, whatever its first lines say.
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", """{"state":"LOCKED","reference_ok":true,"locked":true,"gpsdo_restarts":2,"alarms":[]}""", retained: false);

        reference.Receive("gpsdo/state", RelockingAnd("gpsdo_restarts", "2"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Holdover, "no restart since the good report");
        reference.Receive("gpsdo/state", RelockingAnd("gpsdo_restarts", "3"), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void A_Cold_Or_Warming_Gpsdo_Is_Never_Holdover()
    {
        // No good report before it: a GPSDO just powered up says UNLOCKED with a small TIE too.
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Receive("gpsdo/state", Relocking, retained: false);
        Reporting(reference, Relocking, 600);

        reference.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void A_Holdover_First_Seen_Retained_Or_After_A_Gap_Is_Not_Holdover()
    {
        using (GpsdoReference retained = Watching())
        {
            retained.Receive("gpsdo/state", Relocking, retained: true);
            retained.Current.State.Should().Be(ReferenceState.Unreliable);
        }

        using GpsdoReference gap = Watching();
        _time.Advance(TimeSpan.FromSeconds(40));
        gap.Receive("gpsdo/state", Relocking, retained: false);
        gap.Current.State.Should().Be(ReferenceState.Unreliable);
    }

    [Fact]
    public void A_Holdover_Whose_Reports_Stop_Is_Unknown_And_Offline_Is_Unknown()
    {
        using GpsdoReference reference = Watching();
        reference.Receive("gpsdo/state", Relocking, retained: false);

        _time.Advance(TimeSpan.FromSeconds(31));
        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Receive("gpsdo/state", Relocking, retained: false);
        reference.Current.State.Should().Be(ReferenceState.Holdover, "the spell went on while nobody reported");
        reference.Receive("gpsdo/status", "offline", retained: false);
        reference.Current.Detail.Should().Be("display offline");
    }

    [Fact]
    public void Holdover_Wording_Leaves_Out_A_Lock_Counter_That_Is_Not_Counting()
    {
        GpsdoReference.TryRead(NoPps, out GpsdoReport noPps).Should().BeTrue();
        GpsdoReference.HoldoverDetail("NO PPS", noPps).Should().Be("NO PPS, TIE -3 ns");
        GpsdoReference.TryRead(RelockingWith("lock_counter", "1500"), out GpsdoReport done).Should().BeTrue();
        GpsdoReference.HoldoverDetail("HOLD", done).Should().Be("HOLD, TIE -33 ns");
    }

    [Fact]
    public void The_Holdover_Limit_Loads_Defaults_And_Is_Checked()
    {
        Load("""{"device": "null", "gpsdo": {"host": "b"}}""").Config!.Gpsdo!.HoldoverHours.Should().Be(6);
        Load("""{"device": "null", "gpsdo": {"host": "b", "holdoverHours": 24}}""").Config!.Gpsdo!.HoldoverHours.Should().Be(24);
        Load("""{"device": "null", "gpsdo": {"host": "b", "holdoverHours": 0}}""").Config!.Gpsdo!.HoldoverHours.Should().Be(0);

        (DaemonConfig? config, string error) = Load("""{"device": "null", "gpsdo": {"host": "b", "holdoverHours": -1}}""");
        config.Should().BeNull();
        error.Should().Contain("\"gpsdo\".\"holdoverHours\" is -1");
    }

    // ---------------------------------------------------------------- the feed

    [Fact]
    public async Task An_Outage_Is_One_Line_However_Many_Retries_Or_Flaps_And_Back_Once_A_Connection_Lasts()
    {
        using GpsdoReference reference = Reference();
        var config = new GpsdoConfig { Host = "broker.lan" };
        int attempts = 0;
        var sessionEnds = new TaskCompletionSource<string>();
        var staying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        // Up and dropped; refused three times; then five connections the broker drops at once,
        // which are still the same outage; then one that stays.
        Task<string> Session(Action connected, CancellationToken cancel)
        {
            attempts++;
            if (attempts == 1 || attempts is >= 5 and <= 9)
            {
                connected();
                return Task.FromResult("connection reset");
            }

            if (attempts <= 4)
            {
                throw new System.Net.Sockets.SocketException(111);
            }

            connected();
            staying.TrySetResult();
            return sessionEnds.Task.WaitAsync(cancel);
        }

        // The clock moves only by the retry waits the feed announces, each once the feed is waiting
        // on it, until the connection that stays has been made; then by the time that connection
        // must last to count. The feed's own steady timer is never moved by the ladder: on the
        // connections that drop at once it is cancelled, and moving it first would count them.
        var waits = new RetryWaits(_time);
        Task running = new GpsdoFeed(config, reference, new StationJournal("", line =>
            {
                _lines.Add(line);
                if (line.Contains("back on", StringComparison.Ordinal))
                {
                    back.TrySetResult();
                }
            }, _errors.Add), waits, Session)
            .RunAsync(stop.Token);
        while (!staying.Task.IsCompleted)
        {
            Task<TimeSpan> next = waits.NextAsync();
            await Eventually.SignalAsync(Task.WhenAny(next, staying.Task), "the next retry wait, or the connection that stays");
            if (next.IsCompleted)
            {
                _time.Advance(await next);
            }
        }

        _time.Advance(GpsdoFeed.SteadyAfter);
        await Eventually.SignalAsync(back.Task, "the line saying the broker is back");

        attempts.Should().Be(10);
        _lines.Should().Equal(
            "gpsdo: connected to the MQTT broker at broker.lan:1883",
            "gpsdo: back on the MQTT broker at broker.lan:1883");
        _errors.Should().HaveCount(1, "one line for the outage, none for its refused retries or its flapping");
        _errors[0].Should().Be("gpsdo: lost the MQTT broker at broker.lan:1883 (connection reset); reconnecting");

        await stop.CancelAsync();
        await Eventually.SignalAsync(running, "running to complete");
    }

    /// <summary>
    /// The test clock, telling the test about each retry wait the feed starts on it: a one-shot
    /// timer that is not the feed's steady timer.
    /// </summary>
    private sealed class RetryWaits(FakeTimeProvider inner) : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<TimeSpan> _waits =
            global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();

        public Task<TimeSpan> NextAsync() => _waits.Reader.ReadAsync().AsTask();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Made first and only then announced, so a test that hears of a wait and moves the
            // clock straight away cannot move it past a timer the fake clock has not been given.
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            if (period == Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero
                && dueTime != Timeout.InfiniteTimeSpan && dueTime != GpsdoFeed.SteadyAfter)
            {
                _waits.Writer.TryWrite(dueTime);
            }

            return timer;
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;
    }

    // ---------------------------------------------------------------- /api/txlease

    private static SoundModemChannel Channel()
    {
        var channel = new SoundModemChannel(12000, randomSeed: 1);
        channel.AddModem(3, sink => new Afsk1200Modem(12000, sink));
        return channel;
    }

    [Fact]
    public void Without_A_Feed_The_Lease_Answer_Has_No_Reference_At_All()
    {
        (int status, JsonObject answer) = TxLeaseApi.Handle(Channel(), "GET", "", null);

        status.Should().Be(200);
        answer.ContainsKey("reference").Should().BeFalse("a head end tells \"no feed\" from \"unknown\" by its absence");
    }

    [Fact]
    public void With_A_Feed_The_Lease_Answer_Carries_The_Verdict_Its_Reason_And_Since_When()
    {
        using GpsdoReference reference = Reference();
        SoundModemChannel channel = Channel();

        JsonObject unknown = TxLeaseApi.Handle(channel, "GET", "", null, reference: reference).Answer;
        unknown["reference"]!.ToJsonString().Should().Be(
            """{"state":"unknown","detail":"no message yet","since":"2026-11-08T12:00:00Z"}""");

        _time.Advance(TimeSpan.FromMinutes(2));
        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
        JsonObject unreliable = TxLeaseApi.Handle(channel, "GET", "", null, reference: reference).Answer;
        unreliable["reference"]!.ToJsonString().Should().Be(
            """{"state":"unreliable","detail":"GPS LOST","since":"2026-11-08T12:02:00Z"}""");

        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        (int status, JsonObject taken) = TxLeaseApi.Handle(
            channel, "POST", """{"subChannel": 3, "seconds": 60}""", null, reference: reference);
        status.Should().Be(200);
        taken["reference"]!["state"]!.GetValue<string>().Should().Be("ok");

        _time.Advance(TimeSpan.FromSeconds(47));
        TxLeaseApi.Handle(channel, "GET", "", null, reference: reference).Answer["reference"]!["detail"]!
            .GetValue<string>().Should().Be("last report 47 s old");

        // An answer that does not describe the lease (a malformed request) carries nothing extra.
        JsonObject bad = TxLeaseApi.Handle(channel, "POST", "{", null, reference: reference).Answer;
        bad.ContainsKey("reference").Should().BeFalse();
    }
}
