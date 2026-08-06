namespace Packet.SoundModem.Modems;

/// <summary>
/// One logical modem: a demodulator chain producing AX.25 frames from audio, and a
/// modulator producing audio from AX.25 frames. Several modems can share one audio
/// channel (the QtSoundModem multiplex model); each is addressed by its KISS sub-channel.
/// </summary>
public interface IModem
{
    /// <summary>Human-readable mode name (e.g. "afsk1200", "bpsk300-il2pc").</summary>
    string Mode { get; }

    /// <summary>Raised for every decoded frame with its receive diagnostics - FEC
    /// corrections, CRC state, winning decoder branch. This is the <b>monitor</b> path:
    /// subscribe when per-frame quality matters, ignore when it does not.</summary>
    /// <remarks>
    /// <para>This used to fire strictly in addition to the constructor's frame sink, on the same
    /// decode, and that invariant no longer holds. A frame carrying
    /// <see cref="FrameQuality.MonitorOnly"/> fires this event and <em>never reaches the
    /// sink</em>: it is a frame the station read and is not passing to its host, which today
    /// means plain IL2P heard by an IL2P+CRC link that was not told to accept it (see
    /// <see cref="Il2pReceiver"/>). Everything hanging off this event - display, frame log,
    /// journal line, signal survey - wants such a frame, because the station did hear it.</para>
    /// <para>So anything that <em>relays</em> frames onward rather than displaying them must test
    /// that flag. Do not infer delivery from the order the two fire in: they still fire from the
    /// same synchronous decode when both fire, which makes ordering look like a usable signal
    /// right up until the sink is silent.</para>
    /// </remarks>
    event Action<byte[], FrameQuality>? FrameDecoded;

    /// <summary>True while the demodulator sees a coherent packet signal.</summary>
    bool CarrierDetect { get; }

    /// <summary>True while the demodulator sees packet or non-packet in-band energy.</summary>
    bool ChannelBusy { get; }

    /// <summary>Feeds received audio at the channel's DSP rate.</summary>
    void Process(ReadOnlySpan<float> samples);

    /// <summary>Modulates one AX.25 frame (no flags/FCS) to audio at the DSP rate,
    /// including the mode's preamble/framing. TXDELAY is expressed in the returned
    /// samples.</summary>
    float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds);

    /// <summary>Clears receive carrier state (call while the channel transmits).</summary>
    void ResetCarrierState();
}
