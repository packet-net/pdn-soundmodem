using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Packet.Mailcast;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The built-in pdn-mailcast receiver: hear the hourly bulletins on 40 m and forward them into
/// the local BBS. Null (the default) is a station that does none of it.
/// </summary>
/// <remarks>
/// <para>Present, the station runs an MS110D receive modem on the mailcast signal (centred
/// <see cref="DialKHz"/> + 1.8 kHz) beside its own modems. It never transmits on it and it has no
/// KISS port. Frames to MCAST from one of <see cref="Sources"/> feed a store on disk, and each bulletin rebuilt from them
/// is offered to the BBS as the forwarding partner <see cref="MailcastBbsConfig.Login"/>.</para>
/// <para>Where the modem listens is decided at start-up, see <see cref="MailcastPlacement"/>: on
/// the station's own passband when that already hears the signal, else by retuning the rig around
/// each slot when <see cref="Retune"/> is true and there is a <c>rig</c> section, else the station
/// refuses to start and says why.</para>
/// </remarks>
public sealed partial class MailcastConfig
{
    /// <summary>The usual USB dial, 7.052 MHz, which puts the signal's centre at 1800 Hz audio.</summary>
    public const double DefaultDialKHz = 7052.0;

    /// <summary>The lowest dial accepted: the bottom of 160 m.</summary>
    public const double LowestDialKHz = 1800;

    /// <summary>The highest dial accepted: the top of HF.</summary>
    public const double HighestDialKHz = 30000;

    /// <summary>
    /// The stations the broadcast is taken from when <see cref="Sources"/> is left out: GB7RDG,
    /// which sends it, and M0LTE, which it may move to. The one place this list is written.
    /// </summary>
    public static IReadOnlyList<string> DefaultSources { get; } = ["GB7RDG", "M0LTE"];

    /// <summary>Where the BBS is and how to log in to it.</summary>
    public MailcastBbsConfig? Bbs { get; set; }

    /// <summary>
    /// The callsigns whose frames to MCAST are the broadcast, as base callsigns: case is ignored,
    /// and so is any SSID given, since every SSID of a source is accepted. Null (left out):
    /// <see cref="DefaultSources"/>. An empty list is refused.
    /// </summary>
    public List<string?>? Sources { get; set; }

    /// <summary>The USB dial the signal is heard on, in kHz; its centre is 1800 Hz above.</summary>
    public double DialKHz { get; set; } = DefaultDialKHz;

    /// <summary>
    /// Where the pieces, the rebuilt bulletins and the record of deliveries are kept. Null: a
    /// <c>mailcast</c> folder in the station's state directory.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Whether the rig may be retuned to <see cref="DialKHz"/> around each slot, when the
    /// station's own passband does not hear the signal. Needs a <c>rig</c> section. False by
    /// default: retuning somebody's radio is something they ask for.
    /// </summary>
    public bool Retune { get; set; }

    /// <summary>
    /// Programs to run before and after each slot the receiver listens to, for a radio shared
    /// with something else (stopping Ardopcf and starting it again, say). Null runs nothing.
    /// </summary>
    public MailcastHooksConfig? Hooks { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>The dial in Hz.</summary>
    [JsonIgnore]
    public double DialHz => DialKHz * 1000;

    /// <summary>The signal's centre on the band, in Hz.</summary>
    [JsonIgnore]
    public double CentreHz => DialHz + MailcastOnAir.CentreAudioHz;

    /// <summary>Whether <see cref="Sources"/> was left out, so <see cref="DefaultSources"/> is used.</summary>
    [JsonIgnore]
    internal bool SourcesDefaulted => Sources is null;

    /// <summary>
    /// The callsigns frames are accepted from, as a frame's address carries them: upper case,
    /// without an SSID, each once. Only meaningful once <see cref="Validate"/> has passed.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string> SourcesInUse =>
        Sources is null ? DefaultSources : [.. Sources.Select(s => BaseCall(s ?? "")).Distinct(StringComparer.Ordinal)];

    /// <summary>A list of callsigns for a person: "GB7RDG", "GB7RDG or M0LTE", "A, B or C".</summary>
    internal static string DescribeSources(IReadOnlyList<string> sources) =>
        sources.Count <= 1 ? string.Join("", sources) : $"{string.Join(", ", sources.Take(sources.Count - 1))} or {sources[^1]}";

    /// <summary>A source as written in the file, as a frame carries it: trimmed, upper case, no SSID.</summary>
    private static string BaseCall(string given)
    {
        string call = given.Trim().ToUpperInvariant();
        int dash = call.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? call : call[..dash];
    }

    /// <summary>The BBS settings, defaults filled in where the section is absent.</summary>
    [JsonIgnore]
    internal MailcastBbsConfig BbsInUse => Bbs ?? new MailcastBbsConfig();

    /// <summary>Where the receiver's files go for a station whose state lives in
    /// <paramref name="stationStateDirectory"/>.</summary>
    internal string StateDirectoryFor(string stationStateDirectory) =>
        StateDirectory is { Length: > 0 } stated ? stated : Path.Combine(stationStateDirectory, "mailcast");

    /// <summary>Refuses a <c>mailcast</c> section that cannot do what it says.</summary>
    internal static void Validate(DaemonConfig config)
    {
        if (config.Mailcast is not { } mailcast)
        {
            return;
        }

        if (config.Monitor is not null)
        {
            throw new InvalidDataException(
                "\"mailcast\" is set in a \"monitor\" configuration. A monitor fronts other people's "
                + "web receivers and has no BBS to deliver to - remove \"mailcast\", or run the "
                + "receiver on a station of its own.");
        }

        if (!(mailcast.DialKHz >= LowestDialKHz && mailcast.DialKHz <= HighestDialKHz))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"\"mailcast\".\"dialKHz\" is {mailcast.DialKHz}. That is the USB dial in kHz the "
                + $"signal is heard on, between {LowestDialKHz:F0} and {HighestDialKHz:F0}; the usual one is "
                + $"{DefaultDialKHz:F1}, which is also what you get if you leave it out."));
        }

        if (mailcast.Sources is { } sources)
        {
            string defaults = string.Join(", ", DefaultSources.Select(d => $"\"{d}\""));
            if (sources.Count == 0)
            {
                throw new InvalidDataException(
                    "\"mailcast\".\"sources\" is empty, so no frame would ever be taken. List the "
                    + $"callsigns the broadcast is sent from, or leave it out for [{defaults}].");
            }

            foreach (string? source in sources)
            {
                if (source is null || !SourcePattern().IsMatch(source.Trim()))
                {
                    throw new InvalidDataException(
                        $"\"mailcast\".\"sources\" has {(source is null ? "null" : $"\"{MailcastOnAir.Ascii(source)}\"")}, "
                        + "which is not a callsign. Give each source as a callsign of 1 to 6 letters and "
                        + $"digits, such as [{defaults}]; any SSID is accepted, so none is needed.");
                }
            }
        }

        if (mailcast.StateDirectory is { } directory && directory.Trim().Length == 0)
        {
            throw new InvalidDataException(
                "\"mailcast\".\"stateDirectory\" is empty. Give a folder, or leave it out for a "
                + "\"mailcast\" folder in the station's state directory.");
        }

        MailcastBbsConfig bbs = mailcast.Bbs ?? throw new InvalidDataException(
            "\"mailcast\" has no \"bbs\" section. The receiver forwards bulletins into your BBS, so it "
            + "needs at least {\"bbs\": {\"password\": \"...\"}} (LinBPQ on 127.0.0.1:8011 as Q0CAST "
            + "unless you say otherwise).");

        foreach ((string key, string? value) in (ReadOnlySpan<(string, string?)>)
            [("type", bbs.Type), ("host", bbs.Host), ("login", bbs.Login), ("password", bbs.Password), ("command", bbs.Command)])
        {
            if (value is null)
            {
                throw new InvalidDataException(
                    $"\"mailcast\".\"bbs\".\"{key}\" is null. Give it a value, or leave it out for the default"
                    + (key == "password" ? "; the password has none and is required." : "."));
            }
        }

        if (!bbs.IsLinBpq && !bbs.IsFbb)
        {
            throw new InvalidDataException(
                $"\"mailcast\".\"bbs\".\"type\" is \"{bbs.Type}\". Use \"linBpq\" for LinBPQ's mail "
                + "(through its FBBPORT) or \"fbb\" for Linux FBB.");
        }

        if (string.IsNullOrWhiteSpace(bbs.Host))
        {
            throw new InvalidDataException(
                "\"mailcast\".\"bbs\".\"host\" is empty. Give the BBS's address, normally 127.0.0.1.");
        }

        if (bbs.Port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"\"mailcast\".\"bbs\".\"port\" is {bbs.Port}, which is not a TCP port. For LinBPQ it is "
                + "the Telnet port's FBBPORT, often 8011.");
        }

        if (string.IsNullOrWhiteSpace(bbs.Login) || bbs.Login.Any(c => c is <= ' ' or > '~'))
        {
            throw new InvalidDataException(
                "\"mailcast\".\"bbs\".\"login\" must be one word, such as Q0CAST (the default): the "
                + "receiver's own login on your BBS, never your callsign or a mailcast source's.");
        }

        if (string.IsNullOrEmpty(bbs.Password))
        {
            throw new InvalidDataException(
                "\"mailcast\".\"bbs\".\"password\" is empty. Give the password of the receiver's login "
                + $"({bbs.Login}) on your BBS.");
        }

        if (bbs.Password.Contains('\r') || bbs.Password.Contains('\n')
            || bbs.Command.Contains('\r') || bbs.Command.Contains('\n'))
        {
            throw new InvalidDataException(
                "\"mailcast\".\"bbs\".\"password\" and \"command\" must each be one line.");
        }

        // Whether each program exists and this user may run it: asked of the system, so a
        // POST /api/config made to the running service is checked as the service's own user.
        if (mailcast.Hooks is { } hooks)
        {
            foreach ((string name, HookCommand? hook) in (ReadOnlySpan<(string, HookCommand?)>)
                [(SlotHookEnvironment.BeforeName, hooks.Before), (SlotHookEnvironment.AfterName, hooks.After)])
            {
                if (hook?.Problem() is { } problem)
                {
                    throw new InvalidDataException($"\"mailcast\".\"hooks\".\"{name}\": {problem}.");
                }
            }
        }
    }

    /// <summary>
    /// What to say about a <c>mailcast.hooks</c> the file's JSON could not be read into: a key
    /// that is not a hook's or the section's (both are refused, so a misspelt
    /// <c>"timeout"</c> is not silently ignored), or a value of the wrong shape.
    /// </summary>
    internal static string HooksReadProblem(JsonException e)
    {
        string at = e.Path ?? "$.mailcast.hooks";
        if (Regex.Match(e.Message, "property '([^']*)' could not be mapped") is { Success: true } unknown)
        {
            // The path may or may not end with the unknown key itself: "$.mailcast.hooks.befor",
            // or "$.mailcast.hooks.before(.timeout)".
            string name = unknown.Groups[1].Value;
            string[] parts = at.Split('.');
            bool inHook = parts.Length > 4 || (parts.Length == 4 && parts[3] != name);
            return inHook
                ? $"\"{MailcastOnAir.Ascii(name)}\" is not a setting of a hook (at {MailcastOnAir.Ascii(at)}): a hook has \"command\", \"args\" and \"timeoutSeconds\"."
                : $"\"{MailcastOnAir.Ascii(name)}\" is not a setting of \"mailcast\".\"hooks\": it has \"before\" and \"after\".";
        }

        return $"\"mailcast\".\"hooks\" cannot be read (at {MailcastOnAir.Ascii(at)}): give each hook as "
            + "{\"command\": \"/full/path/to/program\", \"args\": [\"a list\", \"of strings\"], \"timeoutSeconds\": 30}.";
    }

    /// <summary>A callsign as <c>mailcast.sources</c> takes it: 1 to 6 letters and digits, an optional SSID 0 to 15.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{1,6}(-([0-9]|1[0-5]))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePattern();

    /// <summary>The unknown-key warnings for this section and its <c>bbs</c>.</summary>
    internal static IEnumerable<(string Section, Dictionary<string, JsonElement>? Settings)> UnknownSections(MailcastConfig? mailcast) =>
        mailcast is null
            ? []
            : [("mailcast", mailcast.UnknownSettings), ("mailcast bbs", mailcast.Bbs?.UnknownSettings)];
}

/// <summary>
/// <c>mailcast.hooks</c>: a program run before each slot the receiver listens to, and one run
/// after it, as in pdn-mailcast's standalone receiver. Either may be left out; any other key, here
/// or in a hook, is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MailcastHooksConfig
{
    /// <summary>Run before each slot's window, so that it has finished by the time the window opens.</summary>
    public HookCommand? Before { get; set; }

    /// <summary>Run after each slot's window, whenever <see cref="Before"/> was started.</summary>
    public HookCommand? After { get; set; }
}

/// <summary>Where the mailcast receiver hands bulletins over, and how it logs in.</summary>
public sealed class MailcastBbsConfig
{
    /// <summary>"linBpq" (LinBPQ's mail on its FBBPORT, the default) or "fbb" (Linux FBB).</summary>
    public string Type { get; set; } = "linBpq";

    /// <summary>The BBS's address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>LinBPQ's FBBPORT, or FBB's telnet port.</summary>
    public int Port { get; set; } = 8011;

    /// <summary>The receiver's own login, a forwarding partner on the BBS. Q0CAST: a Q call is
    /// never issued, so it clashes with nobody, and it is never sent on the air.</summary>
    public string Login { get; set; } = "Q0CAST";

    /// <summary>The login's password. Never logged, never served.</summary>
    public string Password { get; set; } = "";

    /// <summary>LinBPQ only: the node command that reaches the mail application.</summary>
    public string Command { get; set; } = "BBS";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>Whether this is LinBPQ.</summary>
    [JsonIgnore]
    public bool IsLinBpq => string.Equals(Type, "linBpq", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is Linux FBB.</summary>
    [JsonIgnore]
    public bool IsFbb => string.Equals(Type, "fbb", StringComparison.OrdinalIgnoreCase);

    /// <summary>The BBS in words for the journal and the page: never the password.</summary>
    internal string Describe() =>
        $"{(IsFbb ? "FBB" : "LinBPQ")} at {Host}:{Port.ToString(CultureInfo.InvariantCulture)} as {Login}";
}
