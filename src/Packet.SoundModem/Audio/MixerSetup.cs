using System.Globalization;

namespace Packet.SoundModem.Audio;

/// <summary>One volume control as the card reports it back.</summary>
/// <param name="Control">The control's name on this card.</param>
/// <param name="Decibels">Its level in dB, when the card publishes a scale; else null.</param>
/// <param name="MinDb">The quietest the card can be set to, in dB; null with no scale.</param>
/// <param name="MaxDb">The loudest, in dB; null with no scale.</param>
/// <param name="Percent">The same level as 0-100 of the card's raw range, which is what
/// <c>alsamixer</c> shows and the only figure there is on a card with no dB scale.</param>
/// <remarks>
/// The range travels with the value everywhere it is shown - the start-up journal, the API, the
/// operator page, <c>--mixer-show</c> - because "6.00 dB" says nothing about how much is left
/// above it, and how much is left is the question an operator setting a level is asking.
/// </remarks>
public sealed record MixerVolumeState(
    string Control, double? Decibels, double? MinDb, double? MaxDb, int Percent)
{
    /// <summary>
    /// Whether the step below <see cref="MinDb"/> is the card's mute rather than a quieter level.
    /// </summary>
    public bool MutesBelowMin { get; init; }

    /// <summary>Whether this card publishes a dB scale for this control, so it can be set in dB.</summary>
    public bool HasDbScale => Decibels is not null && MinDb is not null && MaxDb is not null;
}

/// <summary>One on/off control as the card reports it back.</summary>
/// <param name="Control">The control's name on this card.</param>
/// <param name="On">Whether it is on.</param>
public sealed record MixerSwitchState(string Control, bool On);

/// <summary>
/// What a card's mixer was found to have and what it reads back as, after any settings were
/// applied.
/// </summary>
public sealed record MixerReport
{
    /// <summary>The card, as ALSA names it.</summary>
    public required string Card { get; init; }

    /// <summary>Every control the card offers.</summary>
    public IReadOnlyList<string> Controls { get; init; } = [];

    /// <summary>The capture gain, or null when the card has no control this station knows.</summary>
    public MixerVolumeState? Capture { get; init; }

    /// <summary>The transmit-side level, or null when there is no such control.</summary>
    public MixerVolumeState? Playback { get; init; }

    /// <summary>Automatic gain control, or null when there is no such control.</summary>
    public MixerSwitchState? Agc { get; init; }

    /// <summary>Microphone boost, or null when there is no such control.</summary>
    public MixerSwitchState? MicBoost { get; init; }

    /// <summary>Where each setting that is in force came from, for the API and the page.</summary>
    public MixerSources Sources { get; init; } = new();

    /// <summary>The lines that were journalled, in order, prefix and all.</summary>
    public IReadOnlyList<string> Journal { get; init; } = [];

    /// <summary>
    /// The one line that states the card's whole state, or null when nothing was found to state.
    /// It is also the last entry in <see cref="Journal"/>.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Finding a card's controls by name, applying what the configuration asked for, and reading
/// back what the card actually did - with a journal line for each step.
/// </summary>
/// <remarks>
/// <para>All of the judgement in this feature lives here rather than in <see cref="AlsaMixer"/>,
/// which is deliberately a thin translation of <c>libasound</c>'s entry points. So the name
/// fallbacks, the "not found, skipped" wording, the read-back and the summary line are ordinary
/// code, driven in the tests against a fake card with whatever controls the case needs. What is
/// left unproven without hardware is only the P/Invoke.</para>
/// <para><b>Read-back is not optional.</b> A mixer setting that did not take is invisible - the
/// station simply sounds wrong - so every control this finds is read back from the card after
/// the write and the journal states what the card says, not what it was told. Cards quantise: a
/// capture range of 0-35 raw steps holds whole dB and nothing between, and the line says so by
/// printing what was asked for beside what the card did with it.</para>
/// <para><b>Two ways a dB setting can be impossible, and neither is guessed at.</b> A control
/// with no dB scale is refused with those words and left as the card has it, because converting
/// a dB into a percentage of a raw range the card never published would be setting a number that
/// means nothing. A value outside the card's range is refused with the range, because clamping
/// silently would tell an operator they had 30 dB of gain when the card stops at 23.</para>
/// </remarks>
public static class MixerSetup
{
    /// <summary>What every line here is prefixed with, so the journal groups at a glance.</summary>
    public const string JournalPrefix = "alsa: mixer: ";

    /// <summary>The configuration key the capture level is set by, named in every refusal.</summary>
    public const string CaptureKey = "captureGainDb";

    /// <summary>The configuration key the playback level is set by.</summary>
    public const string PlaybackKey = "playbackDb";

    /// <summary>
    /// Applies <paramref name="wanted"/> to <paramref name="mixer"/> and reads the result back.
    /// </summary>
    /// <param name="mixer">The card's mixer.</param>
    /// <param name="wanted">What to set; every null is a control left alone.</param>
    /// <param name="journal">Where each line goes as it is produced (the daemon's stdout).</param>
    /// <returns>What was found and what it reads back as.</returns>
    public static MixerReport Apply(IAlsaMixer mixer, MixerSettings wanted, Action<string>? journal = null)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(wanted);

        var lines = new List<string>();
        void Say(string line)
        {
            string full = JournalPrefix + line;
            lines.Add(full);
            journal?.Invoke(full);
        }

        Say($"{mixer.Card} has {string.Join(", ", mixer.Controls)}");

        // First, and unconditionally on the start-up path: neither of these is a setting any
        // more. One line for the pair of them, because "the two things that are always off are
        // off" is one fact, and two lines about it on every start-up would be noise.
        if (wanted.ForceAgcAndBoostOff)
        {
            Say(ForcedOffLine(mixer, wanted));
        }

        (MixerVolumeState? capture, double? captureSet) = Volume(
            mixer, wanted.CaptureControls, MixerDirection.Capture, CaptureKey,
            wanted.CaptureGainDb, Say);
        MixerSwitchState? agc = Switch(mixer, wanted.AgcControls);
        MixerSwitchState? boost = Switch(mixer, wanted.MicBoostControls);
        (MixerVolumeState? playback, double? playbackSet) = Volume(
            mixer, wanted.PlaybackControls, MixerDirection.Playback, PlaybackKey,
            wanted.PlaybackDb, Say);

        // A pure read-back - a GET, --mixer-show, a station whose file and state file say
        // nothing - describes the card and stops there, as it always has. Once anything IS being
        // set, every control gets a source or a "left as found", because the question an operator
        // then has is not "what is it" but "what put it there and will it come back".
        bool tag = wanted.SetsAnything;

        var parts = new List<string>();
        if (capture is not null)
        {
            parts.Add(Describe(capture, "capture", captureSet, wanted.Sources.CaptureGain, tag));
        }

        if (agc is not null)
        {
            parts.Add(Describe(agc, wanted.ForceAgcAndBoostOff));
        }

        if (boost is not null)
        {
            parts.Add(Describe(boost, wanted.ForceAgcAndBoostOff));
        }

        if (playback is not null)
        {
            parts.Add(Describe(playback, "playback", playbackSet, wanted.Sources.Playback, tag));
        }

        string? summary = null;
        if (parts.Count > 0)
        {
            summary = JournalPrefix + string.Join(", ", parts);
            lines.Add(summary);
            journal?.Invoke(summary);
        }
        else
        {
            Say($"{mixer.Card} has none of the controls this station looks for, so nothing "
                + "was set; the capture gain and the transmit level stay as the card has them");
        }

        return new MixerReport
        {
            Card = mixer.Card,
            Controls = mixer.Controls,
            Capture = capture,
            Playback = playback,
            Agc = agc,
            MicBoost = boost,
            Sources = wanted.Sources,
            Journal = lines,
            Summary = summary,
        };
    }

    /// <summary>
    /// <see cref="Apply"/>, with anything it throws turned into one journal line and a null.
    /// </summary>
    /// <remarks>
    /// <para>What this is for is a <c>libasound</c> that has some of the mixer API and not the
    /// rest. <see cref="AlsaMixer.TryOpen"/> catches a missing symbol among the ten entry points
    /// it uses itself, but <see cref="Apply"/> then reaches twenty more - the selem id, the
    /// find, the has/get/set families, the dB getters - and an <c>EntryPointNotFoundException</c>
    /// from any of those would leave the daemon's top-level statements with nothing above them to
    /// catch it. That is a crash at every start-up and a systemd restart loop, over a mixer.</para>
    /// <para>Broad on purpose. A mixer is a convenience; a station receiving is not. Whatever
    /// went wrong is named in the journal with its type, so a genuine bug in here is still
    /// visible rather than silently swallowed.</para>
    /// </remarks>
    /// <param name="mixer">The card's mixer.</param>
    /// <param name="wanted">What to set; every null is a control left alone.</param>
    /// <param name="journal">Where each line goes as it is produced.</param>
    /// <param name="why">What went wrong, when this returns null.</param>
    /// <returns>The report, or null if the attempt threw.</returns>
    public static MixerReport? TryApply(
        IAlsaMixer mixer, MixerSettings wanted, Action<string>? journal, out string why)
    {
        why = "";
        try
        {
            return Apply(mixer, wanted, journal);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            journal?.Invoke(
                $"{JournalPrefix}could not be read or set ({why}); the capture gain and the "
                + "transmit level are left as the card has them, and AGC and mic boost could not "
                + "be switched off");
            return null;
        }
    }

    /// <summary>
    /// The first name in <paramref name="names"/> that this card has, or null. Case-insensitive:
    /// ALSA spells its own names consistently, but a configuration file is typed by a person.
    /// </summary>
    public static string? Find(IAlsaMixer mixer, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(names);

        foreach (string name in names)
        {
            foreach (string control in mixer.Controls)
            {
                if (string.Equals(control, name, StringComparison.OrdinalIgnoreCase))
                {
                    return control;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a level control, sets it if a dB was asked for and the card can take one, and reads
    /// it back with the card's range.
    /// </summary>
    /// <returns>What the card reads back as, and the dB that was actually applied to it - which
    /// is null when nothing was asked for and also when the ask was refused, so that the summary
    /// line never claims to have set a level it did not.</returns>
    private static (MixerVolumeState? State, double? Applied) Volume(
        IAlsaMixer mixer, IReadOnlyList<string> names, MixerDirection direction,
        string key, double? decibelsWanted, Action<string> say)
    {
        string side = Side(direction);
        if (Find(mixer, names) is not string control)
        {
            // Only when the operator asked for something. A card that simply has no such control
            // and a configuration that never mentioned it is not news, and a line about it on
            // every start-up would be noise on every station of that card revision.
            if (decibelsWanted is not null)
            {
                say(NotFound(names, mixer.Card));
            }

            return (null, null);
        }

        // Said only when the file asked for this control, the same rule as the not-found line
        // above and for the same reason: a card whose "Capture" is a switch and nothing else
        // would otherwise put a skipped line in the journal of every station of that model, on
        // every start-up, about a setting nobody has ever mentioned.
        void Skipped()
        {
            if (decibelsWanted is not null)
            {
                say($"\"{control}\" on {mixer.Card} has no {side} volume, skipped");
            }
        }

        MixerDbRange? scale = mixer.ReadDbRange(control, direction);
        double? applied = null;

        if (decibelsWanted is double target)
        {
            // Three ways this does not happen, and every one of them journals a sentence and
            // carries on to the read-back rather than dropping the control from the report. The
            // start-up path must never stop over a mixer, and the operator has to be able to see
            // what the level actually is even when what they asked for was not possible.
            if (scale is null)
            {
                // ReadDbRange answers null for three different reasons and only one of them is
                // "this card publishes no dB scale". A control that is present but has no volume
                // on this side at all is a different thing, and used to be told both sentences at
                // once: "this card publishes only raw steps" followed by "has no capture volume,
                // skipped", which contradict each other.
                if (!mixer.TryReadVolume(control, direction, out _, out _))
                {
                    Skipped();
                    return (null, null);
                }

                say(NoDbScale(control, mixer.Card, key));
            }
            else if (OutsideRange(target, scale.MinDb, scale.MaxDb))
            {
                say(OutOfRange(key, target, control, mixer.Card, scale.MinDb, scale.MaxDb));
            }
            else if (!mixer.TrySetDb(control, direction, target))
            {
                Skipped();
                return (null, null);
            }
            else
            {
                applied = target;
            }
        }

        mixer.Refresh();
        if (!mixer.TryReadVolume(control, direction, out int read, out double? decibels))
        {
            Skipped();
            return (null, null);
        }

        return (
            new MixerVolumeState(
                control,
                scale is null ? null : decibels,
                scale?.MinDb,
                scale?.MaxDb,
                read)
            {
                MutesBelowMin = scale?.MutesBelowMin ?? false,
            },
            applied);
    }

    /// <summary>
    /// Why a set of settings cannot be put on this card, or null when it can - for a caller that
    /// has to answer before it acts.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Apply"/> journals these and carries on, which is right for start-up: a
    /// config file with one impossible level must not cost the station the other three controls,
    /// or the station. An API request is the other case - there is somebody waiting for an answer
    /// and nothing has been touched yet - so <c>/api/mixer</c> asks this first and refuses with
    /// the sentence, rather than applying half of a request and reporting success.</para>
    /// <para>Only the two dB refusals. A control the card has not got at all is left to
    /// <see cref="Apply"/>'s "not found, skipped", which is what it has always done and what the
    /// operator page relies on to mark a button missing.</para>
    /// </remarks>
    /// <param name="mixer">The card's mixer.</param>
    /// <param name="wanted">What is about to be asked of it.</param>
    public static string? WhyRefused(IAlsaMixer mixer, MixerSettings wanted)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(wanted);

        return Refusal(wanted.CaptureControls, MixerDirection.Capture, CaptureKey, wanted.CaptureGainDb)
            ?? Refusal(wanted.PlaybackControls, MixerDirection.Playback, PlaybackKey, wanted.PlaybackDb);

        string? Refusal(
            IReadOnlyList<string> names, MixerDirection direction, string key, double? wantedDb)
        {
            if (wantedDb is not double target || Find(mixer, names) is not string control)
            {
                return null;
            }

            if (mixer.ReadDbRange(control, direction) is not MixerDbRange scale)
            {
                // The same distinction the journal makes: no volume on this side is not the same
                // refusal as no dB scale, and a 400 should not say the wrong one.
                return mixer.TryReadVolume(control, direction, out _, out _)
                    ? NoDbScale(control, mixer.Card, key)
                    : NoVolume(control, mixer.Card, Side(direction), key);
            }

            return OutsideRange(target, scale.MinDb, scale.MaxDb)
                ? OutOfRange(key, target, control, mixer.Card, scale.MinDb, scale.MaxDb)
                : null;
        }
    }

    /// <summary>
    /// Whether a level is off the end of a card's range, with a hundredth of a dB of slack.
    /// </summary>
    /// <remarks>
    /// alsa-lib works in hundredths of a dB, so the range's own ends are exact to that and a
    /// value that reads as exactly the maximum must not be refused for a floating-point hair.
    /// </remarks>
    private static bool OutsideRange(double decibels, double minDb, double maxDb) =>
        decibels < minDb - 0.005 || decibels > maxDb + 0.005;

    /// <summary>"capture" or "playback", as every line here names the two sides.</summary>
    private static string Side(MixerDirection direction) =>
        direction == MixerDirection.Capture ? "capture" : "playback";

    /// <summary>The sentence for a control that has no volume on the side being asked about.</summary>
    private static string NoVolume(string control, string card, string side, string key) =>
        $"\"{control}\" on {card} has no {side} volume, so {key} cannot be set. The control is "
        + "there, but not on this side of the card.";

    /// <summary>The sentence for a control the card publishes no dB scale for.</summary>
    private static string NoDbScale(string control, string card, string key) =>
        $"\"{control}\" on {card} has no dB scale, so {key} cannot be set - this card publishes "
        + "only raw steps. The control is left exactly as the card has it.";

    /// <summary>The sentence for a level off the end of the card's range, with the range in it.</summary>
    private static string OutOfRange(
        string key, double decibels, string control, string card, double minDb, double maxDb) =>
        $"{key} {Db(decibels)} dB is outside the range of \"{control}\" on {card}, which is "
        + $"{Db(minDb)} to {Db(maxDb)} dB. The control is left exactly as the card has it.";

    /// <summary>A dB figure as every line here prints one: two places, invariant, ASCII.</summary>
    public static string Db(double decibels) =>
        decibels.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a switch back from the card. Read-only: nothing here writes.
    /// </summary>
    /// <remarks>
    /// The two switches this station knows about are no longer settings, so the only thing left
    /// to do with one is report it. Turning them off is <see cref="ForcedOffLine"/>'s job and
    /// happens once, at start-up, before this.
    /// </remarks>
    private static MixerSwitchState? Switch(IAlsaMixer mixer, IReadOnlyList<string> names)
    {
        if (Find(mixer, names) is not string control)
        {
            return null;
        }

        // Takes in anything changed outside this process since the last read. Volume() refreshes
        // too, but only when it found a level control to read - and a card that has an AGC and
        // none of the level controls this station knows would otherwise be reported from cache.
        mixer.Refresh();
        if (mixer.TryReadSwitch(control, out bool read))
        {
            return new MixerSwitchState(control, read);
        }

        // Some cards present a boost as a level rather than a switch (an HDA "Mic Boost" is four
        // steps of 10 dB). The bottom of that range is what "off" means on such a card.
        if (mixer.TryReadVolume(control, MixerDirection.Capture, out int percent, out _))
        {
            return new MixerSwitchState(control, percent >= 50);
        }

        return null;
    }

    /// <summary>
    /// Switches the AGC and the mic boost off on any card that has them, and says so in one line.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a setting</b> (Tom, 2026-09-06: "AGC should just be forced off, as should mic
    /// boost. No need for buttons for these."). Automatic gain fights the modem's own level
    /// tracking and turns the noise floor into a moving target, and a mic boost left on puts the
    /// receive path 20 dB into clipping and makes every strong signal decode worse than a weak
    /// one (<c>docs/hardware/tm8100-cm108-interface-notes.md</c>). Neither has a case, so neither
    /// has a key, a button or a state file entry.</para>
    /// <para>One line for both, naming the control where there is one and saying so where there
    /// is not: "no mic boost control" is the ordinary answer on a CM108 and an operator reading
    /// the journal should be able to see it was looked for.</para>
    /// </remarks>
    /// <param name="mixer">The card's mixer.</param>
    /// <param name="wanted">The settings, for the control-name lists.</param>
    /// <returns>The line, without the prefix.</returns>
    private static string ForcedOffLine(IAlsaMixer mixer, MixerSettings wanted) =>
        "AGC and mic boost are always off on this station: "
        + $"{Off(mixer, wanted.AgcControls, "AGC")}; "
        + $"{Off(mixer, wanted.MicBoostControls, "mic boost")}";

    /// <summary>Switches one control off, however this card presents it, and says what happened.</summary>
    private static string Off(IAlsaMixer mixer, IReadOnlyList<string> names, string what)
    {
        if (Find(mixer, names) is not string control)
        {
            return $"no {what} control on {mixer.Card}";
        }

        if (!mixer.TrySetSwitch(control, false)
            && !mixer.TrySetVolume(control, MixerDirection.Capture, 0))
        {
            return $"\"{control}\" has neither a switch nor a level to turn off";
        }

        mixer.Refresh();
        if (Switch(mixer, [control]) is not MixerSwitchState read)
        {
            return $"\"{control}\" set off (the card will not say what it is now)";
        }

        // The read-back, not the write. A card that accepts a write and does not act on it is a
        // real card, and an operator hunting a moving noise floor needs to see that here.
        return read.On
            ? $"\"{control}\" is STILL ON - the card would not switch it off"
            : $"\"{control}\" off";
    }

    /// <summary>
    /// The line for a control this station looked for and the card does not have. The first name
    /// is the one an operator would recognise; the rest are said too, so nobody has to guess what
    /// was tried before concluding their card has no such thing.
    /// </summary>
    private static string NotFound(IReadOnlyList<string> names, string card)
    {
        string first = names.Count > 0 ? names[0] : "(none)";
        string also = names.Count > 1
            ? " (also tried " + string.Join(", ", names.Skip(1).Select(n => $"\"{n}\"")) + ")"
            : "";
        return $"no control named \"{first}\" on {card}{also}, skipped";
    }

    private static string Describe(
        MixerVolumeState state, string side, double? applied, MixerSource source, bool tag)
    {
        // A card with no dB scale has only a percentage to report, and saying which it is
        // matters: "57%" and "57 dB" would be a catastrophic thing to confuse on a transmitter.
        // "its lowest step is mute" rather than a quieter number: an operator sliding a
        // transmit level to the bottom is entitled to know the last step is silence.
        string mute = state.MutesBelowMin ? ", below which it mutes" : "";
        string level = state is { Decibels: double value, MinDb: double min, MaxDb: double max }
            ? $"{Db(value)} dB of {Db(min)} to {Db(max)} dB{mute}"
            : $"{state.Percent}% (no dB scale)";
        string set = applied is double asked
            ? $" (set {Db(asked)} dB{From(source)})"
            : tag ? " (left as found)" : "";
        return $"{state.Control} {side} {level}{set}";
    }

    /// <summary>One switch in the summary line: what it reads back as, and why it is that way.</summary>
    /// <param name="state">What the card says.</param>
    /// <param name="forced">Whether this pass switched it off.</param>
    private static string Describe(MixerSwitchState state, bool forced)
    {
        // A switch cannot be quantised, so the read-back alone says everything - unless this pass
        // asked for off and the card is still on, which is the one case worth spelling out.
        string how = !forced ? ""
            : state.On ? " (asked off, the card did not take it)"
            : " (forced)";
        return $"{state.Control} {(state.On ? "on" : "off")}{how}";
    }

    /// <summary>", config" or ", state file" for a value that has a recorded source.</summary>
    /// <remarks>
    /// Empty for a change coming in over the API, which has no source to name beyond the person
    /// who just made it: "(set 6.00 dB)" in answer to a request that said 6.00 dB is enough.
    /// </remarks>
    private static string From(MixerSource source) => source switch
    {
        MixerSource.Config => ", config",
        MixerSource.StateFile => ", state file",
        _ => "",
    };
}
