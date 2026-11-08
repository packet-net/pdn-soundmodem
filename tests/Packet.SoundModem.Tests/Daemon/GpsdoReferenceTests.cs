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
    /// the three fields each test sets.</summary>
    private static string State(string state, bool? referenceOk) =>
        "{\"state\":\"" + state + "\""
        + (referenceOk is bool ok ? ",\"reference_ok\":" + (ok ? "true" : "false") : "")
        + ",\"locked\":false,\"locked_s\":0,\"lock_losses\":0,\"gpsdo_restarts\":0,\"gps_restarts\":0,"
        + "\"tic_ns\":44,\"dac\":30556,\"dac_near_limit\":false,\"ocxo_c\":42.4,\"diff_ns\":-4,\"tic_error_ns\":35.0,"
        + "\"tc_s\":300,\"ambient_c\":30.2,\"earliest_lock_s\":182,\"gpsdo_age_s\":0,"
        + "\"firmware\":\"Rev. 3.4 paulv mods  ID:0\",\"lock_counter\":1319,\"lock_target\":1500,"
        + "\"lock_test_tic_ns\":35,\"lock_test_freq_ns\":0,\"missed_pps_total\":1,\"since_missed_pps_s\":2106,"
        + "\"gps_seen\":true,\"fix\":\"3D\",\"sats_used\":12,\"sats_in_view\":22,\"hdop\":0.75,\"gps_age_s\":0,"
        + "\"alarms\":[\"unlocked\"]}";

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

        const string running = """{"device": "null", "gpsdo": {"host": "a.lan", "password": "pw"}}""";
        string served = ConfigApi.Redact(running);
        served.Should().NotContain("pw");

        ConfigApi.RestoreHidden(served, running, out _).Should().NotBeNull();
        ConfigApi.RestoreHidden(served.Replace("a.lan", "evil.example", StringComparison.Ordinal), running, out string why)
            .Should().BeNull("a moved broker would be sent the password nobody was shown");
        why.Should().Contain("gpsdo.password");
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

        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Chip.Should().Be("10 MHz unreliable: GPS LOST");
    }

    [Fact]
    public void A_Retained_State_Alone_Is_Not_A_Report()
    {
        // What the broker hands a new subscriber is the display's last word, from whenever. A
        // display that died without its last will would read as whatever it said last.
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: true);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: true);

        reference.Current.State.Should().Be(ReferenceState.Unknown);
        reference.Current.Detail.Should().Be("waiting for a fresh report");

        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);
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

        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
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

    // ---------------------------------------------------------------- the feed

    [Fact]
    public async Task An_Outage_Is_One_Line_However_Many_Retries_And_The_Feed_Comes_Back_By_Itself()
    {
        using GpsdoReference reference = Reference();
        var config = new GpsdoConfig { Host = "broker.lan" };
        int attempts = 0;
        var sessionEnds = new TaskCompletionSource<string>();
        using var stop = new CancellationTokenSource();

        // Up, dropped, then refused six times over, then up again for good.
        Task<string> Session(Action connected, CancellationToken cancel)
        {
            attempts++;
            if (attempts == 1)
            {
                connected();
                return Task.FromResult("connection reset");
            }

            if (attempts <= 7)
            {
                throw new System.Net.Sockets.SocketException(111);
            }

            connected();
            return sessionEnds.Task.WaitAsync(cancel);
        }

        Task running = new GpsdoFeed(config, reference, new StationJournal("", _lines.Add, _errors.Add), _time, Session)
            .RunAsync(stop.Token);
        for (int i = 0; i < 400 && (Volatile.Read(ref attempts) < 8 || _lines.Count < 2); i++)
        {
            await Task.Delay(5);
            _time.Advance(GpsdoFeed.MaxRetry);
        }

        attempts.Should().Be(8);
        _lines.Should().Equal(
            "gpsdo: connected to the MQTT broker at broker.lan:1883",
            "gpsdo: back on the MQTT broker at broker.lan:1883");
        _errors.Should().HaveCount(1, "one line for the outage, none for its six refused retries");
        _errors[0].Should().Be("gpsdo: lost the MQTT broker at broker.lan:1883 (connection reset); reconnecting");

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
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
