using System.Net;
using System.Text.Json.Nodes;
using Jint;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Linux.Tests;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// <c>GET /api/config/discover</c> as the configuration page's other endpoints are served, and
/// what picking a found item writes into the configuration.
/// </summary>
public partial class ConfigPageTests
{
    private static HardwareDiscovery FakeMachine()
    {
        string file = Path.Combine(Directory.CreateTempSubdirectory("pdnsm-discover").FullName, "machine.txt");
        File.WriteAllText(
            file,
            new FakeDeviceTree().Cm108(card: 1, hidraw: 1).Describe()
            + "mixer\tDevice\tMic\tSpeaker\n"
            + "flex\tShack Flex\tFLEX-6400\t1234-5678\t10.45.0.76\n");
        try
        {
            return HardwareFixture.Load(file);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    // ---------------------------------------------------------------- the endpoint

    [Fact]
    public async Task Without_A_Key_The_Discovery_Is_Open_On_The_Stations_Own_Address()
    {
        using var station = new Station(_dir, key: "", discovery: FakeMachine());

        HttpResponseMessage answer = await station.Client.GetAsync(station.Url("api/config/discover"));

        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode found = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!;
        found["soundCards"]!["items"]![0]!["id"]!.GetValue<string>().Should().Be("Device");
        found["flexRadios"]!["items"]![0]!["device"]!.GetValue<string>().Should().Be("flex:10.45.0.76");
    }

    [Fact]
    public async Task With_A_Key_The_Discovery_Wants_It()
    {
        using var station = new Station(_dir, key: "sekrit", discovery: FakeMachine());

        (await station.Client.GetAsync(station.Url("api/config/discover"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");
        (await station.Client.GetAsync(station.Url("api/config/discover"))).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_A_Key_A_Request_For_Another_Name_Is_Refused()
    {
        // The rebinding check /api/config has. A listener on every address accepts any Host
        // header, which is the situation the check is for.
        string configPath = Path.Combine(_dir, "rebind.json");
        File.WriteAllText(configPath, "{}");
        var api = new ConfigApi(
            "", configPath, Path.Combine(_dir, "rebind-pending.json"),
            runningJson: () => "{}", ephemeralInForce: false, requestRestart: () => { });
        api.ServeDiscovery(FakeMachine());
        int port = FreePorts.Next();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        Task serving = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync();
            if (!await api.HandleAsync(context, context.Request.Url!.AbsolutePath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/config/discover");
        request.Headers.Host = $"evil.example:{port}";
        HttpResponseMessage answer = await client.SendAsync(request);
        await serving;
        listener.Close();

        answer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_Public_Page_With_No_Key_Serves_No_Discovery()
    {
        using var station = new Station(_dir, key: "", openConfiguration: false, discovery: FakeMachine());

        (await station.Client.GetAsync(station.Url("api/config/discover"))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Discovery_Is_Only_Read_With_A_Get()
    {
        using var station = new Station(_dir, key: "", discovery: FakeMachine());
        string before = File.ReadAllText(station.ConfigPath);

        (await station.Client.PostAsync(station.Url("api/config/discover"), new StringContent("{}")))
            .StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        File.ReadAllText(station.ConfigPath).Should().Be(before);
        station.Restarts.Should().Be(0);
    }

    // ---------------------------------------------------------------- what a pick writes

    private const string FoundJson = """
        {
          "soundCards": { "problem": null, "items": [
            { "id": "Device", "number": 1, "name": "USB PnP Sound Device", "device": "plughw:CARD=Device,DEV=0",
              "capture": true, "playback": true, "kind": "cm108", "cm108": true, "hidraw": "/dev/hidraw1",
              "captureControls": ["Mic"], "playbackControls": ["Speaker"] },
            { "id": "PCH", "number": 2, "name": "HDA Intel PCH", "device": "plughw:CARD=PCH,DEV=0",
              "capture": true, "playback": true, "kind": "soundcard", "cm108": false, "hidraw": null,
              "captureControls": [], "playbackControls": [] } ] },
          "hidDevices": { "problem": null, "items": [
            { "path": "/dev/hidraw1", "name": "USB PnP Sound Device", "cm108": true, "canOpen": true, "soundCard": "Device" },
            { "path": "/dev/hidraw4", "name": "Keyboard", "cm108": false, "canOpen": true, "soundCard": null } ] },
          "serialPorts": { "problem": null, "items": [
            { "path": "/dev/serial/by-id/usb-FTDI-if00", "node": "/dev/ttyUSB0", "name": "FTDI FT232R", "usb": true, "canOpen": true, "soundCard": null },
            { "path": "/dev/ttyUSB1", "node": "/dev/ttyUSB1", "name": "USB Serial", "usb": true, "canOpen": false, "soundCard": null } ] },
          "rigctld": { "address": "localhost:4532", "answering": false, "problem": null },
          "flexRadios": { "problem": null, "items": [
            { "name": "Shack Flex", "model": "FLEX-6400", "serial": "1234-5678", "ip": "10.45.0.76", "device": "flex:10.45.0.76" } ] },
          "uberSdr": { "problem": null, "items": [
            { "host": "m9psy-1.instance.ubersdr.org", "device": "ubersdr:m9psy-1.instance.ubersdr.org", "name": "M9PSY", "location": "Reading", "free": true } ] }
        }
        """;

    /// <summary>Runs a snippet of the page's model with <c>found</c> and <c>doc</c> in scope, and
    /// gives back <c>doc</c> as JSON.</summary>
    private static JsonNode Picked(string docJson, string script)
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);
        js.SetValue("docJson", docJson);
        string answer = js.Evaluate($$"""
            (function () {
              var found = JSON.parse(foundJson), doc = JSON.parse(docJson);
              var M = ConfigModel;
              {{script}}
              return JSON.stringify(doc);
            })()
            """).AsString();
        return JsonNode.Parse(answer)!;
    }

    [Fact]
    public void Picking_A_Sound_Card_Fills_The_Device_With_The_Right_String_And_Keeps_The_Rest()
    {
        JsonNode doc = Picked("""{"device": "default", "ptt": {"type": "cm108", "device": "/dev/hidraw0"}}""", """
            M.pickRadio(doc, M.radioChoices(found)[0]);
            """);

        doc["device"]!.GetValue<string>().Should().Be("plughw:CARD=Device,DEV=0");
        doc["ptt"]!["device"]!.GetValue<string>().Should().Be("/dev/hidraw0", "picking audio does not touch keying");
    }

    [Fact]
    public void The_Cards_Offered_Say_What_They_Are_And_Their_Mixer_Controls()
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);

        string text = js.Evaluate("""
            (function () {
              var c = ConfigModel.radioChoices(JSON.parse(foundJson));
              return c.map(function (x) { return x.kind + "|" + x.title + "|" + x.detail; }).join("\n");
            })()
            """).AsString();

        text.Should().Contain("soundcard|USB PnP Sound Device|plughw:CARD=Device,DEV=0, a CM108 interface, which can key the radio too. Mixer: record Mic; play Speaker.");
        text.Should().Contain("flex|FlexRadio Shack Flex|FLEX-6400 at 10.45.0.76");
    }

    [Fact]
    public void Picking_A_Flex_Fills_The_Device_And_The_Section_And_Takes_Out_What_It_Refuses()
    {
        JsonNode doc = Picked("""
            {"device": "default", "ptt": {"type": "cm108"}, "rig": {"rigctld": "localhost:4532"},
             "alsa": {"mixer": {"captureGainDb": 3}}, "kissPort": 8105}
            """, """
            M.pickRadio(doc, M.radioChoices(found)[2]);
            """);

        doc["device"]!.GetValue<string>().Should().Be("flex:10.45.0.76");
        doc["flex"].Should().NotBeNull();
        doc.AsObject().ContainsKey("ptt").Should().BeFalse("a Flex keys itself and ptt is refused beside it");
        doc.AsObject().ContainsKey("rig").Should().BeFalse();
        doc.AsObject().ContainsKey("alsa").Should().BeFalse("the mixer is refused with a Flex, and nothing else was in the section");
        doc["kissPort"]!.GetValue<int>().Should().Be(8105);
    }

    [Fact]
    public void Picking_An_Ubersdr_Fills_The_Device_And_The_Section_And_Keeps_One_That_Is_There()
    {
        JsonNode fresh = Picked("""{"device": "default"}""", "M.pickRadio(doc, M.uberChoices(found)[0]);");
        fresh["device"]!.GetValue<string>().Should().Be("ubersdr:m9psy-1.instance.ubersdr.org");
        fresh["ubersdr"]!["mode"]!.GetValue<string>().Should().Be("iq48");

        JsonNode kept = Picked("""{"ubersdr": {"mode": "iq96", "gain": 2}}""", "M.pickRadio(doc, M.uberChoices(found)[0]);");
        kept["ubersdr"]!["mode"]!.GetValue<string>().Should().Be("iq96", "the operator's own section is not overwritten");
    }

    [Fact]
    public void The_Cm108_Choice_Is_The_Card_You_Picked_And_Writes_The_Device_With_Gpio_3_As_The_Default()
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);
        js.Evaluate("""
            var found = JSON.parse(foundJson);
            var doc = { device: "plughw:CARD=Device,DEV=0" };
            var choices = ConfigModel.keyingChoices(found, doc);
            """);

        js.Evaluate("choices.filter(function (c) { return c.type === 'cm108'; }).length").AsNumber().Should().Be(1, "the keyboard is not a CM108");
        js.Evaluate("choices[0].detail").AsString().Should().Contain("the same card you picked for audio").And.Contain("/dev/hidraw1");

        JsonNode doc = Picked("""{"device": "plughw:CARD=Device,DEV=0"}""", """
            M.pickKeying(doc, M.keyingChoices(found, doc)[0], {});
            """);
        doc["ptt"]!["type"]!.GetValue<string>().Should().Be("cm108");
        doc["ptt"]!["device"]!.GetValue<string>().Should().Be("/dev/hidraw1");
        doc["ptt"]!.AsObject().ContainsKey("gpio").Should().BeFalse("3 is the default");

        JsonNode other = Picked("""{}""", "M.pickKeying(doc, M.keyingChoices(found, doc)[0], { gpio: 1 });");
        other["ptt"]!["gpio"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void A_Serial_Port_Choice_Writes_The_Stable_Path_And_The_Line()
    {
        JsonNode rts = Picked("{}", """
            var c = M.keyingChoices(found, doc).filter(function (x) { return x.type === "serial"; });
            M.pickKeying(doc, c[0], {});
            """);
        rts["ptt"]!.ToJsonString().Should().Be("""{"type":"serial","device":"/dev/serial/by-id/usb-FTDI-if00"}""");

        JsonNode dtr = Picked("{}", """
            var c = M.keyingChoices(found, doc).filter(function (x) { return x.type === "serial"; });
            M.pickKeying(doc, c[1], { line: "dtr" });
            """);
        dtr["ptt"]!.ToJsonString().Should().Be("""{"type":"serial","device":"/dev/ttyUSB1","line":"dtr"}""");
    }

    [Fact]
    public void Choosing_Rigctld_Sets_The_Type_And_Adds_The_Rig_Section_It_Is_Refused_Without()
    {
        JsonNode doc = Picked("""{"ptt": {"type": "cm108", "device": "/dev/hidraw1"}}""", """
            var c = M.keyingChoices(found, doc).filter(function (x) { return x.type === "rigctld"; })[0];
            M.pickKeying(doc, c, {});
            """);
        doc["ptt"]!.ToJsonString().Should().Be("""{"type":"rigctld"}""");
        doc["rig"]!["rigctld"]!.GetValue<string>().Should().Be("localhost:4532");

        JsonNode own = Picked("""{"rig": {"rigctld": "10.0.0.5:4532", "mode": "PKTUSB"}}""", """
            M.pickKeying(doc, M.keyingChoices(found, doc).filter(function (x) { return x.type === "rigctld"; })[0], {});
            """);
        own["rig"]!["rigctld"]!.GetValue<string>().Should().Be("10.0.0.5:4532", "the operator's rig section is kept");
    }

    [Fact]
    public void Choosing_Vox_Takes_The_Ptt_Section_Out()
    {
        JsonNode doc = Picked("""{"ptt": {"type": "serial", "device": "/dev/ttyUSB0"}, "kissPort": 8105}""", """
            M.pickKeying(doc, M.keyingChoices(found, doc).filter(function (x) { return x.type === "vox"; })[0], {});
            """);

        doc.AsObject().ContainsKey("ptt").Should().BeFalse();
        doc["kissPort"]!.GetValue<int>().Should().Be(8105);
    }

    [Fact]
    public void The_Keying_Choice_That_Is_Picked_Follows_The_Ptt_Section_And_Is_None_When_It_Was_Typed_By_Hand()
    {
        string Which(string docJson) => Picked(docJson, """
            doc = { picked: M.keyingPicked(M.keyingChoices(found, doc), doc) };
            """)["picked"]?.GetValue<string>() ?? "none";

        Which("{}").Should().Be("ptt-vox");
        Which("""{"ptt": {"type": "cm108", "device": "/dev/hidraw1"}}""").Should().Be("ptt-cm108-hidraw1");
        Which("""{"ptt": {"type": "serial", "device": "/dev/ttyUSB1"}}""").Should().Be("ptt-serial-ttyUSB1");
        Which("""{"ptt": {"type": "rigctld"}}""").Should().Be("ptt-rigctld");
        Which("""{"ptt": {"type": "serial", "device": "/dev/ttyS7"}}""").Should().Be("none");
    }

    [Fact]
    public void A_Rigctld_That_Answers_Is_Said_To_And_One_That_Does_Not_Is_Still_Offered()
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);
        string silent = js.Evaluate("""
            (function () {
              var f = JSON.parse(foundJson);
              var a = ConfigModel.keyingChoices(f, {}).filter(function (c) { return c.type === "rigctld"; })[0].detail;
              f.rigctld.answering = true;
              var b = ConfigModel.keyingChoices(f, {}).filter(function (c) { return c.type === "rigctld"; })[0].detail;
              return a + "||" + b;
            })()
            """).AsString();

        silent.Should().Be("Nothing is answering on localhost:4532 now. Choose this if you start rigctld yourself.||Answering on localhost:4532 now.");
    }

    [Fact]
    public void Picking_An_Ubersdr_Also_Takes_Out_Everything_The_Reference_Refuses_Beside_It_And_Says_So()
    {
        const string before = """
            {"device": "plughw:CARD=Device,DEV=0", "captureDevice": "plughw:CARD=A,DEV=0", "playbackDevice": "plughw:CARD=B,DEV=0",
             "sideband": "fm", "publish": {"url": "x"}, "ptt": {"type": "serial"},
             "modems": [ {"mode": "bpsk300"}, {"mode": "bpsk300", "subChannel": 1, "identify": {"callsign": "M0LTE"}} ]}
            """;
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);
        js.SetValue("docJson", before);
        string answer = js.Evaluate("""
            (function () {
              var M = ConfigModel, found = JSON.parse(foundJson), doc = JSON.parse(docJson);
              var c = M.uberChoices(found)[0];
              var note = M.radioNote(doc, c);
              var removed = M.pickRadio(doc, c);
              return JSON.stringify({ note: note, removed: removed, doc: doc });
            })()
            """).AsString();
        JsonNode result = JsonNode.Parse(answer)!;
        JsonObject doc = result["doc"]!.AsObject();

        foreach (string gone in new[] { "captureDevice", "playbackDevice", "sideband", "publish", "ptt" })
        {
            doc.ContainsKey(gone).Should().BeFalse(gone);
        }

        doc["modems"]![1]!.AsObject().ContainsKey("identify").Should().BeFalse();
        doc["modems"]![1]!["subChannel"]!.GetValue<int>().Should().Be(1, "only the identify went");
        doc["device"]!.GetValue<string>().Should().Be("ubersdr:m9psy-1.instance.ubersdr.org");
        result["note"]!.GetValue<string>().Should().StartWith("Choosing this removes:")
            .And.Contain("publishing to the monitor").And.Contain("identification on modem 2");
        result["removed"]!.AsArray().Should().HaveCount(6);
    }

    [Fact]
    public void Picking_A_Flex_Takes_Out_The_Separate_Cards_But_Keeps_Publish_And_Identify()
    {
        JsonNode doc = Picked("""
            {"captureDevice": "plughw:CARD=A,DEV=0", "playbackDevice": "plughw:CARD=B,DEV=0", "publish": {"url": "x"},
             "modems": [ {"mode": "bpsk300", "identify": {"callsign": "M0LTE"}} ]}
            """, "M.pickRadio(doc, M.radioChoices(found)[2]);");

        doc.AsObject().ContainsKey("captureDevice").Should().BeFalse();
        doc.AsObject().ContainsKey("playbackDevice").Should().BeFalse();
        doc.AsObject().ContainsKey("publish").Should().BeTrue("a Flex may publish");
        doc["modems"]![0]!.AsObject().ContainsKey("identify").Should().BeTrue();
    }

    [Fact]
    public void Picking_A_Card_While_A_Separate_Card_Is_Set_Says_That_Direction_Stays_Where_It_Is()
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);

        string note = js.Evaluate("""
            (function () {
              var M = ConfigModel, found = JSON.parse(foundJson);
              var c = M.radioChoices(found)[0];
              var plain = M.radioNote({ device: "default" }, c);
              var split = M.radioNote({ device: "default", captureDevice: "plughw:CARD=PCH,DEV=0" }, c);
              return plain + "||" + split;
            })()
            """).AsString();

        note.Should().StartWith("||This sets the main audio device only: receive stays on plughw:CARD=PCH,DEV=0.");
    }

    [Fact]
    public void The_Cm108_Choice_Warns_That_The_Hidraw_Number_Can_Move()
    {
        Engine js = Model();
        js.SetValue("foundJson", FoundJson);

        js.Evaluate("ConfigModel.keyingChoices(JSON.parse(foundJson), {})[0].detail").AsString()
            .Should().Contain("/dev/hidrawN can change after a replug or a reboot");
    }
}
