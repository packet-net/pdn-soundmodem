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

    [Theory]
    [InlineData("", true)]
    [InlineData("{}", true)]
    [InlineData("// only a comment\n{ }", true)]
    [InlineData("{ \"waterfall\": { \"port\": 8107 }, \"kissPort\": 9000, \"bind\": \"*\" }", true)]
    [InlineData("{ \"device\": \"pipe:/tmp/a,/tmp/a\", \"waterfall\": { \"port\": 8107 } }", true)]
    [InlineData("{ \"modems\": [] }", true)]
    [InlineData("{ \"device\": \"default\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" } }", true)]
    [InlineData("{ \"Device\": \"default\", \"Modems\": [ { \"mode\": \"AFSK1200\" } ], \"PTT\": { \"type\": \"CM108\", \"device\": \"/dev/hidraw0\" }, \"waterfall\": { \"port\": 8108 } }", true)]
    [InlineData("{ \"device\": \"plughw:CARD=Device,DEV=0\", \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" } }", false)]
    [InlineData("{ \"modems\": [ { \"subChannel\": 0, \"mode\": \"bpsk300\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" } }", false)]
    [InlineData("{ \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw1\" } }", false)]
    [InlineData("{ \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" }, { \"subChannel\": 1, \"mode\": \"afsk1200\" } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" } }", false)]
    [InlineData("{ \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\", \"frequency\": 1700 } ], \"ptt\": { \"type\": \"cm108\", \"device\": \"/dev/hidraw0\" } }", false)]
    [InlineData("{ \"modems\": [ { \"subChannel\": 0, \"mode\": \"afsk1200\" } ] }", false)]
    [InlineData("{ \"ptt\": { \"type\": \"serial\", \"device\": \"/dev/ttyUSB0\" } }", false)]
    [InlineData("{ \"rig\": { \"rigctld\": \"localhost:4532\" } }", false)]
    [InlineData("{ \"monitor\": { } }", false)]
    [InlineData("not json", false)]
    public void Never_Configured_Is_Decided_By_The_Choices_And_Nothing_Else(string file, bool expected)
    {
        ConfigSetup.NeverConfigured(file).Should().Be(expected);
    }

    [Fact]
    public async Task The_Station_Says_Whether_It_Was_Never_Configured_And_Whether_It_Could_Key()
    {
        using var configured = new Station(_dir, key: "");
        JsonNode a = JsonNode.Parse(await configured.Client.GetStringAsync(configured.Url("api/config")))!;
        a["setup"]!["neverConfigured"]!.GetValue<bool>().Should().BeFalse("it has a modem");
        a["txTest"]!["available"]!.GetValue<bool>().Should().BeFalse("no transmitter test is served here");
        a["txTest"]!["refusal"]!.GetValue<string>().Should().Contain("no transmitter");

        using var fresh = new Station(_dir, key: "", configText: "{ \"device\": \"null\" }");
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
            .Should().Contain("shack port 9105").And.Contain("keep it on a network you trust");
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
        State(ptt, ptt, "null").Should().StartWith("false|The station cannot key the radio yet.");
        State("{}", ptt, "{available: true}").Should().Contain("you have changed that").And.Contain("Save and start first");
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
