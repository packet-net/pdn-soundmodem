using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// A card whose entry points are not all there, which is what a <c>libasound</c> too old or too
/// stripped for part of the mixer API looks like from above.
/// </summary>
/// <remarks>
/// <see cref="AlsaMixer.TryOpen"/> catches a missing symbol among the ten entry points it uses
/// itself, but the twenty the apply then reaches are outside it, and the daemon's top-level
/// statements have nothing above them to catch anything. So this throws from a setter, exactly
/// where a real one would.
/// </remarks>
internal sealed class ThrowingMixer : IAlsaMixer
{
    public string Card => "hw:9";

    public IReadOnlyList<string> Controls => ["Mic", "Auto Gain Control"];

    public bool Disposed { get; private set; }

    public void Refresh()
    {
    }

    public bool TrySetVolume(string control, MixerDirection direction, int percent) =>
        throw new EntryPointNotFoundException(
            "Unable to find an entry point named 'snd_mixer_selem_set_capture_volume_all'");

    public bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels) =>
        throw new EntryPointNotFoundException(
            "Unable to find an entry point named 'snd_mixer_selem_get_capture_volume'");

    public bool TrySetSwitch(string control, bool on) => throw new EntryPointNotFoundException(
        "Unable to find an entry point named 'snd_mixer_selem_set_capture_switch_all'");

    public bool TryReadSwitch(string control, out bool on) => throw new EntryPointNotFoundException(
        "Unable to find an entry point named 'snd_mixer_selem_get_capture_switch'");

    public void Dispose() => Disposed = true;
}

/// <summary>One control on a made-up sound card.</summary>
/// <remarks>
/// A null level or switch means the control does not have that capability at all, which is the
/// case that matters: card revisions differ in exactly this way, and a control that is present
/// but has no capture volume has to be skipped rather than silently missed.
/// </remarks>
internal sealed class FakeControl
{
    /// <summary>The control's name, as the card spells it.</summary>
    public required string Name { get; init; }

    /// <summary>Its capture level, 0-100, or null when it has no capture volume.</summary>
    public int? Capture { get; set; }

    /// <summary>Its playback level, 0-100, or null when it has no playback volume.</summary>
    public int? Playback { get; set; }

    /// <summary>Its on/off state, or null when it has no switch.</summary>
    public bool? On { get; set; }

    /// <summary>The dB this card would report for a percentage, or null when it publishes none.</summary>
    public Func<int, double>? Decibels { get; init; }

    /// <summary>A control that answers a write with a failure (a card that will not be told).</summary>
    public bool RefusesWrites { get; init; }

    /// <summary>A control that accepts a write and does not act on it. Cards do this.</summary>
    public bool IgnoresWrites { get; init; }
}

/// <summary>
/// A sound card with whatever controls a test needs, in place of a real one.
/// </summary>
/// <remarks>
/// The point of <see cref="IAlsaMixer"/>. There is no sound hardware on a CI runner and a mixer
/// is precisely what cannot be exercised without one, so everything above the P/Invoke - the
/// name fallbacks, the journal wording, the read-back, the skipping - is proved here against a
/// card built to order, including revisions nobody has on the bench.
/// </remarks>
internal sealed class FakeMixer : IAlsaMixer
{
    private readonly List<FakeControl> _controls;

    public FakeMixer(string card, params FakeControl[] controls)
    {
        Card = card;
        _controls = [.. controls];
    }

    /// <summary>The CM108 revision on Tom's bench: no "Mic Boost" control at all.</summary>
    /// <remarks>
    /// Its +20 dB is folded into the top of the capture range (raw 0-35 is -12 to +23 dB), so
    /// there is nothing separate to switch, and a station that asks for micBoost on this card
    /// takes the "not found, skipped" path. Values as surveyed 2026-09-05.
    /// </remarks>
    public static FakeMixer Cm108(string card = "hw:3") => new(
        card,
        new FakeControl
        {
            Name = "Mic",
            Capture = 57,
            Playback = 52,
            On = true,
            Decibels = percent => -12 + (percent / 100.0 * 35),
        },
        new FakeControl { Name = "Auto Gain Control", On = true },
        new FakeControl
        {
            Name = "Speaker",
            Playback = 46,
            On = true,
            Decibels = percent => -37 + (percent / 100.0 * 37),
        });

    /// <inheritdoc />
    public string Card { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Controls => [.. _controls.Select(c => c.Name)];

    /// <summary>How many times the card was asked for fresh values.</summary>
    public int Refreshes { get; private set; }

    /// <summary>
    /// How long the card takes to answer a refresh. Non-zero opens the window between a write and
    /// the read-back that follows it, which is the window two concurrent callers would interleave
    /// in if nothing serialised them.
    /// </summary>
    public TimeSpan RefreshTakes { get; set; }

    /// <summary>Whether this fake has been disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Refresh()
    {
        Refreshes++;
        if (RefreshTakes > TimeSpan.Zero)
        {
            Thread.Sleep(RefreshTakes);
        }
    }

    /// <inheritdoc />
    public bool TrySetVolume(string control, MixerDirection direction, int percent)
    {
        if (Find(control) is not FakeControl found || Level(found, direction) is null)
        {
            return false;
        }

        if (found.RefusesWrites)
        {
            return false;
        }

        if (found.IgnoresWrites)
        {
            return true;
        }

        if (direction == MixerDirection.Capture)
        {
            found.Capture = Math.Clamp(percent, 0, 100);
        }
        else
        {
            found.Playback = Math.Clamp(percent, 0, 100);
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels)
    {
        percent = 0;
        decibels = null;
        if (Find(control) is not FakeControl found || Level(found, direction) is not int level)
        {
            return false;
        }

        percent = level;
        decibels = found.Decibels?.Invoke(level);
        return true;
    }

    /// <inheritdoc />
    public bool TrySetSwitch(string control, bool on)
    {
        if (Find(control) is not FakeControl found || found.On is null || found.RefusesWrites)
        {
            return false;
        }

        if (!found.IgnoresWrites)
        {
            found.On = on;
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryReadSwitch(string control, out bool on)
    {
        on = false;
        if (Find(control) is not FakeControl found || found.On is not bool state)
        {
            return false;
        }

        on = state;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;

    /// <summary>The control by name, for a test to check what the card ended up at.</summary>
    public FakeControl? Find(string name) =>
        _controls.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int? Level(FakeControl control, MixerDirection direction) =>
        direction == MixerDirection.Capture ? control.Capture : control.Playback;
}
