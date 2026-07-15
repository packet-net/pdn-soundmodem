using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// 1200 baud AFSK carrying IL2P (NinoTNC mode 7 "1200 AFSK IL2P+CRC") as an
/// <see cref="IModem"/>. Same Bell-202 tones and demodulator as
/// <see cref="Afsk1200Modem"/>, but the bit layer is IL2P: demodulated levels feed the
/// deframer raw — no NRZI — matching Dire Wolf, whose IL2P receiver taps the bit before
/// NRZI decoding (hdlc_rec_bit), and the proven 9600 IL2P framing choice here.
/// Transparency comes from IL2P's packet-synchronous scrambler, not bit stuffing.
/// </summary>
public sealed class Afsk1200Il2pModem : IModem
{
    private const int Baud = 1200;

    private readonly Afsk1200Demodulator _demodulator;
    private readonly Afsk1200Modulator _modulator;
    private readonly bool _crc;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="crc">Expect/emit the Hamming-protected trailing CRC ("IL2P+CRC" —
    /// what the NinoTNC modes use).</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz standard.</param>
    public Afsk1200Il2pModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double centerFrequency = 1700)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _crc = crc;
        var deframer = new Il2pDeframer((frame, _) => frameReceived(frame), crcMode: crc);
        _demodulator = new Afsk1200Demodulator(sampleRate, deframer.PushBit, centerFrequency);
        _modulator = new Afsk1200Modulator(sampleRate);
    }

    /// <inheritdoc />
    public string Mode => _crc ? "afsk1200-il2pc" : "afsk1200-il2p";

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
        int preambleBits = Math.Max(16, txDelayMilliseconds * Baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        return _modulator.ModulateLevels(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
