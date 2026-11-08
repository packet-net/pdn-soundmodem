using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jint;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The configuration page at <c>/config</c>, and what keeps it in step with the file.
/// </summary>
/// <remarks>
/// <para>The page has no list of settings of its own; it draws one field per key of
/// <c>/api/config/schema</c>, which <see cref="ConfigSchema"/> reads off the config classes. So
/// drift can only happen in three places, and each has a test here: the schema missing a key the
/// file can carry, the page being unable to draw a kind of key, and a value changing on its way
/// through a field and back.</para>
/// <para>The page's form logic is the <c>config-model</c> script block of the page itself, run
/// here in Jint. The suite runs on machines with no browser and no node, and a copy of the logic
/// in C# would be a second thing to drift.</para>
/// </remarks>
public partial class ConfigPageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-configpage").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------- the schema against the classes

    [Fact]
    public void The_Schema_Has_Every_Key_The_Config_Classes_Can_Be_Given_And_No_Other()
    {
        // Walked here with plain reflection, not the serialiser metadata the schema is built
        // from, so the two walks are independent: one forgetting a kind of property shows up as
        // a difference instead of as two lists that agree on being wrong.
        var expected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(DaemonConfig)]);
        seen.Add(typeof(DaemonConfig));
        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is not { IsPublic: true }
                    || property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null
                    || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }

                string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
                expected.Add($"{type.Name}.{name}");
                foreach (Type nested in ConfigClassesIn(property.PropertyType))
                {
                    if (seen.Add(nested))
                    {
                        pending.Enqueue(nested);
                    }
                }
            }
        }

        expected.Should().HaveCountGreaterThan(150, "the walk found almost nothing, so it is broken");

        JsonObject schema = ConfigSchema.Build();
        var served = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string type, JsonNode? body) in schema["types"]!.AsObject())
        {
            foreach (JsonNode? key in body!["keys"]!.AsArray())
            {
                served.Add($"{type}.{key!["name"]!.GetValue<string>()}");
            }
        }

        served.Except(expected, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "the schema offers keys no config class has");
        expected.Except(served, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "every key a config class takes has to be on the configuration page");
    }

    [Fact]
    public void Every_Key_On_The_Page_Has_Its_Help_From_The_Configuration_Reference()
    {
        // The help beside each field is the reference page's own row for the key. A key with no
        // row is a key the reference does not describe where the page looks for it: give it a
        // row in its section's table (docs/reference/config.md).
        var missing = new List<string>();
        foreach ((string type, JsonNode? body) in ConfigSchema.Build()["types"]!.AsObject())
        {
            foreach (JsonNode? key in body!["keys"]!.AsArray())
            {
                if (key!["help"] is null)
                {
                    missing.Add($"{type}.{key["name"]}");
                }
            }
        }

        missing.Should().BeEmpty(
            "every key needs a row in its section of docs/reference/config.md, which is where "
            + "the page's help text comes from; missing: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void The_Secrets_Are_Marked_So_The_Page_Asks_For_Them_As_Passwords()
    {
        JsonObject types = ConfigSchema.Build()["types"]!.AsObject();

        foreach (string path in ConfigApi.SecretPaths)
        {
            string[] parts = path.Split('.');
            string type = nameof(DaemonConfig);
            JsonNode? key = null;
            foreach (string part in parts)
            {
                key = types[type]!["keys"]!.AsArray()
                    .Single(k => string.Equals(k!["name"]!.GetValue<string>(), part, StringComparison.OrdinalIgnoreCase));
                type = key!["ref"]?.GetValue<string>() ?? "";
            }

            key!["secret"]?.GetValue<bool>().Should().BeTrue($"{path} is never served, so the page must not show it");
        }
    }

    [Fact]
    public void A_Document_With_Every_Key_Set_Is_One_The_Daemon_Reads()
    {
        // The kinds the schema gives keys are what the page parses text into. If the schema says
        // "integer" for a key the deserialiser wants as a string, every value the page writes for
        // it is refused; reading a document with every key set from the schema proves the kinds.
        string json = EveryKeySet().ToJsonString();

        Action read = () => JsonSerializer.Deserialize<DaemonConfig>(json, DaemonConfig.Options);

        read.Should().NotThrow();
    }

    // ---------------------------------------------------------------- the page's own form model

    [Fact]
    public void The_Page_Draws_A_Field_For_Every_Key_In_The_Schema()
    {
        string schema = ConfigSchema.Json;
        Engine js = Model();
        js.SetValue("schemaJson", schema);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var doc = ConfigModel.fullDocument(schema);
              var drawn = {}, unsupported = [];
              function walk(nodes, type) {
                nodes.forEach(function (n) {
                  if (n.node === "unknown") return;
                  drawn[type + "." + n.key.name] = true;
                  if (n.node === "unsupported") unsupported.push(n.path + " (" + n.key.kind + ")");
                  if (n.node === "section") walk(n.children, n.key.ref);
                  if (n.node === "list") n.items.forEach(function (i) { walk(i.children, n.key.items.ref); });
                });
              }
              walk(ConfigModel.plan(schema, schema.root, doc, ""), schema.root);
              var missing = [];
              for (var t in schema.types) {
                schema.types[t].keys.forEach(function (k) { if (!drawn[t + "." + k.name]) missing.push(t + "." + k.name); });
              }
              return JSON.stringify({ missing: missing, unsupported: unsupported });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["unsupported"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEmpty(
            "the page has no way to draw these; teach ConfigModel.plan the kind");
        result["missing"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEmpty(
            "every key must have a field somewhere on the page");
    }

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        data.Add(File.ReadAllText(Path.Combine(RepoRoot(), "soundmodem.example.json")));

        // Every example on the reference page that is a whole configuration. They are the files
        // people copy, so they are the ones whose values most need to survive the page.
        string page = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "reference", "config.md"));
        foreach (Match block in JsonBlock().Matches(page))
        {
            try
            {
                if (JsonNode.Parse(block.Groups[1].Value, null,
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    is JsonObject)
                {
                    data.Add(block.Groups[1].Value);
                }
            }
            catch (JsonException)
            {
                // A fragment with "..." in it, not a document.
            }
        }

        data.Add(EveryKeySet().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_Value_Comes_Back_Out_Of_Its_Field_As_It_Went_In(string document)
    {
        // Each field's text, written back through the page's own parser, onto a copy of the
        // document: the copy has to come out as the original. This is the field the operator
        // retypes or edits and puts back, not the untouched one, which the page leaves alone.
        // Null and an empty list both show as an empty field and come back as the key absent,
        // which the daemon reads the same way, so those are the one allowed difference.
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("documentText", document);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              ConfigModel.setHidden(schema.hidden);
              var doc = ConfigModel.parseDocument(documentText);
              var copy = JSON.parse(JSON.stringify(doc));
              var errors = [];
              function walk(nodes) {
                nodes.forEach(function (n) {
                  if (n.node === "field") {
                    var e = ConfigModel.writeField(copy, n.path, n.key, n.text);
                    if (e) errors.push(n.path + ": " + e + " (text " + JSON.stringify(n.text) + ")");
                  }
                  if (n.node === "section" && n.present) walk(n.children);
                  if (n.node === "list") n.items.forEach(function (i) { walk(i.children); });
                });
              }
              walk(ConfigModel.plan(schema, schema.root, doc, ""));
              function norm(v) {
                if (Array.isArray(v)) return v.map(norm);
                if (v && typeof v === "object") {
                  var o = {};
                  Object.keys(v).sort().forEach(function (k) {
                    var x = v[k];
                    if (x === null || (Array.isArray(x) && x.length === 0)) return;
                    o[k] = norm(x);
                  });
                  return o;
                }
                return v;
              }
              return JSON.stringify({ errors: errors, before: norm(doc), after: norm(copy) });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["errors"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEmpty(
            "a field's own text must always be a value it accepts");
        result["after"]!.ToJsonString().Should().Be(result["before"]!.ToJsonString());
    }

    [Fact]
    public void An_Empty_Secret_Field_Keeps_The_Stored_Secret_And_A_Typed_One_Replaces_It()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              ConfigModel.setHidden(schema.hidden);
              var key = schema.types.ApiConfig.keys.filter(function (k) { return k.name === "key"; })[0];
              var kept = { api: { key: schema.hidden } };
              ConfigModel.writeField(kept, "api.key", key, "");
              var typed = { api: { key: schema.hidden } };
              ConfigModel.writeField(typed, "api.key", key, "new-key");
              return JSON.stringify([kept.api.key, typed.api.key]);
            })()
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal(ConfigApi.Hidden, "new-key");
    }

    [Fact]
    public void A_Key_Spelled_Differently_In_The_File_Is_Edited_Where_It_Is()
    {
        // The daemon reads keys without regard to case, so "KissPort" in a file is kissPort. An
        // edit has to change that key, not add a second one beside it.
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var key = schema.types.DaemonConfig.keys.filter(function (k) { return k.name === "kissPort"; })[0];
              var doc = { KissPort: 8105 };
              ConfigModel.writeField(doc, "kissPort", key, "8200");
              return JSON.stringify(doc);
            })()
            """).AsString();

        answer.Should().Be("""{"KissPort":8200}""");
    }

    [Theory]
    [InlineData("integer", "12x", false)]
    [InlineData("integer", "1.5", false)]
    [InlineData("integer", "-3", true)]
    [InlineData("number", "7.0516e6", true)]
    [InlineData("number", "fast", false)]
    [InlineData("boolean", "yes", false)]
    public void A_Field_Refuses_Text_That_Is_Not_A_Value_Of_Its_Kind(string kind, string text, bool accepted)
    {
        Engine js = Model();
        js.SetValue("kind", kind);
        js.SetValue("text", text);

        bool error = js.Evaluate("!!ConfigModel.fromText({ kind: kind }, text).error").AsBoolean();

        error.Should().Be(!accepted);
    }

    [Fact]
    public void Pasted_Jsonc_Is_Read_With_Its_Comments_And_Trailing_Commas_Removed()
    {
        Engine js = Model();
        js.SetValue("text", """
            {
              // the station
              "device": "plughw:1,0", /* the CM108 */
              "title": "a // in a string is not a comment",
              "modems": [ { "subChannel": 0, }, ],
            }
            """);

        string answer = js.Evaluate("JSON.stringify(ConfigModel.parseDocument(text))").AsString();

        answer.Should().Be(
            """{"device":"plughw:1,0","title":"a // in a string is not a comment","modems":[{"subChannel":0}]}""");
    }

    // ---------------------------------------------------------------- the server half

    [Fact]
    public void A_Placeholder_Posted_Back_Keeps_The_Stored_Secret()
    {
        const string running = """
            {
              // comments as a real file has them
              "api": { "key": "the-real-key" },
              "publish": { "url": "wss://x/uplink", "token": "pdnsm_token", "callsign": "GB7RDG-2" }
            }
            """;
        string posted = ConfigApi.Redact(running).Replace("GB7RDG-2", "GB7RDG-3", StringComparison.Ordinal);

        string? restored = ConfigApi.RestoreHidden(posted, running, out string why);

        why.Should().BeEmpty();
        JsonNode root = JsonNode.Parse(restored!)!;
        root["api"]!["key"]!.GetValue<string>().Should().Be("the-real-key");
        root["publish"]!["token"]!.GetValue<string>().Should().Be("pdnsm_token");
        root["publish"]!["callsign"]!.GetValue<string>().Should().Be("GB7RDG-3", "the edit is kept");
    }

    [Fact]
    public void A_Placeholder_Does_Not_Follow_Its_Secret_To_A_New_Address()
    {
        // With the placeholder kept wherever it is posted, anyone who could post a configuration
        // could point publish.url at their own server and have the station send them the token.
        const string running = """{"publish": {"url": "wss://site/uplink", "token": "pdnsm_token"}}""";

        string? moved = ConfigApi.RestoreHidden(
            ConfigApi.Redact(running).Replace("wss://site/uplink", "wss://evil.example/", StringComparison.Ordinal),
            running, out string why);

        moved.Should().BeNull();
        why.Should().Contain("publish.url").And.Contain("publish.token");
    }

    [Fact]
    public void Every_Spelling_Of_A_Secret_Is_Hidden()
    {
        // The file is read without regard to case and the last spelling wins, so hiding only the
        // first served the key in use.
        string served = ConfigApi.Redact("""{"api": {"key": "a", "Key": "REALKEY"}, "Publish": {"token": "T2"}}""");

        served.Should().NotContain("REALKEY").And.NotContain("T2");
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("radio1", true)]
    [InlineData("RADIO1.lan", true)]
    [InlineData("radio1.local.", true)]
    [InlineData("radio1.home.arpa", true)]
    [InlineData("radio1.shack.example.org", true)]
    [InlineData("evil.example", false)]
    [InlineData("radio10", false)]
    [InlineData("radio1.evil.example", false)]
    public void Open_Configuration_Answers_Only_To_This_Machines_Own_Names(string host, bool answered)
    {
        // DNS rebinding (#423) gets past the same-site check, since the hostile page's Origin and
        // Host are both its own name. That name is never this machine's, and a name that merely
        // starts with this machine's is not either: anyone can point radio1.evil.example here.
        ConfigApi.IsOwnName(host, "radio1", "shack.example.org").Should().Be(answered);
    }

    [Fact]
    public void A_Secret_Spelled_Twice_Keeps_Each_Spelling_Its_Own_Value()
    {
        // The station runs on the last spelling. A round trip must not copy the first one over it.
        const string running = """{"api": {"key": "a", "Key": "REALKEY"}}""";

        string restored = ConfigApi.RestoreHidden(ConfigApi.Redact(running), running, out _)!;

        JsonNode.Parse(restored)!["api"]!["Key"]!.GetValue<string>().Should().Be("REALKEY");
    }

    [Fact]
    public async Task A_Public_Page_With_No_Key_Serves_No_Configuration()
    {
        // A public page is for visitors on the open internet; "private home networks" is not
        // about it, so there the configuration wants a key like everything else.
        using var station = new Station(_dir, key: "", openConfiguration: false);

        (await station.Client.GetAsync(station.Url("api/config"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await station.Client.GetAsync(station.Url("config"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Every_Key_Named_Like_A_Secret_Is_Treated_As_One()
    {
        // SecretPaths is a list kept by hand. A new password or token in a config class would be
        // drawn as plain text and served in the clear on an open station, and no other test
        // would notice; this one does. A key that only looks like a secret goes in NotSecrets
        // with the reason.
        string[] notSecrets =
        [
            // The SHA-256 of a token, which is what the monitor keeps so that it holds no token.
            "monitor.uplinks.tokenSha256",
        ];

        var suspicious = new List<string>();
        JsonObject types = ConfigSchema.Build()["types"]!.AsObject();
        void Walk(string type, string path, int depth)
        {
            foreach (JsonNode? key in types[type]!["keys"]!.AsArray())
            {
                string name = key!["name"]!.GetValue<string>();
                string at = path.Length == 0 ? name : $"{path}.{name}";
                if (SecretLike().IsMatch(name) && !ConfigApi.IsSecret(at)
                    && !notSecrets.Contains(at, StringComparer.OrdinalIgnoreCase))
                {
                    suspicious.Add(at);
                }

                string? next = key["ref"]?.GetValue<string>() ?? key["items"]?["ref"]?.GetValue<string>();
                if (next is not null && depth < 6)
                {
                    Walk(next, at, depth + 1);
                }
            }
        }

        Walk(nameof(DaemonConfig), "", 0);

        suspicious.Should().BeEmpty(
            "add each to ConfigApi's secrets so it is never served, or to notSecrets here: {0}",
            string.Join(", ", suspicious));
    }

    [Fact]
    public void A_Placeholder_With_Nothing_Stored_Behind_It_Is_Refused()
    {
        string? restored = ConfigApi.RestoreHidden(
            $$$"""{"api": {"key": "{{{ConfigApi.Hidden}}}"}}""", "{}", out string why);

        restored.Should().BeNull();
        why.Should().Contain("api.key");
    }

    [Fact]
    public void A_Document_With_No_Placeholder_Is_Posted_Exactly_As_It_Came()
    {
        const string posted = "{ // mine\n \"device\": \"null\" }";

        ConfigApi.RestoreHidden(posted, "{}", out _).Should().Be(posted);
    }

    [Fact]
    public void A_Commented_Configuration_Is_Served_Rather_Than_An_Empty_One()
    {
        // The running configuration is the file's own text, and a strict parse threw at the first
        // comment: GET /api/config served {} for every commented file there is.
        string served = ConfigApi.Redact("""
            {
              // the CM108
              "device": "plughw:1,0",
            }
            """);

        JsonNode.Parse(served)!["device"]!.GetValue<string>().Should().Be("plughw:1,0");
    }

    [Fact]
    public void An_Ubersdr_Password_Is_Not_Served()
    {
        string served = ConfigApi.Redact("""{"ubersdr": {"password": "hunter2"}}""");

        served.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task Without_A_Key_The_Configuration_Is_Open_To_The_Network()
    {
        using var station = new Station(_dir, key: "");

        HttpResponseMessage read = await station.Client.GetAsync(station.Url("api/config"));

        read.StatusCode.Should().Be(HttpStatusCode.OK, "Tom, 2026-11-06: fully open on a station with no key");
    }

    [Fact]
    public async Task Without_A_Key_A_Post_From_Another_Site_Is_Still_Refused()
    {
        // Open to a person on the network, not to whatever page their browser has open: a page on
        // another site posting a configuration at a LAN address is the attack this stops.
        using var station = new Station(_dir, key: "");
        using var request = new HttpRequestMessage(HttpMethod.Post, station.Url("api/config?check=true"))
        {
            Content = new StringContent("""{"device": "null"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "http://evil.example");

        HttpResponseMessage answer = await station.Client.SendAsync(request);

        answer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task With_A_Key_The_Configuration_Wants_It_But_The_Page_Does_Not()
    {
        using var station = new Station(_dir, key: "sekrit");

        (await station.Client.GetAsync(station.Url("api/config"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        HttpResponseMessage page = await station.Client.GetAsync(station.Url("config"));
        page.StatusCode.Should().Be(HttpStatusCode.OK, "the page asks for the key itself");
        (await page.Content.ReadAsStringAsync()).Should().Contain("config-model");

        (await station.Client.GetAsync(station.Url("api/config/schema"))).StatusCode
            .Should().Be(HttpStatusCode.OK, "the description holds no secret");
    }

    [Fact]
    public async Task A_Check_Says_Whether_The_Station_Would_Start_And_Changes_Nothing()
    {
        using var station = new Station(_dir, key: "");
        string before = File.ReadAllText(station.ConfigPath);

        HttpResponseMessage good = await station.Client.PostAsync(
            station.Url("api/config?check=true"),
            new StringContent("""{"device": "null", "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""", Encoding.UTF8));
        HttpResponseMessage bad = await station.Client.PostAsync(
            station.Url("api/config?check=true"),
            new StringContent("""{"device": "null", "modems": [{"subChannel": 0, "mode": "bpsk300 "}]}""", Encoding.UTF8));

        good.StatusCode.Should().Be(HttpStatusCode.OK, await good.Content.ReadAsStringAsync());
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("unknown mode");
        File.ReadAllText(station.ConfigPath).Should().Be(before);
        File.Exists(station.PendingPath).Should().BeFalse();
        station.Restarts.Should().Be(0);
    }

    [Fact]
    public async Task Saving_From_The_Page_Keeps_The_Key_The_Page_Was_Never_Shown()
    {
        using var station = new Station(_dir, key: "sekrit");
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");

        JsonNode served = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config")))!;
        JsonNode running = served["running"]!;
        running["api"]!["key"]!.GetValue<string>().Should().Be(ConfigApi.Hidden);
        running["kissPort"] = 8200;

        HttpResponseMessage saved = await station.Client.PostAsync(
            station.Url("api/config?persist=true"),
            new StringContent(running.ToJsonString(), Encoding.UTF8, "application/json"));

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        JsonNode file = JsonNode.Parse(File.ReadAllText(station.ConfigPath))!;
        file["api"]!["key"]!.GetValue<string>().Should().Be("sekrit");
        file["kissPort"]!.GetValue<int>().Should().Be(8200);

        // The answer is written before the restart is asked for, on purpose (the caller hears
        // the outcome first), so the restart can land a moment after the response does.
        for (int i = 0; i < 100 && station.Restarts == 0; i++)
        {
            await Task.Delay(50);
        }

        station.Restarts.Should().Be(1);
    }

    // ---------------------------------------------------------------- labels, areas, conditions

    [Fact]
    public void Every_Key_In_The_Schema_Has_A_Label_And_No_Entry_Names_A_Key_That_Is_Gone()
    {
        // ConfigKeys is the part of the page a person writes. A key with no entry would show on
        // the page under its raw name, and an entry for a key that was renamed or removed is a
        // label nothing uses; both are caught here, so neither survives a change to a config class.
        var served = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string type, JsonNode? body) in ConfigSchema.Build()["types"]!.AsObject())
        {
            foreach (JsonNode? key in body!["keys"]!.AsArray())
            {
                served.Add($"{type}.{key!["name"]!.GetValue<string>()}");
            }
        }

        served.Except(ConfigKeys.Table.Keys).Should().BeEmpty(
            "every key needs an entry in ConfigKeys: a plain label, a unit if it has one, whether it is advanced");
        ConfigKeys.Table.Keys.Except(served).Should().BeEmpty(
            "these ConfigKeys entries name a key no config class has any more");
    }

    [Fact]
    public void Every_Top_Level_Key_Has_A_Known_Area_And_No_Other_Key_Has_One()
    {
        // A nested key is on the page of the top-level key it sits under, so it has no area of
        // its own; a second answer would be one the page could not honour.
        string[] areas = [.. ConfigKeys.Areas.Select(a => a.Id)];
        areas.Should().Contain(["radio", "keying", "modes", "software", "sharing", "advanced", "monitor"]);

        foreach ((string name, ConfigKeys.Entry entry) in ConfigKeys.Table)
        {
            if (name.StartsWith("DaemonConfig.", StringComparison.Ordinal))
            {
                areas.Should().Contain(entry.Area ?? "", $"{name} needs one of the page's areas");
            }
            else
            {
                entry.Area.Should().BeNull($"{name} is not top level, so it follows the key it is under");
            }
        }
    }

    [Fact]
    public void Labels_Are_Plain_Words_With_Units_Kept_Apart()
    {
        foreach ((string name, ConfigKeys.Entry entry) in ConfigKeys.Table)
        {
            entry.Label.Trim().Should().NotBeEmpty($"{name} needs a label");
            entry.Label.Should().NotEndWith(".", $"{name}: a label is not a sentence");
            entry.Label.Should().NotContainAny(["(Hz)", "(dB)", "(s)", "(ms)"], $"{name}: the unit is its own field");
            entry.Label.Should().MatchRegex("^[A-Z0-9]", $"{name}: labels start with a capital");
        }
    }

    [Fact]
    public void The_Schema_Serves_The_Labels_The_Areas_And_The_Rules_The_Page_Draws_From()
    {
        JsonObject schema = ConfigSchema.Build();

        schema["areas"]!.AsArray().Select(a => a!["id"]!.GetValue<string>())
            .Should().Equal(ConfigKeys.Areas.Select(a => a.Id));
        JsonNode flex = schema["types"]!["DaemonConfig"]!["keys"]!.AsArray()
            .Single(k => k!["name"]!.GetValue<string>() == "flex")!;
        flex["label"]!.GetValue<string>().Should().Be("FlexRadio");
        flex["area"]!.GetValue<string>().Should().Be("radio");
        flex["when"]!["deviceKind"]!.AsArray().Select(k => k!.GetValue<string>()).Should().Equal("flex");
        JsonNode freq = schema["types"]!["ModemConfig"]!["keys"]!.AsArray()
            .Single(k => k!["name"]!.GetValue<string>() == "frequency")!;
        freq["unit"]!.GetValue<string>().Should().Be("Hz");
    }

    [Fact]
    public void Every_Key_In_The_Full_Document_Is_Drawn_In_Some_Area()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var doc = ConfigModel.fullDocument(schema);
              var groups = ConfigModel.areas(schema, doc);
              var drawn = {}, unknownArea = [];
              function walk(nodes, type) {
                nodes.forEach(function (n) {
                  if (n.node === "unknown") return;
                  drawn[type + "." + n.key.name] = true;
                  if (n.node === "section") walk(n.children, n.key.ref);
                  if (n.node === "list") n.items.forEach(function (i) { walk(i.children, n.key.items.ref); });
                });
              }
              schema.areas.forEach(function (a) {
                var g = groups[a.id];
                walk(g.fields.concat(g.sections), schema.root);
              });
              Object.keys(groups).forEach(function (id) {
                if (!schema.areas.some(function (a) { return a.id === id; })) unknownArea.push(id);
              });
              // The top-level keys sit in the root type; nested ones are walked from there.
              var missing = [];
              (function check(typeName, seen) {
                if (seen[typeName]) return;
                seen[typeName] = true;
                schema.types[typeName].keys.forEach(function (k) {
                  if (!drawn[typeName + "." + k.name]) missing.push(typeName + "." + k.name);
                  if (k.ref) check(k.ref, seen);
                  if (k.items && k.items.ref) check(k.items.ref, seen);
                });
              })(schema.root, {});
              return JSON.stringify({ missing: missing, unknownArea: unknownArea });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["unknownArea"]!.AsArray().Should().BeEmpty("every key's area is one of the page's");
        result["missing"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEmpty(
            "every key reachable in the full document has to be drawn under some area");
    }

    [Fact]
    public void A_Document_With_Every_Key_Set_Shows_Every_Key()
    {
        // A rule only hides a key the file does not set, so a file that sets everything leaves
        // nothing off the page.
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("documentText", EveryKeySet().ToJsonString());

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              ConfigModel.setHidden(schema.hidden);
              var doc = ConfigModel.parseDocument(documentText);
              var hidden = [];
              function walk(nodes) {
                nodes.forEach(function (n) {
                  if (n.node === "unknown") return;
                  if (!n.shown) hidden.push(n.path);
                  if (n.node === "section" && n.present) walk(n.children);
                  if (n.node === "list") n.items.forEach(function (i) { walk(i.children); });
                });
              }
              walk(ConfigModel.plan(schema, schema.root, doc, ""));
              return JSON.stringify(hidden);
            })()
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0", "alsa")]
    [InlineData("default", "alsa")]
    [InlineData("null", "alsa")]
    [InlineData("pipe:/tmp/in,/tmp/out", "pipe")]
    [InlineData("FLEX:10.0.0.4", "flex")]
    [InlineData("ubersdr:m9psy-1.instance.ubersdr.org", "ubersdr")]
    [InlineData("iqcard:hw:1,0", "iqcard")]
    [InlineData("nonsense:thing", "alsa")]
    public void The_Page_Tells_The_Kind_Of_Audio_Device_From_Its_Prefix(string device, string kind)
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("device", device);

        string answer = js.Evaluate(
            "ConfigModel.deviceKind(JSON.parse(schemaJson), { device: device })").AsString();

        answer.Should().Be(kind);
    }

    [Fact]
    public void A_Section_That_Cannot_Apply_Is_Left_Off_Unless_The_File_Sets_It()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              function shown(doc) {
                var out = {};
                ConfigModel.plan(schema, schema.root, doc, "").forEach(function (n) {
                  if (n.shown) out[n.key.name] = n.inactive ? "inactive" : "yes";
                });
                return out;
              }
              var alsa = shown({ device: "plughw:1,0" });
              var flex = shown({ device: "flex:10.0.0.4" });
              var leftover = shown({ device: "plughw:1,0", flex: { antenna: "ANT2" } });
              return JSON.stringify({
                alsaHasMixer: alsa.alsa, alsaHasFlex: alsa.flex, alsaHasUber: alsa.ubersdr,
                flexHasFlex: flex.flex, flexHasMixer: flex.alsa,
                leftover: leftover.flex
              });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["alsaHasMixer"]!.GetValue<string>().Should().Be("yes");
        result["alsaHasFlex"].Should().BeNull("a sound-card station has no Flex section to fill in");
        result["alsaHasUber"].Should().BeNull();
        result["flexHasFlex"]!.GetValue<string>().Should().Be("yes");
        result["flexHasMixer"].Should().BeNull("a Flex has no sound-card mixer");
        result["leftover"]!.GetValue<string>().Should().Be("inactive", "a section the file has is never hidden from its editor");
    }

    [Fact]
    public void A_Keying_Field_Appears_When_The_Keying_Method_Needs_It()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              function fields(ptt) {
                var node = ConfigModel.plan(schema, schema.root, { ptt: ptt }, "")
                  .filter(function (n) { return n.key && n.key.name === "ptt"; })[0];
                return node.children.filter(function (c) { return c.shown; }).map(function (c) { return c.key.name; });
              }
              return JSON.stringify({
                bare: fields({}), serial: fields({ type: "serial" }), cm108: fields({ type: "cm108" }),
                rigctld: fields({ type: "rigctld" })
              });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        Names(result["bare"]).Should().Equal("type", "device", "line");
        Names(result["serial"]).Should().Equal("type", "device", "line");
        Names(result["cm108"]).Should().Equal("type", "device", "gpio");
        Names(result["rigctld"]).Should().Equal("type");

        static string[] Names(JsonNode? array) => [.. array!.AsArray().Select(n => n!.GetValue<string>())];
    }

    [Fact]
    public void Areas_Group_The_Top_Level_Keys_As_The_Menu_Lists_Them()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var g = ConfigModel.areas(schema, { device: "null", modems: [{ mode: "bpsk300" }], mystery: 1 });
              var names = function (a) { return a.fields.concat(a.sections).map(function (n) { return n.key.name; }); };
              return JSON.stringify({
                radio: names(g.radio), modes: names(g.modes), software: names(g.software),
                unknown: g.advanced.unknown[0].names
              });
            })()
            """).AsString();

        JsonNode result = JsonNode.Parse(answer)!;
        result["radio"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(["device", "sideband", "flex"]);
        result["modes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("modems");
        result["software"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(["kissPort", "bind"]);
        result["unknown"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("mystery");
    }

    [Theory]
    [InlineData(
        "configuration error in /tmp/x.json\n  modem 0: unknown mode 'bpsk300 '. Check the spelling.\n\n  The service will not start until this is fixed.",
        "modem 0: unknown mode 'bpsk300 '. Check the spelling.")]
    [InlineData("a plain sentence", "a plain sentence")]
    public void The_Problem_Is_Taken_Out_Of_The_Daemons_Refusal(string refusal, string problem)
    {
        Engine js = Model();
        js.SetValue("refusal", refusal);

        js.Evaluate("ConfigModel.problemText(refusal)").AsString().Should().Be(problem);
    }

    [Theory]
    [InlineData("""{"device": "null", "modems": [{"subChannel": 0, "mode": "bpsk300 "}]}""",
        "modem 0: unknown mode 'bpsk300 '. Check the spelling against docs/05-modes.md.", "modems[0].mode")]
    [InlineData("""{"device": "null", "kissMaxFrameBytes": 3}""",
        "\"kissMaxFrameBytes\": 3 is below 17: raise it, or remove it for 8192.", "kissMaxFrameBytes")]
    [InlineData("""{"device": "null", "rig": {"rigctld": "x"}}""",
        "\"rig\".\"rigctld\" is \"x\": not an address. Write it as host:port.", "rig.rigctld")]
    [InlineData("""{"device": "null", "alsa": {"mixer": {"stateFile": "a"}}}""",
        "\"alsa\".\"mixer\".\"stateFile\" is \"a\", the configuration file itself: point it elsewhere.", "alsa.mixer.stateFile")]
    [InlineData("""{"device": "null", "modems": [{"subChannel": 0, "mode": "x"}, {"subChannel": 5, "mode": "ms110d-3"}]}""",
        "modem 5 has \"txAmplitude\": 2: use above 0 and at most 1, or remove it for 0.5.", "modems[1].txAmplitude")]
    [InlineData("""{"device": "null", "modems": []}""",
        "the file is quite wrong in a way that names no setting", null)]
    public void A_Refusal_Is_Matched_To_The_Setting_It_Is_About(string document, string refusal, string? path)
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("documentText", document);
        js.SetValue("refusal", refusal);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              return JSON.stringify(ConfigModel.locateIssue(schema, ConfigModel.parseDocument(documentText), refusal).path);
            })()
            """).AsString();

        answer.Should().Be(path is null ? "null" : $"\"{path}\"");
    }

    [Fact]
    public void Settings_Are_Named_In_Words_For_The_Pending_Changes_Bar()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var a = { kissPort: 8105, modems: [{ mode: "bpsk300", frequency: 1500 }], waterfall: { title: "a" } };
              var b = { kissPort: 8200, modems: [{ mode: "bpsk300", frequency: 1600 }], waterfall: { title: "b" } };
              return JSON.stringify(ConfigModel.changedLabels(schema, a, b));
            })()
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("KISS port", "Sub-channel 0: Audio frequency", "Page title (Station page)");
    }

    // ---------------------------------------------------------------- earlier versions

    [Fact]
    public async Task Every_Applied_Change_Is_Kept_And_Listed_Newest_First()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        using var station = new Station(_dir, key: "", time: clock);

        HttpResponseMessage oneRun = await station.Client.PostAsync(
            station.Url("api/config"), new StringContent(WorkableConfig(8201), Encoding.UTF8));
        clock.Advance(TimeSpan.FromMinutes(5));
        HttpResponseMessage saved = await station.Client.PostAsync(
            station.Url("api/config?persist=true"), new StringContent(WorkableConfig(8202), Encoding.UTF8));
        clock.Advance(TimeSpan.FromMinutes(5));
        HttpResponseMessage refused = await station.Client.PostAsync(
            station.Url("api/config"), new StringContent("""{"device": "null", "modems": [{"mode": "bpsk300 "}]}""", Encoding.UTF8));

        oneRun.StatusCode.Should().Be(HttpStatusCode.OK);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode list = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config/history")))!;
        JsonArray versions = list["versions"]!.AsArray();
        versions.Select(v => v!["kind"]!.GetValue<string>()).Should().Equal(
            ["saved", "one-run", "original"], "the first apply keeps where the station started");
        versions[0]!["time"]!.GetValue<string>().Should().StartWith("2026-11-08T10:05:00");
        versions[1]!["time"]!.GetValue<string>().Should().StartWith("2026-11-08T10:00:00");
        JsonNode original = JsonNode.Parse(await station.Client.GetStringAsync(
            station.Url($"api/config/history/{versions[2]!["id"]!.GetValue<string>()}")))!;
        original["config"]!["modems"]![0]!["mode"]!.GetValue<string>().Should().Be("bpsk300");
        versions[2]!["inUse"]!.GetValue<bool>().Should().BeFalse();

        JsonNode newest = JsonNode.Parse(await station.Client.GetStringAsync(
            station.Url($"api/config/history/{versions[0]!["id"]!.GetValue<string>()}")))!;
        newest["config"]!["kissPort"]!.GetValue<int>().Should().Be(8202);
        newest["kind"]!.GetValue<string>().Should().Be("saved");
    }

    [Fact]
    public async Task An_Earlier_Version_Is_Served_With_Its_Secrets_Redacted_As_The_Running_One_Is()
    {
        using var station = new Station(_dir, key: "sekrit");
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");

        HttpResponseMessage applied = await station.Client.PostAsync(
            station.Url("api/config"),
            new StringContent("""{"device": "null", "api": {"key": "sekrit"}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""", Encoding.UTF8));
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        JsonNode list = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config/history")))!;
        string id = list["versions"]![0]!["id"]!.GetValue<string>();

        string one = await station.Client.GetStringAsync(station.Url($"api/config/history/{id}"));

        one.Should().NotContain("sekrit");
        JsonNode.Parse(one)!["config"]!["api"]!["key"]!.GetValue<string>().Should().Be(ConfigApi.Hidden);
    }

    [Theory]
    [InlineData("api/config/history/20250101T000000000Z-saved")]
    [InlineData("api/config/history/..%2Fsoundmodem.json")]
    [InlineData("api/config/history/not-a-version")]
    public async Task A_Version_That_Does_Not_Exist_Is_A_404_Whatever_It_Is_Called(string url)
    {
        using var station = new Station(_dir, key: "");

        (await station.Client.GetAsync(station.Url(url))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_History_Wants_The_Key_The_Configuration_Wants_And_Is_Read_Only()
    {
        using var station = new Station(_dir, key: "sekrit");

        (await station.Client.GetAsync(station.Url("api/config/history"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "sekrit");
        (await station.Client.GetAsync(station.Url("api/config/history"))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await station.Client.PostAsync(station.Url("api/config/history"), new StringContent("{}"))).StatusCode
            .Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_Keyless_Station_Serves_Its_History_To_The_Network()
    {
        using var station = new Station(_dir, key: "");

        (await station.Client.GetAsync(station.Url("api/config/history"))).StatusCode
            .Should().Be(HttpStatusCode.OK, "the same openness as /api/config, which the history is part of");
    }

    [Fact]
    public void Only_The_Last_Twenty_Versions_Are_Kept()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var history = new ConfigHistory(Path.Combine(_dir, "state", "pending-config.json"), clock);

        for (int i = 0; i < 25; i++)
        {
            history.Record($$"""{"kissPort": {{8000 + i}}}""", persisted: i % 2 == 0, running: "// the start\n{}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<ConfigHistory.Version> kept = history.List();
        kept.Should().HaveCount(ConfigHistory.Keep + 1, "the last twenty and the original");
        history.Read(kept[0].Id).Should().Contain("8024", "the newest is first");
        history.Read(kept[^2].Id).Should().Contain("8005", "the five oldest went");
        kept[^1].Kind.Should().Be("original");
        history.Read(kept[^1].Id).Should().Be("// the start\n{}", "the original is the file's own text, comments and all");
    }

    [Fact]
    public void A_Clock_That_Jumps_Back_Or_Stands_Still_Does_Not_Lose_Or_Overwrite_A_Version()
    {
        var clock = new SteppedClock { Now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero) };
        var history = new ConfigHistory(Path.Combine(_dir, "state2", "pending-config.json"), clock);

        for (int i = 0; i < ConfigHistory.Keep; i++)
        {
            history.Record($$"""{"n": {{i}}}""", persisted: true);
            clock.Now += TimeSpan.FromSeconds(1);
        }

        clock.Now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        history.Record("""{"n": 100}""", persisted: false);
        history.Record("""{"n": 101}""", persisted: false);

        IReadOnlyList<ConfigHistory.Version> kept = history.List();
        kept.Should().HaveCount(ConfigHistory.Keep);
        history.Read(kept[0].Id).Should().Contain("101", "two applies in one instant are two versions, newest first");
        history.Read(kept[1].Id).Should().Contain("100", "a clock gone back did not make the new one look old");
        kept.Select(v => v.Id).Distinct().Should().HaveCount(ConfigHistory.Keep);
    }

    [Theory]
    [InlineData("000001-saved-20261108T101500123Z", true)]
    [InlineData("000001-saved-20261108T101500123Z\n", false)]
    [InlineData("1-saved-20261108T101500123Z", false)]
    [InlineData("000001-other-20261108T101500123Z", false)]
    public void A_Version_Id_Must_Be_Exactly_One_The_History_Made(string id, bool found)
    {
        var history = new ConfigHistory(Path.Combine(_dir, "state3", "pending-config.json"), TimeProvider.System);
        Directory.CreateDirectory(Path.Combine(_dir, "state3", "config-history"));
        File.WriteAllText(Path.Combine(_dir, "state3", "config-history", id.TrimEnd('\n') + ".json"), "{}");

        (history.Read(id) is not null).Should().Be(found);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_History_Is_Private_To_The_Service_From_The_Moment_It_Exists()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        var history = new ConfigHistory(Path.Combine(_dir, "state4", "pending-config.json"), TimeProvider.System);

        history.Record("""{"api": {"key": "x"}}""", persisted: true, running: "{}");

        string directory = Path.Combine(_dir, "state4", "config-history");
        File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string file in Directory.GetFiles(directory))
        {
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ---------------------------------------------------------------- device kinds and rules

    [Fact]
    public void The_Kinds_The_Page_Knows_Are_The_Daemons_Own_Device_Kinds_And_The_Monitor()
    {
        ConfigKeys.DeviceKinds.Should().BeEquivalentTo(
            [.. Packet.SoundModem.Daemon.DeviceKinds.All.Select(k => k.Name), "monitor"]);
    }

    [Fact]
    public void Every_Rule_Names_Only_Kinds_Keys_And_Siblings_That_Exist()
    {
        JsonObject types = ConfigSchema.Build()["types"]!.AsObject();
        JsonNode? KeyAt(string path)
        {
            string type = nameof(DaemonConfig);
            JsonNode? key = null;
            foreach (string part in path.Split('.'))
            {
                key = types[type]!["keys"]!.AsArray()
                    .SingleOrDefault(k => string.Equals(k!["name"]!.GetValue<string>(), part, StringComparison.OrdinalIgnoreCase));
                if (key is null)
                {
                    return null;
                }

                type = key["ref"]?.GetValue<string>() ?? "";
            }

            return key;
        }

        int rules = 0;
        foreach ((string name, ConfigKeys.Entry entry) in ConfigKeys.Table)
        {
            if (entry.When is not { } when)
            {
                continue;
            }

            rules++;
            foreach (string kind in when.DeviceKinds ?? [])
            {
                ConfigKeys.DeviceKinds.Should().Contain(kind, $"{name}'s rule names a kind of device");
            }

            foreach (string path in when.Set ?? [])
            {
                KeyAt(path).Should().NotBeNull($"{name}'s rule says \"{path}\" is set, and no such key exists");
            }

            if (when.SiblingKey is not null)
            {
                string type = name[..name.IndexOf('.')];
                types[type]!["keys"]!.AsArray().Select(k => k!["name"]!.GetValue<string>())
                    .Should().Contain(when.SiblingKey, $"{name}'s rule reads a sibling that is not there");
            }
        }

        rules.Should().BeGreaterThan(15);
    }

    [Fact]
    public void Every_Key_That_Offers_A_Few_Values_Names_Each_Of_Them_Except_A_Modems_Mode()
    {
        foreach ((string type, JsonNode? body) in ConfigSchema.Build()["types"]!.AsObject())
        {
            foreach (JsonNode? key in body!["keys"]!.AsArray())
            {
                string name = $"{type}.{key!["name"]}";
                string[] choices = key["choices"] is JsonArray c ? [.. c.Select(x => x!.GetValue<string>())] : [];
                string[] labelled = key["choiceLabels"] is JsonObject l ? [.. l.Select(x => x.Key)] : [];
                if (name == "ModemConfig.mode")
                {
                    labelled.Should().BeEmpty("the catalogue is long and has its own list");
                    continue;
                }

                labelled.Should().BeEquivalentTo(choices, $"{name}: a drop-down shows a plain name for every value it stores");
            }
        }
    }

    [Fact]
    public void A_Monitor_File_Shows_What_A_Monitor_Reads_And_Hides_The_Station_Keys()
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var shown = [];
              ConfigModel.plan(schema, schema.root, { monitor: { modems: [] }, waterfall: { port: 8099 } }, "")
                .forEach(function (n) { if (n.shown) shown.push(n.key.name); });
              return JSON.stringify(shown);
            })()
            """).AsString();

        string[] names = [.. JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>())];
        names.Should().Contain(["monitor", "waterfall", "ubersdr", "frameLog", "bind", "sideband", "dialFrequency", "idBeacons", "deadFeed", "modemPlugins"]);
        names.Should().NotContain(["device", "captureDevice", "playbackDevice", "captureRate", "alsa", "ptt", "rig", "publish",
            "kissPort", "modems", "api", "survey", "metrics", "rawCapture", "frequencyMatching", "paging", "txTest", "carrierSense"]);
    }

    [Theory]
    [InlineData("plughw:1,0", "device,captureDevice,playbackDevice,captureRate,ptt,rig,carrierSense,txTest,alsa,publish")]
    [InlineData("pipe:/a,/b", "device,captureRate,ptt,rig,carrierSense,txTest,publish")]
    [InlineData("flex:10.0.0.4", "device,carrierSense,txTest,flex,publish")]
    [InlineData("ubersdr:x.example", "device,ubersdr")]
    [InlineData("rtltcp:pi.lan", "device,publish,rtltcp")]
    [InlineData("iqcard:hw:1,0", "device,rig,publish,iqCard")]
    public void Each_Kind_Of_Device_Shows_The_Keys_The_Daemon_Takes_For_It(string device, string expected)
    {
        Engine js = Model();
        js.SetValue("schemaJson", ConfigSchema.Json);
        js.SetValue("device", device);

        string answer = js.Evaluate("""
            (function () {
              var schema = JSON.parse(schemaJson);
              var of = ["device","captureDevice","playbackDevice","captureRate","ptt","rig","carrierSense","txTest","alsa","publish",
                        "flex","ubersdr","iqCard","rtltcp"];
              var shown = [];
              ConfigModel.plan(schema, schema.root, { device: device }, "")
                .forEach(function (n) { if (n.shown && of.indexOf(n.key.name) >= 0) shown.push(n.key.name); });
              return JSON.stringify(shown);
            })()
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(expected.Split(','));
    }

    [Fact]
    public void Removing_Or_Moving_The_Station_Page_Is_Told_Apart_From_An_Ordinary_Change()
    {
        Engine js = Model();

        string answer = js.Evaluate("""
            (function () {
              var M = ConfigModel;
              var base = { waterfall: { port: 8107 }, bind: "*" };
              return JSON.stringify([
                M.pageMove(base, { waterfall: { port: 8107 }, bind: "*" }, "radio1.lan", "http:"),
                M.pageMove(base, { bind: "*" }, "radio1.lan", "http:"),
                M.pageMove(base, { waterfall: { port: 8200 }, bind: "*" }, "radio1.lan", "http:"),
                M.pageMove(base, { waterfall: { port: 8107 }, bind: "192.168.1.5" }, "radio1.lan", "http:"),
                M.pageMove({ waterfall: { port: 8107 }, bind: "*" }, { waterfall: { port: 8107 }, bind: "127.0.0.1" }, "radio1.lan", "http:"),
                M.pageMove({ waterfall: { port: 8107 }, bind: "*" }, { waterfall: { port: 8107 }, bind: "127.0.0.1" }, "localhost", "http:")
              ]);
            })()
            """).AsString();

        JsonArray result = JsonNode.Parse(answer)!.AsArray();
        result[0].Should().BeNull("nothing about the page changed");
        result[1]!["kind"]!.GetValue<string>().Should().Be("removed");
        result[2]!["url"]!.GetValue<string>().Should().Be("http://radio1.lan:8200/config");
        result[3]!["url"]!.GetValue<string>().Should().Be("http://192.168.1.5:8107/config");
        result[4]!["url"].Should().BeNull("loopback only cannot be reached from another machine");
        result[5]!["url"]!.GetValue<string>().Should().Be("http://localhost:8107/config");
    }

    // ---------------------------------------------------------------- roll-back and secrets

    [Fact]
    public async Task A_Version_Restored_From_History_Gets_Its_Own_Secrets_Back()
    {
        // The station has since rotated its key and lost its publish section; the placeholders in
        // a document loaded from the old version mean that version's values, not the running ones.
        using var station = new Station(_dir, key: "");
        const string old = """{"device": "null", "kissPort": 8101, "api": {"key": "old-key"}, "publish": {"url": "wss://m.example/uplink", "token": "old-token-old-token-old-token-old-0000", "callsign": "GB7RDG-2"}, "waterfall": {"port": 8107}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""";
        (await station.Client.PostAsync(station.Url("api/config"), new StringContent(old, Encoding.UTF8))).StatusCode.Should().Be(HttpStatusCode.OK);
        File.WriteAllText(station.ConfigPath, """{"device": "null", "api": {"key": "rotated"}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""");
        string id = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config/history")))!["versions"]![0]!["id"]!.GetValue<string>();
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "rotated");
        string loaded = (await station.Client.GetStringAsync(station.Url($"api/config/history/{id}")));
        string document = JsonNode.Parse(loaded)!["config"]!.ToJsonString();
        document.Should().NotContain("old-key");

        HttpResponseMessage without = await station.Client.PostAsync(station.Url("api/config?check=true"), new StringContent(document, Encoding.UTF8));
        HttpResponseMessage with = await station.Client.PostAsync(station.Url($"api/config?check=true&restoreFrom={id}"), new StringContent(document, Encoding.UTF8));
        HttpResponseMessage applied = await station.Client.PostAsync(station.Url($"api/config?persist=true&restoreFrom={id}"), new StringContent(document, Encoding.UTF8));

        without.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the running configuration has no publish token to put there");
        with.StatusCode.Should().Be(HttpStatusCode.OK, await with.Content.ReadAsStringAsync());
        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode file = JsonNode.Parse(File.ReadAllText(station.ConfigPath))!;
        file["api"]!["key"]!.GetValue<string>().Should().Be("old-key", "the key as it was in that version");
        file["publish"]!["token"]!.GetValue<string>().Should().StartWith("old-token");
    }

    [Fact]
    public async Task A_Restored_Secret_Still_Does_Not_Follow_Its_Address_To_Somewhere_New()
    {
        using var station = new Station(_dir, key: "");
        const string old = """{"device": "null", "publish": {"url": "wss://m.example/uplink", "token": "old-token-old-token-old-token-old-0000", "callsign": "GB7RDG-2"}, "waterfall": {"port": 8107}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""";
        (await station.Client.PostAsync(station.Url("api/config"), new StringContent(old, Encoding.UTF8))).StatusCode.Should().Be(HttpStatusCode.OK);
        string id = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config/history")))!["versions"]![0]!["id"]!.GetValue<string>();
        JsonNode document = JsonNode.Parse(await station.Client.GetStringAsync(station.Url($"api/config/history/{id}")))!["config"]!;
        document["publish"]!["url"] = "wss://evil.example/uplink";

        HttpResponseMessage moved = await station.Client.PostAsync(
            station.Url($"api/config?check=true&restoreFrom={id}"), new StringContent(document.ToJsonString(), Encoding.UTF8));

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await moved.Content.ReadAsStringAsync()).Should().Contain("publish.url");
    }

    [Fact]
    public async Task A_Version_Whose_Secrets_Differ_From_The_Running_Ones_Says_So_By_Name_And_Is_Not_In_Use()
    {
        using var station = new Station(_dir, key: "");
        const string old = """{"device": "null", "kissPort": 8101, "api": {"key": "old-key"}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""";
        (await station.Client.PostAsync(station.Url("api/config"), new StringContent(old, Encoding.UTF8))).StatusCode.Should().Be(HttpStatusCode.OK);
        // The same document in every visible way, but the key has been rotated since.
        File.WriteAllText(station.ConfigPath, old.Replace("old-key", "rotated", StringComparison.Ordinal));
        station.Client.DefaultRequestHeaders.Add("X-API-Key", "rotated");

        string list = await station.Client.GetStringAsync(station.Url("api/config/history"));
        JsonNode newest = JsonNode.Parse(list)!["versions"]![0]!;
        string id = newest["id"]!.GetValue<string>();
        string one = await station.Client.GetStringAsync(station.Url($"api/config/history/{id}"));

        newest["inUse"]!.GetValue<bool>().Should().BeFalse("its key is not the one running, though the redacted documents match");
        newest["secretsChanged"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("api.key");
        JsonNode.Parse(one)!["secretsChanged"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("api.key");
        (list + one).Should().NotContain("old-key").And.NotContain("rotated", "names only, never values");
    }

    [Fact]
    public void Secrets_That_Differ_Are_Named_And_An_Absent_Secret_Equals_An_Empty_One()
    {
        ConfigApi.SecretsDiffering("""{"api": {"key": "a"}}""", """{"api": {"key": "a"}}""").Should().BeEmpty();
        ConfigApi.SecretsDiffering("""{"api": {"key": ""}}""", "{}").Should().BeEmpty();
        ConfigApi.SecretsDiffering(
            """{"api": {"key": "a"}, "publish": {"token": "t"}, "ubersdr": {"password": "p"}}""",
            """{"api": {"key": "b"}, "publish": {"token": "t"}}""")
            .Should().Equal("api.key", "ubersdr.password");
    }

    [Fact]
    public void The_Ubersdr_Password_Is_Sent_To_The_Device_And_The_Monitor_Directory_Alike()
    {
        const string running = """{"device": "ubersdr:a.example", "ubersdr": {"password": "pw"}, "monitor": {"directory": "https://d.example/list"}}""";
        string posted = ConfigApi.Redact(running);

        ConfigApi.RestoreHidden(posted, running, out _).Should().NotBeNull("nothing it is sent to changed");

        string? deviceMoved = ConfigApi.RestoreHidden(
            posted.Replace("a.example", "evil.example", StringComparison.Ordinal), running, out string why1);
        deviceMoved.Should().BeNull();
        why1.Should().Contain("device").And.Contain("ubersdr.password");

        string? directoryMoved = ConfigApi.RestoreHidden(
            posted.Replace("d.example", "evil.example", StringComparison.Ordinal), running, out string why2);
        directoryMoved.Should().BeNull("the monitor fetches its receiver list from there and sends each the password");
        why2.Should().Contain("monitor.directory").And.Contain("ubersdr.password");
    }

    [Fact]
    public void A_Document_That_Makes_The_Station_A_Monitor_Is_Told_Apart()
    {
        Engine js = Model();

        string answer = js.Evaluate("""
            (function () {
              var M = ConfigModel;
              return JSON.stringify([
                M.becomesMonitor({ device: "null" }, { device: "null" }),
                M.becomesMonitor({ device: "null" }, { device: "null", monitor: { publicUrl: "https://m.example" } }),
                M.becomesMonitor({ monitor: { publicUrl: "a" } }, { monitor: { publicUrl: "b" } }),
                M.becomesMonitor({ monitor: { publicUrl: "a" } }, { device: "null" })
              ]);
            })()
            """).AsString();

        JsonNode.Parse(answer)!.AsArray().Select(n => n!.GetValue<bool>()).Should().Equal(false, true, false, false);
    }

    [Fact]
    public void Files_The_Earlier_Draft_Named_By_Time_Alone_Are_Pruned_On_The_Next_Apply()
    {
        string directory = Path.Combine(_dir, "state5", "config-history");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "20261108T101500123Z.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "notes.json"), "{}");
        var history = new ConfigHistory(Path.Combine(_dir, "state5", "pending-config.json"), TimeProvider.System);

        history.Record("""{"kissPort": 8101}""", persisted: true, running: "{}");

        File.Exists(Path.Combine(directory, "20261108T101500123Z.json")).Should().BeFalse();
        File.Exists(Path.Combine(directory, "notes.json")).Should().BeTrue("only the earlier draft's own naming is touched");
        history.List().Should().HaveCount(2, "the original and the new one");
    }

    [Fact]
    public async Task A_Restore_From_A_Version_That_Is_Not_There_Is_Refused()
    {
        using var station = new Station(_dir, key: "");

        HttpResponseMessage answer = await station.Client.PostAsync(
            station.Url("api/config?check=true&restoreFrom=000009-saved-20260101T000000000Z"),
            new StringContent(WorkableConfig(8101), Encoding.UTF8));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_Version_In_Use_Is_Marked()
    {
        using var station = new Station(_dir, key: "");
        (await station.Client.PostAsync(station.Url("api/config?persist=true"), new StringContent(WorkableConfig(8301), Encoding.UTF8)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        JsonArray versions = JsonNode.Parse(await station.Client.GetStringAsync(station.Url("api/config/history")))!["versions"]!.AsArray();

        versions.Select(v => v!["inUse"]!.GetValue<bool>()).Should().Equal(true, false);
    }

    /// <summary>A clock the test sets, which can go backwards as a real one can.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string WorkableConfig(int kissPort) =>
        $$"""{"device": "null", "kissPort": {{kissPort}}, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}""";

    [Fact]
    public void The_Model_Script_Uses_Nothing_The_Test_Engine_Lacks()
    {
        // Jint runs the model, so it must stay plain script: no DOM, no fetch, no window.
        string model = ModelScript();

        model.Should().NotContain("document.");
        model.Should().NotContain("window.");
        model.Should().NotContain("fetch(");
    }

    // ---------------------------------------------------------------- helpers

    private static string ModelScript()
    {
        string page = Encoding.UTF8.GetString(ConfigPage.Bytes);
        Match block = ModelBlock().Match(page);
        block.Success.Should().BeTrue("the page keeps its form model in <script id=\"config-model\">");
        return block.Groups[1].Value;
    }

    private static Engine Model()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(20)));
        engine.Execute(ModelScript());
        return engine;
    }

    /// <summary>A document with every key in the schema set to a value of its kind, and two
    /// entries in every list.</summary>
    private static JsonObject EveryKeySet()
    {
        JsonObject schema = ConfigSchema.Build();
        JsonObject types = schema["types"]!.AsObject();

        JsonObject Fill(string type, int depth)
        {
            var obj = new JsonObject();
            foreach (JsonNode? key in types[type]!["keys"]!.AsArray())
            {
                string name = key!["name"]!.GetValue<string>();
                string kind = key["kind"]!.GetValue<string>();
                JsonNode? value = kind switch
                {
                    "object" when depth < 6 => Fill(key["ref"]!.GetValue<string>(), depth + 1),
                    "array" when key["items"]!["ref"] is { } item && depth < 6 =>
                        new JsonArray(Fill(item.GetValue<string>(), depth + 1), Fill(item.GetValue<string>(), depth + 1)),
                    "array" => new JsonArray(Scalar(key["items"]!["kind"]!.GetValue<string>(), key["choices"]),
                        Scalar(key["items"]!["kind"]!.GetValue<string>(), key["choices"])),
                    "json" => new JsonObject { ["kept"] = 1 },
                    "object" or "array" => null,
                    _ => Scalar(kind, key["choices"]),
                };
                if (value is not null)
                {
                    obj[name] = value;
                }
            }

            return obj;
        }

        static JsonNode Scalar(string kind, JsonNode? choices) => kind switch
        {
            "integer" => JsonValue.Create(3),
            "number" => JsonValue.Create(2.5),
            "boolean" => JsonValue.Create(true),
            _ => JsonValue.Create(choices?.AsArray()[0]?.GetValue<string>() ?? "text with spaces"),
        };

        return Fill(schema["root"]!.GetValue<string>(), 0);
    }

    private static IEnumerable<Type> ConfigClassesIn(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            type = type.GetElementType()!;
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type.IsClass && type != typeof(string)
            && type.Namespace?.StartsWith("Packet.SoundModem", StringComparison.Ordinal) == true)
        {
            yield return type;
        }
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "pdn-soundmodem.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("the repository root was not found");
    }

    [GeneratedRegex("<script id=\"config-model\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ModelBlock();

    [GeneratedRegex("```json\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex("password|passphrase|passwd|token|secret|credential|apikey|privatekey|^key$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLike();

    /// <summary>A station's config API on a loopback port, with a config file of its own.</summary>
    private sealed class Station : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _base;
        private readonly Task _serving;

        public Station(string dir, string key, bool openConfiguration = true, TimeProvider? time = null)
        {
            string mine = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            ConfigPath = Path.Combine(mine, "soundmodem.json");
            PendingPath = Path.Combine(mine, "pending.json");
            string api = key.Length > 0 ? $$""", "api": { "key": "{{key}}" }""" : "";
            File.WriteAllText(ConfigPath, $$"""
                {
                  // the station's own file
                  "device": "null",
                  "modems": [ { "subChannel": 0, "mode": "bpsk300" } ]{{api}}
                }
                """);

            var configApi = new ConfigApi(
                key, ConfigPath, PendingPath,
                runningJson: () => File.ReadAllText(ConfigPath),
                ephemeralInForce: false,
                requestRestart: () => Interlocked.Increment(ref _restarts),
                openConfiguration: openConfiguration,
                time: time);

            int port = FreePorts.Next();
            _base = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_base);
            _listener.Start();
            _serving = ServeAsync(configApi);
        }

        public string ConfigPath { get; }

        public string PendingPath { get; }

        private int _restarts;

        public int Restarts => Volatile.Read(ref _restarts);

        public HttpClient Client { get; } = new();

        public Uri Url(string relative) => new(_base + relative, UriKind.Absolute);

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Closed under the accept, which is how the loop ends.
            }

            Client.Dispose();
        }

        private async Task ServeAsync(ConfigApi api)
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException
                                            or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    if (!await api.HandleAsync(context, context.Request.Url!.AbsolutePath))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                });
            }
        }
    }
}
