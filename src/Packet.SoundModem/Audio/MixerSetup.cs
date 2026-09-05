using System.Globalization;

namespace Packet.SoundModem.Audio;

/// <summary>One volume control as the card reports it back.</summary>
/// <param name="Control">The control's name on this card.</param>
/// <param name="Percent">Its level, 0-100 of the card's range.</param>
/// <param name="Decibels">The same level in dB, when the card publishes a scale; else null.</param>
public sealed record MixerVolumeState(string Control, int Percent, double? Decibels);

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
/// capture range of 0-35 steps cannot hold 75%, and the line says so by printing both.</para>
/// </remarks>
public static class MixerSetup
{
    /// <summary>What every line here is prefixed with, so the journal groups at a glance.</summary>
    public const string JournalPrefix = "alsa: mixer: ";

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

        MixerVolumeState? capture = Volume(
            mixer, wanted.CaptureControls, MixerDirection.Capture, wanted.CaptureGainPercent, Say);
        MixerSwitchState? agc = Switch(mixer, wanted.AgcControls, wanted.Agc, Say);
        MixerSwitchState? boost = Switch(mixer, wanted.MicBoostControls, wanted.MicBoost, Say);
        MixerVolumeState? playback = Volume(
            mixer, wanted.PlaybackControls, MixerDirection.Playback, wanted.PlaybackPercent, Say);

        var parts = new List<string>();
        if (capture is not null)
        {
            parts.Add(Describe(capture, "capture", wanted.CaptureGainPercent));
        }

        if (agc is not null)
        {
            parts.Add(Describe(agc, wanted.Agc));
        }

        if (boost is not null)
        {
            parts.Add(Describe(boost, wanted.MicBoost));
        }

        if (playback is not null)
        {
            parts.Add(Describe(playback, "playback", wanted.PlaybackPercent));
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
                + "was set; capture gain, AGC and mic boost stay as the card has them");
        }

        return new MixerReport
        {
            Card = mixer.Card,
            Controls = mixer.Controls,
            Capture = capture,
            Playback = playback,
            Agc = agc,
            MicBoost = boost,
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
                $"{JournalPrefix}could not be read or set ({why}); capture gain, AGC and mic "
                + "boost are left as the card has them");
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

    private static MixerVolumeState? Volume(
        IAlsaMixer mixer, IReadOnlyList<string> names, MixerDirection direction,
        int? percent, Action<string> say)
    {
        string side = direction == MixerDirection.Capture ? "capture" : "playback";
        if (Find(mixer, names) is not string control)
        {
            // Only when the operator asked for something. A card that simply has no such control
            // and a configuration that never mentioned it is not news, and a line about it on
            // every start-up would be noise on every station of that card revision.
            if (percent is not null)
            {
                say(NotFound(names, mixer.Card));
            }

            return null;
        }

        // Said only when the file asked for this control, the same rule as the not-found line
        // above and for the same reason: a card whose "Capture" is a switch and nothing else
        // would otherwise put a skipped line in the journal of every station of that model, on
        // every start-up, about a setting nobody has ever mentioned.
        void Skipped()
        {
            if (percent is not null)
            {
                say($"\"{control}\" on {mixer.Card} has no {side} volume, skipped");
            }
        }

        if (percent is int target && !mixer.TrySetVolume(control, direction, target))
        {
            Skipped();
            return null;
        }

        mixer.Refresh();
        if (!mixer.TryReadVolume(control, direction, out int read, out double? decibels))
        {
            Skipped();
            return null;
        }

        return new MixerVolumeState(control, read, decibels);
    }

    private static MixerSwitchState? Switch(
        IAlsaMixer mixer, IReadOnlyList<string> names, bool? on, Action<string> say)
    {
        if (Find(mixer, names) is not string control)
        {
            if (on is not null)
            {
                say(NotFound(names, mixer.Card));
            }

            return null;
        }

        // As in Volume: only a control the file asked for is worth a line about.
        void Skipped()
        {
            if (on is not null)
            {
                say($"\"{control}\" on {mixer.Card} has no on/off switch, skipped");
            }
        }

        if (on is bool state && !mixer.TrySetSwitch(control, state))
        {
            // Some cards present a boost as a level rather than a switch (an HDA "Mic Boost" is
            // four steps of 10 dB). Off is the bottom of that range and on is the top, which is
            // what a switch would have meant.
            if (mixer.TrySetVolume(control, MixerDirection.Capture, state ? 100 : 0))
            {
                say($"\"{control}\" on {mixer.Card} is a level rather than a switch, "
                    + $"set to {(state ? "100%" : "0%")}");
            }
            else
            {
                Skipped();
                return null;
            }
        }

        mixer.Refresh();
        if (mixer.TryReadSwitch(control, out bool read))
        {
            return new MixerSwitchState(control, read);
        }

        if (mixer.TryReadVolume(control, MixerDirection.Capture, out int percent, out _))
        {
            return new MixerSwitchState(control, percent >= 50);
        }

        Skipped();
        return null;
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

    private static string Describe(MixerVolumeState state, string side, int? asked)
    {
        string db = state.Decibels is double value
            ? " / " + value.ToString("0.00", CultureInfo.InvariantCulture) + " dB"
            : "";
        string set = asked is int percent ? $" (set {percent}%)" : "";
        return $"{state.Control} {side} {state.Percent}%{db}{set}";
    }

    private static string Describe(MixerSwitchState state, bool? asked)
    {
        // A switch cannot be quantised, so the read-back alone says everything - unless the card
        // did not take it, which is the one case worth spelling out.
        string disagreed = asked is bool wanted && wanted != state.On
            ? $" (set {(wanted ? "on" : "off")}, the card did not take it)"
            : "";
        return $"{state.Control} {(state.On ? "on" : "off")}{disagreed}";
    }
}
