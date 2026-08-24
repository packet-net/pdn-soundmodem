using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Modems;

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
/// </remarks>
internal sealed class ConfigApi
{
    private readonly string _key;
    private readonly string _configPath;
    private Func<IReadOnlyList<Survey.ModemProposal>>? _proposals;
    private Func<(long Examined, long Read, long Dropped)>? _prospectorCounts;
    private readonly string _ephemeralPath;
    private readonly Func<string> _runningJson;
    private readonly Action _requestRestart;
    private readonly bool _ephemeralInForce;

    /// <param name="key">The shared secret every request must present.</param>
    /// <param name="configPath">The config file, for <c>?persist=true</c>.</param>
    /// <param name="ephemeralPath">Where a non-persisted change is left for the next start-up.</param>
    /// <param name="runningJson">The configuration this process is running, as JSON.</param>
    /// <param name="ephemeralInForce">Whether this process started from an ephemeral change.</param>
    /// <param name="requestRestart">Asks the daemon to shut down for systemd to bring back.</param>
    public ConfigApi(
        string key, string configPath, string ephemeralPath,
        Func<string> runningJson, bool ephemeralInForce, Action requestRestart)
    {
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

    /// <summary>Where a non-persisted change is left for the next start-up to consume.</summary>
    /// <remarks>The state directory, which systemd creates and owns for the service user. Not
    /// beside the config file: <c>ProtectSystem=full</c> makes <c>/etc</c> read-only to the
    /// service, so that is the one place it reliably cannot write.</remarks>
    public static string EphemeralPathFor(string configPath) =>
        Path.Combine(
            Environment.GetEnvironmentVariable("STATE_DIRECTORY")
                ?? Path.GetDirectoryName(configPath)
                ?? ".",
            "pending-config.json");

    /// <summary>
    /// The configuration with <c>api.key</c> blanked, for serving back to a caller.
    /// </summary>
    /// <remarks>
    /// The caller already knows the key - they just presented it - so this is not keeping a secret
    /// from them. It is keeping it out of everywhere the *answer* subsequently goes: a terminal
    /// scrollback, a pasted diagnostic, a screenshot of a station's config. Secrets escape through
    /// their echoes more often than through their sockets.
    /// </remarks>
    public static string Redact(string json)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root?["api"] is JsonObject api && api.ContainsKey("key"))
            {
                api["key"] = "(set, not shown)";
                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            return json;
        }
        catch (JsonException)
        {
            // The running config parsed once already or the daemon would not have started, so
            // this is close to unreachable - but serving nothing beats serving a key by accident.
            return "{}";
        }
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
    public static string? Validate(string json)
    {
        string temp = Path.Combine(
            Path.GetTempPath(), $"pdn-soundmodem-proposed-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temp, json);
            DaemonConfig? proposed = DaemonConfig.TryLoad(temp, out string error);
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
                        + "Check the spelling against docs/modes.md.";
                }
            }

            try
            {
                // Plan mutates the entries it is given, which is why this runs against the
                // throwaway parse above and never against anything the station is using.
                bool flex = proposed.Device.StartsWith("flex:", StringComparison.OrdinalIgnoreCase);
                int dspRate = proposed.Modems.Any(
                    m => !DaemonConfig.IsArdop(m.Mode) && ModemCatalog.DspRateFor(m.Mode) == 48000)
                    ? 48000
                    : 12000;
                BandPlanner.Plan(
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
    public async Task<bool> HandleAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "";
        if (path is not ("/api/config" or "/api/proposals"))
        {
            return false;
        }

        if (!Authorised(context.Request))
        {
            // 401 without a WWW-Authenticate challenge on purpose: this is a machine interface
            // with a shared secret, and inviting a browser to pop a password box would suggest
            // an interactive login that does not exist.
            await RespondAsync(context, 401, "unauthorized: present the configured api key as "
                + "\"Authorization: Bearer KEY\" or \"X-API-Key: KEY\"").ConfigureAwait(false);
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
                      "configPath": {{JsonSerializer.Serialize(_configPath)}},
                      "running": {{Redact(_runningJson())}}
                    }
                    """).ConfigureAwait(false);
                return true;

            case "POST":
                await ApplyAsync(context).ConfigureAwait(false);
                return true;

            default:
                await RespondAsync(context, 405, "GET to read the running configuration, "
                    + "POST to replace it").ConfigureAwait(false);
                return true;
        }
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

        if (Validate(body) is string refusal)
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
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, body);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

        Console.WriteLine(persist
            ? $"api: configuration accepted and written to {_configPath} - restarting to apply it"
            : $"api: configuration accepted for one run (not persisted) - restarting to apply it. "
              + $"Any later restart returns this station to {_configPath}.");

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
