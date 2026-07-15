using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>Framing carried over the 300 baud HF AFSK baseband — one per NinoTNC "SSB
/// AFSK" mode.</summary>
public enum Afsk300Framing
{
    /// <summary>Legacy HF packet: NRZI + HDLC, no FEC (NinoTNC mode 12).</summary>
    Ax25,

    /// <summary>IL2P without the CRC trailer (NinoTNC mode 13).</summary>
    Il2p,

    /// <summary>IL2P with the Hamming-protected trailing CRC (NinoTNC mode 14).</summary>
    Il2pCrc,
}

/// <summary>
/// 300 baud HF AFSK — the NinoTNC "SSB AFSK" mode family (12 AX.25, 13 IL2P, 14
/// IL2P+CRC): 1600/1800 Hz tone FSK filtered to 500 Hz occupied bandwidth, per Nino's
/// v3/4.43 mode-switch mapping in flashtnc's release-notes.txt. Same demodulator chain as
/// the Bell 202 modes — a quadrature FM discriminator does not care about the shift — with
/// per-mode filters and bit clock.
/// </summary>
/// <remarks>
/// Tone assignment (mark = 1600) is only a convention here: AX.25 rides NRZI, which is
/// polarity-agnostic by construction, and both IL2P receivers hunt sync in both
/// polarities (ours since the 9600 IL2P work; the NinoTNC's since firmware 2.42's "IL2P
/// receive inversion detection"). Bench-confirmed against a NinoTNC either way.
/// </remarks>
public sealed class Afsk300Modem : IModem
{
    private const int Baud = 300;

    // Bench-tuned against recorded NinoTNC mode-12 audio (a 7×7 sweep of both values over
    // six real bursts): ±300 Hz band-pass, 300 Hz I/Q low-pass sits mid-plateau at a full
    // score. Nino filters these modes to 500 Hz OBW and the measured energy spans
    // 1520–1870 Hz, so ±300 passes the signal whole with room for the filter's own
    // transition width, while staying tight enough to keep the discriminator clean.
    private const double BandPassHalfWidth = 400;
    private const double LowPassCutoff = 400;
    private const double ToneShift = 100;

    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
    private readonly Afsk300Framing _framing;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of 300).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="framing">Which of the three HF modes to run.</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz (tones 1600/1800).</param>
    public Afsk300Modem(
        int sampleRate, Action<byte[]> frameReceived, Afsk300Framing framing = Afsk300Framing.Il2pCrc,
        double centerFrequency = 1700)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _framing = framing;

        Action<int> bitSink;
        if (framing == Afsk300Framing.Ax25)
        {
            var deframer = new HdlcDeframer(frameReceived);
            var nrzi = new NrziDecoder();
            bitSink = level => deframer.PushBit(nrzi.Decode(level));
        }
        else
        {
            var deframer = new Il2pDeframer(
                (frame, _) => frameReceived(frame), crcMode: framing == Afsk300Framing.Il2pCrc);
            bitSink = deframer.PushBit;
        }

        _demodulator = new AfskDemodulator(
            sampleRate, bitSink, centerFrequency, Baud, BandPassHalfWidth, LowPassCutoff,
            toneShift: ToneShift);
        _modulator = new AfskModulator(
            sampleRate, Baud, centerFrequency - ToneShift, centerFrequency + ToneShift);
    }

    /// <inheritdoc />
    public string Mode => _framing switch
    {
        Afsk300Framing.Ax25 => "afsk300",
        Afsk300Framing.Il2pCrc => "afsk300-il2pc",
        _ => "afsk300-il2p",
    };

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        if (_framing == Afsk300Framing.Ax25)
        {
            int openingFlags = Math.Max(2, txDelayMilliseconds * Baud / (8 * 1000));
            return _modulator.Modulate(HdlcFramer.FrameBits(ax25Frame, openingFlags, closingFlags: 2));
        }

        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _framing == Afsk300Framing.Il2pCrc);
        int preambleBits = Math.Max(16, txDelayMilliseconds * Baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        return _modulator.ModulateLevels(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
