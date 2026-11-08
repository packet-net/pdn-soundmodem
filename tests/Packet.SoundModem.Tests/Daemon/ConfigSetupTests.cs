using System.Net;
using System.Text.Json.Nodes;
using Jint;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The guided first-time setup: when a station counts as never configured, what the station says
/// about it, and the setup flow's steps and what each writes (the page's model, run in Jint).
/// </summary>
public partial class ConfigPageTests
{
    // ---------------------------------------------------------------- never configured

    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, name));
    }

    [Fact]
    public void The_Shipped_Example_Counts_As_Never_Configured()
    {
        // What a fresh install has: packaging/build-deb.sh copies this file to
        // /etc/pdn-soundmodem/soundmodem.json on first install and touches nothing else.
        ConfigSetup.NeverConfigured(RepoFile("soundmodem.example.json")).Should().BeTrue();
    }

    [Fact]
    public void The_Example_Earlier_Packages_Shipped_Still_Counts_As_Never_Configured()
    {
        // Installs seeded before the starter config hold this; their owners never set anything up.
        ConfigSetup.NeverConfigured(ConfigSetup.LegacyExample).Should().BeTrue();
        ConfigSetup.NeverConfigured("{ \"device\": \"default\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw1\" }, \"waterfall\": { \"port\": 8107 } }")
            .Should().BeFalse("one value changed is a station somebody set up");
    }

    private const string FmDone = "{ \"device\": \"default\", \"sideband\": \"fm\", \"kissPort\": 8105, \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" }, \"waterfall\": { \"port\": 8107 } }";

    [Theory]
    [InlineData("", true)]
    [InlineData("{}", true)]
    [InlineData("// only a comment\n{ }", true)]
    // The shipped example, whatever its spacing, comments and the case of its keys.
    [InlineData("{ \"Device\": \"pipe:/var/lib/pdn-soundmodem/starter-air,/var/lib/pdn-soundmodem/starter-air\", \"BIND\": \"*\", \"MODEMS\": [ { \"mode\": \"afsk1200\", \"subChannel\": 0 } ], \"waterfall\": { \"port\": 8107 } /* x */ }", true)]
    [InlineData("{ \"device\": \"pipe:/var/lib/pdn-soundmodem/starter-air,/var/lib/pdn-soundmodem/starter-air\", \"bind\": \"*\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"waterfall\": { \"port\": 8108 } }", false)]
    [InlineData("{ \"Device\": \"default\", \"MODEMS\": [ { \"mode\": \"afsk1200\", \"subChannel\": 0 } ], \"ptt\": { \"device\": \"/dev/hidraw0\", \"type\": \"cm108\" }, \"waterfall\": { \"port\": 8107 } /* x */ }", true)]
    // Nothing else counts: not plumbing alone, not a device alone, not a station that runs.
    [InlineData("{ \"waterfall\": { \"port\": 8107 }, \"kissPort\": 9000, \"bind\": \"*\" }", false)]
    [InlineData("{ \"device\": \"plughw:CARD=Device,DEV=0\" }", false)]
    [InlineData("{ \"device\": \"ubersdr:m9psy-1.instance.ubersdr.org\", \"ubersdr\": { \"mode\": \"iq48\" }, \"publish\": { \"url\": \"wss://monitor.example/\" } }", false)]
    [InlineData("{ \"device\": \"flex:10.0.0.9\", \"flex\": { } }", false)]
    [InlineData("{ \"device\": \"flex:10.0.0.9\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ] }", false)]
    [InlineData("{ \"sideband\": \"fm\", \"device\": \"default\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" }, \"waterfall\": { \"port\": 8107 } }", false)]
    [InlineData(FmDone, false)]
    [InlineData("{ \"modems\": [] }", false)]
    [InlineData("{ \"ptt\": { \"type\": \"serial\", \"device\": \"/dev/ttyUSB0\" } }", false)]
    [InlineData("{ \"monitor\": { } }", false)]
    [InlineData("not json", false)]
    public void Never_Configured_Is_The_Shipped_Example_Or_Empty_And_Nothing_Else(string file, bool expected)
    {
        ConfigSetup.NeverConfigured(file).Should().Be(expected);
    }

    [Fact]
    public void The_Example_Built_Into_The_Station_Is_The_Shipped_File()
    {
        ConfigSetup.NeverConfigured(RepoFile("soundmodem.example.json"), ConfigSetup.ExampleText()).Should().BeTrue();
        ConfigSetup.NeverConfigured(ConfigSetup.ExampleText(), RepoFile("soundmodem.example.json")).Should().BeTrue();
    }

    [Fact]
    public void The_Example_Seam_Says_So_At_Start_Up_And_Says_When_Its_File_Is_Missing()
    {
        ConfigSetup.NoticeFor(null).Should().BeNull();
        ConfigSetup.NoticeFor("").Should().BeNull();
        string file = Path.Combine(_dir, "example.json");
        File.WriteAllText(file, "{}");
        ConfigSetup.NoticeFor(file).Should().Contain(file).And.Contain("not the shipped one");
        ConfigSetup.NoticeFor(file + ".gone").Should().Contain("not there").And.Contain("shipped example");
    }

    [Fact]
    public async Task The_Station_Says_Whether_It_Was_Never_Configured()
    {
        using var configured = new Station(_dir, key: "");
        JsonNode a = JsonNode.Parse(await configured.Client.GetStringAsync(configured.Url("api/config")))!;
        a["setup"]!["neverConfigured"]!.GetValue<bool>().Should().BeFalse("it has a modem");
        a.AsObject().ContainsKey("txTest").Should().BeFalse("the page asks the station page's socket whether a test can run");

        using var fresh = new Station(_dir, key: "", configText: "{ }");
        JsonNode b = JsonNode.Parse(await fresh.Client.GetStringAsync(fresh.Url("api/config")))!;
        b["setup"]!["neverConfigured"]!.GetValue<bool>().Should().BeTrue();
    }

    // ---------------------------------------------------------------- the steps

    private static string Evaluate(string docJson, string script)
    {
        Engine js = Model();
        js.SetValue("docJson", docJson);
        return js.Evaluate($$"""
            (function () {
              var doc = JSON.parse(docJson);
              var M = ConfigModel;
              {{script}}
            })()
            """).AsString();
    }

    [Theory]
    [InlineData("fm", "radio,card,keying,modes,software,try")]
    [InlineData("ssb", "radio,card,keying,modes,software,try")]
    [InlineData("flex", "radio,keying,modes,software,try")]
    [InlineData("ubersdr", "radio,modes,software,try")]
    [InlineData("other", "radio,modes,software,try")]
    public void The_Steps_Skip_What_The_Kind_Of_Radio_Does_Not_Need(string kind, string expected)
    {
        string walked = Evaluate("{}", $$"""
            var out = ["radio"], id = "radio";
            while ((id = M.setupNext("{{kind}}", id)) !== null) out.push(id);
            return out.join(",");
            """);

        walked.Should().Be(expected);
    }

    [Fact]
    public void Going_Back_Skips_The_Same_Steps()
    {
        Evaluate("{}", """return M.setupPrev("flex", "keying") + "," + M.setupPrev("ubersdr", "modes") + "," + M.setupPrev("fm", "radio");""")
            .Should().Be("radio,radio,null");
    }

    [Fact]
    public void A_Fresh_Station_Starts_From_Nothing_And_Keeps_The_Rest()
    {
        string doc = Evaluate(
            """{"device":"default","modems":[{"subChannel":0,"mode":"afsk1200"}],"ptt":{"type":"cm108","device":"/dev/hidraw0"},"waterfall":{"port":8107},"api":{"key":"k"}}""",
            "M.setupClearChoices(doc); return JSON.stringify(doc);");

        doc.Should().Be("""{"waterfall":{"port":8107},"api":{"key":"k"}}""");
    }

    [Fact]
    public void Choosing_FM_Writes_The_Kind_Of_Radio_And_SSB_Takes_It_Out_Again()
    {
        Evaluate("{}", """M.setupPickKind(doc, "fm"); return JSON.stringify(doc);""").Should().Be("""{"sideband":"fm"}""");
        Evaluate("""{"sideband":"fm","device":"plughw:CARD=Device,DEV=0"}""", """M.setupPickKind(doc, "ssb"); return JSON.stringify(doc);""")
            .Should().Be("""{"device":"plughw:CARD=Device,DEV=0"}""", "the card stays; only the FM kind goes");
    }

    [Fact]
    public void Changing_To_A_Flex_Or_A_Web_Receiver_Takes_The_Old_Device_Out_And_Changing_Back_Takes_Theirs_Out()
    {
        Evaluate("""{"device":"plughw:CARD=Device,DEV=0","sideband":"fm"}""", """M.setupPickKind(doc, "flex"); return JSON.stringify(doc);""")
            .Should().Be("{}");
        Evaluate("""{"device":"flex:10.0.0.9","flex":{}}""", """M.setupPickKind(doc, "fm"); return JSON.stringify(doc);""")
            .Should().Be("""{"sideband":"fm"}""");
        Evaluate("""{"device":"ubersdr:x.org","ubersdr":{"mode":"iq48"}}""", """M.setupPickKind(doc, "ubersdr"); return JSON.stringify(doc);""")
            .Should().Be("""{"device":"ubersdr:x.org","ubersdr":{"mode":"iq48"}}""", "the same kind is left alone");
    }

    [Fact]
    public void The_Kind_Is_Read_Back_From_The_Document()
    {
        Evaluate("""{"device":"flex:1.2.3.4"}""", """return M.setupKindOf(doc);""").Should().Be("flex");
        Evaluate("""{"device":"ubersdr:x.org"}""", """return M.setupKindOf(doc);""").Should().Be("ubersdr");
        Evaluate("""{"sideband":"fm"}""", """return M.setupKindOf(doc);""").Should().Be("fm");
        Evaluate("""{"sideband":"lsb"}""", """return M.setupKindOf(doc);""").Should().Be("ssb");
        Evaluate("{}", """return M.setupKindOf(doc);""").Should().Be("ssb");
        foreach (string device in new[] { "openwebrx:http://x", "rtltcp:host", "sdrconnect:host", "iqcard:hw:1", "iqpipe:-" })
        {
            Evaluate($$"""{"device":"{{device}}"}""", """return M.setupKindOf(doc);""").Should().Be("other", device);
        }
    }

    [Fact]
    public void A_Step_Is_Complete_When_It_Has_What_It_Needs()
    {
        string Complete(string doc, string kind, string step, string flags = "{}") =>
            Evaluate(doc, $$"""return String(M.setupComplete(doc, {{kind}}, "{{step}}", {{flags}}));""");

        Complete("{}", "null", "radio").Should().Be("false", "a kind must be chosen");
        Complete("{}", "\"fm\"", "radio").Should().Be("true");
        Complete("{}", "\"flex\"", "radio").Should().Be("false", "a Flex has to be picked as well");
        Complete("""{"device":"flex:1.2.3.4"}""", "\"flex\"", "radio").Should().Be("true");
        Complete("{}", "\"fm\"", "card").Should().Be("false");
        Complete("""{"device":"plughw:CARD=Device,DEV=0"}""", "\"fm\"", "card").Should().Be("true");
        Complete("{}", "\"fm\"", "keying").Should().Be("false", "VOX has to be chosen, not assumed");
        Complete("{}", "\"fm\"", "keying", "{keying: true}").Should().Be("true");
        Complete("{}", "\"fm\"", "modes").Should().Be("false");
        Complete("""{"modems":[{"mode":"afsk1200"}]}""", "\"fm\"", "modes").Should().Be("true");
        Complete("""{"kissPort":0}""", "\"fm\"", "software").Should().Be("false");
        Complete("{}", "\"fm\"", "software").Should().Be("true", "the default port is fine");
        Complete("""{"ardop":{}}""", "\"ssb\"", "modes").Should().Be("true", "an ARDOP-only station has nothing to add");
        Complete("""{"device":"rtltcp:host"}""", "\"other\"", "radio").Should().Be("true", "its device is left as it is");
    }

    [Fact]
    public void On_A_Station_Never_Configured_Who_May_Reach_The_Port_Must_Be_Chosen()
    {
        string Complete(string flags) =>
            Evaluate("{}", $$"""return String(M.setupComplete(doc, "fm", "software", {{flags}}));""");

        Complete("{softwareChosen: false}").Should().Be("false", "the starter's own open bind is not a choice");
        Complete("{softwareChosen: true}").Should().Be("true");
        Complete("{}").Should().Be("true", "a station that was set up keeps what it has");

        Evaluate("""{"device":"x","bind":"*","modems":[]}""", """M.setupClearChoices(doc); return JSON.stringify(doc);""")
            .Should().Be("{}", "the starter's bind is cleared with its other placeholders");
    }

    [Fact]
    public void Choosing_This_Machine_Only_From_Another_Computer_Is_Warned_About()
    {
        string Warn(string doc, string host) => Evaluate(doc, $$"""return M.setupLocalOnlyWarning(doc, "{{host}}");""");

        Warn("{}", "radio1.lan").Should().Contain("another computer").And.Contain("only be reached from the station itself");
        Warn("{}", "10.0.0.5").Should().NotBeEmpty();
        Warn("{}", "localhost").Should().BeEmpty();
        Warn("{}", "127.0.0.1").Should().BeEmpty();
        Warn("""{"bind":"*"}""", "radio1.lan").Should().BeEmpty("the network was chosen");
    }

    [Fact]
    public void The_Access_Key_Is_Written_To_Api_Key_And_A_Made_One_Is_Strong()
    {
        Evaluate("{}", """M.setupWriteKey(doc, " abc "); return JSON.stringify(doc);""").Should().Be("""{"api":{"key":"abc"}}""");
        Evaluate("""{"api":{"key":"abc"}}""", """M.setupWriteKey(doc, ""); return JSON.stringify(doc);""").Should().Be("{}");
        Evaluate("""{"api":{"key":"abc","x":1}}""", """M.setupWriteKey(doc, ""); return JSON.stringify(doc);""").Should().Be("""{"api":{"x":1}}""");

        string key = Evaluate("{}", """return M.setupMakeKey(Array.from({length: 24}, function (_, i) { return (i * 37 + 11) & 255; }));""");
        key.Should().HaveLength(32).And.MatchRegex("^[A-Za-z0-9_-]+$");
        Evaluate("{}", """return M.setupMakeKey(new Array(24).fill(255));""").Should().Be(new string('_', 32));
    }

    [Fact]
    public void The_Software_Step_Writes_The_Port_And_Who_May_Reach_It_And_Says_What_To_Point_At()
    {
        string doc = Evaluate("""{"bind":"127.0.0.1"}""",
            """M.setupWriteSoftware(doc, "9105", true); return JSON.stringify(doc);""");
        doc.Should().Be("""{"bind":"*","kissPort":9105}""");

        Evaluate(doc, """M.setupWriteSoftware(doc, "9105", false); return JSON.stringify(doc);""")
            .Should().Be("""{"kissPort":9105}""", "this machine only is the default, so it is left out");
        Evaluate("""{"bind":"10.0.0.5"}""", """M.setupWriteSoftware(doc, "8105", true); return JSON.stringify(doc);""")
            .Should().Be("""{"bind":"10.0.0.5","kissPort":8105}""", "a bind someone chose is kept");
        Evaluate("{}", """return M.setupWriteSoftware(doc, "70000", true) + JSON.stringify(doc);""")
            .Should().Contain("1 to 65535").And.EndWith("{}", "a bad port writes nothing");

        Evaluate("{}", """return M.setupSoftwareWords(doc, "shack").text;""")
            .Should().Contain("127.0.0.1 port 8105").And.Contain("Only programs on this machine");
        Evaluate("""{"bind":"*","kissPort":9105}""", """return M.setupSoftwareWords(doc, "shack").text;""")
            .Should().Contain("shack port 9105");
        string keyless = Evaluate("{}", """return M.setupNetworkWarning(doc);""");
        keyless.Should().Contain("KISS port").And.Contain("station page").And.Contain("transmit test").And.Contain("settings page")
            .And.Contain("no access key").And.Contain("key the radio").And.Contain("Set an access key");
        Evaluate("""{"api":{"key":"k"}}""", """return M.setupNetworkWarning(doc);""").Should().NotContain("no access key");
    }

    [Fact]
    public void The_Key_Test_Runs_Only_On_What_The_Station_Is_Running_And_Says_Why_Not_In_Plain_Words()
    {
        string State(string loaded, string doc, string tx) => Evaluate(doc, $$"""
            var s = M.keyTestState(JSON.parse('{{loaded}}'), doc, {{tx}});
            return s.ok + "|" + s.why;
            """);

        string ptt = """{"ptt":{"type":"cm108","device":"/dev/hidraw0"}}""";
        State(ptt, ptt, "{available: true}").Should().Be("true|");
        State(ptt, ptt, "{available: false, refusal: 'no PTT'}").Should().StartWith("false|The station cannot key the radio yet. The station says: no PTT.");
        State(ptt, ptt, "null").Should().StartWith("false|Asking the station", "no answer from the page socket yet");
        State("{}", ptt, "{available: true}").Should().Contain("you have changed that").And.Contain("Save and start first");
    }

    [Fact]
    public void The_Summary_Names_The_Card_And_Keeps_The_Device_As_Small_Print()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Build().ToJsonString());
        string row = js.Evaluate("""
            (function () {
              var found = { soundCards: { items: [ { id: "Device", number: 1, name: "USB PnP Sound Device", device: "plughw:CARD=Device,DEV=0" } ] } };
              var doc = {device: "plughw:CARD=Device,DEV=0", modems: [{mode: "afsk1200"}]};
              return JSON.stringify(ConfigModel.setupSummary(JSON.parse(schemaJson), doc, "fm", found)[1]);
            })()
            """).AsString();

        row.Should().Be("""["Sound card","USB PnP Sound Device","plughw:CARD=Device,DEV=0"]""");
    }

    [Fact]
    public void The_Summary_Says_What_Will_Be_Set_Up()
    {
        JsonObject schema = ConfigSchema.Build();
        Engine js = Model();
        js.SetValue("schemaJson", schema.ToJsonString());
        string summary = js.Evaluate("""
            (function () {
              var doc = {device: "plughw:CARD=Device,DEV=0", ptt: {type: "cm108", device: "/dev/hidraw1"},
                modems: [{subChannel: 0, mode: "afsk1200"}], kissPort: 9105, bind: "*"};
              return ConfigModel.setupSummary(JSON.parse(schemaJson), doc, "fm").map(function (r) { return r.join(": "); }).join("\n");
            })()
            """).AsString();

        summary.Should().Contain("Radio: A sound card into an FM radio")
            .And.Contain("Sound card: plughw:CARD=Device,DEV=0")
            .And.Contain("Keying: The sound card's PTT pin, /dev/hidraw1")
            .And.Contain("KISS port 9105, reachable from your network");
        summary.All(c => c is '\n' or (>= ' ' and <= '~')).Should().BeTrue("setup wording is plain ASCII");
    }
}
