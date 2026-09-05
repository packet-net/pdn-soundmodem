using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Audio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// <c>/api/mixer</c> end to end: an HTTP request in, a change on the card, and the station's
/// configuration document amended so the next start-up sets the same thing.
/// </summary>
/// <remarks>
/// <para>This is what the operator page's Mixer group does when a slider moves, so it is tested
/// as the page uses it - over a real socket, through the real handler, with the real key check -
/// against a made-up card rather than a real one.</para>
/// <para>The one thing that separates this from <c>/api/config</c> is that nothing restarts: the
/// setting lands on the card as the request is served, because restarting a station to trim its
/// own capture gain would drop the waterfall the operator is trimming it against.</para>
/// </remarks>
public class MixerApiTests : IDisposable
{
    private const string Key = "test-key-not-a-secret";

    private static readonly string Running = """
        {
          "device": "plughw:1,0",
          "kissPort": 8105,
          "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-mixer-api").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_Request_Without_The_Key_Is_Refused_Before_The_Card_Is_Touched()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());
        using var stranger = new HttpClient();

        HttpResponseMessage answer = await stranger.GetAsync(new Uri(station.Url, UriKind.Absolute));

        answer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        station.Card!.Refreshes.Should().Be(0, "an unauthorised caller never reaches the card");
    }

    [Fact]
    public async Task Reading_The_Mixer_Reports_The_Cards_Own_State()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.GetAsync();

        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("card").GetString().Should().Be("hw:3");
        body.GetProperty("capture").GetProperty("control").GetString().Should().Be("Mic");
        body.GetProperty("capture").GetProperty("percent").GetInt32().Should().Be(57);
        body.GetProperty("agc").GetProperty("on").GetBoolean().Should().BeTrue();
        body.GetProperty("micBoost").ValueKind.Should().Be(
            JsonValueKind.Null, "this CM108 revision has no mic boost control");
        station.Card!.Find("Mic")!.Capture.Should().Be(57, "reading changes nothing");
    }

    [Fact]
    public async Task Setting_The_Gain_Lands_On_The_Card_And_Comes_Back_Read_Back()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainPercent": 80, "agc": false}""");

        station.Card!.Find("Mic")!.Capture.Should().Be(80);
        station.Card.Find("Auto Gain Control")!.On.Should().BeFalse();
        body.GetProperty("applied").GetBoolean().Should().BeTrue();
        body.GetProperty("capture").GetProperty("percent").GetInt32().Should().Be(80);
        body.GetProperty("summary").GetString().Should()
            .Be("alsa: mixer: Mic capture 80% / 16.00 dB (set 80%), Auto Gain Control off, "
                + "Speaker playback 46% / -19.98 dB");
    }

    [Fact]
    public async Task A_Change_Is_One_Run_Unless_It_Says_Otherwise()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"captureGainPercent": 42}""");

        body.GetProperty("persisted").GetBoolean().Should().BeFalse();
        File.Exists(station.EphemeralPath).Should().BeTrue("the next start-up sets what is set now");

        // Not /api/config's sentence, which is true there and false here: that endpoint restarts
        // at once and the restart consumes the one-run file, while this one does not restart, and
        // a mixer is never reset by a file that does not mention it.
        string note = body.GetProperty("note").GetString()!;
        note.Should().Contain("stays set");
        note.Should().NotContain("In force until the next restart");
        File.ReadAllText(station.ConfigPath).Should().Be(
            Running, "the config file is the description of the intended station");

        DaemonConfig? pending = DaemonConfig.TryLoad(station.EphemeralPath, out string error);
        error.Should().BeEmpty();
        pending!.Alsa!.Mixer!.CaptureGainPercent.Should().Be(42);
        pending.Modems.Should().ContainSingle("the rest of the document is left alone");
    }

    [Fact]
    public async Task Persist_True_Writes_The_Config_File_Instead()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        JsonElement body = await station.PostAsync("""{"agc": false}""", persist: true);

        body.GetProperty("persisted").GetBoolean().Should().BeTrue();
        DaemonConfig? written = DaemonConfig.TryLoad(station.ConfigPath, out _);
        written!.Alsa!.Mixer!.Agc.Should().BeFalse();
        File.Exists(station.EphemeralPath).Should().BeFalse();
    }

    /// <summary>
    /// A persisted change has to clear the one-run change waiting beside it, or the next restart
    /// applies the older one and the persisted level arrives a restart late.
    /// </summary>
    /// <remarks>
    /// Start-up prefers the one-run file over the config file and consumes it, and this endpoint -
    /// alone among the writers here - does not restart, so a one-run change POSTed earlier in the
    /// session is still sitting there when the persisted one is written. <c>POST /api/config</c>
    /// cannot reach this state because every POST there restarts at once.
    /// </remarks>
    [Fact]
    public async Task A_Persisted_Change_Clears_The_One_Run_Change_Waiting_Beside_It()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        await station.PostAsync("""{"captureGainPercent": 45}""");
        File.Exists(station.EphemeralPath).Should().BeTrue("this is the state that used to win");

        JsonElement body = await station.PostAsync("""{"captureGainPercent": 70}""", persist: true);

        body.GetProperty("persisted").GetBoolean().Should().BeTrue();
        File.Exists(station.EphemeralPath).Should().BeFalse(
            "the next restart must start from the file, not from the earlier one-run change");
        body.GetProperty("note").GetString().Should().Contain("has been removed");
        DaemonConfig.TryLoad(station.ConfigPath, out _)!
            .Alsa!.Mixer!.CaptureGainPercent.Should().Be(70);
    }

    /// <summary>
    /// The file is read at the moment of the write, so an operator's edits since start-up are not
    /// replaced by the snapshot this process came up on.
    /// </summary>
    [Fact]
    public async Task Persisting_Amends_The_File_As_It_Is_Now_And_Not_The_Start_Up_Snapshot()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        // The operator edits the file while the station runs, as they are entitled to.
        File.WriteAllText(station.ConfigPath, """
            {
              "device": "plughw:1,0",
              "kissPort": 8199,
              "modems": [ { "subChannel": 0, "mode": "afsk1200" }, { "subChannel": 1, "mode": "bpsk300" } ]
            }
            """);

        await station.PostAsync("""{"captureGainPercent": 65}""", persist: true);

        DaemonConfig written = DaemonConfig.TryLoad(station.ConfigPath, out string error)!;
        error.Should().BeEmpty();
        written.KissPort.Should().Be(8199, "the operator's edit survives");
        written.Modems.Should().HaveCount(2, "and so does the modem they added");
        written.Alsa!.Mixer!.CaptureGainPercent.Should().Be(65);
    }

    [Fact]
    public async Task A_File_Edited_Into_Something_That_Will_Not_Load_Is_Not_Written_Over()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        // Half-finished, as an operator's file can be while the station carries on running.
        File.WriteAllText(station.ConfigPath, """
            {"device": "plughw:1,0", "modems": [{"subChannel": 0, "mode": "afsk1200 "}]}
            """);

        JsonElement body = await station.PostAsync("""{"agc": false}""", persist: true);

        body.GetProperty("persisted").GetBoolean().Should().BeFalse();
        body.GetProperty("note").GetString().Should().Contain("would not load");
        File.ReadAllText(station.ConfigPath).Should().Contain(
            "afsk1200 ", "the operator has to find their own mistake, not have it buried");
        station.Card!.Find("Auto Gain Control")!.On.Should().BeFalse("the card is set regardless");
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
                """{"captureGain": 45, "agc": false}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await answer.Content.ReadAsStringAsync()).Should().Contain("captureGainPercent");
        station.Card!.Find("Mic")!.Capture.Should().Be(57, "nothing reached the card");
        station.Card.Find("Auto Gain Control")!.On.Should().BeTrue();
    }

    [Fact]
    public async Task A_Percentage_Outside_The_Range_Is_Refused_In_The_Same_Words_As_The_File()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        HttpResponseMessage answer = await station.Client.PostAsync(
            new Uri(station.Url, UriKind.Absolute),
            new StringContent("""{"captureGainPercent": 150}""", Encoding.UTF8, "application/json"));

        answer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string text = await answer.Content.ReadAsStringAsync();
        text.Should().Contain("\"alsa\".\"mixer\".\"captureGainPercent\" is 150");
        text.Should().Contain("use 0-100");
        station.Card!.Find("Mic")!.Capture.Should().Be(57, "a refusal costs nothing that was set");
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

    /// <summary>
    /// A config file named without a directory - <c>--config soundmodem.json</c> from the
    /// directory it sits in - must still give a one-run path that can be written.
    /// </summary>
    /// <remarks>
    /// Found on the bench on 2026-09-05, from exactly that invocation.
    /// <c>Path.GetDirectoryName("soundmodem.json")</c> is the empty string rather than null, so
    /// the fallback never fired, the one-run path came out as a bare file name, and
    /// <c>Directory.CreateDirectory("")</c> threw ArgumentException on the way to writing it. The
    /// waterfall's catch-all then aborted the connection, so the POST set the card and answered
    /// nothing at all - the worst possible shape for a failure, since the caller cannot tell it
    /// from "nothing happened".
    /// </remarks>
    [Theory]
    [InlineData("soundmodem.json")]
    [InlineData("./soundmodem.json")]
    [InlineData("/etc/pdn-soundmodem/soundmodem.json")]
    public void A_One_Run_Path_Always_Names_A_Directory_That_Can_Be_Created(string configPath)
    {
        string pending = ConfigApi.EphemeralPathFor(configPath);

        Path.GetFileName(pending).Should().Be("pending-config.json");
        Path.GetDirectoryName(pending).Should().NotBeNullOrEmpty(
            "Directory.CreateDirectory throws on an empty path, and that is where it is written");
    }

    [Fact]
    public async Task A_Change_That_Cannot_Be_Written_Down_Still_Answers_In_Full()
    {
        // The card is set before the file is written, so a failure after that point must arrive as
        // a complete answer saying so. An aborted connection would leave the operator believing
        // nothing had happened while the station listened at a different gain.
        string blocker = Path.Combine(_dir, $"blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        using var station = new Station(
            _dir, FakeMixer.Cm108(), ephemeralPath: Path.Combine(blocker, "pending-config.json"));

        JsonElement body = await station.PostAsync("""{"captureGainPercent": 45}""");

        station.Card!.Find("Mic")!.Capture.Should().Be(45, "the card is set either way");
        body.GetProperty("applied").GetBoolean().Should().BeTrue();
        body.GetProperty("note").GetString().Should().Contain("could not be written");
    }

    [Fact]
    public async Task Every_Answer_Carries_A_Body_And_Not_A_Closed_Socket()
    {
        using var station = new Station(_dir, FakeMixer.Cm108());

        foreach (string url in (string[])[station.Url, station.Url + "?persist=true"])
        {
            HttpResponseMessage answer = await station.Client.PostAsync(
                new Uri(url, UriKind.Absolute),
                new StringContent("""{"captureGainPercent": 45}""", Encoding.UTF8, "application/json"));

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
    /// the whole point of this endpoint. The card here takes a millisecond to refresh, which is
    /// the window they would interleave in.
    /// </remarks>
    [Fact]
    public async Task Two_Changes_At_Once_Each_Get_Their_Own_Read_Back()
    {
        FakeMixer card = FakeMixer.Cm108();
        card.RefreshTakes = TimeSpan.FromMilliseconds(2);
        using var station = new Station(_dir, card);

        int[] wanted = [10, 20, 30, 40, 50, 60, 70, 80];
        JsonElement[] answers = await Task.WhenAll(wanted.Select(percent =>
            station.PostAsync($$"""{"captureGainPercent": {{percent}} }""")));

        for (int i = 0; i < wanted.Length; i++)
        {
            answers[i].GetProperty("capture").GetProperty("percent").GetInt32().Should().Be(
                wanted[i], "each caller is answered with the level it asked for, not another's");
        }

        wanted.Should().Contain(card.Find("Mic")!.Capture!.Value, "and one of them won the card");
    }

    [Fact]
    public void An_Amendment_Adds_The_Mixer_Block_And_Disturbs_Nothing_Else()
    {
        string? amended = MixerApi.Amend(
            Running,
            new MixerChange { CaptureGainPercent = 60, MicBoost = false },
            out string why);

        why.Should().BeEmpty();
        using JsonDocument document = JsonDocument.Parse(amended!);
        JsonElement mixer = document.RootElement.GetProperty("alsa").GetProperty("mixer");
        mixer.GetProperty("captureGainPercent").GetInt32().Should().Be(60);
        mixer.GetProperty("micBoost").GetBoolean().Should().BeFalse();
        mixer.TryGetProperty("agc", out _).Should().BeFalse(
            "a control the request said nothing about stays unmentioned, which leaves it alone");
        document.RootElement.GetProperty("kissPort").GetInt32().Should().Be(8105);
    }

    /// <summary>
    /// A config file here is JSONC and every real one has comments in it. Found on the bench
    /// CM108 on 2026-09-05: the card was set and the fold-in then failed at the first "//".
    /// </summary>
    [Fact]
    public void A_Configuration_With_Comments_Is_Amended_Rather_Than_Refused()
    {
        string running = """
            {
              // #17 bench config. Every real config file on this network looks like this.
              "device": "plughw:CARD=Device,DEV=0",
              /* and the shipped example is most of the way to being a manual */
              "modems": [ { "subChannel": 0, "mode": "afsk1200" }, ],
            }
            """;

        string? amended = MixerApi.Amend(
            running, new MixerChange { CaptureGainPercent = 45, Agc = false }, out string why);

        why.Should().BeEmpty();
        amended.Should().NotBeNull("a commented file is an ordinary file here, not a broken one");
        using JsonDocument document = JsonDocument.Parse(amended!);
        document.RootElement.GetProperty("alsa").GetProperty("mixer")
            .GetProperty("captureGainPercent").GetInt32().Should().Be(45);
        document.RootElement.GetProperty("device").GetString().Should().Be("plughw:CARD=Device,DEV=0");
    }

    [Fact]
    public async Task Persisting_Into_A_Commented_File_Sets_The_Card_And_Declines_The_Write()
    {
        // The card is what the operator can hear, so it is set either way. What is declined is
        // writing a parsed document over the top of their comments, which would delete them.
        using var station = new Station(_dir, FakeMixer.Cm108(), configText: """
            {
              // the operator's own notes, which a round trip would silently delete
              "device": "plughw:1,0",
              "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]
            }
            """);

        JsonElement body = await station.PostAsync("""{"captureGainPercent": 45}""", persist: true);

        station.Card!.Find("Mic")!.Capture.Should().Be(45, "the card is set whatever the file is");
        body.GetProperty("persisted").GetBoolean().Should().BeFalse();
        string note = body.GetProperty("note").GetString()!;
        note.Should().Contain("comments or trailing commas");
        note.Should().Contain("NOT written");
        note.Should().Contain("{\"alsa\":{\"mixer\":{\"captureGainPercent\":45}}}",
            "the operator is given the line to paste");
        File.ReadAllText(station.ConfigPath).Should().Contain(
            "the operator's own notes", "the file is left exactly as it was");
        File.Exists(station.EphemeralPath).Should().BeTrue("the change still lasts this run");
    }

    [Fact]
    public void An_Amendment_Keeps_What_The_File_Already_Said_About_The_Other_Controls()
    {
        string running = """
            {"device": "plughw:1,0", "alsa": {"mixer": {"agc": false, "captureGainPercent": 30}}}
            """;

        string? amended = MixerApi.Amend(
            running, new MixerChange { CaptureGainPercent = 65 }, out _);

        using JsonDocument document = JsonDocument.Parse(amended!);
        JsonElement mixer = document.RootElement.GetProperty("alsa").GetProperty("mixer");
        mixer.GetProperty("captureGainPercent").GetInt32().Should().Be(65);
        mixer.GetProperty("agc").GetBoolean().Should().BeFalse("the file's AGC setting survives");
    }

    /// <summary>
    /// A <see cref="ConfigApi"/> on a real socket with a made-up card behind it, which is the
    /// arrangement the operator page talks to.
    /// </summary>
    private sealed class Station : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public Station(
            string dir, FakeMixer? card, string? configText = null, string? ephemeralPath = null)
        {
            Card = card;
            ConfigPath = Path.Combine(dir, $"soundmodem-{Guid.NewGuid():N}.json");
            EphemeralPath = ephemeralPath ?? Path.Combine(dir, $"pending-{Guid.NewGuid():N}.json");
            string running = configText ?? Running;
            File.WriteAllText(ConfigPath, running);

            var api = new ConfigApi(
                Key, ConfigPath, EphemeralPath,
                runningJson: () => running,
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"));

            if (card is not null)
            {
                var wanted = new MixerSettings();
                api.ServeMixer(
                    read: () => MixerSetup.Apply(card, wanted, null),
                    apply: change => MixerSetup.Apply(
                        card,
                        wanted with
                        {
                            CaptureGainPercent = change.CaptureGainPercent,
                            Agc = change.Agc,
                            MicBoost = change.MicBoost,
                            PlaybackPercent = change.PlaybackPercent,
                        },
                        null));
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

        public string EphemeralPath { get; }

        public string Url { get; }

        /// <summary>A client that carries the station's key, as the operator page does.</summary>
        public HttpClient Client { get; } = new();

        public async Task<JsonElement> GetAsync() =>
            await Client.GetFromJsonAsync<JsonElement>(new Uri(Url, UriKind.Absolute));

        public async Task<JsonElement> PostAsync(string body, bool persist = false)
        {
            HttpResponseMessage answer = await Client.PostAsync(
                new Uri(persist ? Url + "?persist=true" : Url, UriKind.Absolute),
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
}
