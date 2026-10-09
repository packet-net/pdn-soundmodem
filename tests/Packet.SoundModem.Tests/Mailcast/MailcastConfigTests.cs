using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// The <c>mailcast</c> section: what loads, what is refused before anything opens, and where the
/// receiver listens for each kind of station.
/// </summary>
public sealed class MailcastConfigTests : IDisposable
{
    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-config");

    public void Dispose() => _dir.Dispose();

    private (DaemonConfig? Config, string Error) Load(string json)
    {
        string path = Path.Combine(_dir.FullName, "soundmodem.json");
        File.WriteAllText(path, json);
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        return (config, error);
    }

    [Fact]
    public void No_Section_Is_No_Receiver()
    {
        (DaemonConfig? config, string error) = Load("""{"device": "null"}""");

        error.Should().BeEmpty();
        config!.Mailcast.Should().BeNull();
    }

    [Fact]
    public void A_Password_Is_All_It_Needs_And_The_Rest_Is_Gb7rdgs_And_LinBpqs_Defaults()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "mailcast": {"bbs": {"password": "pick-one"}}}""");

        error.Should().BeEmpty();
        MailcastConfig mailcast = config!.Mailcast!;
        mailcast.DialKHz.Should().Be(7052.0);
        mailcast.CentreHz.Should().Be(7_053_800);
        mailcast.Retune.Should().BeFalse("retuning somebody's rig is something they ask for");
        mailcast.StateDirectory.Should().BeNull();
        mailcast.StateDirectoryFor("/var/lib/pdn-soundmodem").Should().Be("/var/lib/pdn-soundmodem/mailcast");
        mailcast.Sources.Should().BeNull();
        mailcast.SourcesDefaulted.Should().BeTrue();
        mailcast.SourcesInUse.Should().Equal("GB7RDG", "M0LTE");
        MailcastBbsConfig bbs = mailcast.Bbs!;
        bbs.IsLinBpq.Should().BeTrue();
        bbs.Host.Should().Be("127.0.0.1");
        bbs.Port.Should().Be(8011);
        bbs.Login.Should().Be("Q0CAST");
        bbs.Command.Should().Be("BBS");
        bbs.Describe().Should().Be("LinBPQ at 127.0.0.1:8011 as Q0CAST");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Every_Key_Loads()
    {
        (DaemonConfig? config, string error) = Load("""
            {"device": "null", "mailcast": {
              "bbs": {"type": "fbb", "host": "bbs.lan", "port": 6300, "login": "Q0CAST", "password": "x", "command": ""},
              "dialKHz": 7051.5, "stateDirectory": "/srv/mailcast", "retune": true, "sources": ["G4XYZ"]}}
            """);

        error.Should().BeEmpty();
        config!.Warnings.Should().BeEmpty();
        MailcastConfig mailcast = config.Mailcast!;
        mailcast.SourcesDefaulted.Should().BeFalse();
        mailcast.SourcesInUse.Should().Equal("G4XYZ");
        mailcast.Bbs!.IsFbb.Should().BeTrue();
        mailcast.Bbs.Describe().Should().Be("FBB at bbs.lan:6300 as Q0CAST");
        mailcast.DialHz.Should().Be(7_051_500);
        mailcast.Retune.Should().BeTrue();
        mailcast.StateDirectoryFor("/var/lib/pdn-soundmodem").Should().Be("/srv/mailcast");
    }

    [Theory]
    [InlineData("""{"retune": true}""", "has no \"bbs\" section")]
    [InlineData("""{"bbs": {}}""", "\"password\" is empty")]
    [InlineData("""{"bbs": {"password": "x", "type": "winlink"}}""", "\"type\" is \"winlink\"")]
    [InlineData("""{"bbs": {"password": "x", "port": 0}}""", "is not a TCP port")]
    [InlineData("""{"bbs": {"password": "x", "host": " "}}""", "\"host\" is empty")]
    [InlineData("""{"bbs": {"password": "x", "login": "Q0 CAST"}}""", "must be one word")]
    [InlineData("""{"bbs": {"password": "x"}, "dialKHz": 7052000}""", "\"dialKHz\" is 7052000")]
    [InlineData("""{"bbs": {"password": "x"}, "stateDirectory": ""}""", "\"stateDirectory\" is empty")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": []}""", "\"sources\" is empty, so no frame would ever be taken")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["GB7RDG", ""]}""", "\"sources\" has \"\", which is not a callsign")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["GB7RDG", null]}""", "\"sources\" has null, which is not a callsign")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["GB7 RDG"]}""", "\"sources\" has \"GB7 RDG\", which is not a callsign")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["GB7RDGX"]}""", "\"sources\" has \"GB7RDGX\"")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["M0LTE-16"]}""", "\"sources\" has \"M0LTE-16\"")]
    [InlineData("""{"bbs": {"password": "x"}, "sources": ["M0LTE-"]}""", "\"sources\" has \"M0LTE-\"")]
    public void A_Section_That_Cannot_Work_Is_Refused_Saying_What_To_Change(string section, string why)
    {
        (DaemonConfig? config, string error) = Load($$"""{"device": "null", "mailcast": {{section}}}""");

        config.Should().BeNull();
        error.Should().Contain(why);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("type")]
    [InlineData("host")]
    [InlineData("login")]
    [InlineData("password")]
    public void A_Null_Bbs_Setting_Is_Refused_By_Name_Not_Thrown(string key)
    {
        string bbs = key == "password" ? """{"password": null}""" : $$"""{"password": "x", "{{key}}": null}""";

        (DaemonConfig? config, string error) = Load($$$"""{"device": "null", "mailcast": {"bbs": {{{bbs}}}}}""");

        config.Should().BeNull();
        error.Should().Contain($"\"mailcast\".\"bbs\".\"{key}\" is null");
    }

    [Fact]
    public void Sources_Are_Upper_Cased_Without_Their_Ssids_And_Each_Kept_Once()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "mailcast": {"bbs": {"password": "x"}, "sources": ["gb7rdg-2", " m0lte ", "GB7RDG", "M0LTE-15"]}}""");

        error.Should().BeEmpty();
        config!.Mailcast!.SourcesInUse.Should().Equal("GB7RDG", "M0LTE");
        config.Mailcast.SourcesDefaulted.Should().BeFalse();
    }

    [Fact]
    public void Sources_Given_As_One_String_Rather_Than_A_List_Is_Refused_Not_Thrown()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "mailcast": {"bbs": {"password": "x"}, "sources": "GB7RDG"}}""");

        config.Should().BeNull();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void The_Api_Checks_Sources_The_Way_Start_Up_Does()
    {
        string asPath = Path.Combine(_dir.FullName, "soundmodem.json");
        const string Station = """{"device": "null", "dialFrequency": 7052000, "mailcast": {"bbs": {"password": "x"}""";

        ConfigApi.Validate(Station + """, "sources": []}}""", asPath).Should().Contain("\"mailcast\".\"sources\" is empty");
        ConfigApi.Validate(Station + """, "sources": ["M0LTE", "not a call"]}}""", asPath)
            .Should().Contain("\"mailcast\".\"sources\" has \"not a call\", which is not a callsign");
        ConfigApi.Validate(Station + """, "sources": ["m0lte-1", "GB7RDG"]}}""", asPath).Should().BeNull();
        ConfigApi.Validate(Station + "}}", asPath).Should().BeNull("left out, the default sources are used");
    }

    [Fact]
    public void A_Monitor_Is_Told_It_Has_No_Bbs_To_Deliver_To()
    {
        (DaemonConfig? config, string error) = Load("""
            {"monitor": {}, "mailcast": {"bbs": {"password": "x"}}}
            """);

        config.Should().BeNull();
        error.Should().Contain("\"mailcast\" is set in a \"monitor\" configuration");
    }

    [Fact]
    public void Unknown_Keys_Are_Warned_About()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "mailcast": {"bbs": {"password": "x", "pasword": "y"}, "dial": 7052}}""");

        error.Should().BeEmpty();
        config!.Warnings.Should().Contain(w => w.StartsWith("mailcast: \"dial\" is not a setting", StringComparison.Ordinal));
        config.Warnings.Should().Contain(w => w.StartsWith("mailcast bbs: \"pasword\" is not a setting", StringComparison.Ordinal));
    }

    [Fact]
    public void Redaction_Matches_Keys_Whatever_Their_Case_As_The_File_Is_Read()
    {
        string redacted = ConfigApi.Redact(
            """{"Mailcast": {"BBS": {"Password": "pick-one"}}, "API": {"Key": "k3y"}, "Publish": {"TOKEN": "t0k"}}""");

        redacted.Should().NotContain("pick-one").And.NotContain("k3y").And.NotContain("t0k");
        redacted.Should().Contain("\"Password\": \"(set, not shown)\"");
    }

    [Fact]
    public void The_Api_Refuses_A_Station_That_Could_Not_Hear_The_Signal_Before_It_Restarts_Onto_It()
    {
        string asPath = Path.Combine(_dir.FullName, "soundmodem.json");

        string? why = ConfigApi.Validate("""{"device": "null", "mailcast": {"bbs": {"password": "x"}}}""", asPath);

        why.Should().Contain("is outside what this station hears");
        ConfigApi.Validate(
            """{"device": "null", "dialFrequency": 7052000, "mailcast": {"bbs": {"password": "x"}}}""", asPath)
            .Should().BeNull("a radio on the mailcast dial hears it");
        Directory.Exists(Path.Combine(_dir.FullName, "mailcast")).Should().BeTrue("the state directory was checked by writing to it");
    }

    [Fact]
    public void The_Api_Refuses_A_Dial_Decide_Accepts_But_Ms110D_Cannot_Actually_Reach()
    {
        // dialFrequency 7052522 Hz puts the signal's centre at 1278 Hz - inside the 1000 to
        // 2000 Hz band Decide() accepts, docs/14-mailcast.md's own FT-450D worked example - but
        // below the roughly 1740 Hz floor MS110D's own occupied width actually allows once
        // shifted (MailcastPlacementTests.A_Centre_Near_The_Low_End...). The API catches this
        // before restarting onto it rather than crashing on the restart.
        string asPath = Path.Combine(_dir.FullName, "soundmodem.json");

        string? why = ConfigApi.Validate(
            """{"device": "null", "dialFrequency": 7052522, "mailcast": {"bbs": {"password": "x"}}}""", asPath);

        why.Should().Contain("cannot be moved that close to the edge of its own occupied band")
            .And.Contain("1278 Hz");
    }

    [Fact]
    public void The_Api_Refuses_A_State_Directory_That_Cannot_Be_Written()
    {
        string notAFolder = Path.Combine(_dir.FullName, "a-file");
        File.WriteAllText(notAFolder, "");
        string json = $$$"""
            {"device": "null", "dialFrequency": 7052000,
             "mailcast": {"bbs": {"password": "x"}, "stateDirectory": "{{{Path.Combine(notAFolder, "mailcast")}}}"}}
            """;

        string? why = ConfigApi.Validate(json, Path.Combine(_dir.FullName, "soundmodem.json"));

        why.Should().StartWith("mailcast: cannot keep its state in").And.Contain("\"mailcast\".\"stateDirectory\"");
    }

    [Fact]
    public void The_Configuration_Api_Never_Serves_The_Bbs_Password_Back()
    {
        string redacted = ConfigApi.Redact("""{"mailcast": {"bbs": {"password": "pick-one", "login": "Q0CAST"}}}""");

        redacted.Should().NotContain("pick-one");
        redacted.Should().Contain("(set, not shown)");
        redacted.Should().Contain("Q0CAST");
    }
}

/// <summary>Where the receiver listens: the two placement cases and the refusals.</summary>
public sealed class MailcastPlacementTests
{
    private const double Half = 1450;

    private static MailcastConfig Config(bool retune = false, double? dialKHz = null) => new()
    {
        Bbs = new MailcastBbsConfig { Password = "x" },
        Retune = retune,
        DialKHz = dialKHz ?? MailcastConfig.DefaultDialKHz,
    };

    [Fact]
    public void A_Headless_Flex_Hears_It_On_Its_Own_Slice_With_No_Retuning()
    {
        // GB7RDG's own arrangement: packet on 7.0503 to 7.0516 MHz, the slice dial at 7.0501.
        var radio = new MailcastRadio(MailcastRadioKind.FlexHeadless, 7_050_100, "usb", 300, 10_000, HasRig: false);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(), radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeFalse();
        placement.AudioCentreHz.Should().BeApproximately(3700, 0.001);
        placement.Describe(Config()).Should().Contain("listening on the station's own passband").And.Contain("3700 Hz audio");
    }

    [Fact]
    public void A_Band_Plan_That_Reaches_The_Signal_Needs_No_Retuning()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_051_000, "usb", 300, 6000, HasRig: true);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(retune: true), radio, Half, out _);

        placement!.Retunes.Should().BeFalse("retune is only for a station that does not hear it");
        placement.AudioCentreHz.Should().BeApproximately(2800, 0.001);
    }

    [Fact]
    public void A_Radio_Already_On_The_Mailcast_Dial_Hears_It_Whatever_Its_Nominal_Window()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_052_000, "usb", 300, 2700, HasRig: false);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(), radio, Half, out _);

        placement!.Retunes.Should().BeFalse();
        placement.AudioCentreHz.Should().BeApproximately(1800, 0.001);
    }

    [Fact]
    public void A_Sound_Card_Dial_Giving_1500_Hz_Still_Counts_Whatever_The_Configured_DialKHz()
    {
        // 7.0523 MHz: centre 1500 Hz, inside the sound-card measure-or-type band (1000 to
        // 2000 Hz). Accepted whether or not "mailcast"."dialKHz" was ever changed from its default.
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_052_300, "usb", 300, 2700, HasRig: false);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(), radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeFalse();
        placement.AudioCentreHz.Should().BeApproximately(1500, 0.001);
    }

    [Theory]
    [InlineData(7_052_800, 1000)] // the low edge of the measure-or-type band
    [InlineData(7_051_800, 2000)] // the high edge
    public void A_Sound_Card_Dial_At_Either_Edge_Of_1000_To_2000_Hz_Still_Counts(double dialHz, double expectedCentreHz)
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, dialHz, "usb", 300, 2700, HasRig: false);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(), radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeFalse();
        placement.AudioCentreHz.Should().BeApproximately(expectedCentreHz, 0.001);
    }

    [Theory]
    [InlineData(7_052_801)] // 999 Hz: just under the low edge
    [InlineData(7_051_799)] // 2001 Hz: just over the high edge
    public void A_Sound_Card_Dial_Just_Outside_1000_To_2000_Hz_Is_Refused_With_The_Rule_And_An_Example(double dialHz)
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, dialHz, "usb", 300, 2700, HasRig: false);

        MailcastPlacement.Decide(Config(), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("tune so the signal's audio centre falls between 1000 and 2000 Hz")
            .And.Contain("7.0538 MHz minus the middle of your receive passband")
            .And.Contain("FT-450D at about 367-2190 Hz, centred on 1278 Hz, wants 7.05252 MHz")
            .And.Contain("POST /api/mailcast/measure");
    }

    [Fact]
    public void A_Flex_Attach_Dial_Is_Untouched_By_The_Sound_Card_Rule_And_Keeps_The_Fixed_7052_Band()
    {
        // 7.0523 MHz (centre 1500 Hz) is well inside the sound-card band, but a Flex in attach
        // mode has no analogue filter for a dial to dodge, so it keeps the original fixed-dial
        // tolerance band around 7.052 MHz alone and is refused this one.
        var radio = new MailcastRadio(MailcastRadioKind.FlexAttach, 7_052_300, "usb", 300, 2700, HasRig: false);

        MailcastPlacement.Decide(Config(), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("A FlexRadio is not retuned for it");
    }

    [Theory]
    [InlineData(7_051_900)]
    [InlineData(7_052_100)]
    public void A_Flex_Attach_Dial_At_Either_Edge_Of_Its_Own_Fixed_Band_Still_Counts(double dialHz)
    {
        var radio = new MailcastRadio(MailcastRadioKind.FlexAttach, dialHz, "usb", 300, 2700, HasRig: false);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(), radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeFalse();
    }

    [Fact]
    public void An_Ordinary_Rig_On_The_Packet_Channels_Is_Retuned_Around_Each_Slot_When_Allowed()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_049_450, "usb", 300, 2700, HasRig: true);

        MailcastPlacement? placement = MailcastPlacement.Decide(Config(retune: true), radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeTrue();
        placement.AudioCentreHz.Should().Be(1800, "on a retuned rig the signal is where the standard puts it");
        placement.Describe(Config()).Should().Contain("retuned to 7.052 MHz USB from 1 minute before each slot to 12 minutes after");
    }

    [Fact]
    public void A_Rig_Retuned_To_A_Configured_Narrow_Filter_Dial_Is_Put_On_The_Signal_At_1500_Hz()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_049_450, "usb", 300, 2700, HasRig: true);
        MailcastConfig config = Config(retune: true, dialKHz: 7052.3);

        MailcastPlacement? placement = MailcastPlacement.Decide(config, radio, Half, out string? why);

        why.Should().BeNull();
        placement!.Retunes.Should().BeTrue();
        placement.AudioCentreHz.Should().Be(1500, "retuning puts the rig on the configured dial, not always 7.052");
        placement.Describe(config).Should().Contain("retuned to 7.0523 MHz USB");
    }

    [Fact]
    public void Without_Retune_A_Station_That_Cannot_Hear_It_Is_Refused_With_The_Way_Out()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_049_450, "usb", 300, 2700, HasRig: true);

        MailcastPlacement.Decide(Config(), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("is outside what this station hears")
            .And.Contain("dial is 7.04945 MHz USB")
            .And.Contain("Set \"mailcast\".\"retune\": true");
    }

    [Fact]
    public void Retune_Without_A_Rig_Is_Refused_Asking_For_One()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, null, "usb", 300, 2700, HasRig: false);

        MailcastPlacement.Decide(Config(retune: true), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("Nothing in this configuration says where the station's dial is")
            .And.Contain("Add a \"rig\" section (rigctld)");
    }

    [Fact]
    public void A_Flex_Out_Of_Reach_Is_Told_To_Move_Its_Slice_Not_To_Retune()
    {
        var radio = new MailcastRadio(MailcastRadioKind.FlexHeadless, 14_100_000, "usb", 300, 10_000, HasRig: false);

        MailcastPlacement.Decide(Config(retune: true), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("A FlexRadio is not retuned for it");
    }

    [Fact]
    public void An_Lsb_Station_Cannot_Hear_A_Usb_Signal_But_A_Rig_Can_Be_Retuned_For_It()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 7_053_000, "lsb", 300, 2700, HasRig: true);

        MailcastPlacement.Decide(Config(), radio, Half, out string? why).Should().BeNull();
        why.Should().Contain("This station is on LSB, and the signal is USB");
        MailcastPlacement.Decide(Config(retune: true), radio, Half, out _)!.Retunes.Should().BeTrue();
    }

    [Fact]
    public void An_Fm_Station_Is_Refused()
    {
        var radio = new MailcastRadio(MailcastRadioKind.SoundCard, 145_300_000, "fm", 300, 2700, HasRig: true);

        MailcastPlacement.Decide(Config(retune: true), radio, Half, out string? why).Should().BeNull();

        why.Should().Contain("this station is an FM radio");
    }

    [Fact]
    public void A_Web_Receiver_Hears_Its_Ssb_Window()
    {
        var radio = new MailcastRadio(MailcastRadioKind.UberSdr, 7_052_000 - 500, "usb", 150, 3450, HasRig: false);

        // 7.0515 MHz: the signal at 2300 Hz, edges 850 to 3750, past the 3450 Hz window.
        MailcastPlacement.Decide(Config(), radio, Half, out string? why).Should().BeNull();
        why.Should().Contain("A web receiver is not retuned for it");
    }

    [Fact]
    public void The_Signal_Is_Measured_Off_The_Modem_As_A_Little_Under_3_Khz_Wide()
    {
        MailcastPlacement.HalfWidthHz().Should().BeInRange(1300, 1650);
    }

    [Fact]
    public void A_Centre_Near_The_Low_End_Of_The_Sound_Card_Band_Cannot_Be_Built_Yet()
    {
        // 1278 Hz is docs/14-mailcast.md's own FT-450D worked example, and Decide() above
        // accepts it (it is between 1000 and 2000 Hz). But MS110D's own occupied width is
        // almost 2.9 kHz, not the 2900 Hz "nominal" figure the placement arithmetic assumes
        // either side of a centre - shifted down to 1278 Hz its lower edge would reach below
        // DC, which Packet.SoundModem.Modems.FrequencyShiftedModem.Wrap refuses rather than
        // fold noise over DC into the demodulator. The measured safe floor is about 1740 Hz;
        // everything from there to 2000 Hz builds. Flagged for a maintainer: today, a station
        // whose dial follows this worked example exactly would be refused at start-up (caught
        // in Program.cs and MailcastStation.Problem, not an unhandled exception) rather than
        // receive anything - the rule as given accepts more than MS110D can actually reach.
        MailcastPlacement.CentreIsConstructible(1278).Should().BeFalse(
            "MS110D's own occupied band reaches below DC once shifted this far down");
        MailcastPlacement.CentreIsConstructible(1000).Should().BeFalse();
        MailcastPlacement.CentreIsConstructible(1800).Should().BeTrue("no shift at all");
        MailcastPlacement.CentreIsConstructible(1950).Should().BeTrue();
        MailcastPlacement.CentreIsConstructible(2000).Should().BeTrue();
    }

    [Fact]
    public void The_Station_Description_Is_Taken_From_What_Start_Up_Knows()
    {
        var flex = MailcastStation.RadioFor(
            deviceIsFlex: true, flexIsHeadless: true, deviceIsUberSdr: false, bandPlan: null,
            dialFrequency: null, receiveDialHz: null, sideband: "usb",
            new Packet.SoundModem.FlexRadio.FlexTuning { Frequency = "7.050100" }, uberSdrConfig: null, hasRig: false);
        flex.Should().Be(new MailcastRadio(MailcastRadioKind.FlexHeadless, 7_050_100, "usb", 300, 10_000, false));

        var card = MailcastStation.RadioFor(
            deviceIsFlex: false, flexIsHeadless: false, deviceIsUberSdr: false, bandPlan: null,
            dialFrequency: 7_052_000, receiveDialHz: null, sideband: "usb",
            new Packet.SoundModem.FlexRadio.FlexTuning(), uberSdrConfig: null, hasRig: true);
        card.Should().Be(new MailcastRadio(MailcastRadioKind.SoundCard, 7_052_000, "usb", 300, 2700, true));
    }
}
