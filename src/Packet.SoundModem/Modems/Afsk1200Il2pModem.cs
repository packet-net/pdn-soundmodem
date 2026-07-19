using M0LTE.Il2p;

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

    /// <summary>Bell 202 deviation of each tone from the centre (mark = centre − 500,
    /// space = centre + 500); the demodulator's default shift.</summary>
    private const double Bell202ToneShift = 500;

    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
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
        var deframer = new Il2pDeframer(
            (frame, info) =>
            {
                frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid));
            },
            crcMode: crc);
        // Reset the deframer on the DCD falling edge — same rationale as BpskModem:
        // a carrier that drops mid-collection leaves the deframer consuming the next
        // transmission's sync word as phantom payload.
        bool previousDcd = false;
        AfskDemodulator? demodulator = null;
        demodulator = new AfskDemodulator(sampleRate, bit =>
        {
            bool dcd = demodulator!.CarrierDetect;
            if (previousDcd && !dcd)
            {
                deframer.Reset();
            }

            previousDcd = dcd;
            deframer.PushBit(bit);
        }, centerFrequency);
        _demodulator = demodulator;
        _modulator = new AfskModulator(
            sampleRate, Baud, centerFrequency - Bell202ToneShift, centerFrequency + Bell202ToneShift);
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

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
