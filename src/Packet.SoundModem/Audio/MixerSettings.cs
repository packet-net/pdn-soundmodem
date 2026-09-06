namespace Packet.SoundModem.Audio;

/// <summary>Where a mixer setting that is in force came from.</summary>
/// <remarks>
/// Carried through to the journal, the API and the operator page because "the capture gain is
/// 6.00 dB" and "the capture gain is 6.00 dB and the config file says so" are different facts to
/// an operator about to change it: the second one goes back to 6.00 dB at the next start-up.
/// </remarks>
public enum MixerSource
{
    /// <summary>Nothing pinned this control; it is whatever the card was left at.</summary>
    None,

    /// <summary>The station's configuration file, which wins over everything else.</summary>
    Config,

    /// <summary>The state file: a page or API change made in some earlier run.</summary>
    StateFile,
}

/// <summary>Where each of the two settings came from.</summary>
/// <param name="CaptureGain">The capture level's source.</param>
/// <param name="Playback">The transmit-side level's source.</param>
/// <remarks>
/// Two, not four. AGC and mic boost stopped being settings in 0.59.0 - they are switched off at
/// every start-up on any card that has them - so there is no longer a source to name for either:
/// the answer is always "this daemon did it", and a field that has one value is not a fact.
/// </remarks>
public sealed record MixerSources(
    MixerSource CaptureGain = MixerSource.None,
    MixerSource Playback = MixerSource.None);

/// <summary>
/// What a station wants its sound card's mixer set to, and which control names to look for.
/// </summary>
/// <remarks>
/// <para>Every level is nullable and null means <b>leave that control exactly as the card has
/// it</b>. That is the whole safety property of this feature: a station that says nothing about
/// its levels gets the behaviour it had before this existed, and one that names the capture gain
/// has the transmit level left alone rather than reset to a default somebody invented. The one
/// exception is <see cref="ForceAgcAndBoostOff"/>, which is not a level and not a preference.</para>
/// <para>The levels are in dB, which is what a level actually sits on and what the radio at the
/// other end of the lead is specified in. See <see cref="AlsaMixer"/>: the card's own dB range
/// bounds them, a card that publishes no dB scale is said so rather than guessed at, and a value
/// outside the range is refused with the range rather than clamped to it.</para>
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
    /// a CM108 presents; "AGC" and "Mic AGC" turn up on other USB codecs. Looked for so that it
    /// can be switched off; see <see cref="ForceAgcAndBoostOff"/>.
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
    /// <para>Looked for so that it can be switched OFF, which is all this station ever does with
    /// it; see <see cref="ForceAgcAndBoostOff"/>.</para>
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultMicBoostControls =
        ["Mic Boost", "Mic Boost (+20dB)", "Internal Mic Boost", "Mic Capture Boost"];

    /// <summary>
    /// Names to look for the transmit-side level under, in order. A CM108's output is "Speaker";
    /// "PCM" and "Master" are the usual alternatives.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPlaybackControls =
        ["Speaker", "PCM", "Master", "Headphone"];

    /// <summary>Capture gain in dB, or null to leave it.</summary>
    public double? CaptureGainDb { get; init; }

    /// <summary>Transmit-side playback level in dB, or null to leave it.</summary>
    public double? PlaybackDb { get; init; }

    /// <summary>
    /// Switch the AGC and the mic boost off, on any card that has them, whatever they were set to.
    /// </summary>
    /// <remarks>
    /// <para>The one thing here that is not "null leaves it alone", and deliberately so (Tom,
    /// 2026-09-06: "AGC should just be forced off, as should mic boost. No need for buttons for
    /// these."). Both are wrong for a data modem in every case anybody has produced: automatic
    /// gain fights the modem's own level tracking and turns the noise floor into a moving target,
    /// and +20 dB ahead of everything is a way of clipping the card rather than of hearing a
    /// quiet radio. There was no third setting worth a button, so there is no setting.</para>
    /// <para>False on the read-back paths - <c>--mixer-show</c>, <c>GET /api/mixer</c>,
    /// <see cref="LeaveEverything"/> - because those must not write to a card at all. It is the
    /// station's own start-up that sets it, once.</para>
    /// </remarks>
    public bool ForceAgcAndBoostOff { get; init; }

    /// <summary>Where to look for the capture gain.</summary>
    public IReadOnlyList<string> CaptureControls { get; init; } = DefaultCaptureControls;

    /// <summary>Where to look for the automatic gain control.</summary>
    public IReadOnlyList<string> AgcControls { get; init; } = DefaultAgcControls;

    /// <summary>Where to look for the microphone boost.</summary>
    public IReadOnlyList<string> MicBoostControls { get; init; } = DefaultMicBoostControls;

    /// <summary>Where to look for the transmit-side level.</summary>
    public IReadOnlyList<string> PlaybackControls { get; init; } = DefaultPlaybackControls;

    /// <summary>
    /// Where each value above came from, for the journal and the API to say so. Purely
    /// descriptive: nothing here changes what is written to the card.
    /// </summary>
    public MixerSources Sources { get; init; } = new();

    /// <summary>
    /// The same control-name lists, asking for nothing to be changed - which turns
    /// <see cref="MixerSetup.Apply"/> into a pure read-back of the card.
    /// </summary>
    public MixerSettings LeaveEverything() => this with
    {
        CaptureGainDb = null,
        PlaybackDb = null,
        ForceAgcAndBoostOff = false,
        Sources = new MixerSources(),
    };

    /// <summary>Whether this asks for any change at all.</summary>
    public bool SetsAnything =>
        CaptureGainDb is not null || PlaybackDb is not null || ForceAgcAndBoostOff;
}
