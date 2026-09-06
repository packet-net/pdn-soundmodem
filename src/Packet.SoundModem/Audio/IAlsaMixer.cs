namespace Packet.SoundModem.Audio;

/// <summary>The span of levels a control can be set to, in dB, as the card publishes it.</summary>
/// <param name="MinDb">The quietest level that is a level, not silence.</param>
/// <param name="MaxDb">The loudest.</param>
/// <param name="MutesBelowMin">Whether the step below <paramref name="MinDb"/> is the card's
/// mute rather than a quieter level.</param>
/// <remarks>
/// <para><b>Why the bottom is not simply what ALSA answers.</b> A control whose TLV carries the
/// mute flag - <c>dBminmaxmute</c>, which the bench CM108's "Speaker" does - makes
/// <c>snd_tlv_get_dB_range</c> report its minimum as <c>SND_CTL_TLV_DB_GAIN_MUTE</c>, the
/// sentinel -9999999 in hundredths of a dB. Passed through, that is "-99999.99 dB" in the
/// journal and a slider a thousand times too long to aim with, for a card whose real span is
/// 37 dB. So the sentinel is recognised and the bottom is the lowest step that is an actual
/// level, found by asking the card what dB each raw step is.</para>
/// <para>The mute step is not thrown away, it is reported: an operator sliding a transmit level
/// to the bottom is entitled to know the last step is silence rather than "very quiet".</para>
/// </remarks>
public sealed record MixerDbRange(double MinDb, double MaxDb, bool MutesBelowMin);

/// <summary>Which half of a mixer control a volume applies to.</summary>
public enum MixerDirection
{
    /// <summary>The capture side: what the modem hears.</summary>
    Capture,

    /// <summary>The playback side: what the radio is driven with.</summary>
    Playback,
}

/// <summary>
/// A sound card's mixer, as much of it as a packet station needs: the controls it has, their
/// levels and their on/off switches.
/// </summary>
/// <remarks>
/// <para>An interface rather than the concrete <see cref="AlsaMixer"/> so that exactly one class
/// in this repository talks to <c>libasound</c>'s mixer, and everything above it - the control
/// name fallbacks, the journal wording, the read-back, the API - is ordinary code that a test
/// can drive against a fake card. There is no sound hardware on a CI runner, and a mixer is
/// precisely the thing you cannot exercise without one.</para>
/// <para>Every method is a <c>Try</c>: a control that is not there, or that has no volume, or no
/// switch, is the normal case rather than an error. Card revisions differ, and a CM108 that has
/// no "Mic Boost" at all (its +20 dB is folded into the capture range) must not be a failure.</para>
/// </remarks>
public interface IAlsaMixer : IDisposable
{
    /// <summary>The card this mixer belongs to, as ALSA names it (<c>hw:1</c>).</summary>
    string Card { get; }

    /// <summary>
    /// Every simple control the card offers, in the order ALSA lists them. Empty means the card
    /// has no mixer at all, which is a thing to say once and carry on from.
    /// </summary>
    IReadOnlyList<string> Controls { get; }

    /// <summary>
    /// Takes in any changes made outside this process - <c>alsamixer</c> in another window, a
    /// re-plug - so a read-back reports the card rather than a cached value.
    /// </summary>
    void Refresh();

    /// <summary>Sets every channel of a control's volume to a level in dB.</summary>
    /// <param name="control">The control's name, as <see cref="Controls"/> spells it.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="decibels">The level wanted, in dB. The card takes the nearest step it has.</param>
    /// <returns>False when there is no such control, it has no volume on that side, or it
    /// publishes no dB scale to set one on. Ask <see cref="ReadDbRange"/> first to tell those
    /// apart: a card with no dB scale is refused with a reason rather than guessed at.</returns>
    bool TrySetDb(string control, MixerDirection direction, double decibels);

    /// <summary>The span of levels a control can be set to, in dB.</summary>
    /// <param name="control">The control's name.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <returns>The range, or null when there is no such control, it has no volume on that side,
    /// or it publishes only raw steps and no dB scale at all. Plenty of cards do, and that is the
    /// case a dB setting has to say "no dB scale" about rather than invent a number for.</returns>
    MixerDbRange? ReadDbRange(string control, MixerDirection direction);

    /// <summary>Sets every channel of a control's volume to a percentage of its range.</summary>
    /// <param name="control">The control's name, as <see cref="Controls"/> spells it.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="percent">0-100, linear on the card's raw range (what <c>amixer</c> means by
    /// a percentage, and what <c>alsamixer</c> shows).</param>
    /// <returns>False when there is no such control, or it has no volume on that side.</returns>
    /// <remarks>
    /// The station's own levels are set in dB (<see cref="TrySetDb"/>); this is here for the one
    /// case that is not a level at all - a "Mic Boost" that some cards present as a few steps
    /// rather than a switch, where on means the top of the range and off means the bottom.
    /// </remarks>
    bool TrySetVolume(string control, MixerDirection direction, int percent);

    /// <summary>Reads a control's volume back from the card.</summary>
    /// <param name="control">The control's name.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="percent">0-100 of the card's raw range.</param>
    /// <param name="decibels">The same setting in dB when the card publishes a dB scale, else
    /// null. This is the figure the station works in; the percentage is what <c>alsamixer</c>
    /// shows beside it, and is all there is to report on a card with no dB scale.</param>
    /// <returns>False when there is no such control, or it has no volume on that side.</returns>
    bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels);

    /// <summary>Turns a control's on/off switch on or off, on every channel.</summary>
    /// <param name="control">The control's name.</param>
    /// <param name="on">The state wanted.</param>
    /// <returns>False when there is no such control, or it has no switch.</returns>
    /// <remarks>
    /// No direction, deliberately. A card that names a control without saying which side it is on
    /// (a CM108's "Auto Gain Control") gets it registered as a global switch, which the simple
    /// mixer API then answers to on both sides; a card that puts it on one side answers on that
    /// one. The implementation tries the capture switch and then the playback switch, so the
    /// caller never has to know which revision it is talking to.
    /// </remarks>
    bool TrySetSwitch(string control, bool on);

    /// <summary>Reads a control's on/off switch back from the card.</summary>
    /// <param name="control">The control's name.</param>
    /// <param name="on">Its state.</param>
    /// <returns>False when there is no such control, or it has no switch.</returns>
    bool TryReadSwitch(string control, out bool on);
}
