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

    public bool TrySetDb(string control, MixerDirection direction, double decibels) =>
        throw new EntryPointNotFoundException(
            "Unable to find an entry point named 'snd_mixer_selem_set_capture_dB_all'");

    // Not throwing, deliberately: this is the first mixer call the apply makes for a level, and a
    // throw here would prove only that the guard catches the first thing it touches. Answering
    // "this card has a dB scale" and then throwing from the setter is the harder case and the
    // realistic one - a libasound with the getters and not the setters.
    public MixerDbRange? ReadDbRange(string control, MixerDirection direction) =>
        new(-12, 23, false);

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

/// <summary>
/// One side of one control on a made-up sound card: raw steps, and the dB they span if the card
/// publishes a scale at all.
/// </summary>
/// <remarks>
/// <para>Raw steps and not a percentage, because the quantisation is the interesting part and a
/// percentage hides it. The bench CM108's capture is 36 steps spanning -12 to +23 dB, which is
/// one whole dB per step: a request for 6.4 dB comes back as 6.00 dB, and a test that models the
/// card as "0-100" would never see that happen.</para>
/// <para>A null dB span is a card that publishes only raw steps. Real ones exist, and they are
/// the case a dB setting has to refuse rather than guess at.</para>
/// </remarks>
internal sealed class FakeLevel
{
    /// <summary>The lowest raw step.</summary>
    public long Min { get; init; }

    /// <summary>The highest raw step.</summary>
    public required long Max { get; init; }

    /// <summary>Where it is now.</summary>
    public required long Raw { get; set; }

    /// <summary>The dB at <see cref="Min"/>, or null when this card publishes no dB scale.</summary>
    public double? MinDb { get; init; }

    /// <summary>
    /// Whether the lowest raw step is the card's mute rather than a level, which is what a TLV
    /// tagged <c>dBminmaxmute</c> means. The bench CM108's "Speaker" is one of these.
    /// </summary>
    public bool MutesAtMin { get; init; }

    /// <summary>The dB at <see cref="Max"/>.</summary>
    public double? MaxDb { get; init; }

    /// <summary>Whether the card publishes a dB scale for this side of this control.</summary>
    public bool HasDb => MinDb is not null && MaxDb is not null && Max > Min;

    /// <summary>The lowest raw step that is a level rather than silence.</summary>
    public long LowestLevel => MutesAtMin ? Min + 1 : Min;

    /// <summary>The dB the card would report for a raw step, ignoring mute.</summary>
    public double DecibelsAt(long raw) =>
        MinDb!.Value + ((MaxDb!.Value - MinDb.Value) * (raw - Min) / (double)(Max - Min));

    /// <summary>The dB the card would report for where it is now.</summary>
    public double? Decibels => HasDb ? DecibelsAt(Raw) : null;

    /// <summary>Its level as a percentage of the raw range, which is what <c>alsamixer</c> shows.</summary>
    public int Percent => Max > Min
        ? (int)Math.Round((Raw - Min) * 100.0 / (Max - Min))
        : 100;

    /// <summary>The raw step nearest a dB, which is what <c>dir = 0</c> asks a real card for.</summary>
    public long NearestTo(double decibels)
    {
        double span = MaxDb!.Value - MinDb!.Value;
        double where = (decibels - MinDb.Value) / span * (Max - Min);
        return Math.Clamp((long)Math.Round(where), LowestLevel, Max);
    }
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

    /// <summary>Its capture side, or null when it has no capture volume.</summary>
    public FakeLevel? Capture { get; init; }

    /// <summary>Its playback side, or null when it has no playback volume.</summary>
    public FakeLevel? Playback { get; init; }

    /// <summary>Its on/off state, or null when it has no switch.</summary>
    public bool? On { get; set; }

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
/// name fallbacks, the journal wording, the read-back, the skipping, the dB range and what is
/// refused against it - is proved here against a card built to order, including revisions nobody
/// has on the bench.
/// </remarks>
internal sealed class FakeMixer : IAlsaMixer
{
    private readonly List<FakeControl> _controls;

    public FakeMixer(string card, params FakeControl[] controls)
    {
        Card = card;
        _controls = [.. controls];
    }

    /// <summary>The CM108 revision on Tom's bench, as <c>amixer contents</c> surveyed it.</summary>
    /// <remarks>
    /// "Mic Capture Volume" is 0-35 raw = -12 to +23 dB in whole-dB steps, "Speaker Playback
    /// Volume" is 0-37 raw = -37 to 0 dB, and there is no "Mic Boost" control at all: its +20 dB
    /// is folded into the top of the capture range, so there is nothing separate to switch and the
    /// forced-off pass reports "no mic boost control" on this card. Values as
    /// surveyed 2026-09-05; the control names here are the short ones the simple mixer API
    /// presents rather than the long ones <c>amixer contents</c> prints. The Speaker's TLV is
    /// tagged <c>dBminmaxmute</c>, so its bottom raw step is the card's mute and alsa-lib reports
    /// the range minimum as the mute sentinel rather than -37 dB - which is why the usable range
    /// this fake offers on playback is -36 to 0 dB.
    /// </remarks>
    public static FakeMixer Cm108(string card = "hw:3") => new(
        card,
        new FakeControl
        {
            Name = "Mic",
            Capture = new FakeLevel { Min = 0, Max = 35, Raw = 20, MinDb = -12, MaxDb = 23 },
            On = true,
        },
        new FakeControl { Name = "Auto Gain Control", On = true },
        new FakeControl
        {
            Name = "Speaker",
            Playback = new FakeLevel
            {
                Min = 0, Max = 37, Raw = 17, MinDb = -37, MaxDb = 0, MutesAtMin = true,
            },
            On = true,
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
    public bool TrySetDb(string control, MixerDirection direction, double decibels)
    {
        if (Find(control) is not FakeControl found || Level(found, direction) is not FakeLevel level)
        {
            return false;
        }

        // A card with no dB scale cannot be told a dB, which is what the layer above has to say
        // rather than convert. alsa-lib answers the same way.
        if (!level.HasDb || found.RefusesWrites)
        {
            return false;
        }

        if (!found.IgnoresWrites)
        {
            level.Raw = level.NearestTo(decibels);
        }

        return true;
    }

    /// <inheritdoc />
    public MixerDbRange? ReadDbRange(string control, MixerDirection direction)
    {
        if (Find(control) is not FakeControl found
            || Level(found, direction) is not FakeLevel level || !level.HasDb)
        {
            return null;
        }

        // As the real thing does: a control whose bottom step is mute reports its range from the
        // lowest step that is a level, and says the step below it is silence.
        return new MixerDbRange(
            level.DecibelsAt(level.LowestLevel), level.MaxDb!.Value, level.MutesAtMin);
    }

    /// <inheritdoc />
    public bool TrySetVolume(string control, MixerDirection direction, int percent)
    {
        if (Find(control) is not FakeControl found || Level(found, direction) is not FakeLevel level)
        {
            return false;
        }

        if (found.RefusesWrites)
        {
            return false;
        }

        if (!found.IgnoresWrites)
        {
            level.Raw = level.Min
                + (long)Math.Round((level.Max - level.Min) * Math.Clamp(percent, 0, 100) / 100.0);
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels)
    {
        percent = 0;
        decibels = null;
        if (Find(control) is not FakeControl found || Level(found, direction) is not FakeLevel level)
        {
            return false;
        }

        percent = level.Percent;
        decibels = level.Decibels;
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

    /// <summary>The dB a named control's capture side is at, for an assertion to read.</summary>
    public double? CaptureDb(string name) => Find(name)?.Capture?.Decibels;

    /// <summary>The dB a named control's playback side is at.</summary>
    public double? PlaybackDb(string name) => Find(name)?.Playback?.Decibels;

    private static FakeLevel? Level(FakeControl control, MixerDirection direction) =>
        direction == MixerDirection.Capture ? control.Capture : control.Playback;
}
