namespace Packet.SoundModem.Audio;

/// <summary>
/// What a station wants its sound card's mixer set to, and which control names to look for.
/// </summary>
/// <remarks>
/// <para>Every value is nullable and null means <b>leave that control exactly as the card has
/// it</b>. That is the whole safety property of this feature: a station that says nothing about
/// its mixer gets the behaviour it had before this existed, and one that names two of the four
/// controls has the other two left alone rather than reset to a default somebody invented.</para>
/// <para>The gains are percentages of the card's own range, not dB. See
/// <see cref="AlsaMixer"/> for why.</para>
/// </remarks>
public sealed record MixerSettings
{
    /// <summary>
    /// Names to look for the capture gain under, in order. A CM108 calls it "Mic"; other
    /// revisions and other cards say "Mic Capture" or just "Capture".
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCaptureControls =
        ["Mic", "Mic Capture", "Capture"];

    /// <summary>
    /// Names to look for the automatic gain control under, in order. "Auto Gain Control" is what
    /// a CM108 presents; "AGC" and "Mic AGC" turn up on other USB codecs.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAgcControls =
        ["Auto Gain Control", "AGC", "Mic AGC"];

    /// <summary>
    /// Names to look for the microphone boost under, in order.
    /// </summary>
    /// <remarks>
    /// Plenty of cards have no such control at all: the CM108 revision on the bench folds its
    /// boost into the top of the capture range (0-35 raw is -12 to +23 dB), so there is nothing
    /// separate to switch. That is the ordinary outcome here, not a fault.
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultMicBoostControls =
        ["Mic Boost", "Mic Boost (+20dB)", "Internal Mic Boost", "Mic Capture Boost"];

    /// <summary>
    /// Names to look for the transmit-side level under, in order. A CM108's output is "Speaker";
    /// "PCM" and "Master" are the usual alternatives.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPlaybackControls =
        ["Speaker", "PCM", "Master", "Headphone"];

    /// <summary>Capture gain as a percentage of the card's range, or null to leave it.</summary>
    public int? CaptureGainPercent { get; init; }

    /// <summary>Automatic gain control on or off, or null to leave it.</summary>
    public bool? Agc { get; init; }

    /// <summary>Microphone boost on or off, or null to leave it.</summary>
    public bool? MicBoost { get; init; }

    /// <summary>Transmit-side playback level as a percentage, or null to leave it.</summary>
    public int? PlaybackPercent { get; init; }

    /// <summary>Where to look for the capture gain.</summary>
    public IReadOnlyList<string> CaptureControls { get; init; } = DefaultCaptureControls;

    /// <summary>Where to look for the automatic gain control.</summary>
    public IReadOnlyList<string> AgcControls { get; init; } = DefaultAgcControls;

    /// <summary>Where to look for the microphone boost.</summary>
    public IReadOnlyList<string> MicBoostControls { get; init; } = DefaultMicBoostControls;

    /// <summary>Where to look for the transmit-side level.</summary>
    public IReadOnlyList<string> PlaybackControls { get; init; } = DefaultPlaybackControls;

    /// <summary>
    /// The same control-name lists, asking for nothing to be changed - which turns
    /// <see cref="MixerSetup.Apply"/> into a pure read-back of the card.
    /// </summary>
    public MixerSettings LeaveEverything() => this with
    {
        CaptureGainPercent = null,
        Agc = null,
        MicBoost = null,
        PlaybackPercent = null,
    };

    /// <summary>Whether this asks for any change at all.</summary>
    public bool SetsAnything =>
        CaptureGainPercent is not null || Agc is not null
        || MicBoost is not null || PlaybackPercent is not null;
}
