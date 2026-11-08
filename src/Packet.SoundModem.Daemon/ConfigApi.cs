using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Changing this station's configuration at runtime, over the waterfall's HTTP listener.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> An experiment on a live station used to cost an edit to
/// <c>/etc/pdn-soundmodem/soundmodem.json</c> and a <c>systemctl restart</c>. The FreeDV campaign
/// of 2026-08-15 spent four of those in an afternoon, and the expensive part was never the
/// fifteen seconds: it was that a hand-edit can be wrong. One of them left a trailing space in a
/// mode name, the daemon correctly refused it, exit 2 stopped systemd retrying, and a production
/// node carrying other people's traffic sat down until somebody noticed.</para>
/// <para><b>Validate and decline.</b> That is the whole point. A proposed configuration is parsed
/// and planned <em>before</em> anything is written, through the same code the start-up path uses,
/// and a bad one is refused with the same operator-facing message - while the running station
/// carries on untouched. The failure mode above becomes an HTTP 400 and a station that never
/// stopped.</para>
/// <para><b>Applying is a full rebuild.</b> No attempt is made to mutate a running channel in
/// place. Adding a 48 kHz mode to a 12 kHz station rebuilds every modem anyway (the channel's DSP
/// rate is chosen once, from the modem set), so the honest implementation is to write the new
/// configuration and let the process come back on it. Tom, 2026-08-15: "a full rebuild is
/// probably the right way, a small interruption is tolerable."</para>
/// <para><b>Ephemeral by default.</b> An applied change lives in the state directory and is
/// consumed - read once, deleted immediately - by the next start-up. So it survives exactly one
/// restart, and any later one (a crash, a reboot, an operator's <c>systemctl restart</c>) returns
/// the station to the file. An experiment that goes wrong therefore self-heals, and the config
/// file stays the description of the intended station. <c>?persist=true</c> writes the file
/// instead, for a change meant to outlive the session.</para>
/// <para><b><c>/api/mixer</c> is the exception</b> (Tom, 2026-09-06). It restarts nothing and it
/// persists by default, because a level trimmed on the operator page is meant to stay trimmed and
/// there is no restart for a one-run default to insure against. It writes the daemon's own state
/// file rather than the config file, which it never touches; see <see cref="MixerRuntime"/>.</para>
/// </remarks>
internal sealed class ConfigApi
{
    private readonly string _key;
    private readonly string _configPath;
    private Func<IReadOnlyList<Survey.ModemProposal>>? _proposals;
    private TxTestRunner? _txTest;
    private Channel.SoundModemChannel? _txLease;
    private Func<int, bool>? _hasModem;
    private string? _txLeaseCannot;
    private TxLeaseTuning? _txLeaseTuning;
    private Func<(long Examined, long Read, long Dropped)>? _prospectorCounts;
    private MixerRuntime? _mixer;
    private RigControl? _rig;
    private KeepAudio.KeepAudioRecorder? _keepAudio;
    private string _mixerWhyNot = "this station has no sound-card mixer";

    /// <summary>One mixer change at a time; see the wait in <see cref="MixerAsync"/>.</summary>
    private readonly SemaphoreSlim _mixerGate = new(1, 1);
    private readonly string _ephemeralPath;
    private readonly ConfigHistory _history;
    private readonly Func<string> _runningJson;
    private readonly Action _requestRestart;
    private readonly bool _ephemeralInForce;
    private readonly bool _openAudioControls;

    /// <summary>Whether a station with no key serves its configuration openly; false on a public
    /// page, which is for visitors and is not a private home network.</summary>
    private readonly bool _openConfiguration;

    /// <summary>Which process answered, so the page can tell the restarted station from the old
    /// one still shutting down.</summary>
    private static readonly string Instance = Guid.NewGuid().ToString("N")[..12];

    /// <param name="key">The shared secret every request must present. Empty serves nothing but
    /// an open mixer.</param>
    /// <param name="configPath">The config file, for <c>?persist=true</c>.</param>
    /// <param name="ephemeralPath">Where a non-persisted change is left for the next start-up.</param>
    /// <param name="runningJson">The configuration this process is running, as JSON.</param>
    /// <param name="ephemeralInForce">Whether this process started from an ephemeral change.</param>
    /// <param name="requestRestart">Asks the daemon to shut down for systemd to bring back.</param>
    /// <param name="time">The clock, for the times on earlier versions; the system's if null.</param>
    /// <param name="openAudioControls">
    /// Whether <c>/api/mixer</c> answers without a key, from
    /// <c>"waterfall"."enableAudioControls"</c>. It opens that one endpoint and nothing else.
    /// </param>
    public ConfigApi(
        string key, string configPath, string ephemeralPath,
        Func<string> runningJson, bool ephemeralInForce, Action requestRestart,
        bool openAudioControls = false, bool openConfiguration = true, TimeProvider? time = null)
    {
        _history = new ConfigHistory(ephemeralPath, time ?? TimeProvider.System);
        _openAudioControls = openAudioControls;
        _openConfiguration = openConfiguration;
        _key = key;
        _configPath = configPath;
        _ephemeralPath = ephemeralPath;
        _runningJson = runningJson;
        _ephemeralInForce = ephemeralInForce;
        _requestRestart = requestRestart;
    }

    /// <summary>
    /// Where the station's modem proposals come from, if it is making any. Installed by the
    /// daemon after start-up because the prospector is built from the very configuration this
    /// class serves.
    /// </summary>
    /// <param name="proposals">What the station would have listening.</param>
    /// <param name="counts">What it has looked at to say so - the number that answers "is this
    /// running?" on a band where nothing has been readable yet.</param>
    public void ServeProposals(
        Func<IReadOnlyList<Survey.ModemProposal>> proposals,
        Func<(long Examined, long Read, long Dropped)> counts)
    {
        _proposals = proposals;
        _prospectorCounts = counts;
    }

    // ---------------------------------------------------------------- TX test
    /// <summary>
    /// Lets this API run the operator's transmitter test, at <c>/api/txtest</c>. Installed by the
    /// daemon once the station's transmit path exists, and only where the page's own button is
    /// installed - the two drive the same runner, so a curl and a click are the same act with the
    /// same rules, and there is no second opinion about the cap.
    /// </summary>
    public void ServeTxTest(TxTestRunner runner) => _txTest = runner;

    // ---------------------------------------------------------------- TX lease
    /// <summary>
    /// Serves the channel's transmit lease at <c>/api/txlease</c>. See <see cref="TxLeaseApi"/>.
    /// </summary>
    /// <param name="channel">The station's channel, whose lease it is.</param>
    /// <param name="cannot">Why this station cannot transmit at all, or null.</param>
    /// <param name="tuning">Tunes the rig for a take that asks for it, or null without a rig.</param>
    public void ServeTxLease(Channel.SoundModemChannel channel, string? cannot, TxLeaseTuning? tuning = null)
    {
        _txLeaseTuning = tuning;
        _txLease = channel;
        _hasModem = channel.Modems.ContainsKey;
        _txLeaseCannot = cannot;
    }

    // ---------------------------------------------------------------- rig
    /// <summary>
    /// Serves the station's rig at <c>/api/rig</c> and <c>/api/rig/tune</c>. See <see cref="RigApi"/>.
    /// Without it both answer 404, saying the station has no <c>rig</c> section.
    /// </summary>
    public void ServeRig(RigControl rig) => _rig = rig;

    // ---------------------------------------------------------------- keep audio
    /// <summary>
    /// Serves <c>/api/keepaudio</c>, keeping a stretch of receive audio with the carrier sense's
    /// reasons. See <see cref="KeepAudio.KeepAudioApi"/>. Without it the path 404s.
    /// </summary>
    public void ServeKeepAudio(KeepAudio.KeepAudioRecorder recorder) => _keepAudio = recorder;

    private async Task KeepAudioAsync(HttpListenerContext context)
    {
        if (_keepAudio is null)
        {
            await RespondAsync(context, 404, "this station keeps no receive audio").ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        (int status, JsonObject answer) = KeepAudio.KeepAudioApi.Handle(_keepAudio, context.Request.HttpMethod, body);
        await RespondJsonAsync(context, status, answer.ToJsonString(Pretty)).ConfigureAwait(false);
    }

    private async Task RigAsync(HttpListenerContext context, string path)
    {
        if (_rig is null)
        {
            await RespondAsync(context, 404,
                "this station has no \"rig\" section, so it controls no rig").ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        (int status, JsonObject answer) = RigApi.Handle(_rig, path, context.Request.HttpMethod, body);
        await RespondJsonAsync(context, status, answer.ToJsonString(Pretty)).ConfigureAwait(false);
    }

    private async Task TxLeaseAsync(HttpListenerContext context)
    {
        if (_txLease is null)
        {
            await RespondAsync(context, 404, "this station offers no transmit lease").ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        (int status, JsonObject answer) = TxLeaseApi.Handle(
            _txLease, context.Request.HttpMethod, body, _txLeaseCannot, _rig, _txLeaseTuning);
        await RespondJsonAsync(context, status, answer.ToJsonString(Pretty)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one test on behalf of an API caller and answers with what happened.
    /// </summary>
    /// <remarks>
    /// Awaited rather than acknowledged, because the caller is a script or a person at a terminal
    /// and "it went out" is the answer they want; the page, which cannot hold a socket open across
    /// a keyup, gets the same outcome pushed to it instead. Bounded by the runner's own channel
    /// wait, so a busy channel ends in a refusal rather than a hung request.
    /// </remarks>
    private async Task TxTestAsync(HttpListenerContext context)
    {
        if (_txTest is null)
        {
            await RespondAsync(context, 404,
                "this station offers no transmitter test: it has no transmitter, or "
                + "\"txTest\".\"enabled\" is false").ConfigureAwait(false);
            return;
        }

        if (context.Request.HttpMethod != "POST")
        {
            await RespondAsync(context, 405,
                "POST to transmit: {\"twoTone\": true, \"seconds\": 5} for the two-tone test, "
                + "{\"twoTone\": false, \"toneHz\": 999, \"seconds\": 5} for a single tone, either "
                + "optionally with \"probe\": {\"kind\": \"zc255\", \"gapSeconds\": 1.5, \"audioHz\": 1800} "
                + "to follow it with a channel-sounding probe, {\"stop\": true} to end one early")
                .ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        JsonNode? asked;
        try
        {
            asked = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (JsonException bad)
        {
            await RespondAsync(context, 400, $"the body is not JSON: {bad.Message}")
                .ConfigureAwait(false);
            return;
        }

        // A stop wins, read first and on its own, whatever else is in the body: the button that
        // ends a transmission must never be refused because some other field was wrong.
        bool stop;
        try
        {
            stop = asked?["stop"]?.GetValue<bool>() == true;
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            await RespondAsync(context, 400, $"\"stop\" is true or false: {wrongType.Message}")
                .ConfigureAwait(false);
            return;
        }

        if (stop)
        {
            _txTest.Stop();
            await RespondJsonAsync(context, 200, TxTestStopped).ConfigureAwait(false);
            return;
        }

        bool twoTone;
        double toneHz;
        double seconds;
        int? subChannel;
        Waterfall.TxTestProbe? probe;
        try
        {
            twoTone = asked?["twoTone"]?.GetValue<bool>() ?? true;
            toneHz = asked?["toneHz"]?.GetValue<double>() ?? Audio.TestTone.TwoToneLowHz;
            seconds = asked?["seconds"]?.GetValue<double>() ?? 0;
            subChannel = asked?["subChannel"]?.GetValue<int>();
            probe = ReadProbe(asked?["probe"]);
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            // A field of the wrong kind ("seconds": "5"), which is a caller's mistake and not a
            // reason to drop the connection with no explanation.
            await RespondAsync(context, 400,
                "\"twoTone\" is true or false, \"toneHz\" and \"seconds\" are "
                + "numbers, \"subChannel\" a whole number, \"probe\" an object with a \"kind\" "
                + $"string and \"gapSeconds\" and \"audioHz\" numbers: {wrongType.Message}").ConfigureAwait(false);
            return;
        }

        // A probe the station cannot make as asked is the caller's mistake and a 400, before
        // anything is prepared: an unknown kind (a head end newer than this station must hear
        // "no", never a tone with the probe silently left off), a negative gap, or a centre that
        // puts the band outside this channel. Only the cap, the station's own limit, is a 409.
        if (probe is { } wanted && _txTest.ProbeProblem(wanted) is string problem)
        {
            await RespondAsync(context, 400, problem).ConfigureAwait(false);
            return;
        }

        // Checked as /api/txlease checks it: a sub-channel that carries no modem is a caller's
        // mistake, and a 400 says so before anything is prepared.
        if (subChannel is int sub && _hasModem is { } hasModem && !hasModem(sub))
        {
            await RespondAsync(context, 400, $"no modem transmits on sub-channel {sub}").ConfigureAwait(false);
            return;
        }

        TxTestOutcome outcome = await _txTest
            .RunAsync(new Waterfall.TxTestRequest(twoTone, toneHz, seconds) { SubChannel = subChannel, Probe = probe })
            .ConfigureAwait(false);

        // Three answers, because a script should be able to tell them apart without reading the
        // sentence. 200: it went out. 409 rather than 400: nothing was wrong with the request,
        // the station was in no state to answer it - no PTT, a channel that would not clear, a
        // test already running. 500: something threw - a PTT that failed, a device that died -
        // which is a fault rather than an answer, and is worth a retry where a 409 is not.
        int status = outcome.Ran ? 200 : outcome.Failed ? 500 : 409;
        await RespondJsonAsync(context, status, new JsonObject
        {
            ["transmitted"] = outcome.Ran,
            ["sent"] = outcome.Ran ? outcome.Text : null,
            ["refused"] = outcome.Failed ? null : outcome.Refusal,
            ["failed"] = outcome.Failed ? outcome.Refusal : null,

            // The id of the probe that followed the tone, whole, or null; and whether all of the
            // probe asked for went out (null when none was asked for). A station older than the
            // probe writes neither key, which is how a head end tells "sent the tone and ignored
            // the probe" from "sent both".
            ["probe"] = outcome.Probe,
            ["probeComplete"] = outcome.ProbeComplete,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a request's <c>probe</c> object, or null when there is none. A <c>probe</c> that is
    /// there but is not an object with a <c>kind</c> string, or that carries a field it does not
    /// take (a misspelt <c>gapSecs</c> silently meaning the default is the mistake this catches),
    /// throws, which the caller answers 400.
    /// </summary>
    private static Waterfall.TxTestProbe? ReadProbe(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject probe)
        {
            throw new InvalidOperationException("\"probe\" is not an object");
        }

        foreach (KeyValuePair<string, JsonNode?> field in probe)
        {
            if (field.Key is not ("kind" or "gapSeconds" or "audioHz"))
            {
                throw new InvalidOperationException(
                    $"\"probe\" has no field \"{TxTestRunner.Printable(field.Key)}\"; it takes \"kind\", "
                    + "\"gapSeconds\" and \"audioHz\"");
            }
        }

        string kind = probe["kind"]?.GetValue<string>()
            ?? throw new InvalidOperationException("\"probe\" has no \"kind\"");
        return new Waterfall.TxTestProbe(
            kind,
            probe["gapSeconds"]?.GetValue<double>() ?? Waterfall.TxTestProbe.DefaultGapSeconds,
            probe["audioHz"]?.GetValue<double>() ?? Waterfall.TxTestProbe.DefaultAudioHz);
    }

    /// <summary>What a stop is answered with, and what it does and does not promise.</summary>
    private const string TxTestStopped =
        "{\n  \"stopped\": true,\n  \"note\": \"a test still waiting for the channel is "
        + "withdrawn and never keys the radio; one already on the air fades out\"\n}";

    /// <summary>
    /// Where the sound card's mixer is, if this station has one. Installed by the daemon after
    /// start-up, for the same reason as the proposals: the mixer is opened from the very
    /// configuration this class serves.
    /// </summary>
    /// <param name="mixer">The station's open card, its state file and what pinned each control.</param>
    public void ServeMixer(MixerRuntime mixer) => _mixer = mixer;

    /// <summary>
    /// Why this station has no mixer to offer, for the answer a caller gets instead of one.
    /// </summary>
    /// <remarks>
    /// Said rather than left to a 404, because "there is no mixer here" and "this daemon is too
    /// old to have the endpoint" are different things to an operator and to the page, which shows
    /// its mixer group on the strength of this answer.
    /// </remarks>
    /// <param name="why">The sentence to serve.</param>
    public void NoMixer(string why) => _mixerWhyNot = why;

    /// <summary>Where a non-persisted change is left for the next start-up to consume.</summary>
    /// <remarks>The state directory, which systemd creates and owns for the service user. Not
    /// beside the config file: <c>ProtectSystem=full</c> makes <c>/etc</c> read-only to the
    /// service, so that is the one place it reliably cannot write.</remarks>
    public static string EphemeralPathFor(string configPath)
    {
        // Every branch has to yield a directory that Path.GetDirectoryName can find again.
        // Path.GetDirectoryName("soundmodem.json") is the EMPTY STRING and not null, so the
        // "?? \".\"" below never fired for a config named without a directory - which gave an
        // ephemeral path of "pending-config.json", whose own directory name is empty, and
        // Directory.CreateDirectory("") throws ArgumentException. On the bench that surfaced as a
        // POST that set the card and then aborted the connection (2026-09-05).
        string directory = StateDirectory.Current
            ?? (Path.GetDirectoryName(configPath) is { Length: > 0 } beside ? beside : ".");
        return Path.Combine(directory, "pending-config.json");
    }

    /// <summary>What a redacted secret reads as, so the answer still says one is set. Posted
    /// back unchanged it keeps the stored value; see <see cref="RestoreHidden"/>.</summary>
    internal const string Hidden = "(set, not shown)";

    /// <summary>
    /// The keys whose values are never served, as paths from the top of the file. The schema marks
    /// the same ones, so the configuration page offers them as password fields.
    /// </summary>
    internal static IReadOnlyList<string> SecretPaths => [.. Secrets.Select(s => s.Path)];

    /// <summary>
    /// Each secret, and the settings that say where it is sent. A placeholder keeps a stored
    /// secret only while all of those settings are unchanged: otherwise anyone who could post a
    /// configuration could point <c>publish.url</c> at their own server and be sent the token
    /// they were never shown. The UberSDR password goes to the <c>device</c> and also to each
    /// receiver the <c>monitor.directory</c> lists.
    /// </summary>
    private static readonly (string Path, string[] SentTo)[] Secrets =
    [
        ("api.key", []),
        ("publish.token", ["publish.url"]),
        ("ubersdr.password", ["device", "monitor.directory"]),
    ];

    /// <summary>
    /// The secrets whose values differ between two documents, by path and never by value. An
    /// absent secret and an empty one are the same here. Used to say what going back to an
    /// earlier version would change, because the served documents have every secret blanked and
    /// so cannot show it.
    /// </summary>
    internal static IReadOnlyList<string> SecretsDiffering(string a, string b)
    {
        JsonNode? left, right;
        try
        {
            left = JsonNode.Parse(a, nodeOptions: null, Jsonc);
            right = JsonNode.Parse(b, nodeOptions: null, Jsonc);
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Value(JsonNode? node)
        {
            string? text = node?.ToJsonString();
            return text is null or "\"\"" ? null : text;
        }

        return [.. Secrets.Select(s => s.Path).Where(path => Value(At(left, path)) != Value(At(right, path)))];
    }

    /// <summary>Whether the key at <paramref name="keyPath"/> is one whose value is never served.</summary>
    internal static bool IsSecret(string keyPath) =>
        SecretPaths.Contains(keyPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>How a document posted here, or the running one, is read: as JSONC, as the file is.</summary>
    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The configuration with each of <see cref="SecretPaths"/>,
    /// <c>mailcast.bbs.password</c> and the <c>args</c> of each <c>mailcast.hooks</c> command
    /// blanked (a retired <c>mailcast</c> section can still be in the file), for serving back to a caller.
    /// </summary>
    /// <remarks>
    /// <para>The caller already knows the key - they just presented it - so this is not keeping a
    /// secret from them. It is keeping it out of everywhere the *answer* subsequently goes: a
    /// terminal scrollback, a pasted diagnostic, a screenshot of a station's config. Secrets
    /// escape through their echoes more often than through their sockets.</para>
    /// <para><c>publish.token</c> is the second one and it is not the caller's at all: it is the
    /// credential a monitor site issued this station, and holding this station's API key is no
    /// reason to be handed it.</para>
    /// </remarks>
    public static string Redact(string json)
    {
        try
        {
            // JSONC, as the file is read. A strict parse threw at the first "//" of a commented
            // file and served "{}" for the whole configuration.
            JsonNode? root = JsonNode.Parse(json, nodeOptions: null, Jsonc);

            // Keys are matched the way the file is read, which is without regard to case: a
            // "Mailcast": {"BBS": {"Password": ...}} is the same password and must not be served.
            // Every spelling, not the first: a file with "key" and "Key" both is read as the last
            // of them, so hiding only the first would serve the one in use.
            void Hide(params string[] path) => HideIn(root as JsonObject, path, 0);

            static void HideIn(JsonObject? node, string[] path, int at)
            {
                if (node is null)
                {
                    return;
                }

                foreach (string key in KeysOf(node, path[at]).ToList())
                {
                    if (at < path.Length - 1)
                    {
                        HideIn(node[key] as JsonObject, path, at + 1);
                    }
                    else if (node[key] is JsonNode value
                             && !(value is JsonValue text && text.TryGetValue(out string? s) && s.Length == 0))
                    {
                        // Only a value that is there: an absent or empty secret is served as what
                        // it is, so the page does not claim a key is set on a station that has none.
                        node[key] = Hidden;
                    }
                }
            }

            foreach (string secret in SecretPaths)
            {
                Hide(secret.Split('.'));
            }

            Hide("mailcast", "bbs", "password");

            // A hook's arguments may hold anything, a password for the program it runs included,
            // so they are treated as the BBS password is: never served. The program's path is not.
            Hide("mailcast", "hooks", "before", "args");
            Hide("mailcast", "hooks", "after", "args");
            // Always re-serialised, comments and all gone: what is served is the configuration's
            // content, and a caller should not have to parse JSONC to read it.
            return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
        }
        catch (JsonException)
        {
            // The running config parsed once already or the daemon would not have started, so
            // this is close to unreachable - but serving nothing beats serving a key by accident.
            return "{}";
        }

        static IEnumerable<string> KeysOf(JsonObject node, string name) =>
            node.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="posted"/> with every <see cref="Hidden"/> placeholder replaced by the value
    /// at the same place in <paramref name="running"/>; null, with <paramref name="why"/> saying
    /// so, when the running configuration has nothing there to keep.
    /// </summary>
    /// <remarks>
    /// A secret is served as the placeholder, so a configuration read from here, edited and posted
    /// back carries the placeholder where the key was. Written as it came, that replaced the
    /// station's key with the text "(set, not shown)". Posted unchanged, a placeholder now means
    /// "keep what is there"; any other value replaces it. A document with no placeholder is
    /// returned exactly as posted, comments and all.
    /// </remarks>
    internal static string? RestoreHidden(string posted, string running, out string why)
    {
        why = "";
        if (!posted.Contains(Hidden, StringComparison.Ordinal))
        {
            return posted;
        }

        JsonNode? root;
        JsonNode? stored;
        try
        {
            root = JsonNode.Parse(posted, nodeOptions: null, Jsonc);
        }
        catch (JsonException e)
        {
            why = $"not valid JSON: {e.Message}";
            return null;
        }

        try
        {
            stored = JsonNode.Parse(running, nodeOptions: null, Jsonc);
        }
        catch (JsonException)
        {
            stored = null;
        }

        string? missing = null;
        var restored = new List<string>();
        root = Walk(root, stored, "");
        if (missing is not null)
        {
            why = $"\"{missing}\" is \"{Hidden}\", which keeps the value the station already has, "
                + "and it has none there. Type the value in, or remove the key.";
            return null;
        }

        foreach ((string path, string[] sentTo) in Secrets)
        {
            if (!restored.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string setting in sentTo)
            {
                if (!JsonNode.DeepEquals(At(root, setting), At(stored, setting)))
                {
                    why = $"\"{setting}\" has changed, so \"{path}\" cannot be kept as it was: type it in again.";
                    return null;
                }
            }
        }

        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

        JsonNode? Walk(JsonNode? node, JsonNode? keep, string at)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (string name in obj.Select(p => p.Key).ToList())
                    {
                        // The same spelling if the stored document has it, else the last spelling,
                        // which is the one the file is read as.
                        JsonNode? match = keep is JsonObject k
                            ? k.ContainsKey(name)
                                ? k[name]
                                : k.LastOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value
                            : null;
                        JsonNode? child = obj[name];
                        JsonNode? walked = Walk(child, match, at.Length == 0 ? name : $"{at}.{name}");
                        if (!ReferenceEquals(walked, child))
                        {
                            obj[name] = walked;
                        }
                    }

                    return obj;

                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        JsonNode? match = keep is JsonArray k && i < k.Count ? k[i] : null;
                        JsonNode? child = array[i];
                        JsonNode? walked = Walk(child, match, $"{at}[{i}]");
                        if (!ReferenceEquals(walked, child))
                        {
                            array[i] = walked;
                        }
                    }

                    return array;

                case JsonValue value when value.TryGetValue(out string? text) && text == Hidden:
                    if (keep is null || (keep is JsonValue v && v.TryGetValue(out string? old) && old == Hidden))
                    {
                        missing ??= at;
                        return node;
                    }

                    restored.Add(at);
                    return keep.DeepClone();

                default:
                    return node;
            }
        }
    }

    /// <summary>The value at a dotted path, keys matched without regard to case, the last
    /// spelling winning as it does when the file is read.</summary>
    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (string part in path.Split('.'))
        {
            node = node is JsonObject obj
                ? obj.LastOrDefault(p => string.Equals(p.Key, part, StringComparison.OrdinalIgnoreCase)).Value
                : null;
        }

        return node;
    }

    /// <summary>The waiting ephemeral configuration, or null if there is none.</summary>
    public static string? PendingPath(string configPath)
    {
        string path = EphemeralPathFor(configPath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Deletes the waiting ephemeral configuration, so it applies to exactly one start-up.
    /// </summary>
    /// <remarks>Called by the caller immediately after the load <em>attempt</em>, whether or not
    /// it succeeded, and always before the station is built from it. That ordering is the whole
    /// safety property: a configuration that parses but then kills the daemon is already gone by
    /// the time systemd restarts, so the station comes back on its config file instead of
    /// restart-looping on the change that broke it.</remarks>
    public static void ConsumePending(string configPath)
    {
        try
        {
            string path = EphemeralPathFor(configPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"api: WARNING - could not remove the one-run configuration ({e.Message}); it "
                + "will be applied again at the next start-up");
        }
    }

    /// <summary>
    /// Whether <paramref name="json"/> is a configuration this daemon could start on: null if it
    /// is, otherwise the message an operator should read.
    /// </summary>
    /// <remarks>
    /// <para>Parsed through <see cref="DaemonConfig.TryLoad"/> from a temporary file rather than a
    /// reimplementation, so the wording an API caller gets is the wording the journal would have
    /// carried - there is no second dialect of the same refusals to drift out of step.</para>
    /// <para>Beyond parsing it checks the two things that actually go wrong and that parsing does
    /// not catch: a mode name the catalogue has never heard of (the 2026-08-15 outage), and a band
    /// plan that cannot be built. Neither is exhaustive - a device that has been unplugged is
    /// discovered only by trying - which is what the ephemeral default is insurance for.</para>
    /// </remarks>
    /// <param name="json">The proposed configuration document.</param>
    /// <param name="asPath">
    /// The config file this document is proposed to become, when the caller knows it. The parse
    /// happens from a temporary file, so without this any check that compares a setting against
    /// the config file's own location sees the temporary name: an <c>alsa.mixer.stateFile</c>
    /// aimed at the station's real <c>soundmodem.json</c> would validate here and only be refused
    /// at the restart, which takes the station down until somebody reads the journal. Null keeps
    /// the old behaviour for a caller with no such path, which is what the tests use.
    /// </param>
    public static string? Validate(string json, string? asPath = null)
    {
        string temp = Path.Combine(
            Path.GetTempPath(), $"pdn-soundmodem-proposed-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temp, json);
            DaemonConfig? proposed = DaemonConfig.TryLoad(temp, out string error, asPath);
            if (proposed is null)
            {
                return error;
            }

            foreach (ModemConfig modem in proposed.Modems)
            {
                if (!DaemonConfig.IsArdop(modem.Mode) && !ModemCatalog.IsKnown(modem.Mode))
                {
                    // Named plainly, because this is the one that took the node down: a mode
                    // string with a stray character parses as perfectly good JSON.
                    return $"modem {modem.SubChannel}: unknown mode '{modem.Mode}'. "
                        + "Check the spelling against docs/05-modes.md.";
                }
            }

            try
            {
                // Plan mutates the entries it is given, which is why this runs against the
                // throwaway parse above and never against anything the station is using.
                bool flex = proposed.Device.StartsWith("flex:", StringComparison.OrdinalIgnoreCase);
                // A leftover mailcast section still counts as an MS110D modem, as start-up has it.
                int dspRate = proposed.Mailcast is not null || proposed.Modems.Any(
                    m => !DaemonConfig.IsArdop(m.Mode) && ModemCatalog.DspRateFor(m.Mode) == 48000)
                    ? 48000
                    : 12000;
                RfPlan.Result? plan = BandPlanner.Plan(
                    proposed.Modems, proposed.Sideband, proposed.DialFrequency, dspRate,
                    flex ? Passband.WideCeilingHz : null);
            }
            catch (Exception plan) when (plan is InvalidDataException or ArgumentException)
            {
                return plan.Message;
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not check the proposed configuration: {e.Message}";
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is untidy, not a reason to fail the request.
            }
        }
    }

    /// <summary>Handles one request under <c>/api/</c>; false leaves it a 404.</summary>
    /// <param name="context">The request.</param>
    /// <param name="path">
    /// Its path relative to the base the waterfall server is served under, so that this matches
    /// on /api/config whether the station is a site of its own or one receiver of a site that
    /// offers several.
    /// </param>
    public async Task<bool> HandleAsync(HttpListenerContext context, string path)
    {
        if (path is not ("/api/config" or "/api/proposals" or "/api/txtest" or "/api/txlease" or "/api/mixer"
            or "/api/rig" or "/api/rig/tune" or "/api/keepaudio" or "/api/config/schema" or ConfigPage.Path)
            && !path.StartsWith("/api/config/history", StringComparison.Ordinal))
        {
            return false;
        }

        bool isConfig = path is "/api/config" || path.StartsWith("/api/config/history", StringComparison.Ordinal);

        // The configuration page and the description it draws its form from hold no secret and
        // change nothing, so they are served to anyone who can reach the listener. The page asks
        // for the key itself, when /api/config turns out to want one.
        if (path is ConfigPage.Path or "/api/config/schema"
            && (_key.Length > 0 || _openConfiguration))
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                await RespondAsync(context, 405, "GET only").ConfigureAwait(false);
                return true;
            }

            if (path is ConfigPage.Path)
            {
                await ConfigPage.ServeAsync(context).ConfigureAwait(false);
            }
            else
            {
                await RespondJsonAsync(context, 200, ConfigSchema.Json).ConfigureAwait(false);
            }

            return true;
        }

        // The one endpoint that can be open: "waterfall"."enableAudioControls" (Tom, 2026-09-06).
        // It is the operator's own capture gain, on the operator's own page, on a station whose
        // operator asked for it in the config file. Everything else here can retune the radio and
        // key the transmitter, and still wants the key - including on a station that also has one,
        // where this flag decides the mixer and nothing more.
        bool open = _openAudioControls && path is "/api/mixer";

        // The configuration itself is open on a station with no key (Tom, 2026-11-06: "fully
        // open - these are private home networks"). Open to a person on the network, that is,
        // not to any web page their browser happens to have open: a POST from a page this
        // station did not serve is refused, as the open mixer's is.
        bool openConfig = _key.Length == 0 && _openConfiguration && isConfig;
        if (openConfig)
        {
            if (!ByOwnName(context.Request))
            {
                // DNS rebinding (#423): a page on evil.example whose name is made to resolve to
                // this station sends an Origin that matches its Host, so the same-site check
                // alone passes it. Its Host is still a name this machine does not have.
                await RespondAsync(context, 403,
                    $"forbidden: with no \"api\".\"key\" the configuration is answered only at this "
                    + $"machine's address or its own name, and this request was for "
                    + $"\"{context.Request.UserHostName}\". Use the station's IP address, or set "
                    + "\"api\".\"key\".")
                    .ConfigureAwait(false);
                return true;
            }

            if (context.Request.HttpMethod == "POST" && !SameSite(context.Request))
            {
                await RespondAsync(context, 403,
                    "forbidden: this request came from a page this station did not serve, and "
                    + "the station has no \"api\".\"key\". The station's own /config page, a "
                    + "client that sends no Origin header (curl, a script), and a caller "
                    + "presenting a key once one is set are all fine.")
                    .ConfigureAwait(false);
                return true;
            }

            open = true;
        }

        if (!open && _key.Length == 0)
        {
            // No key configured, so there is nothing to present one to. A 404, which is what this
            // path answered before the flag gave a keyless station a reason to install a handler
            // at all - a station without an "api" section still has no configuration API.
            return false;
        }

        if (!open && !Authorised(context.Request))
        {
            // 401 without a WWW-Authenticate challenge on purpose: this is a machine interface
            // with a shared secret, and inviting a browser to pop a password box would suggest
            // an interactive login that does not exist.
            await RespondAsync(context, 401, "unauthorized: present the configured api key as "
                + "\"Authorization: Bearer KEY\" or \"X-API-Key: KEY\"").ConfigureAwait(false);
            return true;
        }

        if (path.StartsWith("/api/config/history", StringComparison.Ordinal))
        {
            await GuardedAsync(context, () => HistoryAsync(context, path)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/txtest")
        {
            await GuardedAsync(context, () => TxTestAsync(context)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/rig" or "/api/rig/tune")
        {
            await GuardedAsync(context, () => RigAsync(context, path)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/txlease")
        {
            await GuardedAsync(context, () => TxLeaseAsync(context)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/keepaudio")
        {
            await GuardedAsync(context, () => KeepAudioAsync(context)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/mixer")
        {
            // Only a keyless one. A caller holding the shared secret has better evidence than
            // a header the browser wrote, and refusing them would take away something the key
            // used to buy on a station that has both. The length check is not decoration:
            // FixedTimeEquals of two empty arrays is true, so on a station with no key an empty
            // X-API-Key would otherwise "match" and skip the check below.
            bool keyed = _key.Length > 0 && Authorised(context.Request);

            if (open && !keyed && context.Request.HttpMethod == "POST"
                && !SameSite(context.Request))
            {
                await RespondAsync(context, 403,
                    "forbidden: this request came from a page this station did not serve, and "
                    + "\"waterfall\".\"enableAudioControls\" asks for no key. A page of your own, "
                    + "a client that sends no Origin header (curl, a script), and a caller "
                    + "presenting the api key are all fine.")
                    .ConfigureAwait(false);
                return true;
            }

            await GuardedAsync(context, () => MixerAsync(context)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/proposals")
        {
            if (context.Request.HttpMethod != "GET")
            {
                await RespondAsync(context, 405, "GET to read the station's modem proposals; "
                    + "POST the configuration each one carries to /api/config to act on it")
                    .ConfigureAwait(false);
                return true;
            }

            await ProposalsAsync(context).ConfigureAwait(false);
            return true;
        }

        switch (context.Request.HttpMethod)
        {
            case "GET":
                await RespondJsonAsync(context, 200, $$"""
                    {
                      "source": {{(_ephemeralInForce ? "\"ephemeral\"" : "\"file\"")}},
                      "instance": "{{Instance}}",
                      "configPath": {{JsonSerializer.Serialize(_configPath)}},
                      "running": {{Redact(_runningJson())}}
                    }
                    """).ConfigureAwait(false);
                return true;

            case "POST":
                await GuardedAsync(context, () => ApplyAsync(context)).ConfigureAwait(false);
                return true;

            default:
                await RespondAsync(context, 405, "GET to read the running configuration, "
                    + "POST to replace it").ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>
    /// <c>GET /api/config/history</c>, the versions the station has been applied on, and
    /// <c>GET /api/config/history/ID</c>, one of them with its secrets redacted as
    /// <c>GET /api/config</c> redacts the running one.
    /// </summary>
    private async Task HistoryAsync(HttpListenerContext context, string path)
    {
        if (context.Request.HttpMethod != "GET")
        {
            await RespondAsync(context, 405, "GET to list the earlier versions, GET /api/config/history/ID "
                + "to read one; POST one back to /api/config to go back to it").ConfigureAwait(false);
            return;
        }

        const string prefix = "/api/config/history";
        string rest = path[prefix.Length..].Trim('/');
        if (rest.Length == 0)
        {
            var versions = new JsonArray();
            // The newest version that is what the station is running now, if any: the page says
            // "in use now" on it. Compared as the page would see them, redacted.
            string runningText = _runningJson();
            JsonNode? running = null;
            try
            {
                running = JsonNode.Parse(Redact(runningText));
            }
            catch (JsonException)
            {
            }

            bool marked = false;
            foreach (ConfigHistory.Version v in _history.List())
            {
                bool inUse = false;
                var secretsChanged = new JsonArray();
                if (_history.Read(v.Id) is { } text)
                {
                    // A version whose secrets differ is not what is running, though the redacted
                    // documents look alike.
                    IReadOnlyList<string> differing = SecretsDiffering(text, runningText);
                    foreach (string secret in differing)
                    {
                        secretsChanged.Add(secret);
                    }

                    if (!marked && running is not null && differing.Count == 0)
                    {
                        try
                        {
                            inUse = JsonNode.DeepEquals(running, JsonNode.Parse(Redact(text)));
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }

                marked |= inUse;
                versions.Add(new JsonObject
                {
                    ["id"] = v.Id,
                    ["time"] = v.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["kind"] = v.Kind,
                    ["inUse"] = inUse,
                    ["secretsChanged"] = secretsChanged,
                });
            }

            await RespondJsonAsync(context, 200,
                new JsonObject { ["versions"] = versions }.ToJsonString(Pretty)).ConfigureAwait(false);
            return;
        }

        string? document = _history.Read(rest);
        ConfigHistory.Version? version = document is null ? null : _history.List().FirstOrDefault(v => v.Id == rest);
        if (document is null || version is null)
        {
            await RespondAsync(context, 404, $"no earlier version \"{rest}\"").ConfigureAwait(false);
            return;
        }

        await RespondJsonAsync(context, 200, $$"""
            {
              "id": {{JsonSerializer.Serialize(version.Id)}},
              "time": {{JsonSerializer.Serialize(version.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}},
              "kind": {{JsonSerializer.Serialize(version.Kind)}},
              "secretsChanged": {{JsonSerializer.Serialize(SecretsDiffering(document, _runningJson()))}},
              "config": {{Redact(document)}}
            }
            """).ConfigureAwait(false);
    }

    /// <summary>
    /// What the station thinks it should be listening to and is not, each with the complete
    /// configuration that would act on it.
    /// </summary>
    /// <remarks>
    /// The configuration travels with the proposal rather than being built by whoever acts on
    /// it, so a caller never has to know how a modem entry is spelled or which sub-channel is
    /// free - and, more to the point, so acting on a proposal is an ordinary POST to
    /// <c>/api/config</c> and is validated, refused and made ephemeral by exactly the same code
    /// as an operator's own edit. There is deliberately no apply endpoint here.
    /// </remarks>
    private async Task ProposalsAsync(HttpListenerContext context)
    {
        if (_proposals is null)
        {
            await RespondJsonAsync(context, 200, """
                {
                  "proposing": false,
                  "why": "set \"survey\".\"propose\": true to read each capture back with every mode that could have carried it",
                  "proposals": []
                }
                """).ConfigureAwait(false);
            return;
        }

        string running = _runningJson();
        var items = new JsonArray();
        foreach (Survey.ModemProposal proposal in _proposals())
        {
            string? amended = ProposedConfig.Amend(running, proposal, out string why);
            items.Add(new JsonObject
            {
                ["mode"] = proposal.Mode,
                ["kind"] = proposal.Kind.ToString(),
                ["audioCentreHz"] = proposal.AudioCentreHz,
                ["rfFrequencyHz"] = proposal.RfFrequencyHz,
                ["captures"] = proposal.Captures,
                ["frames"] = proposal.Frames,
                ["stations"] = new JsonArray([.. proposal.Stations.Select(s => JsonValue.Create(s))]),
                ["meanSnrDb"] = proposal.MeanSnrDb,
                ["firstHeard"] = proposal.FirstHeard,
                ["lastHeard"] = proposal.LastHeard,
                ["conflicts"] = new JsonArray([.. proposal.Conflicts.Select(c => JsonValue.Create(c))]),
                ["summary"] = proposal.Summary(),
                ["config"] = amended is null ? null : JsonNode.Parse(Redact(amended)),
                ["cannotApply"] = amended is null ? why : null,
            });
        }

        (long examined, long read, long dropped) = _prospectorCounts?.Invoke() ?? (0, 0, 0);
        var body = new JsonObject
        {
            ["proposing"] = true,
            ["examined"] = examined,
            ["readable"] = read,
            ["skippedForBacklog"] = dropped,
            ["proposals"] = items,
        };
        await RespondJsonAsync(
            context, 200, body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The sound card's mixer: what it reads back as, and a change applied to it live.
    /// </summary>
    /// <remarks>
    /// <para>Unlike <c>/api/config</c> this does not restart anything. A mixer setting takes
    /// effect on the card as it is written, the PCM stream is untouched, and restarting a station
    /// to trim its capture gain would drop the waterfall the operator is trimming it against.</para>
    /// <para><b>And, unlike everything else here, it persists by default</b> (Tom, 2026-09-06).
    /// A level trimmed on the page is meant to stay trimmed, so it is remembered in the daemon's
    /// own state file and applied at the next start-up. Nothing rewrites the config file: what
    /// that file pins is applied first and wins, and the answer says so when the control just
    /// changed is one of them. <c>?persist=false</c> is the one-run try, for a value being
    /// listened to rather than kept.</para>
    /// </remarks>
    private async Task MixerAsync(HttpListenerContext context)
    {
        if (_mixer is not MixerRuntime mixer)
        {
            if (context.Request.HttpMethod == "GET")
            {
                await RespondJsonAsync(
                    context, 200,
                    MixerApi.Unavailable(_mixerWhyNot).ToJsonString(Pretty)).ConfigureAwait(false);
                return;
            }

            await RespondAsync(context, 409, _mixerWhyNot).ConfigureAwait(false);
            return;
        }

        if (context.Request.HttpMethod == "GET")
        {
            await RespondJsonAsync(
                context, 200, MixerApi.Describe(mixer.Read()).ToJsonString(Pretty))
                .ConfigureAwait(false);
            return;
        }

        if (context.Request.HttpMethod != "POST")
        {
            await RespondAsync(context, 405, "GET to read the card's mixer, POST to set it")
                .ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (!MixerApi.TryParse(body, out MixerChange change, out string refusal))
        {
            await RespondAsync(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        // One change at a time. Applying is set-then-read across several calls into the card, so
        // two overlapping POSTs would each be answered with the other's level - and the read-back
        // is the answer, and the answer is the point. They would also both write the state file.
        // The waterfall serves every request on its own task, so nothing else is stopping them.
        await _mixerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Asked of the open card, before anything is touched: a dB the card cannot take is a
            // 400 with the card's own range in it, not a level half-applied and reported as a
            // success. This is the only check that needs the hardware, so it cannot be done at
            // parse time - the config file's equivalent is a journal line at start-up instead.
            if (mixer.WhyRefused(change) is string cannot)
            {
                await RespondAsync(context, 400, cannot).ConfigureAwait(false);
                return;
            }

            // persist=false is the deliberate opt-out; anything else, including no query string
            // at all, persists. The page never sends it: a trim made on the page is meant to
            // stay made.
            bool persist = !string.Equals(
                context.Request.QueryString["persist"], "false", StringComparison.OrdinalIgnoreCase);

            MixerOutcome outcome = mixer.Apply(change, persist);

            // The mixer's own lines have already been printed by the apply above, prefix and all,
            // so this only has to say who asked - not repeat them behind a second prefix.
            Console.WriteLine("api: mixer set over the API");

            JsonObject answer = MixerApi.Describe(outcome.Report);
            answer["applied"] = true;
            answer["persisted"] = outcome.Persisted;
            // Whether the note is something to put in front of the operator now. The page shows
            // it on the strength of this, rather than inferring it from the other fields and
            // getting it subtly wrong for the cases that matter.
            answer["warn"] = outcome.Warn;
            answer["stateFile"] = mixer.StatePath;
            answer["note"] = outcome.Note;
            await RespondJsonAsync(context, 200, answer.ToJsonString(Pretty)).ConfigureAwait(false);
        }
        finally
        {
            _mixerGate.Release();
        }
    }

    /// <summary>Indented, because these answers are read by people as often as by scripts.</summary>
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private async Task ApplyAsync(HttpListenerContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await RespondAsync(context, 400, "the body must be a complete configuration document, "
                + "the same shape as soundmodem.json").ConfigureAwait(false);
            return;
        }

        // A document loaded from an earlier version has that version's secrets as placeholders:
        // they are filled from the version, and the where-it-is-sent checks are made against it.
        string stored = _runningJson();
        string? restoreFrom = context.Request.QueryString["restoreFrom"];
        if (!string.IsNullOrEmpty(restoreFrom))
        {
            if (_history.Read(restoreFrom) is not string version)
            {
                await RespondAsync(context, 400, $"\"restoreFrom\" names no earlier version: \"{restoreFrom}\"")
                    .ConfigureAwait(false);
                return;
            }

            stored = version;
        }

        if (RestoreHidden(body, stored, out string why) is not string restored)
        {
            await RespondAsync(context, 400, why).ConfigureAwait(false);
            return;
        }

        body = restored;

        // ?check=true validates and stops there: the configuration page's Check button, and a
        // way for anything else to ask "would this start?" without the restart that answers it.
        bool check = string.Equals(
            context.Request.QueryString["check"], "true", StringComparison.OrdinalIgnoreCase);
        if (check)
        {
            if (Validate(body, _configPath) is string problem)
            {
                await RespondAsync(context, 400, problem).ConfigureAwait(false);
            }
            else
            {
                await RespondJsonAsync(context, 200, """
                    { "valid": true, "applied": false, "note": "the station would start on this; nothing was changed" }
                    """).ConfigureAwait(false);
            }

            return;
        }

        if (Validate(body, _configPath) is string refusal)
        {
            // The station is untouched. This is the whole reason the API exists rather than an
            // operator editing the file: a refusal here costs nothing that was working.
            Console.Error.WriteLine("api: configuration declined, the station is unchanged");
            foreach (string line in refusal.Split('\n'))
            {
                Console.Error.WriteLine($"api:   {line}");
            }

            await RespondAsync(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        bool persist = string.Equals(
            context.Request.QueryString["persist"], "true", StringComparison.OrdinalIgnoreCase);
        string target = persist ? _configPath : _ephemeralPath;
        try
        {
            EnsureDirectoryOf(target);
            if (persist)
            {
                File.WriteAllText(target, body);
            }
            else
            {
                // The one-run file can hold the key too: private from the moment it exists.
                ConfigHistory.WritePrivate(target, body);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            string extra = persist
                ? " The shipped unit sets ProtectSystem=full, which makes /etc read-only to the "
                  + "service: add ReadWritePaths=/etc/pdn-soundmodem to persist from here, or "
                  + "apply without ?persist=true."
                : "";
            await RespondAsync(context, 500, $"could not write {target}: {e.Message}{extra}")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            _history.Record(body, persist, _runningJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The apply has happened; losing a roll-back point is worth a line, not a failure.
            Console.Error.WriteLine($"api: WARNING - could not keep this version for roll-back ({e.Message})");
        }

        Console.WriteLine(persist
            ? $"api: configuration written to {_configPath}; restarting"
            : $"api: configuration accepted for one run; restarting (a later restart returns to {_configPath})");

        // Answered before the shutdown starts, so the caller learns the outcome rather than
        // losing the connection to the restart it asked for.
        await RespondJsonAsync(context, 200, $$"""
            {
              "applied": true,
              "persisted": {{(persist ? "true" : "false")}},
              "restarting": true,
              "note": {{JsonSerializer.Serialize(persist
                  ? "written to the config file; this is now the station's configuration"
                  : "in force until the next restart, then the config file applies again")}}
            }
            """).ConfigureAwait(false);

        _requestRestart();
    }

    /// <summary>
    /// Runs one handler and turns anything it throws into a 500 with the reason in it.
    /// </summary>
    /// <remarks>
    /// The waterfall server's own catch-all aborts the connection, which is right for a page and
    /// wrong for an API: a caller gets a closed socket with no status and no body, and has no way
    /// to tell a bug from a network fault. Worse here than anywhere, because these handlers act
    /// before they answer - the card is already set by the time a reply is written - so "nothing
    /// came back" must never be allowed to mean "nothing happened". A bug that reaches this is
    /// still a bug; it just has to arrive as one.
    /// </remarks>
    private static async Task GuardedAsync(HttpListenerContext context, Func<Task> handler)
    {
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"api: unhandled failure serving the request: {e}");
            try
            {
                await RespondAsync(context, 500, $"the daemon failed while serving this: {e.Message}")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The response had already started, so there is nothing left to say on it.
            }
        }
    }

    /// <summary>
    /// Makes sure the directory a file is about to be written to exists, and does nothing at all
    /// when the path names no directory - which is a file beside the working directory, not an
    /// error, and is what <c>Directory.CreateDirectory("")</c> would have thrown over.
    /// </summary>
    private static void EnsureDirectoryOf(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Whether a keyless mixer change may be acted on, given where the page that sent it came
    /// from.
    /// </summary>
    /// <remarks>
    /// The same rule as the operator page's transmit test, and the same reasoning: with no key in
    /// front of it, the only thing between the card and a page the operator's browser happens to
    /// load is that a browser sets <c>Origin</c> itself and script cannot change it. A non-browser
    /// client - curl, a script, the test suite - sends none at all and is left alone, having no
    /// ambient credentials for somebody else's page to borrow. See
    /// <see cref="Waterfall.WaterfallWebServer.OriginMayKey"/>, which this calls rather than
    /// reimplements: two dialects of one rule drift apart.
    /// <para>Asked of a POST and not of a GET, deliberately. Nothing is served with CORS headers
    /// anywhere in this tree, so a foreign page can issue the read and cannot see the answer,
    /// and the answer is what the operator's page already shows anyway. What it cannot do is
    /// change the card.</para>
    /// <para>What this rule buys is that a page cannot reach the station <em>by name</em>. It
    /// compares against the Host of the same request, so a page that rebinds its own name to
    /// this station's address arrives with the two agreeing and gets through - inherited from
    /// the transmit test, which has the same hole (issue #423), and a reason the flag belongs on
    /// a network the operator trusts rather than a defence to lean on.</para>
    /// </remarks>
    /// <summary>
    /// Whether the request named this machine by an address, <c>localhost</c>, or the machine's
    /// own host name, bare or in a home network's usual domains (<c>radio1.lan</c>,
    /// <c>radio1.local</c>) or the machine's own. Not any domain: <c>radio1.evil.example</c> is a
    /// name anyone can point at this station.
    /// </summary>
    internal static bool ByOwnName(HttpListenerRequest request) =>
        IsOwnName(request.Url?.Host ?? "", Dns.GetHostName(), OwnDomain.Value);

    private static readonly Lazy<string> OwnDomain = new(() =>
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return "";
        }
    });

    private static readonly string[] HomeDomains = ["local", "lan", "home", "home.arpa", "localdomain"];

    internal static bool IsOwnName(string host, string machine, string domain = "")
    {
        host = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out _)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string self = machine.Split('.')[0];
        if (self.Length == 0)
        {
            return false;
        }

        IEnumerable<string> domains = domain.Trim('.').Length > 0
            ? HomeDomains.Append(domain.Trim('.'))
            : HomeDomains;
        return string.Equals(host, self, StringComparison.OrdinalIgnoreCase)
            || domains.Any(d => string.Equals(host, $"{self}.{d}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameSite(HttpListenerRequest request) =>
        Waterfall.WaterfallWebServer.OriginMayKey(
            new Waterfall.PageOrigin(request.Headers["Origin"] ?? "", request.UserHostName ?? ""));

    private bool Authorised(HttpListenerRequest request)
    {
        string? presented = request.Headers["X-API-Key"];
        if (presented is null
            && request.Headers["Authorization"] is { } header
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = header["Bearer ".Length..].Trim();
        }

        // Fixed-time compare: the key is a shared secret over a socket that may be reachable from
        // the LAN, and a byte-at-a-time comparison leaks it one byte at a time.
        return presented is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_key));
    }

    private static Task RespondAsync(HttpListenerContext context, int status, string text) =>
        WriteAsync(context, status, "text/plain; charset=utf-8", text);

    private static Task RespondJsonAsync(HttpListenerContext context, int status, string json) =>
        WriteAsync(context, status, "application/json; charset=utf-8", json);

    private static async Task WriteAsync(
        HttpListenerContext context, int status, string contentType, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}
