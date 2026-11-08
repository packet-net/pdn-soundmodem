using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Linux.Tests;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The configuration page on a monitor site: when it is served at all, what it declines, and the
/// pieces of its model that make an uplink token in the browser.
/// </summary>
/// <remarks>
/// A site is reachable from the internet, so its configuration is never open: the first group of
/// tests here are the rule, against a real <see cref="MonitorHost"/> on a real port. The second
/// group runs the page's own model in Jint, as the rest of <see cref="ConfigPageTests"/> does.
/// </remarks>
public partial class ConfigPageTests
{
    private const string SiteKey = "a-long-site-key";

    private const string SiteFile = """
        {
          "bind": "127.0.0.1",
          "waterfall": { "port": 8099, "title": "A site" },
          "monitor": {
            "directory": "http://127.0.0.1:1/instances",
            "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ]
          }
        }
        """;

    // ---------------------------------------------------------------- who gets a page

    [Fact]
    public void A_Monitor_Without_A_Key_Is_Given_No_Settings_Page_To_Serve()
    {
        var file = new MonitorStartup.MonitorFile("/nowhere/soundmodem.json", SiteFile, false);

        MonitorStartup.ConfigHandlerFor("", file, () => { }).Should().BeNull("a site is never open");
        MonitorStartup.ConfigHandlerFor("", null, () => { }).Should().BeNull();
        MonitorStartup.ConfigHandlerFor(SiteKey, null, () => { }).Should().BeNull(
            "a run with no file has nothing for the page to edit");
        MonitorStartup.ConfigHandlerFor(SiteKey, file, () => { }).Should().NotBeNull();
    }

    [Fact]
    public async Task A_Monitor_Without_A_Key_Answers_404_To_Every_Settings_Path()
    {
        await using var site = await SiteHost.StartAsync(_dir, key: "");

        foreach (string path in (string[])["config", "api/config", "api/config/schema", "api/config/plan", "api/config/history"])
        {
            (await site.Client.GetAsync(site.Url(path))).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }

        // Not even a post with a key in it: there is no handler to hand it to.
        using var post = new HttpRequestMessage(HttpMethod.Post, site.Url("api/config"))
        {
            Content = new StringContent(SiteFile),
        };
        post.Headers.Add("X-API-Key", SiteKey);
        (await site.Client.SendAsync(post)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        site.Restarts.Should().Be(0);

        // And the site itself is as it was.
        (await site.Client.GetAsync(site.Url(""))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Monitor_With_A_Key_Serves_The_Page_And_Wants_The_Key_For_The_Configuration()
    {
        await using var site = await SiteHost.StartAsync(_dir, SiteKey);

        // The page and the description of the settings hold no secret and are served to anyone,
        // as on a station, so the page can ask for the key.
        HttpResponseMessage page = await site.Client.GetAsync(site.Url("config"));
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().Contain("monitor-uplinks");
        (await site.Client.GetAsync(site.Url("api/config/schema"))).StatusCode.Should().Be(HttpStatusCode.OK);

        // The configuration itself is not.
        foreach (string path in (string[])["api/config", "api/config/history", "api/config/plan"])
        {
            (await site.Client.GetAsync(site.Url(path))).StatusCode.Should().Be(
                HttpStatusCode.Unauthorized, $"{path} without the key");
        }

        using var wrong = new HttpRequestMessage(HttpMethod.Get, site.Url("api/config"));
        wrong.Headers.Add("X-API-Key", "not-it");
        (await site.Client.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var right = new HttpRequestMessage(HttpMethod.Get, site.Url("api/config"));
        right.Headers.Authorization = new("Bearer", SiteKey);
        HttpResponseMessage ok = await site.Client.SendAsync(right);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode running = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["running"]!;
        running["monitor"]!["modems"]![0]!["rfFrequency"]!.GetValue<double>().Should().Be(7050300);
        running["api"]!["key"]!.GetValue<string>().Should().Be(ConfigApi.Hidden, "the key is never served back");
    }

    [Fact]
    public async Task A_Monitor_Does_Not_Serve_The_Stations_Other_Endpoints_Even_With_The_Key()
    {
        await using var site = await SiteHost.StartAsync(_dir, SiteKey);

        // Hardware discovery would list this machine's sound cards and serial ports to the
        // internet; the rest are a station's radio.
        foreach (string path in (string[])["api/config/discover", "api/mixer", "api/rig", "api/txtest", "api/txlease", "api/proposals", "api/keepaudio"])
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, site.Url(path));
            request.Headers.Add("X-API-Key", SiteKey);
            (await site.Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task A_Monitor_Declines_A_Change_It_Would_Exit_2_On_And_Applies_One_It_Would_Start_On()
    {
        await using var site = await SiteHost.StartAsync(_dir, SiteKey);

        async Task<(HttpStatusCode Status, string Body)> PostAsync(string document, string query)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, site.Url("api/config" + query))
            {
                Content = new StringContent(document),
            };
            request.Headers.Add("X-API-Key", SiteKey);
            HttpResponseMessage answer = await site.Client.SendAsync(request);
            return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
        }

        // No radio frequency: a web receiver has no dial to read off, so start-up exits 2.
        (HttpStatusCode noFrequency, string why) = await PostAsync(
            SiteFile.Replace("\"rfFrequency\": 7050300", "\"frequency\": 1500"), "");
        noFrequency.Should().Be(HttpStatusCode.BadRequest);
        why.Should().Contain("rfFrequency");

        // FM: a web receiver is SSB.
        (HttpStatusCode fm, string fmWhy) = await PostAsync(
            SiteFile.Replace("\"waterfall\"", "\"sideband\": \"fm\", \"waterfall\""), "");
        fm.Should().Be(HttpStatusCode.BadRequest);
        fmWhy.Should().Contain("cannot be served by a monitor");

        // A mode with a stray character, the 2026-08-15 outage.
        (HttpStatusCode stray, _) = await PostAsync(SiteFile.Replace("afsk300-il2pc", "afsk300-il2pc "), "");
        stray.Should().Be(HttpStatusCode.BadRequest);

        // A dial no modem set fits into.
        (HttpStatusCode apart, _) = await PostAsync(
            SiteFile.Replace("]", ", { \"subChannel\": 1, \"mode\": \"afsk300-il2pc\", \"rfFrequency\": 7090300 } ]"), "");
        apart.Should().Be(HttpStatusCode.BadRequest);

        site.Restarts.Should().Be(0, "a declined change leaves the site up");
        File.Exists(site.PendingPath).Should().BeFalse();

        // A good one: checked only, then applied for one run.
        string better = SiteFile.Replace("A site", "A better site");
        (await PostAsync(better, "?check=true")).Status.Should().Be(HttpStatusCode.OK);
        site.Restarts.Should().Be(0, "a check never restarts");

        (HttpStatusCode applied, string answer) = await PostAsync(better, "");
        applied.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(answer)!["restarting"]!.GetValue<bool>().Should().BeTrue();
        // The restart is asked for just after the answer goes out.
        await WaitForAsync(() => site.Restarts == 1);
        File.ReadAllText(site.PendingPath).Should().Contain("A better site");
        File.ReadAllText(site.ConfigPath).Should().Contain("\"A site\"", "a one-run change leaves the file alone");

        // Saved: the file is replaced.
        (await PostAsync(better, "?persist=true")).Status.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(site.ConfigPath).Should().Contain("A better site");
    }

    [Fact]
    public async Task The_Band_Plan_Of_A_Monitors_Modems_Is_Served_For_The_Strip()
    {
        await using var site = await SiteHost.StartAsync(_dir, SiteKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, site.Url("api/config/plan"))
        {
            Content = new StringContent(SiteFile.Replace(
                "]", ", { \"subChannel\": 1, \"mode\": \"afsk300-il2pc\", \"rfFrequency\": 7050900 } ]")),
        };
        request.Headers.Add("X-API-Key", SiteKey);
        HttpResponseMessage answer = await site.Client.SendAsync(request);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonNode plan = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!;
        plan["ok"]!.GetValue<bool>().Should().BeTrue();
        plan["axis"]!.GetValue<string>().Should().Be("rf", "a site's modems are placed by radio frequency");
        plan["hasDial"]!.GetValue<bool>().Should().BeTrue();
        JsonArray modems = plan["modems"]!.AsArray();
        modems.Should().HaveCount(2, "the modems inside \"monitor\" are the ones planned");
        modems[0]!["rfHz"]!.GetValue<double>().Should().Be(7050300);
        modems[1]!["rfHz"]!.GetValue<double>().Should().Be(7050900);
    }

    [Fact]
    public async Task A_Station_Without_A_Key_Is_Still_Given_No_Settings_Page_When_Its_Page_Is_Public()
    {
        // The station's side of the same rule: openConfiguration is false for a public page, and
        // with no key that leaves the handler answering nothing.
        using var station = new Station(_dir, key: "", openConfiguration: false);

        foreach (string path in (string[])["config", "api/config", "api/config/schema"])
        {
            (await station.Client.GetAsync(station.Url(path))).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    // ---------------------------------------------------------------- the page's own model

    [Fact]
    public void The_Page_Makes_A_Token_Exactly_As_The_Command_Line_Does()
    {
        Engine js = Model();
        byte[] entropy = [.. Enumerable.Range(0, 32).Select(i => (byte)((i * 37) + 11))];
        js.SetValue("bytes", string.Join(",", entropy));

        string token = js.Evaluate("ConfigModel.tokenFromBytes(bytes.split(',').map(Number))").AsString();

        string expected = UplinkToken.Prefix + Convert.ToBase64String(entropy)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        token.Should().Be(expected);

        // And the same shape as one the command line mints.
        (string minted, _) = UplinkToken.Mint();
        token.Length.Should().Be(minted.Length);
        token.Should().MatchRegex("^pdnsm_[A-Za-z0-9_-]{43}$");
        minted.Should().MatchRegex("^pdnsm_[A-Za-z0-9_-]{43}$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("pdnsm_P3rkyAB2V_VHZ13Mk2D8dEZG4kC5H-idxTUNsqISXCE")]
    [InlineData("a message that is exactly fifty-five bytes long, no more")]
    [InlineData("a message that is exactly fifty-six bytes long, give or take")]
    [InlineData("café € 😀 and then some text to cross a block")]
    public void The_Pages_Sha256_Is_The_Same_As_Dot_Nets(string text)
    {
        Engine js = Model();
        js.SetValue("text", text);

        string hash = js.Evaluate("ConfigModel.sha256Hex(text)").AsString();

        hash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void The_Pages_Sha256_Agrees_With_Dot_Net_Over_Many_Lengths()
    {
        // Every length across a few blocks, so the padding is right at each edge of one.
        Engine js = Model();
        string all = js.Evaluate("""
            (function () {
              var out = [];
              for (var n = 0; n <= 200; n++) {
                var s = "";
                for (var i = 0; i < n; i++) s += String.fromCharCode(33 + ((i * 7 + n) % 90));
                out.push(s + "|" + ConfigModel.sha256Hex(s));
              }
              return out.join("\n");
            })()
            """).AsString();

        foreach (string line in all.Split('\n'))
        {
            int bar = line.LastIndexOf('|');
            string text = line[..bar];
            line[(bar + 1)..].Should().Be(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), $"length {text.Length}");
        }
    }

    [Fact]
    public void A_Token_And_Hash_The_Page_Makes_Are_Accepted_By_The_Daemon_As_An_Uplink()
    {
        Engine js = Model();
        string both = js.Evaluate("""
            (function () {
              var bytes = [];
              for (var i = 0; i < 32; i++) bytes.push((i * 53 + 7) & 255);
              var token = ConfigModel.tokenFromBytes(bytes);
              return token + " " + ConfigModel.sha256Hex(token);
            })()
            """).AsString();
        string[] parts = both.Split(' ');

        UplinkToken.Hash(parts[0]).Should().Be(parts[1], "the daemon hashes a presented token the same way");

        string file = $$"""
            {
              "waterfall": { "port": 8099 },
              "monitor": {
                "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ],
                "uplinks": [ { "callsign": "GB7RDG-2", "slug": "gb7rdg-2", "tokenSha256": "{{parts[1]}}" } ]
              }
            }
            """;
        ConfigApi.Validate(file).Should().BeNull("an entry the page writes is one the site starts on");
    }

    [Theory]
    [InlineData("GB7RDG-2")]
    [InlineData("m0lte")]
    [InlineData("M0LTE-15")]
    [InlineData("--G4 X--")]
    public void The_Page_Names_A_Stations_Page_As_The_Daemon_Does(string callsign)
    {
        Engine js = Model();
        js.SetValue("callsign", callsign);

        js.Evaluate("ConfigModel.slugFor(callsign)").AsString().Should().Be(UberSdrDirectory.SlugForCallsign(callsign));
    }

    [Fact]
    public void A_Monitors_Settings_Are_Sorted_Into_The_Monitor_Areas_And_None_Is_Lost()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var doc = {
                bind: "127.0.0.1", sideband: "usb", waterfall: { port: 8099, title: "t" }, api: { key: "k" },
                ubersdr: { mode: "iq48" }, frameLog: { path: "/tmp" }, idBeacons: false,
                kissPort: 8100,
                monitor: { directory: "http://d", allow: ["a.example"], publicUrl: "https://m.example",
                           modems: [{ subChannel: 0, mode: "afsk300-il2pc", rfFrequency: 7050300 }],
                           uplinks: [{ callsign: "A1B", slug: "a1b", tokenSha256: "x" }] }
              };
              var groups = ConfigModel.areas(schema, doc);
              var out = {};
              schema.areas.forEach(function (a) {
                var d = ConfigModel.drawable(groups[a.id]);
                out[a.id] = d.fields.concat(d.sections).map(function (n) { return n.path; });
              });
              return JSON.stringify(out);
            })()
            """).AsString();

        JsonNode areas = JsonNode.Parse(answer)!;
        string[] In(string area) => [.. areas[area]!.AsArray().Select(n => n!.GetValue<string>())];

        In("monitor").Should().Contain(["monitor.directory", "monitor.allow", "ubersdr"]);
        In("monitor-modems").Should().Contain(["monitor.modems", "sideband"]);
        In("monitor-uplinks").Should().Equal("monitor.uplinks");
        In("monitor-site").Should().Contain(["monitor.publicUrl", "waterfall.title", "waterfall.port", "api", "bind"]);
        In("monitor-logging").Should().Equal("frameLog");
        In("advanced").Should().Contain(["kissPort", "idBeacons"], "what a site does not read is kept where it can be seen");
        foreach (string station in (string[])["radio", "keying", "modes", "software", "sharing"])
        {
            In(station).Should().BeEmpty($"a site has no {station} area to put anything in");
        }
    }

    [Fact]
    public void A_Monitors_Overview_Says_In_Plain_Sentences_What_The_Site_Is()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var doc = { monitor: { directory: "https://instances.example.org/api/instances", publicUrl: "https://m.example",
                modems: [{ subChannel: 0, mode: "afsk300-il2pc", rfFrequency: 7050300 }],
                uplinks: [{ callsign: "GB7RDG-2" }, { callsign: "M0LTE" }] } };
              return ConfigModel.monitorOverview(schema, doc).join("\n") + "\n--\n"
                + ConfigModel.summary(schema, doc, "monitor-uplinks").join(" ");
            })()
            """).AsString();

        answer.Should().Contain("This is a monitor site").And.Contain("transmits nothing")
            .And.Contain("7.050300 MHz").And.Contain("instances.example.org")
            .And.Contain("2 private stations may publish here: GB7RDG-2, M0LTE")
            .And.Contain("https://m.example");
        answer.Should().NotContain("device");
    }

    [Fact]
    public void The_Directory_Pick_List_Shows_Receivers_And_Not_The_Stations_That_Publish()
    {
        Engine js = Model();

        string answer = js.Evaluate("""
            (function () {
              var rows = ConfigModel.receiverRows({ receivers: [
                { kind: "receiver", host: "a.example", callsign: "A1", location: "Leeds", offered: true },
                { kind: "station", host: "", callsign: "GB7RDG" },
                { kind: "receiver", host: "b.example", callsign: "", location: "", offered: false }] });
              var allow = ConfigModel.hostToggled([], "A.example");
              var again = ConfigModel.hostToggled(allow, "a.EXAMPLE");
              return JSON.stringify({ rows: rows, allow: allow, again: again, listed: ConfigModel.hostListed(allow, "a.example") });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["rows"]!.AsArray().Should().HaveCount(2);
        result["rows"]![0]!["who"]!.GetValue<string>().Should().Be("A1, Leeds");
        result["rows"]![1]!["offered"]!.GetValue<bool>().Should().BeFalse();
        result["allow"]!.AsArray().Should().HaveCount(1);
        result["again"]!.AsArray().Should().BeEmpty("a second click takes it off again, whatever the case");
        result["listed"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Taking_The_Key_Out_Of_A_Site_Is_Told_Apart_Because_It_Switches_The_Page_Off()
    {
        Engine js = Model();

        js.Evaluate("""
            JSON.stringify([
              ConfigModel.siteLosesPage({ monitor: {}, api: { key: "(set, not shown)" } }, { monitor: {} }),
              ConfigModel.siteLosesPage({ monitor: {}, api: { key: "(set, not shown)" } }, { monitor: {}, api: { key: "(set, not shown)" } }),
              ConfigModel.siteLosesPage({ device: "null" }, { device: "null" }),
              ConfigModel.siteLosesPage({ monitor: {}, api: { key: "k" } }, { monitor: {}, api: {} })
            ])
            """).AsString().Should().Be("[true,false,false,true]");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should have come true");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// A real <see cref="MonitorHost"/> on a loopback port, with the settings page handed to it
    /// the way the daemon hands it, and no receivers (its directory is a canned empty one).
    /// </summary>
    private sealed class SiteHost : IAsyncDisposable
    {
        private readonly MonitorHost _host;
        private int _restarts;

        private SiteHost(string dir, string key)
        {
            string mine = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            ConfigPath = Path.Combine(mine, "soundmodem.json");
            PendingPath = Path.Combine(mine, "pending-config.json");
            string withKey = key.Length == 0
                ? SiteFile
                : SiteFile.Replace("\"waterfall\"", $"\"api\": {{ \"key\": \"{key}\" }}, \"waterfall\"");
            File.WriteAllText(ConfigPath, withKey);

            Func<HttpListenerContext, string, Task<bool>>? handler = null;
            if (key.Length > 0)
            {
                // The same construction the daemon makes, but with the one-run file in this
                // test's own directory.
                handler = new ConfigApi(
                    key, ConfigPath, PendingPath,
                    runningJson: () => File.ReadAllText(ConfigPath),
                    ephemeralInForce: false,
                    requestRestart: () => Interlocked.Increment(ref _restarts),
                    openConfiguration: false).HandleAsync;
            }

            int port = FreePorts.Next();
            Base = $"http://127.0.0.1:{port}/";
            _host = new MonitorHost(new MonitorHostOptions
            {
                Directory = new UberSdrDirectoryOptions
                {
                    Url = "https://instances.example.org/api/instances",
                    IqMode = "iq48",
                    WindowLowHz = 7050136,
                    WindowHighHz = 7051776,
                },
                Port = port,
                Bind = "127.0.0.1",
                Modems = [new ModemConfig { SubChannel = 0, Mode = "afsk300-il2pc", Frequency = 1700 }],
                DspRate = 12000,
                DialHz = 7049450,
                IdBeacons = false,
                ConfigHandler = handler,
                Journal = _ => new StationJournal("", _ => { }, _ => { }),
                FetchDirectory = _ => Task.FromResult("""{"instances": []}"""),
                OpenInput = (_, _, _) => throw new InvalidOperationException("no receiver is opened here"),
            });
        }

        public string Base { get; }

        public string ConfigPath { get; }

        public string PendingPath { get; }

        public int Restarts => Volatile.Read(ref _restarts);

        public HttpClient Client { get; } = new();

        public Uri Url(string relative) => new(Base + relative, UriKind.Absolute);

        public static async Task<SiteHost> StartAsync(string dir, string key)
        {
            var site = new SiteHost(dir, key);
            (await site._host.StartAsync()).Should().Be(0, "the site has to come up");
            return site;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.DisposeAsync();
        }
    }
}
