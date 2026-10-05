namespace Packet.SoundModem.Modems;

/// <summary>
/// How a modem that can pack several frames into one burst is told to: the longest burst it may
/// build, and how long the channel lets a run of frames gather before contending for the air.
/// </summary>
/// <param name="MaxBurst">The longest one packed burst may run, preamble included and TXDELAY
/// not. A single frame longer than this still goes out, alone.</param>
/// <param name="Gather">How long the first frame of a run waits for the rest to arrive before
/// the channel contends for the air on its behalf. Zero packs only what is already queued when
/// the transmitter gets to it.</param>
public sealed record FramePacking(TimeSpan MaxBurst, TimeSpan Gather);

/// <summary>
/// A modem that can carry several frames in one burst rather than one burst per frame. Optional,
/// beside <see cref="IModem"/>, and discovered by type test, exactly as
/// <see cref="IHardwareControllable"/> is.
/// </summary>
/// <remarks>
/// <para>For waveforms whose fixed cost per burst is large beside a frame: MS110D pays a
/// preamble and an interleaver flush on every burst, so a run of frames sent one burst each
/// spends much of its airtime on framing. Packed, the run pays that once.</para>
/// <para><b>Off unless <see cref="Packing"/> is set.</b> With it null the channel never calls
/// the two methods below and the modem sends one frame per burst through
/// <see cref="IModem.Modulate"/>, as it always has. The channel reads <see cref="Packing"/> at the
/// moment it renders, so a change applies from the next burst.</para>
/// <para>Each packed frame is still its own transmission to the channel's callers: its own
/// <c>FrameTransmitted</c>, its own ACKMODE answer and its own wait report. Only the audio is
/// shared.</para>
/// </remarks>
public interface IFramePackingModem
{
    /// <summary>How this modem packs, or null (the default) for one frame per burst.</summary>
    FramePacking? Packing { get; set; }

    /// <summary>
    /// How many of <paramref name="frames"/>, counted from the first, fit one burst inside
    /// <see cref="FramePacking.MaxBurst"/>. At least 1 for a non-empty list: a first frame that
    /// is too long on its own, or that the modem would refuse, is answered 1 so that the channel
    /// sends it (or refuses it) through <see cref="IModem.Modulate"/> exactly as it would unpacked.
    /// A later frame the modem would refuse ends the count before it.
    /// </summary>
    int FramesPerBurst(IReadOnlyList<byte[]> frames);

    /// <summary>
    /// Modulates <paramref name="frames"/> as one burst, in order, with TXDELAY expressed in the
    /// returned samples as for <see cref="IModem.Modulate"/>.
    /// </summary>
    float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds);
}
