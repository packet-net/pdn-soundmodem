using System.Globalization;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Linux;

/// <summary>The controls on a card that a station's rules are about.</summary>
/// <param name="Capture">The receive level: what the modem hears.</param>
/// <param name="Playback">The transmit level: what the radio is driven with.</param>
/// <param name="Monitors">Controls whose playback side monitors an input to the card's output
/// (a CM108's "Mic"), which on a radio interface is received audio going back to the transmitter.</param>
/// <param name="Agc">Automatic gain controls.</param>
/// <param name="Boost">Microphone boosts.</param>
public sealed record MixerRoles(
    string? Capture,
    string? Playback,
    IReadOnlyList<string> Monitors,
    IReadOnlyList<string> Agc,
    IReadOnlyList<string> Boost);

/// <summary>Something about a card's mixer that is wrong for a radio interface.</summary>
/// <param name="Description">What, in an operator's words.</param>
/// <param name="CanFix">Whether <see cref="MixerHygiene.Fix"/> can put it right.</param>
public sealed record MixerIssue(string Description, bool CanFix);

/// <summary>
/// The mixer rules for a radio interface on Linux, as the Windows library's endpoint hygiene
/// has them for Windows: nothing above 0 dB, AGC and mic boost off, neither side muted, and no
/// input monitored to the output.
/// </summary>
/// <remarks>
/// <para>Controls are found by what they do as well as by name. The daemon's name lists
/// (<see cref="MixerSettings"/>) cover the CM108 family ("Mic", "Speaker") and most onboard
/// cards, but an AIOC calls its controls "AIOC Audio In" and "AIOC Audio Out Volume", and the
/// next interface will call them something else again. So a named control is preferred and,
/// failing one, the card's only (or first) control with a volume on that side is used.</para>
/// <para>Everything goes through <see cref="IAlsaMixer"/>, so every rule here is tested against a
/// fake card; only <see cref="AlsaMixer"/> talks to libasound.</para>
/// </remarks>
public static class MixerHygiene
{
    /// <summary>Works out which control plays which part on this card.</summary>
    public static MixerRoles Identify(IAlsaMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        List<string> agc = Named(mixer, MixerSettings.DefaultAgcControls);
        List<string> boost = Named(mixer, MixerSettings.DefaultMicBoostControls);
        bool Special(string control) => agc.Contains(control) || boost.Contains(control);

        string? capture = Named(mixer, MixerSettings.DefaultCaptureControls).FirstOrDefault(c => mixer.HasVolume(c, MixerDirection.Capture))
            ?? mixer.Controls.FirstOrDefault(c => !Special(c) && mixer.HasVolume(c, MixerDirection.Capture));

        // A control with both a playback and a capture side is an input with a monitor path, not
        // the transmit level, whatever it is called.
        bool HasCaptureSide(string c) => mixer.HasVolume(c, MixerDirection.Capture) || mixer.TryReadSwitch(c, MixerDirection.Capture, out _);
        bool HasPlaybackSide(string c) => mixer.HasVolume(c, MixerDirection.Playback) || mixer.TryReadSwitch(c, MixerDirection.Playback, out _);
        string? playback = Named(mixer, MixerSettings.DefaultPlaybackControls).FirstOrDefault(c => mixer.HasVolume(c, MixerDirection.Playback) && !HasCaptureSide(c))
            ?? mixer.Controls.FirstOrDefault(c => !Special(c) && mixer.HasVolume(c, MixerDirection.Playback) && !HasCaptureSide(c));

        List<string> monitors = mixer.Controls
            .Where(c => c != playback && !Special(c) && HasPlaybackSide(c) && HasCaptureSide(c))
            .ToList();
        return new MixerRoles(capture, playback, monitors, agc, boost);
    }

    /// <summary>What is wrong with the card's mixer for a radio interface.</summary>
    public static IReadOnlyList<MixerIssue> Check(IAlsaMixer mixer) => Check(mixer, Identify(mixer));

    /// <summary>
    /// Puts right what can be put right and returns what is still wrong afterwards (read back
    /// from the card, not assumed). <paramref name="fixedOne"/> hears about each fix as it lands.
    /// </summary>
    public static IReadOnlyList<MixerIssue> Fix(IAlsaMixer mixer, Action<string>? fixedOne = null)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        MixerRoles roles = Identify(mixer);
        foreach (MixerIssue issue in Check(mixer, roles).Where(i => i.CanFix))
        {
            fixedOne?.Invoke(issue.Description);
        }

        foreach (string control in roles.Agc)
        {
            _ = mixer.TrySetSwitch(control, false);
        }

        foreach (string control in roles.Boost)
        {
            if (!mixer.TrySetSwitch(control, false))
            {
                _ = mixer.TrySetVolume(control, MixerDirection.Capture, 0);
            }
        }

        foreach (string control in roles.Monitors)
        {
            if (!mixer.TrySetSwitch(control, MixerDirection.Playback, false))
            {
                _ = mixer.TrySetVolume(control, MixerDirection.Playback, 0);
            }
        }

        foreach ((string? control, MixerDirection direction) in new[] { (roles.Capture, MixerDirection.Capture), (roles.Playback, MixerDirection.Playback) })
        {
            if (control is null)
            {
                continue;
            }

            _ = mixer.TrySetSwitch(control, direction, true);
            if (mixer.ReadDbRange(control, direction) is { } range
                && mixer.TryReadVolume(control, direction, out _, out double? level) && level > Ceiling(range))
            {
                _ = mixer.TrySetDb(control, direction, Ceiling(range));
            }
        }

        mixer.Refresh();
        return Check(mixer, roles);
    }

    /// <summary>The highest level the rules allow on a control: 0 dB, or its bottom if even that
    /// is above 0 dB.</summary>
    public static double Ceiling(MixerDbRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return Math.Max(range.MinDb, Math.Min(0, range.MaxDb));
    }

    private static List<MixerIssue> Check(IAlsaMixer mixer, MixerRoles roles)
    {
        var issues = new List<MixerIssue>();
        foreach (string control in roles.Agc)
        {
            if (mixer.TryReadSwitch(control, out bool on) && on)
            {
                issues.Add(new MixerIssue($"automatic gain control is on ({control})", CanFix: true));
            }
        }

        foreach (string control in roles.Boost)
        {
            if ((mixer.TryReadSwitch(control, out bool on) && on)
                || (mixer.TryReadVolume(control, MixerDirection.Capture, out int percent, out _) && percent > 0))
            {
                issues.Add(new MixerIssue($"mic boost is on ({control})", CanFix: true));
            }
        }

        foreach (string control in roles.Monitors)
        {
            bool open = mixer.TryReadSwitch(control, MixerDirection.Playback, out bool on)
                ? on
                : mixer.TryReadVolume(control, MixerDirection.Playback, out int percent, out _) && percent > 0;
            if (open)
            {
                issues.Add(new MixerIssue(
                    $"{control} is monitored to the output, which sends received audio back to the transmitter", CanFix: true));
            }
        }

        Level(mixer, roles.Capture, MixerDirection.Capture, "receive", issues);
        Level(mixer, roles.Playback, MixerDirection.Playback, "transmit", issues);
        return issues;
    }

    private static void Level(IAlsaMixer mixer, string? control, MixerDirection direction, string side, List<MixerIssue> issues)
    {
        if (control is null)
        {
            issues.Add(new MixerIssue($"the card has no {side} level control, so the {side} level is whatever the card starts at", CanFix: false));
            return;
        }

        if (mixer.TryReadSwitch(control, direction, out bool on) && !on)
        {
            issues.Add(new MixerIssue($"{side} audio is muted on the card ({control})", CanFix: true));
        }

        if (mixer.ReadDbRange(control, direction) is not { } range)
        {
            issues.Add(new MixerIssue(
                $"{control} publishes no dB scale, so the {side} level cannot be held at 0 dB; set it in alsamixer", CanFix: false));
            return;
        }

        if (range.MinDb > 0.05)
        {
            issues.Add(new MixerIssue($"{side} level cannot go below {Db(range.MinDb)} on this card ({control})", CanFix: false));
        }

        if (mixer.TryReadVolume(control, direction, out _, out double? level) && level is double db && db > Ceiling(range) + 0.05)
        {
            issues.Add(new MixerIssue(
                $"{side} level is {Db(db)}, above {(Ceiling(range) <= 0 ? "0 dB" : "the lowest the card has")} ({control})", CanFix: true));
        }
    }

    private static List<string> Named(IAlsaMixer mixer, IReadOnlyList<string> names) =>
        names.Select(n => mixer.Controls.FirstOrDefault(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct()
            .ToList();

    private static string Db(double db) => string.Create(CultureInfo.InvariantCulture, $"{db:+0.0;-0.0;0.0} dB");
}
