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

    /// <summary>Raised for every decoded frame with its receive diagnostics — FEC
    /// corrections, CRC state, winning decoder branch. Fires in addition to (and after
    /// the same decode as) the constructor's frame sink; subscribe when per-frame
    /// quality matters, ignore when it does not.</summary>
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
