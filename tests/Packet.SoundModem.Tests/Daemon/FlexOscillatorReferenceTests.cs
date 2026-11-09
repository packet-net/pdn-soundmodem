using System.Text.Json.Nodes;
using AwesomeAssertions;
using M0LTE.Flex;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The 10 MHz reference verdict on a Flex follows the oscillator the radio is actually running
/// on: its own TCXO, locked, is OK whatever the GPSDO says; the external reference, locked, is the
/// GPSDO's verdict; not locked or not said is unknown, unless the GPSDO says unreliable. On 9 Nov
/// 2026 the GPSDO was unplugged, the Flex fell back to its TCXO, and the GPSDO's "unreliable" for
/// 29 minutes would have held mailcast slots on a clean transmitter.
/// </summary>
public sealed class FlexOscillatorReferenceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly RadioOscillator Tcxo = new("tcxo", Locked: true);
    private static readonly RadioOscillator External = new("external", Locked: true);
    private static readonly RadioOscillator GpsdoOption = new("gpsdo", Locked: true);
    private static readonly RadioOscillator ExternalUnlocked = new("external", Locked: false);
    private static readonly RadioOscillator TcxoUnlocked = new("tcxo", Locked: false);
    private static readonly RadioOscillator NotSaid = new(null, Locked: false);

    private readonly FakeTimeProvider _time = new(Noon);
    /// <summary>Every journal line in the order written, problems and ordinary alike.</summary>
    private readonly List<string> _journal = [];
    private readonly List<string> _errors = [];

    private GpsdoReference Reference() =>
        new("gpsdo", new StationJournal("", _journal.Add, line => { _journal.Add(line); _errors.Add(line); }), _time);

    private IEnumerable<string> Journal => _journal;

    private static string State(string state, bool referenceOk) =>
        "{\"state\":\"" + state + "\",\"reference_ok\":" + (referenceOk ? "true" : "false") + "}";

    private static RadioOscillator? Flex(string name) => name switch
    {
        "none" => null,
        "tcxo" => Tcxo,
        "external" => External,
        "gpsdo" => GpsdoOption,
        "external-unlocked" => ExternalUnlocked,
        "tcxo-unlocked" => TcxoUnlocked,
        "not-said" => NotSaid,
        _ => throw new ArgumentException(name),
    };

    private static ReferenceState Parse(string word) => word switch
    {
        "ok" => ReferenceState.Ok,
        "unreliable" => ReferenceState.Unreliable,
        _ => ReferenceState.Unknown,
    };

    private static (ReferenceState, string, string) Gpsdo(ReferenceState state, string detail) => (state, detail, detail);

    // ---------------------------------------------------------------- the combination table

    [Theory]
    // No Flex: the GPSDO's verdict alone, as before.
    [InlineData("none", "ok", "LOCKED", "ok", "LOCKED")]
    [InlineData("none", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    [InlineData("none", "unknown", "display offline", "unknown", "display offline")]
    // On its own TCXO: OK whatever the GPSDO says.
    [InlineData("tcxo", "ok", "LOCKED", "ok", "Flex on its own TCXO (GPSDO not in use)")]
    [InlineData("tcxo", "unreliable", "GPS LOST", "ok", "Flex on its own TCXO (GPSDO not in use)")]
    [InlineData("tcxo", "unknown", "display offline", "ok", "Flex on its own TCXO (GPSDO not in use)")]
    // On the external reference (REF IN, or a built-in GPSDO): the GPSDO's verdict.
    [InlineData("external", "ok", "LOCKED", "ok", "LOCKED")]
    [InlineData("external", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    [InlineData("external", "unknown", "display offline", "unknown", "display offline")]
    [InlineData("gpsdo", "ok", "LOCKED", "ok", "LOCKED")]
    [InlineData("gpsdo", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    // Not locked, or not said: unknown, unless the GPSDO says unreliable.
    [InlineData("external-unlocked", "ok", "LOCKED", "unknown", "Flex reports its external reference not locked")]
    [InlineData("external-unlocked", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    [InlineData("tcxo-unlocked", "ok", "LOCKED", "unknown", "Flex reports its TCXO reference not locked")]
    [InlineData("tcxo-unlocked", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    [InlineData("not-said", "ok", "LOCKED", "unknown", "Flex has not said which oscillator it is on")]
    [InlineData("not-said", "unknown", "display offline", "unknown", "Flex has not said which oscillator it is on")]
    [InlineData("not-said", "unreliable", "GPS LOST", "unreliable", "GPS LOST")]
    public void The_Flex_Oscillator_Decides_How_Much_The_Gpsdo_Counts(
        string flex, string gpsdo, string gpsdoDetail, string expected, string expectedDetail)
    {
        (ReferenceState state, string detail, _) = GpsdoReference.Combine(Gpsdo(Parse(gpsdo), gpsdoDetail), Flex(flex));

        ReferenceVerdict.Words(state).Should().Be(expected);
        detail.Should().Be(expectedDetail);
    }

    // ---------------------------------------------------------------- the radio's own status

    /// <summary>
    /// Real status lines, through the real M0LTE.Flex client against the mock radio: what the
    /// daemon follows is what the library reads off the wire.
    /// </summary>
    [Theory]
    [InlineData("radio oscillator state=tcxo setting=tcxo locked=1", "tcxo", true)]
    [InlineData("radio oscillator state=external setting=external locked=1", "external", true)]
    [InlineData("radio oscillator state=gpsdo setting=gpsdo locked=1", "gpsdo", true)]
    [InlineData("radio oscillator state=external setting=external locked=0", "external", false)]
    [InlineData("radio oscillator state=tcxo setting=external locked=1", "tcxo", true)]
    public async Task A_Flex_Status_Line_Reads_As_The_Oscillator_In_Use(string status, string source, bool locked)
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        var seen = new List<RadioOscillator>();
        client.ReferenceChanged += r =>
        {
            lock (seen)
            {
                seen.Add(RadioOscillator.FromFlex(r));
            }
        };

        await mock.InjectStatusAsync("S0|" + status);
        await Until(() => RadioOscillator.FromFlex(client.Reference).Source is not null, "the radio said which oscillator it is on");

        RadioOscillator.FromFlex(client.Reference).Should().Be(new RadioOscillator(source, locked));
        lock (seen)
        {
            seen.Should().NotBeEmpty("the daemon follows the radio through ReferenceChanged");
            seen[^1].Should().Be(new RadioOscillator(source, locked));
        }
    }

    [Fact]
    public void Before_The_Radio_Has_Said_The_Oscillator_Is_Not_Known() =>
        RadioOscillator.FromFlex(FlexReferenceStatus.Unknown).Should().Be(NotSaid);

    // ---------------------------------------------------------------- live, with the journal

    [Fact]
    public void The_Ninth_Of_November_Reads_Ok_Because_The_Flex_Was_On_Its_Tcxo()
    {
        using GpsdoReference reference = Reference();
        reference.FlexOscillator(External);
        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        reference.Current.State.Should().Be(ReferenceState.Ok);

        // The GPSDO is unplugged: the Flex falls back to its TCXO and the display goes bad.
        reference.FlexOscillator(Tcxo);
        reference.Receive("gpsdo/state", State("NO 10 MHz", false), retained: false);
        for (int i = 0; i < 29 * 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            reference.Receive("gpsdo/state", State("NO 10 MHz", false), retained: false);
            reference.FlexOscillator(Tcxo);
        }

        ReferenceVerdict now = reference.Current;
        now.State.Should().Be(ReferenceState.Ok, "the transmitter was on its own clean TCXO all along");
        now.Detail.Should().Be("Flex on its own TCXO (GPSDO not in use)");
        now.Chip.Should().Be("10 MHz OK (Flex on TCXO)");

        // Back on the external reference after a power cycle: the GPSDO's verdict counts again.
        reference.FlexOscillator(External);
        reference.Current.State.Should().Be(ReferenceState.Unreliable);
        reference.Current.Chip.Should().Be("10 MHz unreliable: NO 10 MHz (Flex on external)");

        Journal.Should().Equal(
            "10 MHz reference OK: LOCKED (was unknown for 0s)",
            "flex: now on its TCXO, the GPSDO is not in use; the reference is judged OK",
            "flex: now on its external 10 MHz reference; the GPSDO's verdict counts",
            "10 MHz reference unreliable: NO 10 MHz (was OK for 29m00s)");
    }

    [Fact]
    public void Each_Move_Of_The_Flex_Is_One_Line_And_The_Same_Report_Again_Is_None()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Receive("gpsdo/state", State("LOCKED", true), retained: false);
        var shown = new List<ReferenceVerdict>();
        reference.Changed += shown.Add;

        reference.FlexOscillator(Tcxo);
        reference.FlexOscillator(Tcxo);
        reference.FlexOscillator(TcxoUnlocked);
        reference.FlexOscillator(TcxoUnlocked);
        reference.FlexOscillator(NotSaid);
        reference.FlexOscillator(Tcxo);

        Journal.Should().Equal(
            "10 MHz reference OK: LOCKED (was unknown for 0s)",
            "flex: its TCXO reference is not locked; the reference is judged unknown unless the GPSDO says unreliable",
            "10 MHz reference unknown: Flex reports its TCXO reference not locked (was OK for 0s)",
            "flex: its oscillator state is unknown; the reference is judged unknown unless the GPSDO says unreliable",
            "flex: now on its TCXO, the GPSDO is not in use; the reference is judged OK",
            "10 MHz reference OK: Flex on its own TCXO (GPSDO not in use) (was unknown for 0s)");
        _errors.Should().HaveCount(3, "the Flex not locked or not saying, and the unknown verdict, are problems");
        shown.Select(v => v.Chip).Should().Equal(
            "10 MHz OK (Flex on TCXO)",
            "10 MHz unknown (Flex ref not locked)",
            "10 MHz unknown (Flex ref unknown)",
            "10 MHz OK (Flex on TCXO)");
    }

    [Fact]
    public void The_First_Report_From_The_Flex_Is_Not_A_Line_Because_Bring_Up_Said_It()
    {
        using GpsdoReference reference = Reference();
        reference.FlexOscillator(Tcxo);

        Journal.Should().Equal("10 MHz reference OK: Flex on its own TCXO (GPSDO not in use) (was unknown for 0s)");
    }

    [Fact]
    public void A_Station_That_Is_Not_A_Flex_Has_No_Flex_Part()
    {
        using GpsdoReference reference = Reference();
        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);

        reference.Current.Chip.Should().Be("10 MHz unreliable: GPS LOST");
        reference.Describe().ContainsKey("oscillator").Should().BeFalse("only a Flex reports one");
    }

    // ---------------------------------------------------------------- the lease

    [Fact]
    public void The_Lease_Carries_The_Combined_Verdict_And_The_Flex_Oscillator()
    {
        using GpsdoReference reference = Reference();
        var channel = new SoundModemChannel(12000, randomSeed: 5);
        reference.Receive("gpsdo/status", "online", retained: false);
        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
        _time.Advance(TimeSpan.FromMinutes(1));
        reference.FlexOscillator(Tcxo);

        JsonObject answer = TxLeaseApi.Handle(channel, "GET", "", null, reference: reference).Answer;
        answer["reference"]!.ToJsonString().Should().Be(
            """{"state":"ok","detail":"Flex on its own TCXO (GPSDO not in use)","since":"2026-11-09T12:01:00Z","oscillator":"tcxo"}""");

        reference.Receive("gpsdo/state", State("GPS LOST", false), retained: false);
        reference.FlexOscillator(NotSaid);
        TxLeaseApi.Handle(channel, "GET", "", null, reference: reference).Answer["reference"]!.ToJsonString().Should().Be(
            """{"state":"unreliable","detail":"GPS LOST","since":"2026-11-09T12:01:00Z","oscillator":null}""");
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue(because);
    }
}
