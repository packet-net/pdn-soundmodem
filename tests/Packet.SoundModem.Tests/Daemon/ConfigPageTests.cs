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
        station.Restarts.Should().Be(1);
    }

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

        public Station(string dir, string key, bool openConfiguration = true)
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
                requestRestart: () => Restarts++,
                openConfiguration: openConfiguration);

            int port = FreePorts.Next();
            _base = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_base);
            _listener.Start();
            _serving = ServeAsync(configApi);
        }

        public string ConfigPath { get; }

        public string PendingPath { get; }

        public int Restarts { get; private set; }

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
