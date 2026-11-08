using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jint;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The plan endpoint behind the configuration page's band strip, the plain words for every mode,
/// and the arithmetic the strip and the modem cards do.
/// </summary>
public partial class ConfigPageTests
{
    // ---------------------------------------------------------------- a label for every mode

    [Fact]
    public void Every_Catalogue_Mode_Has_A_Plain_Label_And_A_Group()
    {
        var missing = new List<string>();
        foreach (string mode in ModemCatalog.KnownModes)
        {
            ModeLabels.Label? label = ModeLabels.For(mode);
            if (label is null || label.Name.Length == 0 || label.TalksTo.Length == 0
                || !ModeLabels.Groups.Any(g => g.Id == label.Group))
            {
                missing.Add(mode);
            }
        }

        missing.Should().BeEmpty(
            "every mode needs a name, a sentence for what it talks to and a group of the picker "
            + "in ModeLabels; missing: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void No_Label_Is_Kept_For_A_Mode_That_Is_Gone()
    {
        HashSet<string> known = [.. ModemCatalog.KnownModes, "ardop"];

        ModeLabels.Table.Keys.Where(m => !known.Contains(m)).Should().BeEmpty(
            "a label for a mode the catalogue no longer has");
        ModeLabels.For("ardop").Should().NotBeNull("ARDOP is not a catalogue mode but is in the picker");
    }

    [Fact]
    public void Mode_Labels_Are_Plain_Ascii_Sentences_With_No_Dashes_Of_The_Long_Kind()
    {
        foreach ((string mode, ModeLabels.Label label) in ModeLabels.Table)
        {
            foreach (string text in new[] { label.Name, label.TalksTo })
            {
                text.All(c => c is >= ' ' and <= '~').Should().BeTrue($"{mode}: \"{text}\" is not plain ASCII");
            }
        }

        foreach (ModeLabels.Group group in ModeLabels.Groups)
        {
            (group.Title + group.Blurb).All(c => c is >= ' ' and <= '~').Should().BeTrue(group.Id);
        }
    }

    [Fact]
    public void Every_Group_Of_The_Picker_Has_Modes_And_Every_Nino_Mode_Number_Is_Used_Once()
    {
        var schema = ConfigSchema.Build();
        JsonArray list = schema["modes"]!["list"]!.AsArray();

        foreach (ModeLabels.Group group in ModeLabels.Groups.Where(g => g.Id != ModeLabels.NinoGroup))
        {
            list.Count(m => m!["group"]!.GetValue<string>() == group.Id).Should().BeGreaterThan(0, group.Id);
        }

        int[] numbers = [.. list.Where(m => m!["ninoMode"] is not null).Select(m => m!["ninoMode"]!.GetValue<int>())];
        numbers.Should().OnlyHaveUniqueItems("a NinoTNC mode number names one mode");
        numbers.Should().OnlyContain(n => n >= 0 && n <= 15);
    }

    [Fact]
    public void The_Nino_Numbers_Are_The_Switch_Settings_The_Modes_Page_Gives()
    {
        // docs/05-modes.md writes the setting in binary in the "Interops with" column.
        var expected = new Dictionary<string, string>
        {
            ["fsk9600"] = "0000", ["c4fsk19200"] = "0001", ["fsk9600-il2p"] = "0010", ["c4fsk9600"] = "0011",
            ["fsk4800-il2p"] = "0100", ["qpsk3600"] = "0101", ["afsk1200-il2p"] = "0111", ["bpsk300"] = "1000",
            ["qpsk600"] = "1001", ["bpsk1200"] = "1010", ["qpsk2400"] = "1011", ["afsk300"] = "1100",
            ["afsk300-il2p"] = "1101", ["afsk300-il2pc"] = "1110",
        };
        string modes = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "05-modes.md"));

        foreach ((string mode, string bits) in expected)
        {
            ModeLabels.For(mode)!.NinoMode.Should().Be(Convert.ToInt32(bits, 2), mode);
            string row = modes.Split('\n').Single(l => l.StartsWith($"| `{mode}` |", StringComparison.Ordinal));
            row.Should().Contain($"NinoTNC ({bits})", $"the row of {mode} gives {bits}");
        }

        // Every "mode N (bits)" a label says is its own number, in the form the docs give.
        foreach ((string mode, ModeLabels.Label label) in ModeLabels.Table)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         label.TalksTo, @"mode (\d+) \((\d{4})\)"))
            {
                label.NinoMode.Should().NotBeNull($"{mode} names a NinoTNC mode number");
                Convert.ToInt32(m.Groups[2].Value, 2).Should().Be(int.Parse(m.Groups[1].Value), mode);
                Convert.ToInt32(m.Groups[2].Value, 2).Should().Be(label.NinoMode!.Value, mode);
            }
        }

        ModeLabels.Table.Where(t => t.Value.NinoMode is not null).Select(t => t.Key)
            .Should().BeEquivalentTo(expected.Keys, "a mode with a number is one the page documents with one");
    }

    // ---------------------------------------------------------------- the plan endpoint

    private const string DialStation = """
        {
          "device": "null",
          "sideband": "usb",
          "modems": [
            { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 },
            { "subChannel": 1, "mode": "qpsk600", "rfFrequency": 7051600 }
          ]
        }
        """;

    private static async Task<JsonNode> PlanAsync(Station station, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        HttpResponseMessage answer = await station.Client.PostAsync(
            station.Url("api/config/plan"), new StringContent(json, Encoding.UTF8, "application/json"));
        string text = await answer.Content.ReadAsStringAsync();
        answer.StatusCode.Should().Be(status, text);
        return status == HttpStatusCode.OK ? JsonNode.Parse(text)! : JsonValue.Create(text)!;
    }

    [Fact]
    public async Task A_Plan_Gives_The_Dial_The_Passband_And_Each_Modems_Place()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, DialStation);

        plan["ok"]!.GetValue<bool>().Should().BeTrue();
        plan["radio"]!.GetValue<string>().Should().Be("usb");
        plan["axis"]!.GetValue<string>().Should().Be("rf");
        double dial = plan["dialHz"]!.GetValue<double>();
        plan["passband"]!["audioLowHz"]!.GetValue<double>().Should().Be(300);
        plan["passband"]!["audioHighHz"]!.GetValue<double>().Should().Be(2700);
        plan["passband"]!["rfLowHz"]!.GetValue<double>().Should().Be(dial + 300);
        plan["passband"]!["rfHighHz"]!.GetValue<double>().Should().Be(dial + 2700);
        plan["warnings"]!.AsArray().Should().BeEmpty();

        JsonArray modems = plan["modems"]!.AsArray();
        modems.Should().HaveCount(2);
        foreach (JsonNode? m in modems)
        {
            double rf = m!["rfHz"]!.GetValue<double>();
            m["audioCentreHz"]!.GetValue<double>().Should().BeApproximately(rf - dial, 0.01, "upper sideband");
            m["widthHz"]!.GetValue<double>().Should().BeGreaterThan(0);
            m["audioHighHz"]!.GetValue<double>().Should().BeApproximately(m["audioCentreHz"]!.GetValue<double>() + (m["widthHz"]!.GetValue<double>() / 2), 0.01);
            m["rfLowHz"]!.GetValue<double>().Should().BeApproximately(rf - (m["widthHz"]!.GetValue<double>() / 2), 0.01);
        }

        modems[0]!["rfHz"]!.GetValue<double>().Should().Be(7050800);
        modems[1]!["rfHz"]!.GetValue<double>().Should().Be(7051600);
    }

    [Fact]
    public void A_Plan_Is_The_Plan_The_Station_Would_Start_On()
    {
        // The strip must show what start-up does, so the dial and the audio centres are those of
        // the same planner, called the way the daemon calls it.
        string temp = Path.Combine(_dir, "plan-direct.json");
        File.WriteAllText(temp, DialStation);
        DaemonConfig config = DaemonConfig.TryLoad(temp, out _, null)!;
        RfPlan.Result direct = ConfigApi.PlanFor(config)!;

        JsonNode served = ConfigPlan.Describe(
            DaemonConfig.TryLoad(temp, out _, null)!);

        served["dialHz"]!.GetValue<double>().Should().Be(direct.DialHz);
        served["modems"]!.AsArray().Select(m => m!["audioCentreHz"]!.GetValue<double>())
            .Should().Equal(config.Modems.Select(m => m.Frequency!.Value),
                "BandPlanner writes the audio centre back into the modem, which is what start-up runs");
    }

    [Fact]
    public async Task A_Pinned_Dial_Is_Kept_And_A_Modem_Past_The_Edge_Is_Said_In_Words()
    {
        using var station = new Station(_dir, key: "");

        // The dial is pinned at 7.050000, so the passband is 7.050300 to 7.052700. This modem is
        // centred 2.7 kHz up, so about half of its 300 baud width is past the top edge.
        JsonNode plan = await PlanAsync(station, """
            {
              "device": "null", "sideband": "usb", "dialFrequency": 7050000,
              "modems": [ { "subChannel": 0, "mode": "afsk300", "rfFrequency": 7052700 } ]
            }
            """);

        plan["dialHz"]!.GetValue<double>().Should().Be(7050000);
        JsonArray warnings = plan["warnings"]!.AsArray();
        warnings.Should().HaveCount(1);
        warnings[0]!["kind"]!.GetValue<string>().Should().Be("outside");
        warnings[0]!["subChannel"]!.GetValue<int>().Should().Be(0);
        string text = warnings[0]!["text"]!.GetValue<string>();
        text.Should().Contain("Its top").And.Contain("falls outside what the radio hears");
        text.Should().MatchRegex(@"Move it to 7\.0\d{5} MHz");
        text.Should().Contain("Radio and audio page");
    }

    [Fact]
    public async Task Modems_That_Share_Part_Of_The_Band_Are_Each_Told_So()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, """
            {
              "device": "null", "sideband": "usb", "dialFrequency": 7050000,
              "modems": [
                { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7051500 },
                { "subChannel": 3, "mode": "bpsk300", "rfFrequency": 7051600 }
              ]
            }
            """);

        JsonArray warnings = plan["warnings"]!.AsArray();
        warnings.Select(w => w!["kind"]!.GetValue<string>()).Should().OnlyContain(k => k == "overlap");
        warnings.Select(w => w!["subChannel"]!.GetValue<int>()).Should().BeEquivalentTo(new[] { 0, 3 });
        warnings.Single(w => w!["subChannel"]!.GetValue<int>() == 0)!["text"]!.GetValue<string>()
            .Should().Contain("sub-channel 3");
    }

    [Fact]
    public async Task On_Lower_Sideband_The_Audio_Runs_The_Other_Way()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, """
            {
              "device": "null", "sideband": "lsb", "dialFrequency": 7050000,
              "modems": [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7048500 } ]
            }
            """);

        plan["radio"]!.GetValue<string>().Should().Be("lsb");
        plan["modems"]![0]!["audioCentreHz"]!.GetValue<double>().Should().Be(1500);
        plan["passband"]!["rfLowHz"]!.GetValue<double>().Should().Be(7050000 - 2700);
        plan["passband"]!["rfHighHz"]!.GetValue<double>().Should().Be(7050000 - 300);
    }

    [Fact]
    public async Task On_FM_There_Is_No_Dial_And_The_Picture_Is_Of_The_Audio_Band()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, """
            {
              "device": "null", "sideband": "fm",
              "modems": [
                { "subChannel": 0, "mode": "afsk1200", "frequency": 1700 },
                { "subChannel": 1, "mode": "fsk9600" }
              ]
            }
            """);

        plan["radio"]!.GetValue<string>().Should().Be("fm");
        plan["axis"]!.GetValue<string>().Should().Be("audio");
        plan["hasDial"]!.GetValue<bool>().Should().BeFalse();
        plan["passband"].Should().BeNull("the daemon cannot know an FM radio's audio path");
        JsonArray modems = plan["modems"]!.AsArray();
        modems[0]!["audioCentreHz"]!.GetValue<double>().Should().Be(1700);
        modems[1]!["audioCentreHz"].Should().BeNull("a baseband mode has no centre");
        modems[1]!["audioLowHz"]!.GetValue<double>().Should().Be(0);
        modems[1]!["tunable"]!.GetValue<bool>().Should().BeFalse();
        modems[1]!["widthHz"]!.GetValue<double>().Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task A_Station_Placed_In_Audio_Terms_Gets_The_Audio_Passband()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, """
            { "device": "null", "modems": [ { "subChannel": 0, "mode": "bpsk300", "frequency": 1600 } ] }
            """);

        plan["axis"]!.GetValue<string>().Should().Be("audio");
        plan["dialHz"].Should().BeNull();
        plan["passband"]!["audioLowHz"]!.GetValue<double>().Should().Be(300);
        plan["modems"]![0]!["audioCentreHz"]!.GetValue<double>().Should().Be(1600);
        plan["modems"]![0]!["rfHz"].Should().BeNull();
    }

    [Fact]
    public async Task A_Modem_With_No_Frequency_Is_Planned_At_The_Modes_Own_Centre()
    {
        using var station = new Station(_dir, key: "");

        JsonNode plan = await PlanAsync(station, """
            { "device": "null", "modems": [ { "subChannel": 0, "mode": "afsk1200" }, { "subChannel": 1, "mode": "ardop" } ] }
            """);

        plan["modems"]![0]!["audioCentreHz"]!.GetValue<double>().Should().Be(1700);
        plan["modems"]![1]!["audioCentreHz"]!.GetValue<double>().Should().Be(1500, "ARDOP's native centre");
        plan["modems"]![1]!["widthHz"]!.GetValue<double>().Should().Be(2000, "ARDOP is planned for its widest session");
    }

    [Fact]
    public async Task A_Plan_The_Station_Would_Refuse_Is_Still_Drawn_With_The_Reason()
    {
        using var station = new Station(_dir, key: "");

        // Two fixed-width modems asked for a dial apart from each other by far more than one
        // passband: the planner refuses to find a dial for them when the dial is the station's to choose.
        JsonNode plan = await PlanAsync(station, """
            {
              "device": "null", "sideband": "usb",
              "modems": [
                { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050000 },
                { "subChannel": 1, "mode": "bpsk300", "rfFrequency": 7090000 }
              ]
            }
            """);

        plan["modems"]!.AsArray().Should().HaveCount(2);
        if (!plan["ok"]!.GetValue<bool>())
        {
            plan["problem"]!.GetValue<string>().Should().NotBeEmpty();
            plan["axis"]!.GetValue<string>().Should().Be("rf");
            plan["modems"]![1]!["rfHz"]!.GetValue<double>().Should().Be(7090000);
        }
        else
        {
            plan["warnings"]!.AsArray().Should().NotBeEmpty("a plan that could be built still says what is wrong with it");
        }
    }

    [Fact]
    public async Task Asking_For_A_Plan_Applies_Nothing()
    {
        using var station = new Station(_dir, key: "");
        string before = File.ReadAllText(station.ConfigPath);

        await PlanAsync(station, DialStation);

        File.ReadAllText(station.ConfigPath).Should().Be(before);
        File.Exists(station.PendingPath).Should().BeFalse();
        Directory.Exists(Path.Combine(Path.GetDirectoryName(station.ConfigPath)!, "history")).Should().BeFalse();
        station.Restarts.Should().Be(0);
    }

    [Fact]
    public async Task A_Document_The_Station_Could_Not_Read_Gets_Its_Refusal_And_No_Plan()
    {
        using var station = new Station(_dir, key: "");

        string bad = (await PlanAsync(station, """
            { "device": "null", "modems": [ { "subChannel": 0, "mode": "bpsk300 " } ] }
            """, HttpStatusCode.BadRequest)).GetValue<string>();

        bad.Should().Contain("unknown mode");

        (await PlanAsync(station, "  ", HttpStatusCode.BadRequest)).GetValue<string>()
            .Should().Contain("complete configuration document");
    }

    [Fact]
    public async Task A_Plan_Is_Only_Made_For_A_Post()
    {
        using var station = new Station(_dir, key: "");

        (await station.Client.GetAsync(station.Url("api/config/plan"))).StatusCode
            .Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task With_A_Key_A_Plan_Wants_It_As_The_Configuration_Does()
    {
        using var station = new Station(_dir, key: "sekrit");

        HttpResponseMessage without = await station.Client.PostAsync(
            station.Url("api/config/plan"), new StringContent(DialStation, Encoding.UTF8));
        without.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");
        HttpResponseMessage with = await station.Client.PostAsync(
            station.Url("api/config/plan"), new StringContent(DialStation, Encoding.UTF8));
        with.StatusCode.Should().Be(HttpStatusCode.OK, await with.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_A_Key_A_Plan_From_Another_Site_Is_Refused()
    {
        using var station = new Station(_dir, key: "");

        using var crossSite = new HttpRequestMessage(HttpMethod.Post, station.Url("api/config/plan"))
        {
            Content = new StringContent(DialStation, Encoding.UTF8, "application/json"),
        };
        crossSite.Headers.Add("Origin", "http://evil.example");
        (await station.Client.SendAsync(crossSite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_Plan_Of_A_Document_That_Still_Has_A_Secret_Placeholder_Is_Made_With_The_Stored_Secret()
    {
        using var station = new Station(_dir, key: "sekrit");
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");
        JsonNode served = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config")))!["running"]!;
        served["api"]!["key"]!.GetValue<string>().Should().Be(ConfigApi.Hidden);

        HttpResponseMessage answer = await station.Client.PostAsync(
            station.Url("api/config/plan"), new StringContent(served.ToJsonString(), Encoding.UTF8));

        answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- the strip's arithmetic

    [Theory]
    [InlineData(7050800, "7.050800")]
    [InlineData(14100000, "14.100000")]
    [InlineData(144800000, "144.800000")]
    [InlineData(7050800.4, "7.050800")]
    public void A_Frequency_Is_Shown_In_Mhz_To_The_Hz(double hz, string text)
    {
        Engine js = Model();
        js.SetValue("hz", hz);

        js.Evaluate("ConfigModel.mhzText(hz)").AsString().Should().Be(text);
    }

    [Theory]
    [InlineData("7.0508", 7050800)]
    [InlineData(" 7.050800 ", 7050800)]
    [InlineData("14.1", 14100000)]
    [InlineData("7", 7000000)]
    [InlineData("7.1234567", 7123457)]
    public void Mhz_Typed_Into_A_Field_Becomes_Hz(string text, double hz)
    {
        Engine js = Model();
        js.SetValue("text", text);

        js.Evaluate("ConfigModel.mhzFromText(text).value").AsNumber().Should().Be(hz);
    }

    [Theory]
    [InlineData("")]
    [InlineData("seven")]
    [InlineData("7,05")]
    [InlineData("-7.05")]
    [InlineData("0")]
    [InlineData("7.05 MHz")]
    public void Text_That_Is_Not_A_Frequency_In_Mhz_Is_Refused(string text)
    {
        Engine js = Model();
        js.SetValue("text", text);

        js.Evaluate("ConfigModel.mhzFromText(text).error").AsString().Should().Contain("MHz");
    }

    [Theory]
    [InlineData(7050830, 7050800)]
    [InlineData(7050850, 7050900)]
    [InlineData(7050949, 7050900)]
    [InlineData(1649, 1600)]
    [InlineData(1650, 1700)]
    public void Positions_Snap_To_A_Hundred_Hz(double hz, double snapped)
    {
        Engine js = Model();
        js.SetValue("hz", hz);

        js.Evaluate("ConfigModel.snap(hz, 100)").AsNumber().Should().Be(snapped);
    }

    [Theory]
    [InlineData("usb", 7051500, 1500)]
    [InlineData("lsb", 7048500, 1500)]
    public void The_Audio_Tone_Is_Measured_From_The_Dial_The_Way_The_Sideband_Runs(string radio, double rf, double tone)
    {
        Engine js = Model();
        js.SetValue("radio", radio);
        js.SetValue("rf", rf);

        js.Evaluate("ConfigModel.toneHz(rf, 7050000, radio)").AsNumber().Should().Be(tone);
    }

    [Theory]
    [InlineData(7050800, 0, 7050800)]
    [InlineData(7050800, 4, 7050800)]
    [InlineData(7050800, 10, 7050900)]
    [InlineData(7050800, 40, 7051200)]
    [InlineData(7050800, -40, 7050400)]
    [InlineData(7050800, 0.4, 7050800)]
    public void Dragging_A_Block_Moves_It_By_The_Pointer_In_Hundreds_Of_Hz(double start, double dxPx, double expected)
    {
        // Ten Hz to the pixel, as a 2.5 kHz span drawn across 250 pixels.
        Engine js = Model();
        js.SetValue("start", start);
        js.SetValue("dx", dxPx);

        js.Evaluate("ConfigModel.dragTo(start, dx, 10, 100)").AsNumber().Should().Be(expected);
    }

    [Fact]
    public void A_Drag_Stops_At_The_Ends_Of_The_Strip_On_A_Step()
    {
        Engine js = Model();

        js.Evaluate("ConfigModel.dragTo(1500, 5000, 10, 100, 400, 2650)").AsNumber().Should().Be(2600);
        js.Evaluate("ConfigModel.dragTo(1500, -5000, 10, 100, 430, 2650)").AsNumber().Should().Be(500);
    }

    [Theory]
    [InlineData("ArrowRight", false, 100)]
    [InlineData("ArrowUp", false, 100)]
    [InlineData("ArrowLeft", false, -100)]
    [InlineData("ArrowDown", false, -100)]
    [InlineData("ArrowRight", true, 1000)]
    [InlineData("ArrowLeft", true, -1000)]
    [InlineData("a", false, 0)]
    [InlineData("Tab", true, 0)]
    public void Arrow_Keys_Move_A_Block_A_Hundred_Hz_And_Shift_A_Kilohertz(string key, bool shift, double delta)
    {
        Engine js = Model();
        js.SetValue("key", key);
        js.SetValue("shift", shift);

        js.Evaluate("ConfigModel.keyDelta(key, shift)").AsNumber().Should().Be(delta);
    }

    [Theory]
    [InlineData(7050800, 100, 7050900)]
    [InlineData(7050800, -100, 7050700)]
    [InlineData(7050830, 100, 7050900)]
    [InlineData(7050830, -100, 7050800)]
    [InlineData(7050800, 1000, 7051800)]
    public void A_Nudge_Always_Ends_On_A_Multiple_Of_A_Hundred(double pos, double delta, double expected)
    {
        Engine js = Model();
        js.SetValue("pos", pos);
        js.SetValue("delta", delta);

        js.Evaluate("ConfigModel.nudged(pos, delta, 100)").AsNumber().Should().Be(expected);
    }

    [Fact]
    public void A_New_Modem_Goes_In_The_Lowest_Free_Part_Of_The_Passband()
    {
        Engine js = Model();

        // 7.050300 to 7.052700 is free but for one block from 7.050400 to 7.050800.
        string answer = js.Evaluate("""
            JSON.stringify([
              ConfigModel.freeCentre(400, [], 7050300, 7052700, 100),
              ConfigModel.freeCentre(400, [{ lo: 7050400, hi: 7050800 }], 7050300, 7052700, 100),
              ConfigModel.freeCentre(400, [{ lo: 7050300, hi: 7052700 }], 7050300, 7052700, 100),
              ConfigModel.freeCentre(3000, [], 7050300, 7052700, 100)
            ])
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n?.GetValue<double?>())
            .Should().Equal(7050500d, 7051000d, null, null);
    }

    [Fact]
    public void A_New_Modem_Takes_The_Lowest_Free_Sub_Channel()
    {
        Engine js = Model();

        string answer = js.Evaluate("""
            JSON.stringify([
              ConfigModel.nextSubChannel([]),
              ConfigModel.nextSubChannel([{ subChannel: 0 }, { subChannel: 1 }]),
              ConfigModel.nextSubChannel([{ subChannel: 1 }, { mode: "afsk1200" }]),
              ConfigModel.nextSubChannel([{ SubChannel: 0 }, { subChannel: 2 }]),
              ConfigModel.nextSubChannel((function () { var a = []; for (var i = 0; i < 16; i++) a.push({ subChannel: i }); return a; })())
            ])
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n?.GetValue<int?>())
            .Should().Equal(0, 2, 2, 1, null);
    }

    [Fact]
    public void The_Picker_Offers_A_Nino_Mode_Number_List_And_Each_Other_Group_By_Its_Modes()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              return JSON.stringify({
                nino: ConfigModel.modesOfGroup(schema, "nino").map(function (m) { return m.ninoMode; }),
                ardop: ConfigModel.modesOfGroup(schema, "ardop").map(function (m) { return m.mode; }),
                p1200: ConfigModel.modesOfGroup(schema, "p1200").map(function (m) { return m.mode; }),
                info: ConfigModel.modeInfo(schema, "bpsk300")
              });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["nino"]!.AsArray().Select(n => n!.GetValue<int>()).Should().BeInAscendingOrder().And.HaveCountGreaterThan(10);
        result["ardop"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("ardop");
        result["p1200"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("afsk1200");
        result["info"]!["name"]!.GetValue<string>().Should().Be("300 baud BPSK");
        result["info"]!["widthHz"]!.GetValue<double>().Should().BeGreaterThan(0);
        result["info"]!["tunable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void The_Strip_Is_Laid_Out_From_The_Plan_With_The_Dial_The_Passband_And_A_Block_Each()
    {
        Engine js = Model();
        js.SetValue("planJson", DialPlanJson);

        string answer = js.Evaluate("""
            (function () {
              var layout = ConfigModel.stripLayout(JSON.parse(planJson), {});
              return JSON.stringify({
                axis: layout.axis, lo: layout.lo, hi: layout.hi, dial: layout.dial,
                passband: layout.passband,
                blocks: layout.blocks.map(function (b) { return [b.subChannel, b.centre, b.lo, b.hi, b.movable, b.kinds]; }),
                ticks: layout.ticks.length,
                first: layout.ticks[0].label
              });
            })()
            """).AsString();

        JsonNode layout = JsonNode.Parse(answer)!;
        layout["axis"]!.GetValue<string>().Should().Be("rf");
        layout["dial"]!.GetValue<double>().Should().Be(7050000);
        layout["passband"]!["lo"]!.GetValue<double>().Should().Be(7050300);
        layout["lo"]!.GetValue<double>().Should().BeLessThan(7050000, "the dial is in view");
        layout["hi"]!.GetValue<double>().Should().BeGreaterThan(7052700);
        layout["blocks"]!.AsArray().Should().HaveCount(2);
        layout["blocks"]![0]![2]!.GetValue<double>().Should().Be(7050800 - 150);
        layout["blocks"]![1]![4]!.GetValue<bool>().Should().BeTrue();
        layout["blocks"]![1]![5]!.AsArray().Select(k => k!.GetValue<string>()).Should().Equal("outside");
        layout["ticks"]!.GetValue<int>().Should().BeInRange(3, 8);
        layout["first"]!.GetValue<string>().Should().MatchRegex(@"^7\.0\d{3,4}$");
    }

    [Fact]
    public void A_Block_Being_Dragged_Is_Drawn_Where_The_Pointer_Has_Put_It()
    {
        Engine js = Model();
        js.SetValue("planJson", DialPlanJson);

        double centre = js.Evaluate("""
            ConfigModel.stripLayout(JSON.parse(planJson), { 0: 7051400 }).blocks[0].centre
            """).AsNumber();

        centre.Should().Be(7051400);
    }

    [Fact]
    public void The_Strip_Is_Drawn_In_Audio_Hz_Where_There_Is_No_Dial()
    {
        Engine js = Model();
        js.SetValue("planJson", """
            { "ok": true, "radio": "fm", "axis": "audio", "dialHz": null, "hasDial": false, "passband": null, "warnings": [],
              "modems": [
                { "subChannel": 0, "mode": "afsk1200", "widthHz": 2000, "tunable": true, "audioCentreHz": 1700 },
                { "subChannel": 1, "mode": "fsk9600", "widthHz": 9000, "tunable": false, "audioCentreHz": null }
              ] }
            """);

        string answer = js.Evaluate("""
            (function () {
              var plan = JSON.parse(planJson);
              var layout = ConfigModel.stripLayout(plan, {});
              return JSON.stringify({ lo: layout.lo, hi: layout.hi, blocks: layout.blocks.map(function (b) { return [b.lo, b.hi, b.movable]; }),
                                      dial: layout.dial, pass: layout.passband, caption: ConfigModel.stripCaption(plan) });
            })()
            """).AsString();

        JsonNode layout = JsonNode.Parse(answer)!;
        layout["dial"].Should().BeNull();
        layout["pass"].Should().BeNull();
        layout["blocks"]![0]![0]!.GetValue<double>().Should().Be(700);
        layout["blocks"]![0]![2]!.GetValue<bool>().Should().BeTrue();
        layout["blocks"]![1]![0]!.GetValue<double>().Should().Be(0, "a baseband mode starts at DC");
        layout["blocks"]![1]![2]!.GetValue<bool>().Should().BeFalse("it has no centre to move");
        layout["lo"]!.GetValue<double>().Should().BeLessThan(0);
        layout["caption"]!["head"]!.GetValue<string>().Should().Contain("audio");
    }

    [Fact]
    public void The_Line_Over_The_Strip_Says_What_The_Radio_Hears_And_From_What_Dial()
    {
        Engine js = Model();
        js.SetValue("planJson", DialPlanJson);

        string answer = js.Evaluate("JSON.stringify(ConfigModel.stripCaption(JSON.parse(planJson)))").AsString();

        JsonNode caption = JsonNode.Parse(answer)!;
        caption["head"]!.GetValue<string>().Should().Be("The radio hears 7.050300 to 7.052700 MHz");
        caption["tail"]!.GetValue<string>().Should().Be("upper sideband from a dial of 7.050000 MHz");
    }

    private const string DialPlanJson = """
        { "ok": true, "radio": "usb", "axis": "rf", "dialHz": 7050000, "hasDial": true,
          "passband": { "audioLowHz": 300, "audioHighHz": 2700, "rfLowHz": 7050300, "rfHighHz": 7052700 },
          "warnings": [ { "subChannel": 1, "kind": "outside", "text": "x" } ],
          "modems": [
            { "subChannel": 0, "mode": "bpsk300", "widthHz": 300, "tunable": true, "audioCentreHz": 800, "rfHz": 7050800 },
            { "subChannel": 1, "mode": "qpsk600", "widthHz": 700, "tunable": true, "audioCentreHz": 2700, "rfHz": 7052700 }
          ] }
        """;
}
