using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The operator-facing half of configuration loading. These messages are what an admin reads in
/// `journalctl` after the service refuses to start, so they are worth pinning: every failure must
/// name the file, say what is wrong in words, and say what to do about it — never surface a raw
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
        // Counted from 1, the way an editor does — System.Text.Json counts from 0.
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
        // System.Text.Json drops unknown members without a word, so a typo — or a setting that
        // has since been withdrawn — would look accepted and do nothing.
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
        // dial chosen for the others happened to put it — silently, and differently each time
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

    [Fact]
    public void A_Bind_That_Is_Not_An_Address_Is_Rejected()
    {
        string path = WriteConfig("""{"device": "null", "bind": "everywhere"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("\"bind\"").And.Contain("127.0.0.1");
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
}
