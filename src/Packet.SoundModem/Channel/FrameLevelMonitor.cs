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
    /// <summary>
    /// How much of each end of the span is left out of the reading, in samples.
    /// </summary>
    /// <remarks>
    /// <para>Both of a span's marks are late by the demodulator's own front-end group delay -
    /// the band-pass and matched filters between the antenna and the bit - because both are
    /// taken from the same chain. The span's length is therefore right and its position is a
    /// little late, so its late end overhangs the burst by that much, and the reading is a peak,
    /// so one cell of louder audio hanging over the edge takes the whole answer over.</para>
    /// <para><b>Samples, not milliseconds.</b> The overhang is a few symbol periods of the
    /// front end, so it shrinks with the symbol rate rather than staying put in time, and the
    /// channels that run at 48 kHz here are exactly the fast modes. Measured through the real
    /// channel by the review: the worst overhang is 17.1 ms on the 12 kHz modes (bpsk300 and
    /// qpsk600, 205 samples) and 0.4 ms on the 48 kHz ones (19 samples). One count of samples
    /// covers both with room - 20 ms at 12 kHz, 5 ms at 48 kHz - where 25 ms flat took the level
    /// away from most real frames on the 9600 and 19200 modes, which are the ones this feature
    /// was asked for.</para>
    /// <para>Taken off the near end too. Nothing measured needs it there - the mark is already
    /// inside the burst, with the sender's transmit delay in front of it - but it costs a long
    /// frame nothing, it covers a sync taken a symbol early, and a rule with one number in it is
    /// a rule that can be checked.</para>
    /// </remarks>
    public const int MarginSamples = 240;

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
    /// <param name="fromSample">Where the frame's sync was taken, from
    /// <see cref="IFrameSpanSource.TryTakeFrameSpan"/>.</param>
    /// <param name="toSample">Where its last bit was taken.</param>
    /// <returns>The peak in dBFS over the frame's own audio and whether the card clipped in it.</returns>
    public (double? PeakDbFs, bool? Clipped) Measure(long fromSample, long toSample)
    {
        long from = fromSample + MarginSamples;
        long to = toSample - MarginSamples;
        return _history.TryMeasure(from, to, out double peakDbFs, out bool? clipped)
            ? (Math.Round(peakDbFs, 1), clipped)
            : (null, null);
    }
}
