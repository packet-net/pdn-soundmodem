namespace Packet.SoundModem.Audio;

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

    /// <summary>Sets every channel of a control's volume to a percentage of its range.</summary>
    /// <param name="control">The control's name, as <see cref="Controls"/> spells it.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="percent">0-100, linear on the card's raw range (what <c>amixer</c> means by
    /// a percentage, and what <c>alsamixer</c> shows).</param>
    /// <returns>False when there is no such control, or it has no volume on that side.</returns>
    bool TrySetVolume(string control, MixerDirection direction, int percent);

    /// <summary>Reads a control's volume back from the card.</summary>
    /// <param name="control">The control's name.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="percent">0-100 of the card's raw range.</param>
    /// <param name="decibels">The same setting in dB when the card publishes a dB scale, else
    /// null. Cards that report one are worth quoting: dB is what a level sits on.</param>
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
