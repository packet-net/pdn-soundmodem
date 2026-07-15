using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>300 baud BPSK carrying IL2P (the NinoTNC HF mode family) as an
/// <see cref="IModem"/>.</summary>
public sealed class Bpsk300Modem : IModem
{
    private readonly Bpsk300Demodulator _demodulator;
    private readonly Bpsk300Modulator _modulator;
    private readonly bool _crc;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of 300).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC
    /// networks.</param>
    /// <param name="carrierFrequency">Carrier centre; 1500 Hz convention.</param>
    public Bpsk300Modem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double carrierFrequency = 1500)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _crc = crc;
        var deframer = new Il2pDeframer((frame, _) => frameReceived(frame), crc);
        _demodulator = new Bpsk300Demodulator(sampleRate, deframer.PushBit, carrierFrequency);
        _modulator = new Bpsk300Modulator(sampleRate, carrierFrequency);
    }

    /// <inheritdoc />
    public string Mode => _crc ? "bpsk300-il2pc" : "bpsk300-il2p";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);
        int preambleBits = Math.Max(24, txDelayMilliseconds * 300 / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return _modulator.Modulate(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
