using Packet.SoundModem.Dsp;
using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>Framing carried over the 9600 baud baseband.</summary>
public enum Fsk9600Framing
{
    /// <summary>Classic G3RUH: HDLC, NRZI, free-running x¹⁷+x¹²+1 scrambler.</summary>
    ClassicHdlc,

    /// <summary>IL2P with trailing CRC (NinoTNC "9600 GFSK IL2P+CRC"): raw bits — no
    /// NRZI, no G3RUH scrambler (IL2P scrambles packet-synchronously itself).</summary>
    Il2pCrc,

    /// <summary>IL2P without the CRC trailer.</summary>
    Il2p,
}

/// <summary>
/// 9600 baud baseband FSK ("RUH") modem, Dire Wolf demod_9600 lineage: the receive chain
/// is a low-pass filter, an envelope-tracking slicer and the shared DPLL; transmit shapes
/// a ±1 NRZ pulse train through the same low-pass design. Runs at 48 kHz (5 samples per
/// bit). Framing per <see cref="Fsk9600Framing"/>. Over-air NinoTNC mode-2 equivalence is
/// bench-gated; the classic and IL2P legs are cross-validated against Dire Wolf audio.
/// </summary>
public sealed class Fsk9600Modem : IModem
{
    private const int Baud = 9600;

    private readonly int _sampleRate;
    private readonly Fsk9600Framing _framing;
    private readonly FirFilter _rxFilter;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private float _peakHigh;
    private float _peakLow;
    private float _previousExcess;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Sample rate; must be a multiple of 9600 (48000 typical).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="framing">Wire framing (classic G3RUH vs IL2P).</param>
    public Fsk9600Modem(int sampleRate, Action<byte[]> frameReceived, Fsk9600Framing framing)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        if (sampleRate % Baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {Baud}", nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _framing = framing;
        _rxFilter = new FirFilter(FilterDesign.LowPass(0.55 * Baud, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate);

        Action<int> bitSink;
        if (framing == Fsk9600Framing.ClassicHdlc)
        {
            var deframer = new HdlcDeframer(frameReceived);
            var descrambler = new G3ruhScrambler();
            var nrzi = new NrziDecoder();
            bitSink = level => deframer.PushBit(nrzi.Decode(descrambler.Descramble(level)));
        }
        else
        {
            var deframer = new Il2pDeframer(
                (frame, _) => frameReceived(frame), crcMode: framing == Fsk9600Framing.Il2pCrc);
            bitSink = deframer.PushBit;
        }

        _dpll = new BitDpll(Baud, sampleRate, bitSink, transitionObserver: _packetDcd.OnTransition);
    }

    /// <inheritdoc />
    public string Mode => _framing switch
    {
        Fsk9600Framing.ClassicHdlc => "fsk9600",
        Fsk9600Framing.Il2pCrc => "fsk9600-il2pc",
        _ => "fsk9600-il2p",
    };

    /// <inheritdoc />
    public bool CarrierDetect => _packetDcd.Asserted;

    /// <inheritdoc />
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _rxFilter.Next(sample);
            _energyBusy.Process(filtered);

            // Envelope-midpoint slicer (as in the AFSK demod): tracks soundcard DC offset
            // and level without assuming a centred signal.
            _peakHigh += (filtered - _peakHigh) * (filtered > _peakHigh ? 0.08f : 0.0002f);
            _peakLow += (filtered - _peakLow) * (filtered < _peakLow ? 0.08f : 0.0002f);
            float excess = filtered - (_peakHigh + _peakLow) * 0.5f;
            int level = excess > 0 ? 1 : 0;

            // NOTE: sub-sample crossing interpolation (a measured win for AFSK/BPSK) is
            // deliberately NOT used here. At 5 samples/bit behind the tight 0.55·baud
            // pulse filter, the crossings carry strong data-dependent ISI offsets, and
            // interpolating them faithfully makes the DPLL chase that jitter into the
            // closed eye for unlucky bit patterns (found by the back-to-back loopback
            // test). Quantised nudges average the ISI out; revisit with matched-filter
            // timing against a real off-air 9600 corpus.
            _previousExcess = excess;
            _dpll.Sample(level);
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wireBits;
        if (_framing == Fsk9600Framing.ClassicHdlc)
        {
            int openingFlags = Math.Max(2, txDelayMilliseconds * Baud / (8 * 1000));
            byte[] hdlcBits = HdlcFramer.FrameBits(ax25Frame, openingFlags, closingFlags: 2);
            var nrzi = new NrziEncoder();
            var scrambler = new G3ruhScrambler();
            wireBits = new byte[hdlcBits.Length];
            for (int i = 0; i < hdlcBits.Length; i++)
            {
                wireBits[i] = (byte)scrambler.Scramble(nrzi.Encode(hdlcBits[i]));
            }
        }
        else
        {
            byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _framing == Fsk9600Framing.Il2pCrc);
            int preambleBits = Math.Max(16, txDelayMilliseconds * Baud / 1000);
            wireBits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        }

        // ±1 NRZ pulse train through the pulse-shaping low-pass ('1' = positive deviation).
        int samplesPerBit = _sampleRate / Baud;
        var shaper = new FirFilter(FilterDesign.LowPass(0.55 * Baud, _sampleRate, 48 * _sampleRate / 48000));
        var samples = new float[wireBits.Length * samplesPerBit];
        int position = 0;
        foreach (byte bit in wireBits)
        {
            float level = bit != 0 ? 0.8f : -0.8f;
            for (int i = 0; i < samplesPerBit; i++)
            {
                samples[position++] = shaper.Next(level);
            }
        }

        return samples;
    }

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
        _peakHigh = 0;
        _peakLow = 0;
        _previousExcess = 0;
    }
}
