using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A card whose mixer could not be opened, standing in for it on a station that receives on one
/// card and transmits through another, so the other card's levels are still set.
/// </summary>
/// <remarks>
/// It has no controls, so <see cref="MixerSetup"/> finds none of the names it looks for on this
/// side and says so only where the configuration asked for a level here. Nothing is ever written
/// through it, and there is nothing to release.
/// </remarks>
/// <param name="card">The card, as ALSA names it, for the journal.</param>
internal sealed class AbsentMixer(string card) : IAlsaMixer
{
    public string Card { get; } = card;

    public IReadOnlyList<string> Controls { get; } = [];

    public void Refresh()
    {
    }

    public bool TrySetDb(string control, MixerDirection direction, double decibels) => false;

    public MixerDbRange? ReadDbRange(string control, MixerDirection direction) => null;

    public bool TrySetVolume(string control, MixerDirection direction, int percent) => false;

    public bool TryReadVolume(
        string control, MixerDirection direction, out int percent, out double? decibels)
    {
        percent = 0;
        decibels = null;
        return false;
    }

    public bool TrySetSwitch(string control, bool on) => false;

    public bool TryReadSwitch(string control, out bool on)
    {
        on = false;
        return false;
    }

    public void Dispose()
    {
    }
}
