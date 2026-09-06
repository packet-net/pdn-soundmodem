using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// The transmitter test the operator's page may ask for: a two-tone linearity check, or one tone
/// for a carrier-level or FM deviation check. Installed by the host (the daemon knows what a
/// transmission costs; this library only draws the button), and never offered on a public page.
/// </summary>
/// <remarks>
/// <para><b>Operator only.</b> A test transmission is a licensed transmission and is always the
/// operator's own act, so this is null on every public page and on every relayed one - the page
/// the monitor site serves is fed by an uplink and has no transmitter at the end of it at all.
/// The daemon installs it on the station's own page and nowhere else.</para>
/// <para>The delegates are fire and forget. Keying, waiting for the channel and unkeying take
/// seconds, and the socket that carried the request must not be held open across them; what
/// happened comes back to every open page as a <see cref="TxTestStatus"/> instead.</para>
/// </remarks>
public sealed class TxTestControl
{
    /// <summary>How long a test runs when the operator does not say, in seconds.</summary>
    public double DefaultSeconds { get; init; } = 5;

    /// <summary>The longest test that will be run, in seconds, whatever is asked for.</summary>
    public double MaxSeconds { get; init; } = 30;

    /// <summary>The low tone of the two-tone pair, in Hz.</summary>
    public double LowToneHz { get; init; } = TestTone.TwoToneLowHz;

    /// <summary>The high tone of the two-tone pair, in Hz.</summary>
    public double HighToneHz { get; init; } = TestTone.TwoToneHighHz;

    /// <summary>The single-tone presets on offer, in the order they are shown.</summary>
    public IReadOnlyList<TxTestPreset> Presets { get; init; } = [];

    /// <summary>
    /// Why this station cannot run one, in a sentence for the operator, or null when it can. The
    /// control is still shown - saying why is more use than a control that is simply missing -
    /// but nothing can be started from it.
    /// </summary>
    public string? Refusal { get; init; }

    /// <summary>Runs one test. Returns at once; the outcome arrives as a status.</summary>
    public required Action<TxTestRequest> Start { get; init; }

    /// <summary>Ends a test early, or cancels one still waiting for the channel.</summary>
    public required Action Stop { get; init; }

    /// <summary>
    /// Whether a test is running right now, queued or on the air. Read into every config message
    /// (see <c>WaterfallWebServer.BuildConfigMessage</c>), which is how a page that just connected
    /// or reconnected finds out - a <see cref="TxTestStatus"/> is an event for whoever was already
    /// listening when it happened, and a page that was not is told nothing by it, ever. Null (no
    /// station offers this) reads as not running, same as a control this station never installs.
    /// </summary>
    public Func<bool>? IsRunning { get; init; }
}

/// <summary>One single-tone preset: the tone, and the FM deviation its Bessel null calibrates.</summary>
/// <param name="ToneHz">The modulating tone.</param>
/// <param name="DeviationHz">The deviation the carrier nulls at, 2.405 x the tone.</param>
public readonly record struct TxTestPreset(double ToneHz, double DeviationHz)
{
    /// <summary>The preset for a tone, with its deviation worked out rather than tabulated.</summary>
    public static TxTestPreset For(double toneHz) =>
        new(toneHz, TestTone.BesselNullDeviationHz(toneHz));
}

/// <summary>What the operator asked for.</summary>
/// <param name="TwoTone">True for the two-tone pair, false for the single tone below.</param>
/// <param name="ToneHz">The single tone, in Hz; ignored when <paramref name="TwoTone"/> is set.</param>
/// <param name="Seconds">How long to transmit for. Clamped to the cap by whoever runs it.</param>
public sealed record TxTestRequest(bool TwoTone, double ToneHz, double Seconds);

/// <summary>What became of a test, for every page that is open.</summary>
/// <param name="State">
/// <c>running</c> while it is queued or on the air, <c>done</c> when it has finished,
/// <c>refused</c> when it never started. One word, so the page switches on it rather than
/// reading the sentence.
/// </param>
/// <param name="Text">The sentence to show, which is the journal's own wording.</param>
public sealed record TxTestStatus(string State, string Text);
