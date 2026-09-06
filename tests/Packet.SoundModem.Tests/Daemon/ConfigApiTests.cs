using System.Net;
using System.Text;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The runtime configuration API's decision half: what it accepts, what it declines, and the
/// one-run semantics of a change that was not persisted.
/// </summary>
/// <remarks>
/// The point of this feature is that a bad configuration costs nothing, so the cases that matter
/// are the refusals. Each one below is a real way an operator gets it wrong, and the first is the
/// one that actually took the GB7RDG node off the air on 2026-08-15.
/// </remarks>
public class ConfigApiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-api").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_Workable_Configuration_Is_Accepted()
    {
        string? refusal = ConfigApi.Validate("""
            {"device": "null", "modems": [{"subChannel": 0, "mode": "bpsk300", "frequency": 1500}]}
            """);

        refusal.Should().BeNull();
    }

    [Fact]
    public void A_Mode_Name_With_A_Stray_Character_Is_Declined_By_Name()
    {
        // The 2026-08-15 outage, exactly: a `sed` left a trailing space in the mode name, the
        // JSON was perfectly valid, the daemon refused it at start-up with exit 2, and
        // RestartPreventExitStatus=2 left a production node down until somebody noticed. Through
        // the API the same mistake is an HTTP 400 and a station that never stopped.
        string? refusal = ConfigApi.Validate("""
            {"device": "null", "modems": [{"subChannel": 0, "mode": "freedv-datac3 "}]}
            """);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("freedv-datac3 ", "the operator has to see the stray character");
        refusal.Should().Contain("modem 0", "and which modem carries it");
    }

    [Fact]
    public void A_Band_Plan_That_Cannot_Be_Built_Is_Declined_Before_Anything_Is_Written()
    {
        // A baseband mode has no centre frequency to be placed on, so "put it at 7.0516 MHz" has
        // no meaning. Parsing cannot catch this; planning can, which is why validation plans.
        string? refusal = ConfigApi.Validate("""
            {"device": "null", "modems": [
              {"subChannel": 0, "mode": "fsk9600", "rfFrequency": 7051600}]}
            """);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("baseband");
    }

    [Fact]
    public void Malformed_Json_Is_Declined_With_An_Explanation_Not_A_Stack_Trace()
    {
        string? refusal = ConfigApi.Validate("{\"device\": \"null\",");

        refusal.Should().NotBeNull();
        refusal.Should().NotContain("Exception", "a stack trace is not an explanation");
        refusal.Should().NotContain("   at ");
    }

    [Fact]
    public void An_Unknown_Setting_Is_A_Warning_Not_A_Refusal()
    {
        // Consistent with the file path: an unknown key is reported at start-up and ignored, and
        // it must not become a hard failure just because it arrived over HTTP.
        string? refusal = ConfigApi.Validate("""
            {"device": "null", "wibble": 3, "modems": [{"subChannel": 0, "mode": "bpsk300"}]}
            """);

        refusal.Should().BeNull();
    }

    [Fact]
    public void A_One_Run_Change_Is_Consumed_So_It_Applies_To_Exactly_One_Start_Up()
    {
        string configPath = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(configPath, "{}");
        string pending = ConfigApi.EphemeralPathFor(configPath);
        File.WriteAllText(pending, """{"device": "null"}""");

        ConfigApi.PendingPath(configPath).Should().Be(pending, "a change is waiting");

        ConfigApi.ConsumePending(configPath);

        ConfigApi.PendingPath(configPath).Should().BeNull(
            "consuming it is what makes it one-run: the restart after this one returns the "
            + "station to its config file, so an experiment that goes wrong self-heals");
    }

    /// <summary>
    /// <c>--config soundmodem.json</c> from the directory it sits in must give a one-run path that
    /// can actually be written.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetDirectoryName("soundmodem.json")</c> is the empty string rather than null, so the
    /// fallback here never fired for a config named without a directory: the one-run path came out
    /// as a bare file name, whose own directory name is empty, and <c>Directory.CreateDirectory("")</c>
    /// throws. Found through <c>/api/mixer</c> on the bench (2026-09-05), but this write path is
    /// shared with <c>/api/config</c> and only an absolute <c>--config</c> had ever been used.
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

    /// <summary>
    /// A POST that cannot be written down still answers, with a status and a reason.
    /// </summary>
    /// <remarks>
    /// The waterfall's own catch-all aborts the connection, which is right for a page and wrong
    /// for an API: a caller gets a closed socket with no status and no body and cannot tell a bug
    /// from a network fault. The path here throws <c>ArgumentException</c> from the write, which
    /// is the shape the bare-file-name fault took before it was fixed.
    /// </remarks>
    [Fact]
    public async Task A_Configuration_That_Cannot_Be_Written_Down_Answers_Instead_Of_Aborting()
    {
        const string key = "test-key-not-a-secret";
        string configPath = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(configPath, "{}");

        var api = new ConfigApi(
            key, configPath,
            // A null character is the one thing a Linux path may not contain, so the write throws
            // ArgumentException rather than an IOException - the class the catch used to miss.
            ephemeralPath: Path.Combine(_dir, "pending\0config.json"),
            runningJson: () => "{}",
            ephemeralInForce: false,
            requestRestart: () => { });

        int port = FreePorts.Next();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
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
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        HttpResponseMessage answer = await client.PostAsync(
            new Uri($"http://127.0.0.1:{port}/api/config", UriKind.Absolute),
            new StringContent(
                """{"device": "null", "modems": [{"subChannel": 0, "mode": "afsk1200"}]}""",
                Encoding.UTF8, "application/json"));

        await serving;
        listener.Close();

        answer.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        string text = await answer.Content.ReadAsStringAsync();
        text.Should().NotBeEmpty("a closed socket tells a caller nothing at all");
        text.Should().Contain("could not write", "and it has to say what went wrong");
    }

    [Fact]
    public void Consuming_Nothing_Is_Not_An_Error()
    {
        string configPath = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(configPath, "{}");

        Action twice = () =>
        {
            ConfigApi.ConsumePending(configPath);
            ConfigApi.ConsumePending(configPath);
        };

        twice.Should().NotThrow("every ordinary start-up takes this path");
    }

    [Fact]
    public void The_Api_Key_Is_Blanked_In_What_Is_Served_Back()
    {
        // Not keeping it from the caller, who just presented it - keeping it out of everywhere
        // the answer subsequently goes: scrollback, pasted diagnostics, screenshots.
        string redacted = ConfigApi.Redact("""
            {"device": "null", "api": {"key": "a-long-random-string"}}
            """);

        redacted.Should().NotContain("a-long-random-string");
        redacted.Should().Contain("not shown");
        redacted.Should().Contain("null", "the rest of the configuration is still served");
    }

    [Fact]
    public void A_Configuration_With_No_Api_Section_Passes_Through_Redaction_Unchanged()
    {
        const string json = """{"device": "null", "modems": []}""";

        ConfigApi.Redact(json).Should().Be(json);
    }

    /// <summary>
    /// The uplink token is not the caller's secret at all: it is the credential a monitor site
    /// issued this station, and holding this station's API key is no reason to be handed it.
    /// </summary>
    [Fact]
    public void A_Publish_Token_Is_Not_Read_Back_By_The_Config_Api()
    {
        string redacted = ConfigApi.Redact("""
            {
              "device": "null",
              "api": {"key": "the-api-key"},
              "publish": {
                "url": "wss://monitor.example/uplink",
                "token": "pdnsm_the-site-issued-this-and-it-is-not-yours",
                "callsign": "GB7RDG-2"
              }
            }
            """);

        redacted.Should().NotContain("pdnsm_the-site-issued-this-and-it-is-not-yours");
        redacted.Should().NotContain("the-api-key", "both secrets go, not one of them");
        redacted.Should().Contain("not shown");
        redacted.Should().Contain("wss://monitor.example/uplink",
            "the rest of the block is still served, so an operator can read their own config back");
        redacted.Should().Contain("GB7RDG-2");
    }

    [Fact]
    public void A_Publish_Block_With_No_Token_Passes_Through_Redaction_Unchanged()
    {
        const string json = """{"device": "null", "publish": {"callsign": "GB7RDG-2"}}""";

        ConfigApi.Redact(json).Should().Be(json);
    }
    /// <summary>
    /// A posted configuration whose <c>stateFile</c> names the station's real config file is
    /// refused at the POST, not at the restart afterwards.
    /// </summary>
    /// <remarks>
    /// Validation parses the proposal out of a temporary file, so without being told the path the
    /// document is proposed to become, the guard compared <c>stateFile</c> against the temporary
    /// name and passed anything. The change would then be written or staged and the restart would
    /// exit 2 on the new sentence, taking the station down until somebody read the journal.
    /// </remarks>
    [Fact]
    public void A_State_File_Aimed_At_The_Real_Config_Is_Refused_At_The_Post()
    {
        const string json = """
            {
              "device": "plughw:1,0",
              "modems": [ { "subChannel": 0, "mode": "afsk1200" } ],
              "alsa": { "mixer": { "stateFile": "/etc/pdn-soundmodem/soundmodem.json" } }
            }
            """;

        ConfigApi.Validate(json, "/etc/pdn-soundmodem/soundmodem.json")
            .Should().Contain("which is this configuration file");

        // And it is still accepted for a station whose config file is somewhere else, so the
        // guard is about this file rather than about the name.
        ConfigApi.Validate(json, "/etc/pdn-soundmodem/other.json").Should().BeNull();
        ConfigApi.Validate(json).Should().BeNull(
            "a caller with no such path to offer keeps the old behaviour");
    }

}
