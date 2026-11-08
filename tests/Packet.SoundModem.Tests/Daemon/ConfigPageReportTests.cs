using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// "Report a problem": <c>GET /api/report</c>, the console lines it carries, and the page's
/// report model that turns them into a pre-filled GitHub link.
/// </summary>
public partial class ConfigPageTests
{
    // ---------------------------------------------------------------- the console copy

    [Fact]
    public void The_Console_Copy_Keeps_The_Newest_Lines_And_No_More_Than_The_Cap()
    {
        var recent = new RecentOutput();

        for (int i = 0; i < RecentOutput.MaxLines + 50; i++)
        {
            recent.Add($"line {i}");
        }

        IReadOnlyList<string> lines = recent.Lines([]);
        lines.Should().HaveCount(RecentOutput.MaxLines);
        lines[^1].Should().EndWith($"line {RecentOutput.MaxLines + 49}");
        lines[0].Should().EndWith("line 50", "the oldest fifty went");
    }

    [Fact]
    public void A_Very_Long_Line_Is_Cut()
    {
        var recent = new RecentOutput();

        recent.Add(new string('x', 5000));

        recent.Lines([]).Single().Length.Should().BeLessThan(RecentOutput.MaxLineChars + 20);
    }

    [Fact]
    public void The_Total_Kept_Is_Bounded_By_Characters_Not_Only_By_Lines()
    {
        var recent = new RecentOutput();

        for (int i = 0; i < 100; i++)
        {
            recent.Add(new string((char)('a' + i % 26), 7000));
        }

        recent.Lines([]).Count.Should().BeLessThanOrEqualTo(RecentOutput.MaxStoredChars / 7000);
    }

    [Fact]
    public void The_Tee_Writes_Through_And_Keeps_Whole_Lines_Only()
    {
        var recent = new RecentOutput();
        var real = new StringWriter();
        TextWriter tee = recent.Tee(real);

        tee.WriteLine("first");
        tee.Write("sec");
        tee.Write("ond");
        tee.WriteLine();
        tee.Write("unfinished");

        real.ToString().Should().Be($"first{Environment.NewLine}second{Environment.NewLine}unfinished");
        recent.Lines([]).Select(l => l["HH:mm:ss UTC ".Length..]).Should().Equal("first", "second");
    }

    [Theory]
    [InlineData("connecting wss://rx.example/ws?mode=usb&password=hunter22&x=1", "hunter22")]
    [InlineData("token=abc123def456 rejected", "abc123def456")]
    [InlineData("sent Authorization: Bearer abcdef0123456789", "abcdef0123456789")]
    [InlineData("X-API-Key: k3yk3yk3y failed", "k3yk3yk3y")]
    [InlineData("GET https://user:s3cretpass@host.example/path", "s3cretpass")]
    public void A_Line_Shaped_Like_A_Credential_Is_Scrubbed_As_It_Is_Kept(string line, string secret)
    {
        var recent = new RecentOutput();

        recent.Add(line);

        string kept = recent.Lines([]).Single();
        kept.Should().NotContain(secret);
        kept.Should().Contain(RecentOutput.Hidden);
    }

    [Fact]
    public void A_Known_Secret_Is_Hidden_Wherever_It_Appears()
    {
        var recent = new RecentOutput();
        recent.Add("odd message mentioning RealSecretValue in passing");

        string kept = recent.Lines(["RealSecretValue", null, "ab"]).Single();

        kept.Should().NotContain("RealSecretValue");
        kept.Should().Contain("passing", "only the secret goes");
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("sent header Authorization: Basic Zm9vOmJhcmJheg", "Zm9vOmJhcmJheg")]
    [InlineData("{\"password\": \"two words\"}", "two words")]
    [InlineData("config password='two words' loaded", "two words")]
    [InlineData("{\"key\": \"it's value\"}", "value")]
    [InlineData("password='say \"hi\" now' loaded", "now")]
    [InlineData("secret_key=abc123xyz789 in use", "abc123xyz789")]
    [InlineData("client.secret: s3cr3tvalue1", "s3cr3tvalue1")]
    [InlineData("aws_access_key=AKIAEXAMPLE12345", "AKIAEXAMPLE12345")]
    [InlineData("GET https://ghp_abcdefTOKEN123@example.org/x", "ghp_abcdefTOKEN123")]
    public void The_Shape_Scrub_Covers_Basic_Quoted_Suffixed_And_Username_Only_Credentials(string line, string secret)
    {
        var recent = new RecentOutput();

        recent.Add(line);

        string kept = recent.Lines([]).Single();
        kept.Should().NotContain(secret);
        kept.Should().Contain(RecentOutput.Hidden);
    }

    [Fact]
    public void Ordinary_Lines_Are_Not_Scrubbed()
    {
        var recent = new RecentOutput();

        recent.Add("modem 0: bpsk300 @ 1500 Hz, Basic setup done, keyboard ready");

        recent.Lines([]).Single().Should().EndWith("modem 0: bpsk300 @ 1500 Hz, Basic setup done, keyboard ready");
    }

    [Fact]
    public void A_Secret_Straddling_The_Served_Cut_Is_Hidden_Not_Left_Half_Showing()
    {
        // The value is not in a shape the scrub knows, so only the by-value pass can find it, and
        // it has to see the whole line: cut first, it would find half of it and miss.
        const string Secret = "STRADDLING-SECRET-VALUE-0123456789";
        var recent = new RecentOutput();
        recent.Add(new string('x', RecentOutput.MaxLineChars - 10) + Secret + " tail");

        string served = recent.Lines([Secret]).Single();

        served.Should().NotContain("STRADDLING").And.NotContain("SECRET-VALUE");
    }

    [Fact]
    public void A_Secret_Straddling_The_Stored_Cut_Is_Not_Left_Half_Showing()
    {
        const string Secret = "STRADDLING-SECRET-VALUE-0123456789";
        var recent = new RecentOutput();
        var tee = recent.Tee(TextWriter.Null);
        tee.Write(new string('x', RecentOutput.MaxStoredLine - 12));
        tee.Write(Secret);
        tee.WriteLine(" tail");

        string served = string.Concat(recent.Lines([Secret]));

        served.Should().NotContain("STRADDLING").And.NotContain("SECRET");
    }

    [Theory]
    [InlineData("peer 203.0.113.77 connected", "peer x.x.x.77 connected")]
    [InlineData("from 192.168.1.20:8107 and 10.45.0.66", "from x.x.x.20:8107 and x.x.x.66")]
    [InlineData("peer 2001:db8::1a2b connected", "peer x:x:x:x:x:x:x:1a2b connected")]
    [InlineData("peer 2001:db8:85a3:0:0:8a2e:370:7334 up", "peer x:x:x:x:x:x:x:7334 up")]
    [InlineData("listening on 127.0.0.1:8107 and ::1", "listening on 127.0.0.1:8107 and ::1")]
    [InlineData("started at 12:00:00 build 0.103.0 id 999", "started at 12:00:00 build 0.103.0 id 999")]
    [InlineData("bad address 999.1.1.1", "bad address 999.1.1.1")]
    public void Network_Addresses_Are_Masked_But_Loopback_And_Times_Are_Not(string line, string expected)
    {
        var recent = new RecentOutput();
        recent.Add(line);

        recent.Lines([]).Single().Should().EndWith(expected);
    }

    [Fact]
    public void The_Stations_Own_Addresses_Stay_Readable()
    {
        var recent = new RecentOutput();
        recent.Add("listening on 192.168.1.20 and 2001:db8::5, peer 192.168.1.99");

        string served = recent.Lines([], ["192.168.1.20", "2001:db8::5"]).Single();

        served.Should().Contain("192.168.1.20").And.Contain("2001:db8::5").And.Contain("x.x.x.99");
    }

    [Fact]
    public async Task The_Report_Masks_Addresses_In_The_Log_And_Keeps_Loopback()
    {
        var recent = new RecentOutput();
        recent.Add("peer 203.0.113.77 and 127.0.0.1");
        using var station = new Station(_dir, key: "", recent: recent);

        string body = await station.Client.GetStringAsync(station.Url("api/report"));

        body.Should().Contain("x.x.x.77").And.Contain("127.0.0.1").And.NotContain("203.0.113.77");
    }

    // ---------------------------------------------------------------- the endpoint

    [Fact]
    public async Task The_Report_Wants_The_Key_When_There_Is_One()
    {
        using var station = new Station(_dir, key: "sekrit");

        (await station.Client.GetAsync(station.Url("api/report"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, station.Url("api/report"));
        request.Headers.Add("Authorization", "Bearer sekrit");
        (await station.Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_A_Key_The_Report_Is_Open_To_The_Network()
    {
        using var station = new Station(_dir, key: "");
        (await station.Client.GetAsync(station.Url("api/report"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Public_Page_With_No_Key_Serves_No_Report()
    {
        using var station = new Station(_dir, key: "", openConfiguration: false);

        (await station.Client.GetAsync(station.Url("api/report"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Report_Is_Read_Only()
    {
        using var station = new Station(_dir, key: "");

        (await station.Client.PostAsync(station.Url("api/report"), new StringContent("{}"))).StatusCode
            .Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_Report_Carries_The_Version_The_Device_The_Uptime_And_The_Log()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var recent = new RecentOutput(clock);
        recent.Add("start-up: listening");
        using var station = new Station(_dir, key: "", time: clock, recent: recent);
        clock.Advance(TimeSpan.FromMinutes(90));

        JsonNode report = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/report")))!;

        report["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        report["deviceKind"]!.GetValue<string>().Should().Be("alsa");
        report["uptimeSeconds"]!.GetValue<long>().Should().Be(5400);
        report["config"]!["device"]!.GetValue<string>().Should().Be("null");
        report["logLines"]!.AsArray().Select(l => l!.GetValue<string>()).Should().ContainSingle()
            .Which.Should().Be("10:00:00 UTC start-up: listening");
        report["logLimit"]!.GetValue<int>().Should().Be(RecentOutput.MaxLines);
    }

    [Fact]
    public async Task No_Secret_Value_Is_Anywhere_In_The_Report()
    {
        // The key, the monitor token and the UberSDR password, and the same values turning up in
        // the log in the shapes they could really arrive in.
        const string Key = "KEYVALUE-9f3a";
        const string Token = "TOKENVALUE-77bc";
        const string Password = "PASSWORDVALUE-12de";
        var recent = new RecentOutput();
        recent.Add($"ubersdr: connecting wss://rx.example/ws?password={Password}");
        recent.Add($"publish: refused, the monitor said it does not know {Token}");
        recent.Add($"api: bad key presented, expected {Key}");
        using var station = new Station(_dir, key: Key, recent: recent, apiExtra: ", \"publish\": { \"url\": \"https://monitor.example/up\", \"token\": \"" + Token + "\" }"
            + ", \"ubersdr\": { \"password\": \"" + Password + "\" }");
        using var request = new HttpRequestMessage(HttpMethod.Get, station.Url("api/report"));
        request.Headers.Add("X-API-Key", Key);

        string body = await (await station.Client.SendAsync(request)).Content.ReadAsStringAsync();

        body.Should().NotContain(Key).And.NotContain(Token).And.NotContain(Password);
        body.Should().Contain("publish", "the rest of the configuration is there");
    }

    // ---------------------------------------------------------------- the page's report model

    [Fact]
    public void A_Short_Report_Becomes_One_Link_With_Every_Part_In_It()
    {
        Engine js = Report();

        JsonNode built = Build(js, what: "It stopped decoding", logs: ["a", "b"]);

        string url = built["url"]!.GetValue<string>();
        url.Should().StartWith("https://github.com/packet-net/pdn-soundmodem/issues/new?template=problem.yml&");
        Param(url, "what-happened").Should().Be("It stopped decoding");
        Param(url, "version").Should().Contain("0.103.0").And.Contain("commit 0123456789ab");
        Param(url, "device-kind").Should().Be("alsa");
        Param(url, "configuration").Should().Contain("\"device\": \"null\"");
        Param(url, "logs").Should().Be("a\nb");
        Param(url, "title").Should().Be("Problem: It stopped decoding");
        built["left"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void An_Unticked_Part_Is_Not_In_The_Link()
    {
        JsonNode built = Build(Report(), what: "x", logs: ["a"], config: false, includeLogs: false);

        string url = built["url"]!.GetValue<string>();
        url.Should().NotContain("configuration=").And.NotContain("logs=");
        built["left"]!.AsArray().Should().BeEmpty("a part left out by choice is not one that did not fit");
    }

    [Fact]
    public void Long_Log_Lines_Are_Left_Out_Of_The_Link_And_Handed_Back_For_The_Clipboard()
    {
        string[] logs = [.. Enumerable.Range(0, 400).Select(i => $"12:00:00 a typical line of console output number {i}")];

        JsonNode built = Build(Report(), what: "stuck", logs: logs);

        built["url"]!.GetValue<string>().Length.Should().BeLessThanOrEqualTo(7500);
        built["url"]!.GetValue<string>().Should().Contain("configuration=", "the configuration still fits");
        built["url"]!.GetValue<string>().Should().NotContain("logs=");
        JsonArray left = built["left"]!.AsArray();
        left.Should().ContainSingle();
        left[0]!["id"]!.GetValue<string>().Should().Be("logs");
        left[0]!["text"]!.GetValue<string>().Should().Contain("number 399");
    }

    [Fact]
    public void Both_Are_Left_Out_When_Neither_Fits()
    {
        string[] logs = [.. Enumerable.Range(0, 400).Select(i => $"12:00:00 UTC console line {i} of a busy station, with some detail")];

        JsonNode built = Build(Report(), what: "stuck", logs: logs, configPadding: 9000);

        built["url"]!.GetValue<string>().Length.Should().BeLessThanOrEqualTo(7500);
        built["left"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).Should().BeEquivalentTo(["logs", "configuration"], "both are too big to fit");
    }

    [Fact]
    public void A_Smaller_Part_Goes_Back_In_When_It_Fits_So_Less_Has_To_Be_Pasted()
    {
        // Configuration too big for the link, logs small: only the configuration is left out.
        JsonNode built = Build(Report(), what: "stuck", logs: ["only a few", "short lines"], configPadding: 9000);

        string url = built["url"]!.GetValue<string>();
        url.Length.Should().BeLessThanOrEqualTo(7500);
        url.Should().Contain("logs=").And.NotContain("configuration=");
        built["left"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).Should().Equal("configuration");
    }

    [Fact]
    public void A_Part_Dropped_For_A_Larger_One_Is_Tried_Again_Smallest_First()
    {
        // Logs 5000 characters, configuration 3000: both do not fit, the smaller configuration does.
        string[] logs = [.. Enumerable.Range(0, 150).Select(i => $"console line {i:D3} of a station")];

        JsonNode built = Build(Report(), what: "x", logs: logs, configPadding: 3000);

        string url = built["url"]!.GetValue<string>();
        url.Length.Should().BeLessThanOrEqualTo(7500);
        url.Should().Contain("configuration=");
        built["left"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).Should().Equal("logs");
    }

    [Fact]
    public void A_Very_Long_Description_Is_Left_Out_Of_The_Link_And_Handed_Back()
    {
        string what = "It does not work. " + string.Concat(Enumerable.Repeat("More and more detail about it. ", 400));

        JsonNode built = Build(Report(), what: what, logs: ["a"]);

        string url = built["url"]!.GetValue<string>();
        url.Length.Should().BeLessThanOrEqualTo(7500);
        url.Should().NotContain("what-happened=");
        url.Should().Contain("title=Problem", "the title is short and stays");
        JsonNode first = built["left"]!.AsArray()[0]!;
        first["id"]!.GetValue<string>().Should().Be("what-happened");
        first["label"]!.GetValue<string>().Should().Be("What happened?");
        first["text"]!.GetValue<string>().Should().StartWith("It does not work.");
    }

    [Fact]
    public void No_Combination_Of_Long_Parts_Ever_Makes_A_Link_Over_The_Limit()
    {
        foreach (int words in new[] { 10, 300, 600 })
        {
            foreach (int padding in new[] { 0, 2500, 9000 })
            {
                string what = string.Concat(Enumerable.Repeat("detail ", words));
                string[] logs = [.. Enumerable.Range(0, words).Select(i => $"line {i}")];

                JsonNode built = Build(Report(), what, logs, configPadding: padding);

                built["url"]!.GetValue<string>().Length.Should().BeLessThanOrEqualTo(7500, $"words {words}, padding {padding}");
            }
        }
    }

    [Fact]
    public void Special_Characters_Survive_The_Link()
    {
        JsonNode built = Build(Report(), what: "100% & \"quotes\" + a=b#c\nsecond line", logs: []);

        Param(built["url"]!.GetValue<string>(), "what-happened").Should().Be("100% & \"quotes\" + a=b#c\nsecond line");
    }

    [Fact]
    public void The_Copied_Report_Has_Every_Ticked_Part_And_Only_Those()
    {
        Engine js = Report();
        js.Execute("var R1 = " + ReportJson(["boot", "ready"], 0));

        string all = js.Evaluate("ReportModel.plain(R1, ' it broke ', {config: true, logs: true})").AsString();
        string bare = js.Evaluate("ReportModel.plain(R1, 'it broke', {config: false, logs: false})").AsString();

        all.Should().Contain("## What went wrong\n\nit broke\n").And.Contain("0.103.0").And.Contain("\"device\": \"null\"").And.Contain("boot\nready");
        bare.Should().Contain("0.103.0").And.NotContain("Configuration").And.NotContain("Recent log lines");
    }

    [Fact]
    public void The_Report_Model_Is_Plain_Script()
    {
        string model = ReportModelScript();

        model.Should().NotContain("document.").And.NotContain("window.").And.NotContain("fetch(");
    }

    // ---------------------------------------------------------------- the issue form

    [Fact]
    public void The_Issue_Form_Parses_And_Every_Field_The_Page_Fills_In_Exists_In_It()
    {
        IReadOnlyList<FormElement> body = ReadIssueForm(File.ReadAllText(
            Path.Combine(RepoRoot(), ".github", "ISSUE_TEMPLATE", "problem.yml")), out Dictionary<string, string> top);

        top.Should().ContainKeys("name", "description", "body");
        var ids = body.Where(e => e.Type != "markdown").Select(e => e.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        body.Where(e => e.Type != "markdown").Should().OnlyContain(e => e.Id != null && e.Label != null);

        Engine js = Report();
        foreach (string field in js.Evaluate("Object.keys(ReportModel.fields).map(function (k) { return ReportModel.fields[k]; })")
            .AsArray().Select(v => v.AsString()))
        {
            ids.Should().Contain(field, "the page pre-fills it");
        }

        js.Evaluate("ReportModel.template").AsString().Should().Be("problem.yml");
        body.Single(e => e.Id == "what-happened").Required.Should().BeTrue("a report with nothing in it is no use");
    }

    [Fact]
    public void The_Page_Fills_In_Only_Fields_That_Exist_In_The_Form()
    {
        // The other direction: every id the model puts in a link is one the form has.
        JsonNode built = Build(Report(), what: "x", logs: ["a"]);
        var ids = ReadIssueForm(File.ReadAllText(Path.Combine(RepoRoot(), ".github", "ISSUE_TEMPLATE", "problem.yml")), out _)
            .Select(e => e.Id).ToList();

        foreach (string pair in new Uri(built["url"]!.GetValue<string>()).Query.TrimStart('?').Split('&'))
        {
            string name = pair.Split('=')[0];
            if (name is not ("template" or "title"))
            {
                ids.Should().Contain(name);
            }
        }
    }

    /// <summary>One element of the form's <c>body</c>.</summary>
    private sealed record FormElement(string Type, string? Id, string? Label, bool Required);

    /// <summary>
    /// Reads the shape GitHub issue forms take, which is a small subset of YAML: top-level keys,
    /// and a <c>body</c> list of elements with <c>type</c>, <c>id</c>, an <c>attributes</c> map
    /// and a <c>validations</c> map. A line that does not fit that fails the test, so a form
    /// that has gone out of shape is caught here and not on github.com. (No YAML library is
    /// a dependency of this repo, and one is not worth adding for this file.)
    /// </summary>
    private static IReadOnlyList<FormElement> ReadIssueForm(string yaml, out Dictionary<string, string> top)
    {
        yaml.Should().NotContain("\t", "YAML does not allow tabs for indentation");
        top = [];
        var elements = new List<FormElement>();
        string? type = null, id = null, label = null;
        bool required = false, inBody = false, inBlockText = false;
        int blockIndent = 0;
        string section = "";

        void Flush()
        {
            if (type is not null)
            {
                elements.Add(new FormElement(type, id, label, required));
            }

            type = id = label = null;
            required = false;
        }

        foreach (string raw in yaml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int indent = line.Length - line.TrimStart().Length;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (inBlockText && indent > blockIndent)
            {
                continue;
            }

            inBlockText = false;
            string text = line.Trim();
            if (indent == 0)
            {
                Match top0 = Regex.Match(text, @"^([a-z]+):\s*(.*)$");
                top0.Success.Should().BeTrue($"top-level line {text}");
                top[top0.Groups[1].Value] = top0.Groups[2].Value;
                inBody = top0.Groups[1].Value == "body";
                continue;
            }

            inBody.Should().BeTrue($"only body has nested lines: {text}");
            if (text.StartsWith("- type:", StringComparison.Ordinal))
            {
                Flush();
                type = text["- type:".Length..].Trim();
                section = "";
                continue;
            }

            Match kv = Regex.Match(text, @"^([a-z]+):\s*(.*)$");
            kv.Success.Should().BeTrue($"a key: value line, got {text}");
            string key = kv.Groups[1].Value, value = kv.Groups[2].Value.Trim();
            if (value is "|" or ">")
            {
                inBlockText = true;
                blockIndent = indent;
            }

            if (indent == 4 && key is "attributes" or "validations")
            {
                section = key;
            }
            else if (indent == 4 && key == "id")
            {
                id = value;
            }
            else if (section == "attributes" && key == "label")
            {
                label = value;
            }
            else if (section == "validations" && key == "required")
            {
                required = value == "true";
            }
        }

        Flush();
        return elements;
    }

    // ---------------------------------------------------------------- helpers

    private static string ReportModelScript()
    {
        string page = Encoding.UTF8.GetString(ConfigPage.Bytes);
        Match block = ReportBlock().Match(page);
        block.Success.Should().BeTrue("the page keeps the report logic in <script id=\"report-model\">");
        return block.Groups[1].Value;
    }

    private static Engine Report()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(20)));
        engine.Execute(ReportModelScript());
        return engine;
    }

    private static string ReportJson(IEnumerable<string> logs, int configPadding) =>
        new JsonObject
        {
            ["version"] = "0.103.0",
            ["commit"] = "0123456789abcdef0123456789abcdef01234567",
            ["release"] = true,
            ["deviceKind"] = "alsa",
            ["uptimeSeconds"] = 4000,
            ["config"] = new JsonObject
            {
                ["device"] = "null",
                ["note"] = new string('n', configPadding),
            },
            ["logLines"] = new JsonArray([.. logs.Select(l => (JsonNode)JsonValue.Create(l)!)]),
        }.ToJsonString();

    private static JsonNode Build(Engine js, string what, IEnumerable<string> logs,
        bool config = true, bool includeLogs = true, int configPadding = 0)
    {
        js.Execute("var REPORT = " + ReportJson(logs, configPadding) + ";");
        js.SetValue("WHAT", what);
        js.Execute($"var BUILT = ReportModel.build(REPORT, WHAT, {{config: {(config ? "true" : "false")}, logs: {(includeLogs ? "true" : "false")}}});");
        return JsonNode.Parse(js.Evaluate("JSON.stringify({url: BUILT.url, left: BUILT.left})").AsString())!;
    }

    private static string Param(string url, string name)
    {
        foreach (string pair in new Uri(url).Query.TrimStart('?').Split('&'))
        {
            string[] kv = pair.Split('=', 2);
            if (kv[0] == name)
            {
                return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
        }

        throw new InvalidOperationException($"no {name} in {url}");
    }

    [GeneratedRegex("<script id=\"report-model\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ReportBlock();
}
