using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Channel;

/// <summary>
/// The channel's per-frame audio level: an <see cref="InputLevelHistory"/> over the receive
/// audio, read over the span the demodulator says the frame occupied, so that every decoded
/// frame can carry the level of the audio <em>it</em> arrived on
/// (<see cref="FrameQuality.PeakDbFs"/>, <see cref="FrameQuality.Clipped"/>) - into the frame
/// log, the waterfall's panel and the uplink.
/// </summary>
/// <remarks>
/// <para><b>Why not the level meter</b> (issue #426, Tom): the meter reads the whole input five
/// times a second, and a qpsk3600 frame is over inside one of its intervals. Worse, on an FM
/// radio with the squelch open the noise between the frames is louder than the frames, so the
/// meter's bar answers a question about the hiss. A per-frame figure has to be measured over the
/// frame's own stretch of audio and nothing else.</para>
/// <para><b>The span is asked for, not inferred.</b> The first cut of this worked the window out
/// from the frame's length and the block the decode was reported in, and the review measured
/// what that costs: on a station reading 100 ms blocks, a frame shorter than a block (which is
/// every qpsk3600 frame under 45 bytes - the case the issue was raised about) read the audio
/// around it rather than itself on 5 of 8 alignments, and reported the tone beside it as the
/// frame's own level, badged TOO LOUD. Nothing outside the demodulator knows where in a block a
/// frame ended; the demodulator knows to the sample. So it says, and a modem that cannot say
/// gets no level at all (<see cref="IFrameSpanSource"/>).</para>
/// <para><b>Beside <see cref="BurstSnrMonitor"/>, and for the same reason</b>: it is measured at
/// the one point every modem's decode passes through, so the frame log, the page and a monitor
/// receiving this station's uplink all carry the identical figure rather than three
/// re-measurements that can disagree.</para>
/// <para>Single-threaded with the receive path, like the modems it serves. The card-rate half
/// (<see cref="NoteCardClipping"/>) is fed from the same thread, one block ahead of the
/// channel-rate block it describes.</para>
/// </remarks>
internal sealed class FrameLevelMonitor
{
    private readonly InputLevelHistory _history;

    public FrameLevelMonitor(int sampleRate) => _history = new InputLevelHistory(sampleRate);

    /// <summary>Feeds received (never transmitted) audio, at the channel's rate.</summary>
    public void Process(ReadOnlySpan<float> samples) => _history.Add(samples);

    /// <summary>
    /// Feeds one block of card samples for the clip flag, before the channel-rate block it
    /// becomes. See <see cref="InputLevelHistory.NoteCardClipping"/>.
    /// </summary>
    public void NoteCardClipping(ReadOnlySpan<float> samples) => _history.NoteCardClipping(samples);

    /// <summary>
    /// The level of the audio a just-decoded frame arrived on, over the span its own demodulator
    /// reported, or nulls where that span is too short to read or older than the history holds.
    /// </summary>
    /// <remarks>
    /// <para><b>The margin comes off the late end only, and the modem says how much.</b> Both of
    /// a span's marks are late by the demodulator's own front-end group delay - the band-pass and
    /// matched filters between the antenna and the bit - because both are taken from the same
    /// chain. So the late end overhangs the burst by that delay, and the reading is a peak, which
    /// half a millisecond of louder audio at the edge takes over completely. The near end needs
    /// nothing: being late by the same amount puts it inside the frame already, with the sender's
    /// transmit delay in front of that.</para>
    /// <para>The size is <see cref="IFrameSpanSource.FrameSpanMarginSamples"/>, which is the
    /// mode's own bit rate over <see cref="FrameSpan.MarginBits"/>. A single figure for every
    /// mode cannot work: 20 ms is a fifth of a bpsk300 frame and two and a half times the whole
    /// of a c4fsk19200 supervisory one.</para>
    /// </remarks>
    /// <param name="fromSample">Where the frame's sync was taken, from
    /// <see cref="IFrameSpanSource.TryTakeFrameSpan"/>.</param>
    /// <param name="toSample">Where its last bit was taken.</param>
    /// <param name="marginSamples">What to leave off the late end, from
    /// <see cref="IFrameSpanSource.FrameSpanMarginSamples"/>.</param>
    /// <returns>The peak in dBFS over the frame's own audio and whether the card clipped in it.</returns>
    public (double? PeakDbFs, bool? Clipped) Measure(long fromSample, long toSample, int marginSamples)
    {
        return _history.TryMeasure(
            fromSample, toSample - marginSamples, out double peakDbFs, out bool? clipped)
            ? (Math.Round(peakDbFs, 1), clipped)
            : (null, null);
    }
}
