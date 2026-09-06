using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Audio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// <c>/api/mixer</c> end to end: an HTTP request in, a change on the card in dB, and the change
/// remembered in the station's mixer state file so the next start-up sets the same thing.
/// </summary>
/// <remarks>
/// <para>This is what the operator page's Mixer group does when a slider moves, so it is tested
/// as the page uses it - over a real socket, through the real handler, with the real key check -
/// against a made-up card rather than a real one.</para>
/// <para>Two things separate this from <c>/api/config</c>. Nothing restarts: the setting lands on
/// the card as the request is served, because restarting a station to trim its own capture gain
/// would drop the waterfall the operator is trimming it against. And it persists by default
/// (Tom, 2026-09-06): a trim made on the page is meant to stay made, so it goes to the daemon's
/// own state file. The config file is never written from here and always wins at start-up.</para>
/// </remarks>
public class MixerApiTests : IDisposable
{
    private const string Key = "test-key-not-a-secret";
    private const string Device = "plughw:CARD=Device,DEV=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-mixer-api").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_Request_Without_The_Key_Is_Refused_Before_The_Card_Is_Touched()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());
        using var stranger = new HttpClient();

        HttpResponseMessage answer = await stranger.GetAsync(new Uri(station.Url, UriKind.Absolute));

        answer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        station.Card!.CaptureDb("Mic").Should().Be(8, "an unauthorised caller never reaches the card");
    }

    [Fact]
    public async Task Reading_The_Mixer_Reports_The_Cards_Own_State_And_Its_Range()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.GetAsync();

        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("card").GetString().Should().Be("hw:3");
        JsonElement capture = body.GetProperty("capture");
        capture.GetProperty("control").GetString().Should().Be("Mic");
        capture.GetProperty("decibels").GetDouble().Should().Be(8);
        capture.GetProperty("dbRange").GetProperty("min").GetDouble().Should().Be(-12);
        capture.GetProperty("dbRange").GetProperty("max").GetDouble().Should().Be(23);
        capture.GetProperty("percent").GetInt32().Should().Be(
            57, "the percentage alsamixer shows is still reported beside the dB");
        capture.GetProperty("source").GetString().Should().Be(
            "none", "nothing has pinned this control");
        body.GetProperty("agc").GetProperty("on").GetBoolean().Should().BeTrue();
        body.GetProperty("micBoost").ValueKind.Should().Be(
            JsonValueKind.Null, "this CM108 revision has no mic boost control");
        station.Card!.CaptureDb("Mic").Should().Be(8, "reading changes nothing");
    }

    [Fact]
    public async Task Setting_The_Gain_Lands_On_The_Card_In_Db_And_Comes_Back_Read_Back()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6, "agc": false}""");

        station.Card!.CaptureDb("Mic").Should().Be(6);
        station.Card.Find("Auto Gain Control")!.On.Should().BeFalse();
        body.GetProperty("applied").GetBoolean().Should().BeTrue();
        body.GetProperty("capture").GetProperty("decibels").GetDouble().Should().Be(6);
        body.GetProperty("summary").GetString().Should()
            .Be("alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.00 dB), "
                + "Auto Gain Control off, "
                + "Speaker playback -20.00 dB of -37.00 to 0.00 dB (left as found)",
                "a control the request said nothing about is named as untouched, not omitted");
    }

    /// <summary>
    /// The card quantises a dB to its own steps, and the answer is the card's, not the request's.
    /// </summary>
    [Fact]
    public async Task A_Level_The_Card_Cannot_Hold_Comes_Back_As_The_Step_It_Took()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6.4}""");

        body.GetProperty("capture").GetProperty("decibels").GetDouble().Should().Be(
            6, "the bench CM108's capture moves in whole dB");
    }

    [Fact]
    public async Task A_Change_Is_Remembered_In_The_State_File_By_Default()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6, "agc": false}""");

        body.GetProperty("persisted").GetBoolean().Should().BeTrue();
        body.GetProperty("stateFile").GetString().Should().Be(station.StatePath);
        body.GetProperty("warn").GetBoolean().Should().BeFalse(
            "nothing here needs the operator's attention: it was set and it was kept");
        body.GetProperty("note").GetString().Should().Contain("Remembered in");

        JsonElement written = station.State();
        written.GetProperty("captureGainDb").GetDouble().Should().Be(6);
        written.GetProperty("agc").GetBoolean().Should().BeFalse();
        written.GetProperty("device").GetString().Should().Be(Device);
        written.TryGetProperty("playbackDb", out _).Should().BeFalse(
            "a control nobody has ever set here stays absent, so it keeps whatever the card has");
        written.GetProperty("writtenAt").GetDateTimeOffset().Should().Be(Station.Now);
    }

    [Fact]
    public async Task The_Config_File_Is_Never_Written_By_A_Mixer_Change()
    {
        using var station = new Station(_dir, FakeMixer.Cm108(), configText: """
            {
              // the operator's own notes, which nothing here is entitled to delete
              "device": "plughw:CARD=Device,DEV=0",
              "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]
            }
            """);
        string before = File.ReadAllText(station.ConfigPath);

        await station.PostAsync("""{"captureGainDb": 6}""");

        File.ReadAllText(station.ConfigPath).Should().Be(
            before, "the config file is the operator's, comments and all");
        station.State().GetProperty("captureGainDb").GetDouble().Should().Be(6);
    }

    [Fact]
    public async Task A_Second_Change_Keeps_What_The_First_One_Set()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        await station.PostAsync("""{"captureGainDb": 6}""");
        await station.PostAsync("""{"agc": false}""");

        JsonElement written = station.State();
        written.GetProperty("captureGainDb").GetDouble().Should().Be(
            6, "the earlier change is still what the next start-up should set");
        written.GetProperty("agc").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Persist_False_Sets_The_Card_And_Writes_Nothing()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6}""", persist: false);

        station.Card!.CaptureDb("Mic").Should().Be(6, "the card is set: that is the point");
        body.GetProperty("persisted").GetBoolean().Should().BeFalse();
        body.GetProperty("note").GetString().Should().Contain("persist=false");
        File.Exists(station.StatePath).Should().BeFalse(
            "a value being tried is not a value being kept");
    }

    /// <summary>
    /// A control the config file pins is still changed, still remembered, and the operator is
    /// told that the file takes it back at the next start-up.
    /// </summary>
    /// <remarks>
    /// Precedence is config, then state file, then leave the card alone. Applying the change and
    /// saying nothing would look like it had stuck; refusing it would be useless to somebody
    /// trimming a level against the waterfall in front of them. So: do it, keep it, say it.
    /// </remarks>
    [Fact]
    public async Task A_Control_The_Config_File_Pins_Is_Changed_And_The_Answer_Says_It_Comes_Back()
    {
        using var station = new Station(
            _dir, FakeMixer.Cm108(), pinned: new AlsaMixerConfig { CaptureGainDb = -3 });

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6}""");

        station.Card!.CaptureDb("Mic").Should().Be(6, "the change is real");
        body.GetProperty("persisted").GetBoolean().Should().BeTrue("and it is still remembered");
        body.GetProperty("warn").GetBoolean().Should().BeTrue();
        body.GetProperty("note").GetString().Should().Contain(
            "captureGainDb is set in the config file as -3.00 dB; this change lasts until the "
            + "next start.");
        body.GetProperty("capture").GetProperty("source").GetString().Should().Be(
            "config", "which is what will still put -3 dB on the card at the next start-up");
    }

    [Fact]
    public async Task The_Config_File_Note_Is_Absent_For_A_Control_The_File_Says_Nothing_About()
    {
        using var station = new Station(
            _dir, FakeMixer.Cm108(), pinned: new AlsaMixerConfig { CaptureGainDb = -3 });

        JsonElement body = await station.PostAsync("""{"agc": false}""");

        body.GetProperty("warn").GetBoolean().Should().BeFalse();
        body.GetProperty("note").GetString().Should().NotContain("set in the config file");
        body.GetProperty("agc").GetProperty("source").GetString().Should().Be(
            "state", "the state file is what pins the AGC now");
        body.GetProperty("capture").GetProperty("source").GetString().Should().Be("config");
    }

    [Fact]
    public async Task A_State_File_That_Cannot_Be_Written_Costs_The_Persistence_And_Not_The_Change()
    {
        // A file where the state file's directory should be, which is as close as a test gets to
        // a read-only /var/lib without being root.
        string blocker = Path.Combine(_dir, $"blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        using var station = new Station(
            _dir, FakeMixer.Cm108(), statePath: Path.Combine(blocker, "mixer-state.json"));

        JsonElement body = await station.PostAsync("""{"captureGainDb": 6}""");

        station.Card!.CaptureDb("Mic").Should().Be(6, "the card is set either way");
        body.GetProperty("applied").GetBoolean().Should().BeTrue();
        body.GetProperty("persisted").GetBoolean().Should().BeFalse();
        body.GetProperty("warn").GetBoolean().Should().BeTrue();
        string note = body.GetProperty("note").GetString()!;
        note.Should().Contain("could not be written");
        note.Should().Contain("the next start-up will not set it");
    }

    [Fact]
    public async Task An_Unknown_Field_In_The_Body_Is_Refused_Rather_Than_Dropped()
    {
        // Dropped, this would set the AGC, silently ignore the misspelled gain and report
        // success - and the only clue would be a read-back the caller did not think to check.
        using var station = new Station(_dir, FakeMixer.Cm108());

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent(
                """{"captureGain": 6, "agc": false}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await answer.Content.ReadAsStringAsync()).Should().Contain("captureGainDb");
        station.Card!.CaptureDb("Mic").Should().Be(8, "nothing reached the card");
        station.Card.Find("Auto Gain Control")!.On.Should().BeTrue();
    }

    /// <summary>
    /// A body still carrying 0.57.0's percentage key is refused by name, with the key that
    /// replaced it.
    /// </summary>
    /// <remarks>
    /// Not aliased. 60 meant 60% of the card's raw range and now means 60 dB, which is 37 dB past
    /// the top of the bench CM108 - so reading the old key as the new one would set a level
    /// nobody asked for on the first request after an upgrade.
    /// </remarks>
    [Theory]
    [InlineData("captureGainPercent", "captureGainDb")]
    [InlineData("playbackPercent", "playbackDb")]
    public async Task A_Percentage_Key_From_The_Version_Before_Is_Refused_By_Name(string gone, string now)
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent($$"""{"{{gone}}": 60}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string text = await answer.Content.ReadAsStringAsync();
        text.Should().Contain($"{gone} is no longer read; use {now}");
        text.Should().Contain("--mixer-show");
        station.Card!.CaptureDb("Mic").Should().Be(8, "a refusal costs nothing that was set");
        File.Exists(station.StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_Level_Outside_The_Cards_Range_Is_Refused_With_The_Range()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent("""{"captureGainDb": 30}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string text = await answer.Content.ReadAsStringAsync();
        text.Should().Contain("captureGainDb 30.00 dB is outside the range of \"Mic\" on hw:3");
        text.Should().Contain("-12.00 to 23.00 dB");
        station.Card!.CaptureDb("Mic").Should().Be(8, "a refusal costs nothing that was set");
    }

    [Fact]
    public async Task A_Control_With_No_Db_Scale_Is_Refused_In_Words_Rather_Than_Guessed_At()
    {
        var card = new FakeMixer(
            "hw:5", new FakeControl { Name = "Mic", Capture = new FakeLevel { Max = 15, Raw = 6 } });
        using var station = new Station(_dir, card);

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent("""{"captureGainDb": 6}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await answer.Content.ReadAsStringAsync()).Should().Contain("has no dB scale");
        card.Find("Mic")!.Capture!.Raw.Should().Be(6);

        // And a GET says the same thing in the shape a page can act on: no range to draw a
        // slider between.
        JsonElement read = await station.GetAsync();
        read.GetProperty("capture").GetProperty("dbRange").ValueKind.Should().Be(JsonValueKind.Null);
        read.GetProperty("summary").GetString().Should().Contain("no dB scale");
    }

    [Fact]
    public async Task An_Empty_Body_Is_Refused_Rather_Than_Silently_Doing_Nothing()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent("{}", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await answer.Content.ReadAsStringAsync()).Should().Contain("no mixer settings in the body");
    }

    [Fact]
    public async Task A_Station_With_No_Mixer_Says_So_Rather_Than_Serving_A_Not_Found()
    {
        // What the page needs to tell "there is no mixer here" from "this daemon is too old to
        // have the endpoint": the second is a 404 and hides the group, the first is this.
        using var station = new Station(_dir, card: null);

        JsonElement body = await station.GetAsync();

        body.GetProperty("available").GetBoolean().Should().BeFalse();
        body.GetProperty("why").GetString().Should().Contain("no mixer");
    }

    [Fact]
    public async Task Setting_A_Mixer_That_Is_Not_There_Is_A_Conflict_Not_A_Silent_Success()
    {
        using var station = new Station(_dir, card: null);

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent("""{"agc": false}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Every_Answer_Carries_A_Body_And_Not_A_Closed_Socket()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        foreach (string url in (string[])[station.Url, station.Url + "?persist=false"])
        {
            HttpResponseMessage answer = await station.Client.PostAsync(
                new Uri(url, UriKind.Absolute),
                new StringContent("""{"captureGainDb": 6}""", Encoding.UTF8, "application/json"));

            answer.StatusCode.Should().Be(HttpStatusCode.OK);
            string text = await answer.Content.ReadAsStringAsync();
            text.Should().NotBeEmpty($"{url} must answer with something a caller can read");
            JsonSerializer.Deserialize<JsonElement>(text)
                .GetProperty("applied").GetBoolean().Should().BeTrue();
        }
    }

    /// <summary>
    /// Two operators, or two tabs, changing the mixer at once each get their own answer.
    /// </summary>
    /// <remarks>
    /// The waterfall serves every request on a task of its own, and applying is set-then-read
    /// across several calls into the card, so without a lock two overlapping POSTs can each be
    /// answered with the other's level - and the read-back is the answer, and the read-back is
    /// the whole point of this endpoint. They would also both write the state file. The card here
    /// takes a couple of milliseconds to refresh, which is the window they would interleave in.
    /// </remarks>
    [Fact]
    public async Task Two_Changes_At_Once_Each_Get_Their_Own_Read_Back()
    {
        FakeMixer card = FakeMixer.Cm108();
        card.RefreshTakes = TimeSpan.FromMilliseconds(2);
        using var station = new Station(_dir, card);

        double[] wanted = [-10, -8, -6, -4, -2, 0, 2, 4];
        JsonElement[] answers = await Task.WhenAll(wanted.Select(db =>
            station.PostAsync($$"""{"captureGainDb": {{db}} }""")));

        for (int i = 0; i < wanted.Length; i++)
        {
            answers[i].GetProperty("capture").GetProperty("decibels").GetDouble().Should().Be(
                wanted[i], "each caller is answered with the level it asked for, not another's");
        }

        wanted.Should().Contain(card.CaptureDb("Mic")!.Value, "and one of them won the card");
        station.State().GetProperty("captureGainDb").GetDouble().Should().BeOneOf(
            [.. wanted], "and one of them was the last to write the state file");
    }

    /// <summary>
    /// A <see cref="ConfigApi"/> on a real socket with a made-up card behind it, which is the
    /// arrangement the operator page talks to.
    /// </summary>
    private sealed class Station : IDisposable
    {
        /// <summary>The moment every state file written here is stamped with.</summary>
        public static readonly DateTimeOffset Now =
            new(2026, 9, 6, 11, 22, 33, TimeSpan.Zero);

        private static readonly string Running = """
            {
              "device": "plughw:CARD=Device,DEV=0",
              "kissPort": 8105,
              "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]
            }
            """;

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public Station(
            string dir, FakeMixer? card, string? configText = null, string? statePath = null,
            AlsaMixerConfig? pinned = null)
        {
            Card = card;
            string mine = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            ConfigPath = Path.Combine(mine, "soundmodem.json");
            StatePath = statePath ?? Path.Combine(mine, MixerStateFile.DefaultName);
            File.WriteAllText(ConfigPath, configText ?? Running);

            var api = new ConfigApi(
                Key, ConfigPath, Path.Combine(mine, "pending.json"),
                runningJson: () => File.ReadAllText(ConfigPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"));

            if (card is not null)
            {
                AlsaMixerConfig mixerConfig = pinned ?? new AlsaMixerConfig();
                mixerConfig.StateFile = StatePath;
                MixerRuntime runtime = MixerRuntime.Start(
                    card, mixerConfig, ConfigPath, Device, Journal.Add, out string why,
                    new FixedClock(Now))!;
                why.Should().BeEmpty();
                api.ServeMixer(runtime);
            }
            else
            {
                api.NoMixer("hw:9 has no mixer: snd_mixer_attach(hw:9): No such file or directory");
            }

            Client.DefaultRequestHeaders.Add("X-API-Key", Key);
            int port = FreePorts.Next();
            Url = $"http://127.0.0.1:{port}/api/mixer";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = ServeAsync(api);
        }

        public FakeMixer? Card { get; }

        public string ConfigPath { get; }

        public string StatePath { get; }

        public string Url { get; }

        /// <summary>Everything the daemon would have journalled, for an assertion to read.</summary>
        public List<string> Journal { get; } = [];

        /// <summary>A client that carries the station's key, as the operator page does.</summary>
        public HttpClient Client { get; } = new();

        /// <summary>The state file as it stands, parsed.</summary>
        public JsonElement State() =>
            JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(StatePath));

        public async Task<JsonElement> GetAsync() =>
            await Client.GetFromJsonAsync<JsonElement>(new Uri(Url, UriKind.Absolute));

        public async Task<JsonElement> PostAsync(string body, bool persist = true)
        {
            HttpResponseMessage answer = await Client.PostAsync(
                new Uri(persist ? Url : Url + "?persist=false", UriKind.Absolute),
                new StringContent(body, Encoding.UTF8, "application/json"));
            answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
            return JsonSerializer.Deserialize<JsonElement>(await answer.Content.ReadAsStringAsync());
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener was closed under the accept, which is how this loop ends.
            }

            Client.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync(ConfigApi api)
        {
            while (!_stop.IsCancellationRequested)
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

                // Each request on a task of its own, as WaterfallWebServer does
                // (`_ = ServeAsync(context)`). Awaiting here instead would serialise every
                // caller in the harness and make the concurrency test prove nothing.
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

    /// <summary>A clock that does not move, so a written state file is byte-predictable.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
