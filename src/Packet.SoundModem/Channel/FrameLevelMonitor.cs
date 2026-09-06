using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Channel;

/// <summary>
/// The channel's per-frame audio level: an <see cref="InputLevelHistory"/> over the receive
/// audio, plus one measured airtime-per-byte figure per modem, so that every decoded frame can
/// carry the level of the audio <em>it</em> arrived on
/// (<see cref="FrameQuality.PeakDbFs"/>, <see cref="FrameQuality.Clipped"/>) - into the frame
/// log, the waterfall's panel and the uplink.
/// </summary>
/// <remarks>
/// <para><b>Why not the level meter</b> (issue #426, Tom): the meter reads the whole input five
/// times a second, and a qpsk3600 frame is over inside one of its intervals. Worse, on an FM
/// radio with the squelch open the noise between the frames is louder than the frames, so the
/// meter's bar answers a question about the hiss. A per-frame figure has to be measured over the
/// frame's own stretch of audio and nothing else.</para>
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
    /// <summary>Payload bytes in the short probe frame.</summary>
    private const int ShortProbePayload = 8;

    /// <summary>And in the long one. The difference between the two is what is being measured.</summary>
    private const int LongProbePayload = 136;

    private readonly InputLevelHistory _history;

    /// <summary>Samples of air per frame byte, per sub-channel; absent for a modem that would not
    /// modulate the probe frames, which then reports no level rather than a made-up one.</summary>
    private readonly Dictionary<int, double> _samplesPerByte = [];

    private int _blockSamples;

    public FrameLevelMonitor(int sampleRate) => _history = new InputLevelHistory(sampleRate);

    /// <summary>
    /// Measures how much air one byte costs this modem, by modulating two frames that differ
    /// only in length and taking the difference.
    /// </summary>
    /// <remarks>
    /// <para>Measured rather than tabulated, exactly as <see cref="ModemBandProbe"/> measures the
    /// band rather than keeping a table of 38 modes' bandwidths: a table of bit rates, framing
    /// overheads and Reed-Solomon block sizes would be one more thing to keep in step with the
    /// modems, and would be wrong the first time one was retuned.</para>
    /// <para><b>The difference of two lengths, so the intercept is deliberately thrown away.</b>
    /// What is wanted is a figure that <em>under</em>-states a burst rather than over-stating it
    /// (see <see cref="Measure"/>): the real burst also carries the sending station's transmit
    /// delay, the mode's own preamble and sync, and the FEC parity, none of which this counts.
    /// Losing them all shortens the window and keeps it inside the burst, which is the safe
    /// direction; adding a guess at them would push it out into the noise either side.</para>
    /// </remarks>
    public void AddModem(int subChannel, IModem modem)
    {
        try
        {
            byte[] shortFrame = ProbeFrame(ShortProbePayload);
            byte[] longFrame = ProbeFrame(LongProbePayload);
            int shortSamples = modem.Modulate(shortFrame, txDelayMilliseconds: 0).Length;
            int longSamples = modem.Modulate(longFrame, txDelayMilliseconds: 0).Length;
            double perByte = (double)(longSamples - shortSamples) / (longFrame.Length - shortFrame.Length);
            if (perByte > 0)
            {
                _samplesPerByte[subChannel] = perByte;
            }
        }
        catch (ArgumentException)
        {
            // A mode that will not carry one of the probe frames - a fixed-length burst format,
            // or one whose size bound the long probe crosses. No airtime model, so no level,
            // which is the same answer this gives for a modem whose band cannot be probed.
        }
    }

    /// <summary>Feeds received (never transmitted) audio, at the channel's rate.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        _blockSamples = samples.Length;
        _history.Add(samples);
    }

    /// <summary>
    /// Feeds one block of card samples for the clip flag, before the channel-rate block it
    /// becomes. See <see cref="InputLevelHistory.NoteCardClipping"/>.
    /// </summary>
    public void NoteCardClipping(ReadOnlySpan<float> samples) => _history.NoteCardClipping(samples);

    /// <summary>
    /// The level of the audio a just-decoded frame arrived on, or nulls where it cannot be
    /// placed.
    /// </summary>
    /// <remarks>
    /// <para><b>The alignment, and its arithmetic.</b> Write <c>P</c> for how much audio the
    /// history has taken in (<see cref="InputLevelHistory.Position"/>), <c>B</c> for the block
    /// being processed right now, and <c>D</c> for the frame's measured airtime,
    /// <c>samplesPerByte * frameBytes</c>. This is called from inside the modem's decode of
    /// block <c>B</c>, and the history was fed that block before the modems were, so <c>P</c> is
    /// the end of the block and the frame's last on-air sample <c>T</c> lies somewhere in
    /// <c>[P-B, P]</c> - nothing here knows where in the block the modem's deframer fired.
    /// The frame's audio is <c>[T-D, T]</c> or longer. Intersecting that over every <c>T</c> the
    /// block allows leaves <c>[P-D, P-B]</c>, which is <b>inside the burst wherever in the block
    /// the frame ended</b>, and is the window measured. It is <c>D-B</c> long: a 100 ms block
    /// costs the first 100 ms of the reading and nothing else.</para>
    /// <para>Two things are knowingly left out of it. The modem's own latency - filter group
    /// delay, and the closing flag or trailer it decodes before it hands the frame over - puts
    /// <c>T</c> a few milliseconds earlier still, which would shift the window earlier by the
    /// same few milliseconds; not correcting for it can only leave the window's late end a
    /// few milliseconds past the frame's last data sample, which is still inside the burst,
    /// because the trailer and the transmitter's tail are there. And <c>D</c> under-states the
    /// burst (see <see cref="AddModem"/>), so the window's early end sits inside the frame
    /// rather than out in front of it.</para>
    /// <para><b>A frame shorter than the audio block</b> has no such window - the block's own
    /// length is the whole uncertainty - so it takes the best estimate available instead: the
    /// same <c>D</c> of audio, ending half a block back, which is where the frame ended if the
    /// modem fired half way through the block. That is the case a station reading 100 ms at a
    /// time cannot do better on without the modems reporting where in a block they finished; a
    /// station reading 20 ms blocks (which is what a station with ARDOP on it does) has it for
    /// every frame there is.</para>
    /// </remarks>
    /// <param name="subChannel">The modem that decoded it.</param>
    /// <param name="frameBytes">The decoded frame's length, as the quality reports it.</param>
    /// <returns>The peak in dBFS over the frame's own audio and whether the card clipped in it;
    /// nulls for a modem with no airtime model, or a window the ring no longer holds.</returns>
    public (double? PeakDbFs, bool? Clipped) Measure(int subChannel, int frameBytes)
    {
        if (frameBytes <= 0 || !_samplesPerByte.TryGetValue(subChannel, out double perByte))
        {
            return (null, null);
        }

        long airtime = (long)(perByte * frameBytes);
        long block = Math.Max(0, _blockSamples);
        long end = airtime >= block ? _history.Position - block : _history.Position - (block / 2);
        long start = airtime >= block ? _history.Position - airtime : end - airtime;
        return _history.TryMeasure(start, end, out double peakDbFs, out bool? clipped)
            ? (Math.Round(peakDbFs, 1), clipped)
            : (null, null);
    }

    /// <summary>A probe frame of a known length, the same shape <see cref="ModemBandProbe"/>
    /// uses so that a mode which refuses one refuses both.</summary>
    private static byte[] ProbeFrame(int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        for (int n = 0; n < payload.Length; n++)
        {
            payload[n] = (byte)((n + 16) * 37); // arbitrary non-repeating payload
        }

        return Waterfall.Ax25UiFrame.Build("PDNSM", "PDNSM", payload);
    }
}
