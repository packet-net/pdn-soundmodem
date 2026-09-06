using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// What a page or API mixer change is remembered as between runs.
/// </summary>
/// <remarks>
/// <para>Only the two levels, the card they were written for and when. Deliberately not a
/// configuration document: this file is the daemon's own scribble, not a description of the
/// intended station, and an operator is never expected to edit it. The config file remains the
/// place a level is written down on purpose, and it wins - see <see cref="MixerStateFile"/>.</para>
/// <para><b>The AGC and the mic boost are not here and never will be.</b> They are switched off
/// at every start-up on any card that has them, so there is nothing to remember: a file that
/// recorded them could only ever say "off", and a file from 0.58.x that does say so is read
/// without complaint and rewritten without them.</para>
/// <para>Every setting is nullable and only the ones that have actually been set through the page
/// or the API are written. A control nobody has ever touched stays absent, so it keeps whatever
/// the card has and the state file never quietly starts pinning something.</para>
/// </remarks>
internal sealed record MixerState
{
    /// <summary>The <c>device</c> this was written for, so another card's file is not applied.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; init; }

    /// <summary>When it was written, in UTC, for a human reading the file.</summary>
    [JsonPropertyName("writtenAt")]
    public DateTimeOffset? WrittenAt { get; init; }

    /// <summary>The capture level in dB, or null if none has ever been set here.</summary>
    [JsonPropertyName("captureGainDb")]
    public double? CaptureGainDb { get; init; }

    /// <summary>The transmit-side level in dB, or null if none has ever been set here.</summary>
    [JsonPropertyName("playbackDb")]
    public double? PlaybackDb { get; init; }

    /// <summary>Whether this holds any setting at all.</summary>
    [JsonIgnore]
    public bool HoldsAnything => CaptureGainDb is not null || PlaybackDb is not null;

    /// <summary>This state with one change folded into it, keeping what the change is silent about.</summary>
    /// <param name="change">What was just set on the card.</param>
    /// <param name="device">The station's device, stamped in as this is written.</param>
    /// <param name="now">The moment to record.</param>
    public MixerState With(MixerChange change, string device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        return this with
        {
            Device = device,
            WrittenAt = now,
            CaptureGainDb = change.CaptureGainDb ?? CaptureGainDb,
            PlaybackDb = change.PlaybackDb ?? PlaybackDb,
        };
    }

    /// <summary>What this holds, for one journal line.</summary>
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
}

/// <summary>
/// Reading, writing and applying the mixer state file - where a change made from the operator
/// page or <c>/api/mixer</c> is remembered so the next start-up sets it again.
/// </summary>
/// <remarks>
/// <para><b>Why a state file and not the config file</b> (Tom, 2026-09-06): "If specified in the
/// config file I'm happy for this value to be applied at startup. If not specified in the config
/// file then persist between runs in some kind of state file." So the config file keeps its one
/// job - the description of the intended station, hand-edited, full of comments, never written by
/// the daemon - and a level trimmed on the page survives a restart without anything rewriting it.
/// </para>
/// <para><b>The config file wins, per control.</b> A station that pins <c>captureGainDb</c> comes
/// up on that value every time, whatever the page did last week; the controls it says nothing
/// about come from the state file; the rest are left exactly as the card has them. That ordering
/// is what makes the file safe to write without asking: it can never override a deliberate
/// setting, only fill in for one that was never made.</para>
/// <para><b>Stamped with the device.</b> A state file holding a capture level for one card,
/// applied to the different card that turned up in its place, would set a level nobody chose on
/// hardware nobody checked. So the device it was written for goes in, and a file for another
/// device is ignored with a journal line rather than applied.</para>
/// </remarks>
internal static class MixerStateFile
{
    /// <summary>What the file is called when the configuration does not name one.</summary>
    public const string DefaultName = "mixer-state.json";

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Where the state file lives: what the configuration named, else the systemd state
    /// directory, else beside the config file.
    /// </summary>
    /// <remarks>
    /// <para><c>$STATE_DIRECTORY</c> is <c>/var/lib/pdn-soundmodem</c> under the shipped unit,
    /// which systemd creates and chowns to the service user from <c>StateDirectory=</c>. So the
    /// packaged daemon can write this with no packaging change at all, and in particular without
    /// <c>/etc</c> becoming writable - <c>ProtectSystem=full</c> stays exactly as it is.</para>
    /// <para>Beside the config file is the fallback for a bare run from a terminal, where there
    /// is no systemd and no state directory. <c>Path.GetDirectoryName</c> of a bare file name is
    /// the empty string rather than null, which is the shape that gave
    /// <c>Directory.CreateDirectory("")</c> on the bench in 0.57.0, so it is checked for.</para>
    /// </remarks>
    /// <param name="configured">The <c>alsa.mixer.stateFile</c> setting, if any.</param>
    /// <param name="configPath">The station's config file.</param>
    public static string PathFor(string? configured, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string? state = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrEmpty(state))
        {
            // systemd hands over a colon-separated list when there is more than one; the first
            // is this unit's own.
            string first = state.Split(':')[0];
            if (first.Length > 0)
            {
                return Path.Combine(first, DefaultName);
            }
        }

        string directory = Path.GetDirectoryName(configPath) is { Length: > 0 } beside ? beside : ".";
        return Path.Combine(directory, DefaultName);
    }

    /// <summary>
    /// Reads the state file, or explains in one journal-ready sentence why it is being ignored.
    /// </summary>
    /// <remarks>
    /// Every failure here is a line and a shrug. A state file is a convenience; a station
    /// receiving is not, and there is no failure of this file that is worth costing a start-up.
    /// </remarks>
    /// <param name="path">Where the file is.</param>
    /// <param name="device">The station's device, which the file has to match.</param>
    /// <param name="why">Why it is being ignored, when this returns null; empty when the file is
    /// simply not there, which is the ordinary case and says nothing.</param>
    public static MixerState? TryRead(string path, string device, out string why)
    {
        why = "";
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"{path} could not be read ({e.Message}), so it is ignored";
            return null;
        }

        MixerState? state;
        try
        {
            state = JsonSerializer.Deserialize<MixerState>(text, Read);
        }
        catch (JsonException e)
        {
            why = $"{path} will not parse ({e.Message}), so it is ignored; delete it to start "
                + "again, or set the levels in the config file";
            return null;
        }

        if (state is null || !state.HoldsAnything)
        {
            return null;
        }

        // Not a "close enough" comparison. Two cards that differ by one character are two cards.
        if (!string.Equals(state.Device, device, StringComparison.Ordinal))
        {
            why = $"{path} was written for \"{state.Device}\" and this station is \"{device}\", "
                + "so it is ignored; a level chosen for one card is not a level for another";
            return null;
        }

        return state;
    }

    /// <summary>
    /// Writes the state file, atomically, or says why it could not be.
    /// </summary>
    /// <remarks>
    /// Temp file in the same directory then a rename, so a state file is never a half-written
    /// one: the rename is atomic within a filesystem, and a crash mid-write leaves the previous
    /// file intact rather than a truncated one that the next start-up would refuse to parse.
    /// </remarks>
    /// <param name="path">Where to write it.</param>
    /// <param name="state">What to write.</param>
    /// <param name="why">What went wrong, when this returns false.</param>
    public static bool TryWrite(string path, MixerState state, out string why)
    {
        why = "";
        string temp = "";
        try
        {
            string directory = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
            Directory.CreateDirectory(directory);
            temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Write) + "\n");
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            why = e.Message;
            try
            {
                if (temp.Length > 0 && File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is untidy, not a reason to fail the request twice.
            }

            return false;
        }
    }

    /// <summary>
    /// What to apply at start-up: the config file's settings, filled in from the state file where
    /// the config file said nothing, with each one's source recorded.
    /// </summary>
    /// <param name="config">The <c>alsa.mixer</c> block, or null when there is none.</param>
    /// <param name="state">What the state file held, or null when it is absent or ignored.</param>
    public static MixerSettings Combine(AlsaMixerConfig? config, MixerState? state)
    {
        MixerSettings settings = config?.ToSettings() ?? new MixerSettings();

        double? capture = settings.CaptureGainDb ?? state?.CaptureGainDb;
        double? playback = settings.PlaybackDb ?? state?.PlaybackDb;

        return settings with
        {
            CaptureGainDb = capture,
            PlaybackDb = playback,

            // The one thing here that is not "what the file said, else what the state file
            // holds". It is the start-up pass, so it is the pass that switches the two off.
            ForceAgcAndBoostOff = true,
            Sources = new MixerSources(
                Source(settings.CaptureGainDb, capture),
                Source(settings.PlaybackDb, playback)),
        };

        static MixerSource Source(object? fromConfig, object? applied) =>
            fromConfig is not null ? MixerSource.Config
            : applied is not null ? MixerSource.StateFile
            : MixerSource.None;
    }

    /// <summary>
    /// The line the journal carries about the state file at start-up, whatever happened to it.
    /// </summary>
    /// <param name="path">Where the file is.</param>
    /// <param name="state">What was read from it, or null.</param>
    /// <param name="ignored">Why it was ignored, or empty.</param>
    public static string StartUpLine(string path, MixerState? state, string ignored)
    {
        if (ignored.Length > 0)
        {
            return ignored;
        }

        if (state is null)
        {
            return $"page and API changes are remembered in {path} (nothing there yet)";
        }

        string when = state.WrittenAt is DateTimeOffset at
            ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "an unrecorded time";
        return $"{path} holds {state.Describe()} from {when}";
    }
}
