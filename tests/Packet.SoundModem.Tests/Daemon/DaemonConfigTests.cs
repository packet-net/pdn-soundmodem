using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The operator-facing half of configuration loading. These messages are what an admin reads in
/// `journalctl` after the service refuses to start, so they are worth pinning: every failure must
/// name the file, say what is wrong in words, and say what to do about it - never surface a raw
/// exception. See CONFIG.md § What is rejected at start-up.
/// </summary>
public class DaemonConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Every failure message must be actionable, not just accurate.</summary>
    private static void ShouldGuideTheOperator(string error, string path)
    {
        error.Should().Contain(path, "the operator has to know which file to edit");
        error.Should().Contain("systemctl restart pdn-soundmodem",
            "the message must say how to apply the fix");
        error.Should().Contain("CONFIG.md", "the message must point at the reference");
        error.Should().NotContain("Exception", "a stack trace is not an explanation");
        error.Should().NotContain("   at ", "a stack trace is not an explanation");
    }

    [Fact]
    public void A_Valid_File_Loads_And_Reports_No_Error()
    {
        string path = WriteConfig("""{"device": "null", "kissPort": 8105}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull();
        error.Should().BeEmpty();
        config!.Device.Should().Be("null");
    }

    [Fact]
    public void An_Empty_Object_Is_Valid_And_Yields_One_Default_Modem()
    {
        string path = WriteConfig("{}");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Modems.Should().ContainSingle();
        config.Modems[0].SubChannel.Should().Be(0);
        config.Modems[0].Mode.Should().Be("afsk1200");
    }

    [Fact]
    public void Comments_And_Trailing_Commas_Are_Accepted_Because_The_Shipped_Example_Uses_Them()
    {
        string path = WriteConfig("""
            {
              // the annotated example is full of these
              /* and these */
              "device": "null",
              "modems": [ { "subChannel": 0, "mode": "afsk1200", }, ],
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Modems.Should().ContainSingle();
    }

    [Fact]
    public void Malformed_Json_Names_The_Line_And_Position()
    {
        string path = WriteConfig("""
            {
              "device": "null",
              "modems": [ { "subChannel": 0, "mode": } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("not valid JSON");
        // Counted from 1, the way an editor does - System.Text.Json counts from 0.
        error.Should().Contain("line 3", "the operator needs to be told where to look");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Duplicate_Sub_Channel_Says_Which_One_And_What_To_Do()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 1, "mode": "afsk1200"},
              {"subChannel": 1, "mode": "bpsk300"}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("subChannel").And.Contain("1");
        error.Should().Contain("renumber");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void An_Empty_File_Says_So_Rather_Than_Talking_About_Json_Tokens()
    {
        string path = WriteConfig("   \n  ");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("the file is empty");
        error.Should().NotContain("JSON tokens");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_File_Of_Literal_Null_Offers_A_Minimal_Working_Config()
    {
        string path = WriteConfig("null");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("only `null`");
        error.Should().Contain("afsk1200", "showing a working file beats describing one");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void The_Suggested_Commands_Never_Assume_Sudo_Exists()
    {
        // Debian installs sudo only when the root password is left blank at setup, so a good
        // number of the machines this runs on do not have it. The message says "as root" and
        // gives bare commands, which is right on those and on sudo systems alike.
        string path = WriteConfig("{ not json");

        DaemonConfig.TryLoad(path, out string error);

        error.Should().NotContain("sudo", "the command would not exist on a sudo-less Debian");
        error.Should().Contain("As root", "the privilege needed has to be stated some other way");
    }

    [Fact]
    public void A_Setting_This_Version_Does_Not_Know_Is_Called_Out_Rather_Than_Silently_Ignored()
    {
        // System.Text.Json drops unknown members without a word, so a typo - or a setting that
        // has since been withdrawn - would look accepted and do nothing.
        string path = WriteConfig("""
            {"device": "null", "csma": {"txDelayMilliseconds": 50, "persistence": 200}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error, "an unknown setting is a warning, not a refusal to start");
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("csma").And.Contain("IGNORED");
    }

    [Fact]
    public void An_Unknown_Setting_Is_Called_Out_So_A_Typo_Is_Not_Silent()
    {
        string path = WriteConfig("""{"device": "null", "modemz": []}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle().Which.Should().Contain("modemz");
    }

    [Fact]
    public void A_Correct_File_Warns_About_Nothing()
    {
        string path = WriteConfig("""
            {"device": "null", "kissPort": 8105, "bind": "127.0.0.1",
             "modems": [{"subChannel": 0, "mode": "afsk1200", "port": 8110}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Warnings.Should().BeEmpty();
        config.Modems[0].Port.Should().Be(8110);
    }

    [Theory]
    // A modem's port against the shared one, and against another modem's.
    [InlineData("""{"kissPort": 8105, "modems": [{"subChannel": 0, "port": 8105}]}""", "8105")]
    [InlineData("""{"modems": [{"subChannel": 0, "port": 9000}, {"subChannel": 1, "port": 9000}]}""", "9000")]
    // And against the other services sharing the daemon.
    [InlineData("""{"waterfall": {"port": 9100}, "modems": [{"subChannel": 0, "port": 9100}]}""", "9100")]
    [InlineData("""{"paging": {"port": 9200}, "modems": [{"subChannel": 0, "port": 9200}]}""", "9200")]
    // ardopcf's data port is always command+1, so a service on 8516 collides with ardop 8515.
    [InlineData("""{"ardop": {"port": 8515}, "waterfall": {"port": 8516}}""", "8516")]
    public void Two_Services_Wanting_The_Same_Port_Is_Rejected_By_Name(string json, string port)
    {
        string path = WriteConfig(json.Insert(1, "\"device\": \"null\", "));

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull("a port clash must not be left to whichever listener binds second");
        error.Should().Contain(port).And.Contain("both want TCP port");
        ShouldGuideTheOperator(error, path);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    public void A_Usable_Bind_Is_Accepted(string bind)
    {
        string path = WriteConfig($$"""{"device": "null", "bind": "{{bind}}"}""");

        DaemonConfig.TryLoad(path, out string error).Should().NotBeNull(error);
    }

    [Fact]
    public void A_Blank_Bind_Stays_On_Loopback_Rather_Than_Opening_Up()
    {
        // The unsafe reading of an empty value would put an unauthenticated transmit interface
        // on every interface because somebody left a string empty.
        DaemonConfig.ParseBind("").Should().Be(System.Net.IPAddress.Loopback);
        DaemonConfig.ParseBind(null).Should().Be(System.Net.IPAddress.Loopback);
        DaemonConfig.ParseBind("*").Should().Be(System.Net.IPAddress.Any);
    }

    [Fact]
    public void Ardop_Sits_In_The_Modems_List_Beside_The_Packet_Modes()
    {
        // The whole point of the change: one 3 kHz passband carrying ARDOP and packet modes at
        // once, each with its own centre and its own host port.
        string path = WriteConfig("""
            {"device": "null", "captureRate": 12000, "modems": [
              {"subChannel": 0, "mode": "afsk300-il2pc", "frequency": 300,  "port": 8100},
              {"subChannel": 1, "mode": "ardop",         "frequency": 950,  "port": 8101},
              {"subChannel": 2, "mode": "bpsk300",       "frequency": 1600, "port": 8103}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error, "ardop beside packet modems is no longer exclusive");
        config!.Warnings.Should().BeEmpty();
        config.Modems.Should().HaveCount(3);
        config.Modems[1].Mode.Should().Be("ardop");
        config.Modems[1].Frequency.Should().Be(950);
    }

    [Fact]
    public void An_Ardop_Port_Reserves_The_Data_Port_Above_It()
    {
        // ardopcf's data port is always command + 1, so this config looks fine and is not:
        // ARDOP's data port and the third modem both want 8102.
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 1, "mode": "ardop",   "port": 8101},
              {"subChannel": 2, "mode": "bpsk300", "port": 8102}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("8102").And.Contain("ARDOP data port");
    }

    [Fact]
    public void Two_Ardop_Modems_Are_Rejected()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "ardop", "port": 8515},
              {"subChannel": 1, "mode": "ardop", "port": 8600}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("One ARDOP TNC per channel");
    }

    [Fact]
    public void Ardop_Configured_Both_Ways_At_Once_Is_Rejected_With_The_Way_Out()
    {
        string path = WriteConfig("""
            {"device": "null", "ardop": {"port": 8515},
             "modems": [{"subChannel": 0, "mode": "ardop", "port": 8600}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("configured twice").And.Contain("delete the \"ardop\" section");
    }

    [Fact]
    public void The_Old_Top_Level_Ardop_Section_Still_Loads()
    {
        // Released in 0.7.x; it keeps working and is folded into a modem entry at start-up.
        string path = WriteConfig("""{"device": "null", "ardop": {"port": 8515}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Ardop!.Port.Should().Be(8515);
    }

    [Fact]
    public void An_Unknown_Setting_On_A_Modem_Is_Called_Out_With_Its_Sub_Channel()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [{"subChannel": 0, "mode": "afsk1200", "kissPort": 8110}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Modems[0].Port.Should().BeNull("a key this version does not know does nothing");
        config.Warnings.Should().ContainSingle()
            .Which.Should().Contain("modem 0").And.Contain("kissPort");
    }

    [Fact]
    public void A_Modem_Can_Be_Told_To_Identify_In_Morse()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "afsk1200"},
              {"subChannel": 1, "mode": "bpsk300", "identify": {"callsign": "M0LTE"}}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull();
        error.Should().BeEmpty();
        config!.Modems[0].Identify.Should().BeNull(
            "identification is per modem: a modem that did not ask for it never transmits one");
        config.Modems[1].Identify!.Callsign.Should().Be("M0LTE");
        config.Modems[1].Identify!.IntervalMinutes.Should().Be(10, "the documented default");
        config.Modems[1].Identify!.Wpm.Should().Be(20, "the documented default");
        config.Modems[1].Identify!.ToneHz.Should().BeNull(
            "unset means the modem's own centre, which is resolved from the band plan at "
            + "start-up rather than written down here");
    }

    [Fact]
    public void An_Unknown_Setting_Inside_Identify_Is_Called_Out_With_Its_Modem()
    {
        // A typo in a licence-condition setting must not vanish into the deserialiser: the
        // failure mode is a station that believes it is identifying and is not.
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "bpsk300",
               "identify": {"callsign": "M0LTE", "intervalMins": 10}}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Modems[0].Identify!.IntervalMinutes.Should().Be(10, "the default still stands");
        config.Warnings.Should().ContainSingle()
            .Which.Should().Contain("modem 0 identify").And.Contain("intervalMins");
    }

    [Fact]
    public void A_Modem_Can_Be_Told_To_Accept_Plain_Il2p_As_Well()
    {
        // The 40 m station hearing the BPQ32 node next door (see ModemConfig.AcceptPlainIl2p).
        // It has to be a key the daemon knows - an ignored one would leave the operator believing
        // their modem got more tolerant while the frames kept being dropped.
        string path = WriteConfig("""
            {"device": "null", "captureRate": 12000, "modems": [
              {"subChannel": 0, "mode": "bpsk300", "frequency": 2150, "acceptPlainIl2p": true},
              {"subChannel": 1, "mode": "bpsk300", "frequency": 1500}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Warnings.Should().BeEmpty();
        config.Modems[0].AcceptPlainIl2p.Should().BeTrue();
        config.Modems[1].AcceptPlainIl2p.Should().BeFalse(
            "it is per modem: a station may want it on the BPSK slot and nowhere else");
    }

    [Fact]
    public void A_Band_Plan_In_Rf_Terms_Loads()
    {
        string path = WriteConfig("""
            {"device": "null", "captureRate": 12000, "sideband": "usb", "modems": [
              {"subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300},
              {"subChannel": 1, "mode": "ardop",         "rfFrequency": 7050950, "bandwidth": 500},
              {"subChannel": 2, "mode": "bpsk300",       "rfFrequency": 7051600}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Sideband.Should().Be("usb");
        config.Modems[0].RfFrequency.Should().Be(7_050_300);
        config.Modems[1].Bandwidth.Should().Be(500);
        config.Modems.Should().AllSatisfy(m => m.Frequency.Should().BeNull(
            "audio centres are the plan's output, not its input"));
    }

    [Fact]
    public void Saying_It_Both_Ways_On_One_Modem_Is_Rejected()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "bpsk300", "frequency": 1500, "rfFrequency": 7051600}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("both").And.Contain("frequency").And.Contain("rfFrequency");
    }

    [Fact]
    public void Mixing_Rf_Addressed_And_Audio_Addressed_Modems_Is_Rejected()
    {
        // The dial is shared, so a modem pinned in audio terms would sit at whatever RF the
        // dial chosen for the others happened to put it - silently, and differently each time
        // the plan changed.
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300},
              {"subChannel": 1, "mode": "bpsk300",       "frequency": 1500}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("modem 1 (bpsk300)").And.Contain("every modem");
    }

    [Fact]
    public void One_Bind_Covers_Every_Listener()
    {
        string path = WriteConfig("""{"device": "null", "bind": "0.0.0.0"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Bind.Should().Be("0.0.0.0");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void An_Unknown_Setting_Inside_A_Section_Is_Called_Out_With_Its_Section()
    {
        string path = WriteConfig("""{"device": "null", "waterfall": {"port": 8099, "colour": "green"}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("waterfall").And.Contain("colour");
    }

    /// <summary>
    /// The page's Mixer group and <c>/api/mixer</c> without an <c>api.key</c>: read as written,
    /// off on a file that says nothing about it, and off on a public page whatever it says.
    /// </summary>
    /// <remarks>
    /// <para>Off by default is the whole safety of it: every station that upgrades onto this
    /// release keeps the behaviour it had, where the card is out of reach without the key.</para>
    /// <para><c>AudioControlsOpen</c> is the last column and the one that matters. That single
    /// "and not public" is the whole of "never on a public page" - the page, the GET and the POST
    /// all follow from whether the daemon installs the API open - and it lives on the config
    /// object so that this test can reach it. In <c>Program.cs</c> it was a conjunction in
    /// top-level statements that no test constructs, where dropping it would have left the suite
    /// green and the card in reach of strangers.</para>
    /// </remarks>
    [Theory]
    [InlineData("""{"device": "null", "waterfall": {"port": 8107, "enableAudioControls": true}}""", true, true)]
    [InlineData("""{"device": "null", "waterfall": {"port": 8107, "enableAudioControls": false}}""", false, false)]
    [InlineData("""{"device": "null", "waterfall": {"port": 8107}}""", false, false)]
    [InlineData("""{"device": "null", "waterfall": {"port": 8099, "public": true, "enableAudioControls": true}}""", true, false)]
    public void The_Page_May_Set_The_Card_Without_A_Key_Only_When_The_File_Says_So(
        string json, bool asked, bool served)
    {
        string path = WriteConfig(json);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Waterfall!.EnableAudioControls.Should().Be(asked, "the file is read as written");
        config.Waterfall.AudioControlsOpen.Should().Be(
            served, "a public page never carries the operator's mixer, whatever the file asks for");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_Misspelt_Audio_Controls_Setting_Is_Called_Out_Rather_Than_Quietly_Off()
    {
        // The failure this has to catch: a station left with the card locked away and an
        // operator who believes they opened it, because the singular reads perfectly well.
        string path = WriteConfig("""
            {"device": "null", "waterfall": {"enableAudioControl": true}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Waterfall!.EnableAudioControls.Should().BeFalse();
        config.Warnings.Should().ContainSingle()
            .Which.Should().Contain("waterfall").And.Contain("enableAudioControl");
    }

    [Fact]
    public void A_Bind_That_Is_Not_An_Address_Is_Rejected()
    {
        string path = WriteConfig("""{"device": "null", "bind": "everywhere"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"bind\"").And.Contain("127.0.0.1");
    }

    [Fact]
    public void An_Fm_Radio_Is_A_Radio_Kind_Beside_The_Two_Sidebands()
    {
        // Issue #413: an FM set is a channel radio, and the field that says which sideband a
        // station is on is the field that says it is not on one at all.
        string path = WriteConfig("""
            {"device": "null", "captureRate": 48000, "sideband": "fm", "modems": [
              {"subChannel": 0, "mode": "afsk1200", "rfFrequency": 145300000, "frequency": 1500}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Sideband.Should().Be("fm");

        // And both frequencies together, which on a sideband radio is refused as one thing said
        // twice: on FM they are the channel and where the tones sit in its audio.
        config.Modems[0].RfFrequency.Should().Be(145_300_000);
        config.Modems[0].Frequency.Should().Be(1500);
    }

    [Fact]
    public void A_Sideband_That_Names_No_Radio_This_Knows_Is_Rejected()
    {
        string path = WriteConfig("""{"device": "null", "sideband": "am"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"am\"")
            .And.Contain("\"usb\"").And.Contain("\"lsb\"").And.Contain("\"fm\"",
                "the message has to name every kind there is, not the two it used to");

        // The page's own copy of the setting goes through the same check, for the same reason:
        // taken as "usb" without a word, every frequency it drew would be wrong.
        string page = WriteConfig(
            """{"device": "null", "waterfall": {"port": 8107, "sideband": "am"}}""");
        DaemonConfig.TryLoad(page, out string pageError).Should().BeNull();
        pageError.Should().Contain("\"waterfall\".\"sideband\"");
    }

    [Fact]
    public void Whether_Sideband_Was_Written_Down_Is_Distinguishable_From_The_Default()
    {
        // On a Flex the slice mode states the sideband, so a defaulted value is corrected
        // silently while one written down that contradicts the radio is an error. Those two
        // are the same value once deserialized, hence reading it back off the document.
        string stated = WriteConfig("""{"device": "null", "sideband": "usb"}""");
        DaemonConfig.TryLoad(stated, out _)!.SidebandWasStated.Should().BeTrue();

        string defaulted = WriteConfig("""{"device": "null"}""");
        DaemonConfig.TryLoad(defaulted, out _)!.SidebandWasStated.Should().BeFalse();
    }

    [Fact]
    public void An_Unset_Flex_Dax_Channel_Stays_Out_Of_SmartSDRs_Way()
    {
        // SmartSDR grabs DAX 1 and the two contend (live finding, 2026-07-17), so the default
        // has to be somewhere else or the order they are started in decides whether it works.
        string path = WriteConfig("""{"device": "flex:mock", "flex": {"antenna": "ANT1"}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Flex!.DaxChannel.Should().BeNull("unset means the daemon chooses");
        FlexConfig.DefaultHeadlessDaxChannel.Should().NotBe("1");
    }

    [Fact]
    public void An_UberSdr_Section_Loads_With_Its_Defaults()
    {
        string path = WriteConfig("""
            {
              "device": "ubersdr:m9psy-1.instance.ubersdr.org",
              "ubersdr": { "gain": 4.0 },
              "modems": [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7051600 } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.UberSdr!.Gain.Should().Be(4.0);
        config.UberSdr.Mode.Should().Be("iq48", "every public instance offers it");
        config.UberSdr.SsbLowHz.Should().BeNull("unset means the device's own default");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_Misspelt_UberSdr_Setting_Is_Reported_Rather_Than_Dropped()
    {
        // System.Text.Json discards unknown members in silence, which turns a typo into a config
        // that looks accepted and quietly does something else.
        string path = WriteConfig("""
            {"device": "ubersdr:sdr.example", "ubersdr": {"passwrd": "hunter2"}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("passwrd").And.Contain("ubersdr");
    }

    [Fact]
    public void A_Transmit_Filter_Cut_Off_That_Is_Really_A_Frequency_Is_Rejected()
    {
        // The units are the trap: "transmitFilterHighHz" next to a "frequency" in MHz invites
        // 14100000, which the radio would take and leave the operator wondering.
        string path = WriteConfig("""{"device": "flex:mock", "flex": {"transmitFilterHighHz": 14100000}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("transmitFilterHighHz").And.Contain("500-10000");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Transmit_Filter_Cut_Off_Of_Zero_Means_Leave_The_Radio_Alone()
    {
        string path = WriteConfig("""{"device": "flex:mock", "flex": {"transmitFilterHighHz": 0}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Flex!.TransmitFilterHighHz.Should().Be(0);
    }

    [Fact]
    public void An_Unset_Transmit_Filter_Cut_Off_Is_Derived_Rather_Than_Defaulted()
    {
        string path = WriteConfig("""{"device": "flex:mock", "flex": {"antenna": "ANT2"}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Flex!.TransmitFilterHighHz.Should().BeNull(
            "null is what start-up reads as 'work it out from the modems'");
    }

    [Fact]
    public void Dead_Feed_Defaults_Follow_The_Device()
    {
        // flex/ubersdr: a healthy stream always carries noise-floor energy and can also stop
        // delivering, so both watches. ALSA: genuinely-silent wired inputs exist (a
        // disconnected cable must not restart-loop), so starvation only. wav-loop: a
        // recording paces itself and cannot starve, and looping a silent one is legitimate.
        DeadFeedConfig.Resolve(null, DeadFeedDevice.Flex).Should().Be((30.0, 30.0));
        DeadFeedConfig.Resolve(null, DeadFeedDevice.UberSdr).Should().Be((30.0, 30.0));
        DeadFeedConfig.Resolve(null, DeadFeedDevice.Alsa).Should().Be((0.0, 30.0));
        DeadFeedConfig.Resolve(null, DeadFeedDevice.WavLoop).Should().Be((0.0, 0.0));
    }

    [Fact]
    public void A_Stated_Dead_Feed_Threshold_Overrides_Only_Its_Own_Watch()
    {
        var config = new DeadFeedConfig { SilenceSeconds = 10 };

        DeadFeedConfig.Resolve(config, DeadFeedDevice.Alsa).Should().Be(
            (10.0, 30.0), "silence was stated (turning it ON for an ALSA card whose input "
            + "always carries noise floor); starvation keeps the device default");
    }

    [Fact]
    public void Zero_Turns_A_Dead_Feed_Watch_Off()
    {
        var config = new DeadFeedConfig { SilenceSeconds = 0, StarvationSeconds = 0 };

        DeadFeedConfig.Resolve(config, DeadFeedDevice.Flex).Should().Be(
            (0.0, 0.0), "0 is the documented off-switch, honoured on any device");
    }

    [Fact]
    public void Dead_Feed_Thresholds_Load_From_The_File()
    {
        string path = WriteConfig(
            """{"device": "null", "deadFeed": {"silenceSeconds": 45, "starvationSeconds": 0}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.DeadFeed!.SilenceSeconds.Should().Be(45);
        config.DeadFeed.StarvationSeconds.Should().Be(0);
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_Negative_Dead_Feed_Threshold_Is_Rejected_With_Guidance()
    {
        string path = WriteConfig("""{"device": "null", "deadFeed": {"starvationSeconds": -30}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("starvationSeconds").And.Contain("-30");
        error.Should().Contain("0 to turn that watch off");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void An_Unknown_Dead_Feed_Setting_Is_Warned_About()
    {
        string path = WriteConfig("""{"device": "null", "deadFeed": {"silenceSecs": 30}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("deadFeed").And.Contain("silenceSecs").And.Contain("IGNORED");
    }

    [Fact]
    public void A_Missing_File_Is_Reported_As_Configuration_Not_As_A_Crash()
    {
        string path = Path.Combine(_dir, "does-not-exist.json");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("no such file");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Missing_Directory_Names_The_Directory()
    {
        string path = Path.Combine(_dir, "nope", "soundmodem.json");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("no such directory");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void Modem_Plugins_Are_A_List_Of_Paths_And_Nothing_Else()
    {
        string path = WriteConfig("""
            {
              "device": "null",
              "modemPlugins": [ { "path": "/opt/pdn/plugins/M0LTE.OfdmFm.dll" } ],
              "modems": [ { "subChannel": 0, "mode": "ofdm-fm:nb" } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.ModemPlugins.Should().ContainSingle()
            .Which.Path.Should().Be("/opt/pdn/plugins/M0LTE.OfdmFm.dll");
    }

    [Fact]
    public void No_Modem_Plugins_Is_The_Usual_Case_And_Needs_No_Key()
    {
        string path = WriteConfig("""{"device": "null"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.ModemPlugins.Should().BeEmpty();
    }

    [Fact]
    public void A_Null_Modem_Plugins_List_Is_No_Plugins_Rather_Than_A_Crash()
    {
        // An explicit JSON null deserialises to a null list, not to the property initialiser, so
        // every read of it afterwards would throw where the operator expects a message about
        // their file.
        string path = WriteConfig("""{"device": "null", "modemPlugins": null}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.ModemPlugins.Should().BeEmpty();
    }

    [Fact]
    public void A_Modem_Plugin_Entry_With_No_Path_Is_An_Unfinished_Line_Not_A_Request()
    {
        // Left to the loader this becomes "no path given" at start-up, which reads as the plugin
        // mechanism misbehaving rather than as the file being half written.
        string path = WriteConfig("""
            {
              "device": "null",
              "modemPlugins": [ { } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("has no \"path\"");
        error.Should().Contain("no directory to scan",
            "the operator has to be told there is no location it could be found in instead");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Typo_Inside_A_Modem_Plugin_Entry_Is_Called_Out()
    {
        string path = WriteConfig("""
            {
              "device": "null",
              "modemPlugins": [ { "path": "/tmp/x.dll", "pathh": "/tmp/y.dll" } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("modemPlugins[0]: \"pathh\"");
    }

    // ---- publish: a station offering itself to a public monitor site -----------------------
    //
    // Section 4.3 of docs/uplink-plan.md. All of these are exit 2 at start-up rather than a
    // station that comes up and quietly does not publish, because an operator who has written the
    // block wants to know now, and because the alternative is a permanently silent uplink nobody
    // notices for a month.

    /// <summary>The whole block, valid, for a test to break one thing at a time.</summary>
    private const string PublishBlock = """
          "publish": {
            "url": "wss://monitor.example/uplink",
            "token": "pdnsm_0123456789012345678901234567890123456789",
            "callsign": "GB7RDG-2",
            "operator": "Tom M0LTE",
            "location": "Reading, England"
          }
        """;

    /// <summary>A station with a waterfall and the block above, with one part replaced.</summary>
    private string PublishingStation(string block = PublishBlock, string device = "null") =>
        WriteConfig($$"""
            {
              "device": "{{device}}",
              "waterfall": { "port": 8107 },
            {{block}}
            }
            """);

    [Fact]
    public void A_Valid_Publish_Block_Loads()
    {
        string path = PublishingStation();

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.Publish.Should().NotBeNull();
        config.Publish!.Callsign.Should().Be("GB7RDG-2");
        config.Publish.Frames.Should().Be("always", "frames go up whether or not anybody watches");
        DaemonConfig.PublishedAudioRate(config.Publish, 48000).Should().Be(12000,
            "a 48 kHz station that says nothing gets the 194 kbit/s default, not 770");
        DaemonConfig.PublishedAudioRate(config.Publish, 12000).Should().Be(12000);
    }

    [Fact]
    public void A_Publish_Block_Without_A_Token_Is_A_Configuration_Error()
    {
        string path = PublishingStation("""
              "publish": { "url": "wss://monitor.example/uplink", "callsign": "GB7RDG-2" }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"publish\".\"token\" is missing");
        error.Should().Contain("Ask the site owner",
            "there is no default and no way to generate one locally");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Publish_Token_Too_Short_To_Be_One_Is_A_Configuration_Error()
    {
        string path = PublishingStation("""
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "hunter2",
                "callsign": "GB7RDG-2"
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"publish\".\"token\" is too short");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Publish_Block_Without_A_Callsign_Is_A_Configuration_Error()
    {
        string path = PublishingStation("""
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "pdnsm_0123456789012345678901234567890123456789"
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"publish\".\"callsign\" is missing");
        error.Should().Contain("has to say whose it is",
            "a station on a public page that will not say who it is has no business being there");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Publish_Block_Without_A_Waterfall_Is_A_Configuration_Error()
    {
        string path = WriteConfig($$"""
            {
              "device": "null",
            {{PublishBlock}}
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"publish\" needs a \"waterfall\" section");
        error.Should().Contain("nothing to publish");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Publish_Block_On_A_Web_Receiver_Is_A_Configuration_Error()
    {
        string path = PublishingStation(device: "ubersdr:m9psy-1.instance.ubersdr.org");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("somebody else's public web receiver");
        error.Should().Contain("twice under two names",
            "the sentence says why rather than just refusing");
        error.Should().Contain("daily listening allowance");
        error.Should().Contain("\"deny\"", "and it says where to go instead");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void Publish_And_Monitor_Together_Is_A_Configuration_Error()
    {
        string path = WriteConfig($$"""
            {
              "waterfall": { "port": 8099 },
              "monitor": { "modems": [ { "subChannel": 0, "mode": "afsk1200" } ] },
            {{PublishBlock}}
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("both \"publish\" and \"monitor\"");
        error.Should().Contain("one process is not both");
        ShouldGuideTheOperator(error, path);
    }

    [Theory]
    [InlineData("https://monitor.example/uplink")]
    [InlineData("monitor.example/uplink")]
    [InlineData("")]
    public void A_Publish_Url_That_Is_Not_Ws_Or_Wss_Is_A_Configuration_Error(string url)
    {
        string path = PublishingStation($$"""
              "publish": {
                "url": "{{url}}",
                "token": "pdnsm_0123456789012345678901234567890123456789",
                "callsign": "GB7RDG-2"
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("not an absolute ws or wss URL");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void An_Unencrypted_Uplink_To_Another_Machine_Is_A_Warning_Not_A_Refusal()
    {
        // The shape of a smoke test and the shape of a mistake, and only the operator knows which.
        string path = PublishingStation("""
              "publish": {
                "url": "ws://monitor.example/uplink",
                "token": "pdnsm_0123456789012345678901234567890123456789",
                "callsign": "GB7RDG-2"
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("unencrypted ws to monitor.example");
    }

    [Fact]
    public void An_Audio_Rate_Outside_The_Publishable_Range_Is_A_Configuration_Error()
    {
        string path = PublishingStation("""
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "pdnsm_0123456789012345678901234567890123456789",
                "callsign": "GB7RDG-2",
                "audioRate": 96000
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"publish\".\"audioRate\" is 96000 Hz");
        error.Should().Contain("6000 to 48000");
        ShouldGuideTheOperator(error, path);
    }

    /// <summary>
    /// The audio is decimated to the published rate rather than resampled, so the rate has to
    /// divide the channel's. Checked against the DSP rate, which is settled from the modem set
    /// after any plugins have loaded, so it is its own function rather than part of the load.
    /// </summary>
    [Fact]
    public void An_Audio_Rate_That_Does_Not_Divide_The_Dsp_Rate_Is_A_Configuration_Error()
    {
        var publish = new PublishConfig { AudioRate = 8000 };

        string? at12k = DaemonConfig.PublishRateProblem(publish, 12000);

        at12k.Should().NotBeNull();
        at12k.Should().Contain("8000 Hz").And.Contain("12000 Hz");
        at12k.Should().Contain("integer divisor");
        at12k.Should().Contain("6000, 12000", "the message names the rates that would work");

        DaemonConfig.PublishRateProblem(publish, 48000).Should().BeNull(
            "8000 divides 48000, which is the station a 48 kHz mode makes");
        DaemonConfig.PublishRateProblem(new PublishConfig(), 12000).Should().BeNull(
            "the default is the channel's own rate capped at 12000, which always divides it");
        DaemonConfig.PublishRateProblem(new PublishConfig(), 48000).Should().BeNull();
    }

    [Theory]
    [InlineData("\"callsign\": \"NOT A CALLSIGN AT ALL\"", "not a callsign")]
    [InlineData("\"callsign\": \"GB7RDG-42\"", "not a callsign")]
    [InlineData("\"callsign\": \"GB7RDG-2\", \"operator\": \"forty characters is the limit and this one is longer\"", "the limit is 40")]
    [InlineData("\"callsign\": \"GB7RDG-2\", \"frames\": \"sometimes\"", "\"always\"")]
    [InlineData("\"callsign\": \"GB7RDG-2\", \"site\": \"javascript:alert(1)\"", "absolute http or https URL")]
    public void A_Publish_Field_The_Site_Would_Have_To_Show_Is_Checked_Here(
        string fields, string expected)
    {
        // Everything a station says about itself lands on somebody else's public page, so the
        // caps and the URL check are here, at the point the operator can still fix it, rather
        // than as a truncation or a broken link on the site.
        string path = PublishingStation($$"""
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "pdnsm_0123456789012345678901234567890123456789",
                {{fields}}
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain(expected);
        ShouldGuideTheOperator(error, path);
    }

    /// <summary>
    /// The one refusal that cannot be made while the file is being read still reads like every
    /// other one: an operator should not be able to tell which kind of check stopped them.
    /// </summary>
    [Fact]
    public void A_Refusal_Found_After_The_File_Was_Read_Is_Framed_Like_Every_Other()
    {
        string path = PublishingStation();
        string problem = DaemonConfig.PublishRateProblem(
            new PublishConfig { AudioRate = 7000 }, 12000)!;

        string error = DaemonConfig.ConfigurationError(path, problem);

        error.Should().Contain("7000 Hz", "the sentence itself is still there");
        error.Should().Contain("6000, 12000");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void The_Transmitter_Test_Is_On_By_Default_With_Its_Two_Bounds()
    {
        // There is nothing to switch on: nothing happens until an operator asks for it, and the
        // two numbers here are the only ones that decide what a keyup can cost.
        string path = WriteConfig("""{"device": "null"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.TxTest.Enabled.Should().BeTrue();
        config.TxTest.Seconds.Should().Be(5);
        config.TxTest.MaxSeconds.Should().Be(30);
        config.TxTest.Amplitude.Should().Be(
            0.8, "the modulators' own peak, so the test measures what a frame gets");
    }

    [Fact]
    public void The_Transmitter_Tests_Bounds_Are_Read_From_The_File()
    {
        string path = WriteConfig("""
            {"device": "null", "txTest": {"seconds": 3, "maxSeconds": 10, "amplitude": 0.5}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config!.TxTest.Seconds.Should().Be(3);
        config.TxTest.MaxSeconds.Should().Be(10);
        config.TxTest.Amplitude.Should().Be(0.5);
    }

    [Fact]
    public void A_Typo_Inside_The_Transmitter_Test_Block_Is_Called_Out()
    {
        // A misspelt cap is a cap that is not there, and the setting it was meant to be is the
        // one thing standing between a click and a PA held up for as long as it says.
        string path = WriteConfig("""
            {"device": "null", "txTest": {"maxSec": 10}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("txTest: \"maxSec\"");
        config.TxTest.MaxSeconds.Should().Be(30, "and the default still stands");
    }

    [Fact]
    public void An_Impossible_Transmitter_Test_Level_Stops_Start_Up_With_A_Sentence()
    {
        // Without this the daemon starts, journals "tx test: ready", and then throws on every
        // press - which, since the page discards what Start throws, looks exactly like a button
        // that does nothing. A station that looks configured and is not is the failure here.
        foreach (string bad in new[] { "1.5", "0", "-0.2" })
        {
            string path = WriteConfig(
                "{\"device\": \"null\", \"txTest\": {\"amplitude\": " + bad + "}}");

            DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

            config.Should().BeNull($"an amplitude of {bad} is not a level");
            error.Should().Contain("txTest").And.Contain("amplitude");
            ShouldGuideTheOperator(error, path);
        }
    }

    [Fact]
    public void A_Transmitter_Test_That_Would_Last_No_Time_Stops_Start_Up()
    {
        string path = WriteConfig("""{"device": "null", "txTest": {"seconds": 0}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("txTest").And.Contain("seconds");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Cap_Beyond_The_Ceiling_Is_Accepted_And_Clamped_Rather_Than_Refused()
    {
        // The cap is clamped to 1..60 in force whatever the file says, so an over-large one is
        // not a configuration error - the clamp is the safety property rather than a correction,
        // and refusing to start over a number that cannot do any harm would be the wrong trade.
        string path = WriteConfig("""{"device": "null", "txTest": {"maxSeconds": 3600}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config!.TxTest.MaxSeconds.Should().Be(3600, "the file says what it says");
    }

    [Fact]
    public void A_Typo_Inside_The_Publish_Block_Is_Called_Out()
    {
        string path = PublishingStation("""
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "pdnsm_0123456789012345678901234567890123456789",
                "callsign": "GB7RDG-2",
                "audioRateHz": 6000
              }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("publish: \"audioRateHz\"");
    }
}
