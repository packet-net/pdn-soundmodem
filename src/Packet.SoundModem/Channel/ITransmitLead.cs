using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>
/// An <see cref="IAudioOutput"/> that already has silence ahead of a transmission's first sample,
/// and can say how much before the transmission is written (issue #828).
/// </summary>
/// <remarks>
/// <para>The ALSA output plays silence between transmissions so that its stream never stops, and
/// some of that silence is still ahead of a transmission's audio when it starts: carrier with
/// nothing on it, at the front of every keyup. A transmission that begins with silence of its
/// own, because that is how it spends TXDELAY, can leave that much of it out, and the keyup then
/// has the lead that was configured and not that lead plus the queue.</para>
/// <para>Optional, and separate from <see cref="IAudioOutput"/>, which belongs to another
/// package. An output without it is given everything, as it always was. A wrapper passes the
/// question on to what it wraps.</para>
/// </remarks>
public interface ITransmitLead
{
    /// <summary>
    /// How much silence will be ahead of the first sample of the next transmission written to
    /// this output. No less than this is ahead when that transmission starts, so the caller may
    /// leave up to this much leading silence out of what it writes. Zero inside a transmission,
    /// and zero where nothing is queued.
    /// </summary>
    TimeSpan PromiseLead();
}
