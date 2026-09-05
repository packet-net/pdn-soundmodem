using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// One request to change the sound card's mixer: every field optional, and absent means "leave
/// that control alone" exactly as it does in the configuration file.
/// </summary>
public sealed class MixerChange
{
    /// <summary>Capture gain, 0-100 as a percentage of the card's range.</summary>
    [JsonPropertyName("captureGainPercent")]
    public int? CaptureGainPercent { get; set; }

    /// <summary>Automatic gain control on or off.</summary>
    [JsonPropertyName("agc")]
    public bool? Agc { get; set; }

    /// <summary>Microphone boost on or off.</summary>
    [JsonPropertyName("micBoost")]
    public bool? MicBoost { get; set; }

    /// <summary>Transmit-side playback level, 0-100 as a percentage.</summary>
    [JsonPropertyName("playbackPercent")]
    public int? PlaybackPercent { get; set; }

    /// <summary>Whether this asks for anything at all.</summary>
    [JsonIgnore]
    public bool SetsAnything =>
        CaptureGainPercent is not null || Agc is not null
        || MicBoost is not null || PlaybackPercent is not null;
}

/// <summary>
/// The mixer half of the configuration API: parsing a change, folding it into the station's
/// configuration document, and saying what the card reads back as.
/// </summary>
/// <remarks>
/// <para><b>Why this is not a POST of a whole configuration.</b> Everything else the API changes
/// needs the station rebuilt around it, so <c>/api/config</c> writes the document and restarts.
/// A mixer setting needs neither: it lands on the card the moment it is written, the PCM stream
/// is not touched, and restarting the daemon to nudge a capture gain would drop the very
/// waterfall the operator is watching it on. So this applies live and then writes the same
/// document to the same place, by the same ephemeral-unless-you-say-otherwise rule, purely so
/// that the next start-up sets what is set now.</para>
/// <para>The change is folded into the running document rather than replacing it, because a
/// slider on a page has no business restating the station's modems.</para>
/// </remarks>
internal static class MixerApi
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A field this does not know is refused rather than dropped, which is the same answer the
        // config file gives the same four keys. Dropped, {"captureGain": 45, "agc": false} would
        // set the AGC, silently ignore the gain, and report success - and the caller's only clue
        // would be a read-back they did not think to check.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// How a configuration document is read here: with comments and trailing commas, because a
    /// pdn-soundmodem config file is JSONC and the shipped example is most of the way to being a
    /// manual. <see cref="DaemonConfig"/> loads them the same way; a strict reader here meant
    /// every real config file on the network answered a mixer change with a parse error at the
    /// first "//" (found on the bench CM108, 2026-09-05).
    /// </summary>
    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads a request body, or explains what is wrong with it.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <param name="change">What was asked for.</param>
    /// <param name="why">The operator-facing refusal when this returns false.</param>
    public static bool TryParse(string body, out MixerChange change, out string why)
    {
        change = new MixerChange();
        why = "";
        try
        {
            change = JsonSerializer.Deserialize<MixerChange>(body, Options) ?? new MixerChange();
        }
        catch (JsonException e)
        {
            why = "the body must be a JSON object of mixer settings - captureGainPercent, agc, "
                + "micBoost, playbackPercent - e.g. {\"captureGainPercent\": 70, \"agc\": false}. "
                + e.Message;
            return false;
        }

        // The same sentence the config file's refusal uses, from the same method: an operator who
        // types 150 gets told the same thing whichever door they came in by.
        if (AlsaMixerConfig.WhyNotUsable(change.CaptureGainPercent, change.PlaybackPercent)
            is string wrong)
        {
            why = wrong;
            return false;
        }

        if (!change.SetsAnything)
        {
            why = "no mixer settings in the body. Send at least one of captureGainPercent, agc, "
                + "micBoost or playbackPercent; an empty object would change nothing.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The configuration <paramref name="running"/> would become with <paramref name="change"/>
    /// folded into its <c>alsa.mixer</c> block, or null when it cannot be built.
    /// </summary>
    /// <param name="running">The configuration this process is running, as JSON.</param>
    /// <param name="change">What to set.</param>
    /// <param name="why">Why not, in an operator's terms, when this returns null.</param>
    public static string? Amend(string running, MixerChange change, out string why)
    {
        ArgumentNullException.ThrowIfNull(change);
        why = "";

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(running, nodeOptions: null, Jsonc);
        }
        catch (JsonException e)
        {
            why = $"the running configuration will not parse: {e.Message}";
            return null;
        }

        if (root is not JsonObject document)
        {
            why = "the running configuration is not a JSON object";
            return null;
        }

        if (document["alsa"] is not JsonObject alsa)
        {
            alsa = [];
            document["alsa"] = alsa;
        }

        if (alsa["mixer"] is not JsonObject mixer)
        {
            mixer = [];
            alsa["mixer"] = mixer;
        }

        // Only what was asked for. A control the request said nothing about keeps whatever the
        // file said about it, including saying nothing, which is what leaves it alone.
        if (change.CaptureGainPercent is int capture)
        {
            mixer["captureGainPercent"] = capture;
        }

        if (change.Agc is bool agc)
        {
            mixer["agc"] = agc;
        }

        if (change.MicBoost is bool boost)
        {
            mixer["micBoost"] = boost;
        }

        if (change.PlaybackPercent is int playback)
        {
            mixer["playbackPercent"] = playback;
        }

        return document.ToJsonString(Indented);
    }

    /// <summary>
    /// The file as it is right now, if writing it back from a parsed document would lose nothing
    /// the operator put there; false and a sentence if it would.
    /// </summary>
    /// <remarks>
    /// <para>A config file here is JSONC, and the shipped example is most of the way to being a
    /// manual: comments are a large part of what is in one. Serialising a parsed document over the
    /// top of that would silently delete all of it, so this daemon does not do it - which is also
    /// why <c>POST /api/config</c> writes the caller's own bytes rather than anything it built.
    /// This is the one place that has no caller-supplied bytes to write, so it asks first.</para>
    /// <para>Asked by parsing strictly. A file that a strict reader accepts has nothing in it that
    /// a round trip could drop, whatever it happens to contain; one that it refuses has comments
    /// or trailing commas, and both are the operator's.</para>
    /// </remarks>
    /// <param name="configPath">The station's configuration file.</param>
    /// <param name="text">The file as it is right now, when this returns true.</param>
    /// <param name="why">What an operator should read when this returns false.</param>
    public static bool TryReadRewritable(string configPath, out string text, out string why)
    {
        why = "";
        text = "";
        try
        {
            // Read here rather than taken from what the process started on: an operator who has
            // edited the file since start-up must not have those edits replaced by a snapshot.
            text = File.ReadAllText(configPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"{configPath} could not be read ({e.Message})";
            return false;
        }

        try
        {
            _ = JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            why = $"{configPath} has comments or trailing commas in it, and this daemon never "
                + "writes a config file back from a parsed document - it would delete them";
            return false;
        }
    }

    /// <summary>
    /// The line an operator can paste into their config file to keep a change, which is what
    /// they are told to do when the file is one this daemon will not rewrite.
    /// </summary>
    /// <param name="change">What was set.</param>
    public static string Snippet(MixerChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var mixer = new JsonObject();
        if (change.CaptureGainPercent is int capture)
        {
            mixer["captureGainPercent"] = capture;
        }

        if (change.Agc is bool agc)
        {
            mixer["agc"] = agc;
        }

        if (change.MicBoost is bool boost)
        {
            mixer["micBoost"] = boost;
        }

        if (change.PlaybackPercent is int playback)
        {
            mixer["playbackPercent"] = playback;
        }

        return new JsonObject { ["alsa"] = new JsonObject { ["mixer"] = mixer } }.ToJsonString();
    }

    /// <summary>What a station with no mixer to offer answers with.</summary>
    /// <param name="why">What it would say to an operator.</param>
    public static JsonObject Unavailable(string why) => new()
    {
        ["available"] = false,
        ["why"] = why,
    };

    /// <summary>The card's state, for a page or a script to read.</summary>
    /// <param name="report">What the mixer layer found and read back.</param>
    public static JsonObject Describe(MixerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new JsonObject
        {
            ["available"] = true,
            ["card"] = report.Card,
            ["controls"] = new JsonArray([.. report.Controls.Select(c => JsonValue.Create(c))]),
            ["capture"] = Volume(report.Capture),
            ["playback"] = Volume(report.Playback),
            ["agc"] = Switch(report.Agc),
            ["micBoost"] = Switch(report.MicBoost),
            ["summary"] = report.Summary,
            ["journal"] = new JsonArray([.. report.Journal.Select(l => JsonValue.Create(l))]),
        };
    }

    private static JsonNode? Volume(MixerVolumeState? state) => state is null
        ? null
        : new JsonObject
        {
            ["control"] = state.Control,
            ["percent"] = state.Percent,
            ["decibels"] = state.Decibels,
        };

    private static JsonNode? Switch(MixerSwitchState? state) => state is null
        ? null
        : new JsonObject
        {
            ["control"] = state.Control,
            ["on"] = state.On,
        };
}
