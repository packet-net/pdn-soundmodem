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
    /// <summary>Capture gain in dB, inside the card's own range.</summary>
    [JsonPropertyName("captureGainDb")]
    public double? CaptureGainDb { get; set; }

    /// <summary>Transmit-side playback level in dB, inside the card's range.</summary>
    [JsonPropertyName("playbackDb")]
    public double? PlaybackDb { get; set; }

    /// <summary>Whether this asks for anything at all.</summary>
    [JsonIgnore]
    public bool SetsAnything => CaptureGainDb is not null || PlaybackDb is not null;

    /// <summary>What this asks for, in one phrase, for a journal line.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CaptureGainDb is double capture)
        {
            parts.Add($"{MixerSetup.CaptureKey} {MixerSetup.Db(capture)} dB");
        }

        if (PlaybackDb is double playback)
        {
            parts.Add($"{MixerSetup.PlaybackKey} {MixerSetup.Db(playback)} dB");
        }

        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>
    /// A station's mixer settings with this change folded over them, keeping the control-name
    /// lists and asking for nothing this change did not mention.
    /// </summary>
    /// <remarks>
    /// One method rather than the same object initialiser in four places. It was in three when
    /// this shipped, and a fourth caller getting one field wrong would set the card to something
    /// nobody asked for - which is exactly the kind of fault a mixer makes inaudible until a
    /// station stops decoding.
    /// </remarks>
    /// <param name="baseline">The station's settings, for the control names.</param>
    public MixerSettings Over(MixerSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return baseline with
        {
            CaptureGainDb = CaptureGainDb,
            PlaybackDb = PlaybackDb,

            // A request never forces the switches off: they were forced off at start-up and a
            // GET or a POST is not a start-up. Writing to a card in answer to a read would also
            // make --mixer-show and the page's own probe change the station they are describing.
            ForceAgcAndBoostOff = false,

            // Nothing here came from the config file or the state file: it came from whoever is
            // holding the API key, this second. Saying "config" beside it would be a lie the
            // journal then carries for ever.
            Sources = new MixerSources(),
        };
    }
}

/// <summary>
/// The mixer half of the configuration API: reading a change off the wire, and turning the card's
/// state into the JSON a page or a script reads.
/// </summary>
/// <remarks>
/// <para><b>Why this is not a POST of a whole configuration.</b> Everything else the API changes
/// needs the station rebuilt around it, so <c>/api/config</c> writes the document and restarts.
/// A mixer setting needs neither: it lands on the card the moment it is written, the PCM stream
/// is not touched, and restarting the daemon to nudge a capture gain would drop the very
/// waterfall the operator is watching it on.</para>
/// <para><b>The config file is never written from here</b> (Tom, 2026-09-06). A change is
/// remembered in the daemon's own state file instead, and the config file stays what it has
/// always been: hand-edited, full of comments, and the thing that wins at start-up. See
/// <see cref="MixerStateFile"/>.</para>
/// </remarks>
internal static class MixerApi
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A field this does not know is refused rather than dropped, which is the same answer the
        // config file gives the same keys. Dropped, {"captureGain": 6, "playbackDb": -8} would
        // set the transmit level, silently ignore the capture gain, and report success - and the
        // caller's only clue would be a read-back they did not think to check.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// How a request body is looked over before it is deserialised: with comments and trailing
    /// commas, because a body is as often pasted out of a config file as generated.
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

        // The keys that are gone first, and by name rather than by letting the strict reader
        // trip over them, because "captureGainPercent is not a field this endpoint knows" is true
        // and useless to somebody whose script worked in 0.57.0. The unit changed as well as the
        // name: 60 used to mean 60% of the card's range and now means 60 dB, which on the bench
        // CM108 is 37 dB past the top of it. Aliasing them would have set exactly that. "agc" and
        // "micBoost" went the same way in 0.59.0 for a different reason - they are switched off
        // at every start-up and are not settings any more - and a script that still sends one
        // deserves to be told which of the two happened to it.
        if (Gone(body) is string gone)
        {
            why = gone;
            return false;
        }

        try
        {
            change = JsonSerializer.Deserialize<MixerChange>(body, Options) ?? new MixerChange();
        }
        catch (JsonException e)
        {
            why = $"the body must be a JSON object of mixer settings - {MixerSetup.CaptureKey}, "
                + $"{MixerSetup.PlaybackKey} - e.g. "
                + $"{{\"{MixerSetup.CaptureKey}\": 6.0}}. The levels are in dB; GET this "
                + "endpoint for the card's own range. " + e.Message;
            return false;
        }

        if (!change.SetsAnything)
        {
            why = $"no mixer settings in the body. Send {MixerSetup.CaptureKey}, "
                + $"{MixerSetup.PlaybackKey}, or both; an empty object would change nothing.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The refusal for a body still carrying a key that no longer exists, or null.
    /// </summary>
    /// <param name="body">The request body.</param>
    private static string? Gone(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body, nodeOptions: null, Jsonc);
        }
        catch (JsonException)
        {
            // Not this method's business. The deserialiser produces the better sentence about a
            // body that is not JSON at all.
            return null;
        }

        if (root is not JsonObject sent)
        {
            return null;
        }

        foreach (string key in sent.Select(pair => pair.Key))
        {
            if (AlsaMixerConfig.WhyRenamed(key) is string sentence)
            {
                return $"{sentence}, and by GET on this endpoint.";
            }

            if (AlsaMixerConfig.WhyForcedOff(key) is string forced)
            {
                return forced;
            }
        }

        return null;
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
            ["capture"] = Volume(report.Capture, report.Sources.CaptureGain),
            ["playback"] = Volume(report.Playback, report.Sources.Playback),
            ["agc"] = Switch(report.Agc),
            ["micBoost"] = Switch(report.MicBoost),
            ["summary"] = report.Summary,
            ["journal"] = new JsonArray([.. report.Journal.Select(l => JsonValue.Create(l))]),
        };
    }

    /// <summary>
    /// What a control's level is, what it can be, and what pinned it there.
    /// </summary>
    /// <remarks>
    /// The range travels with the value because "6.00 dB" says nothing about how much is left
    /// above it, and a slider cannot be drawn without it. <c>dbRange</c> is null on a card that
    /// publishes only raw steps, which is how a caller knows this control cannot be set in dB at
    /// all - and the summary line says "no dB scale" in words beside it.
    /// </remarks>
    private static JsonNode? Volume(MixerVolumeState? state, MixerSource source) => state is null
        ? null
        : new JsonObject
        {
            ["control"] = state.Control,
            ["decibels"] = state.Decibels,
            ["dbRange"] = state is { MinDb: double min, MaxDb: double max }
                ? new JsonObject
                {
                    ["min"] = min,
                    ["max"] = max,
                    // True where the step below "min" is the card's mute rather than a quieter
                    // level, so a page can say so instead of implying the scale runs on down.
                    ["mutesBelowMin"] = state.MutesBelowMin,
                }
                : null,
            ["percent"] = state.Percent,
            ["source"] = Name(source),
        };

    /// <summary>
    /// A switch this station does not set, reported so that a caller can see it was found and see
    /// what it actually is.
    /// </summary>
    /// <remarks>
    /// <para><b>Kept rather than dropped</b>, and read-only. AGC and mic boost stopped being
    /// settings in 0.59.0 - they are switched off at every start-up - so there is no
    /// <c>source</c> to name and no value to POST. What is left is still worth serving: it is the
    /// only way for a script or a page to see that a card <em>has</em> an AGC and that the card
    /// refused to switch it off, which is a real thing cards do and is invisible otherwise.
    /// Dropping the fields would have made that failure look exactly like the ordinary CM108,
    /// which simply has no mic boost at all.</para>
    /// <para>Null still means "this card has not got one".</para>
    /// </remarks>
    private static JsonNode? Switch(MixerSwitchState? state) => state is null
        ? null
        : new JsonObject
        {
            ["control"] = state.Control,
            ["on"] = state.On,
            // Always true, and said anyway: a caller reading "on": true needs the next sentence
            // to be "and this daemon asked for off", not a search of the release notes.
            ["forcedOff"] = true,
        };

    /// <summary>The source as the API spells it: lower case, ASCII, stable for a script.</summary>
    private static string Name(MixerSource source) => source switch
    {
        MixerSource.Config => "config",
        MixerSource.StateFile => "state",
        _ => "none",
    };
}
