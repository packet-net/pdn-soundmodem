namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// Something outside the demodulator that knows whether the channel is occupied.
/// </summary>
/// <remarks>
/// <para>It exists so that carrier sense can come from something that actually measures the
/// channel - a squelch and a calibrated signal-strength meter, read over a control cable - rather
/// than being inferred from the audio a receiver produces. On an FM path that inference is harder
/// than it looks, and both attempts at it here are wrong in opposite directions: see
/// <c>docs/dev/ofdm-fm/carrier-sense.md</c>.</para>
/// <para><b>Why the audio cannot settle it, measured rather than assumed.</b> An FM receiver goes
/// QUIET when a carrier arrives, so an energy detector waiting for audio to RISE asserts at the
/// END of every burst rather than at its start. A detector that correctly watches for the
/// quieting instead does work, and then needs ABSOLUTE levels to do it:
/// <see cref="FmQuietingBusyDetector"/>'s thresholds are in dBFS, and two nominally identical
/// stations on this bench measured 22 dB apart. One threshold is then right on one of them and
/// stops the other transmitting at all, which is the worse failure by a distance. Nothing inside
/// the audio path fixes that, which is the whole argument for asking something outside it.</para>
/// </remarks>
public interface IChannelBusySource
{
    /// <summary>
    /// True when the channel is occupied, false when it is clear, and <see langword="null"/> when
    /// this source does not know.
    /// </summary>
    /// <remarks>
    /// <b>Null is a real answer and callers must handle it.</b> A source that has lost its link
    /// says null rather than guessing, and a caller that treats null as busy would hand it the
    /// power to silence a station by failing.
    /// </remarks>
    bool? Busy { get; }
}
