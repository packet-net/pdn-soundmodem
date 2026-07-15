using Packet.SoundModem.Dsp;
using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>Framing carried over the direct-FSK baseband.</summary>
public enum FskFraming
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
/// Direct baseband FSK ("RUH") modem, Dire Wolf demod_9600 lineage: the receive chain is
/// a low-pass filter, an envelope-tracking slicer and the shared DPLL; transmit shapes a
/// ±1 NRZ pulse train through the same low-pass design. Runs at 48 kHz (5 samples per bit
/// at 9600, 10 at 4800). Framing per <see cref="FskFraming"/>. Covers the NinoTNC 9600
/// GFSK (modes 0 AX.25 / 2 IL2P+CRC) and 4800 GFSK (mode 4, IL2P+CRC) modes; the classic
/// and IL2P legs are cross-validated against Dire Wolf audio and bench-proven against a
/// NinoTNC.
/// </summary>
public sealed class FskModem : IModem
{
    private readonly int _baud;
    private readonly int _sampleRate;
    private readonly FskFraming _framing;
    private readonly FirFilter _rxFilter;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly int _upsample;
    private float _peakHigh;
    private float _peakLow;
    private float _previousFiltered;
    private float _previousExcess;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Sample rate; must be a multiple of <paramref name="baud"/>
    /// (48000 typical).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="framing">Wire framing (classic G3RUH vs IL2P).</param>
    /// <param name="baud">Baseband symbol rate: 9600 (modes 0/2) or 4800 (mode 4).</param>
    public FskModem(int sampleRate, Action<byte[]> frameReceived, FskFraming framing, int baud = 9600)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        _baud = baud;
        _sampleRate = sampleRate;
        _framing = framing;
        _rxFilter = new FirFilter(FilterDesign.LowPass(0.55 * baud, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate);

        Action<int> bitSink;
        if (framing == FskFraming.ClassicHdlc)
        {
            var deframer = new HdlcDeframer(frameReceived);
            var descrambler = new G3ruhScrambler();
            var nrzi = new NrziDecoder();
            bitSink = level => deframer.PushBit(nrzi.Decode(descrambler.Descramble(level)));
        }
        else
        {
            var deframer = new Il2pDeframer(
                (frame, _) => frameReceived(frame), crcMode: framing == FskFraming.Il2pCrc);
            bitSink = deframer.PushBit;
        }

        // At 48 kHz there are only 5 samples per bit at 9600 — each quantised DPLL nudge
        // is ±10% of a bit. Dire Wolf's demod_9600 interpolates ×2 before its PLL for the
        // same reason ("upsample" in demod_9600.c); do likewise so timing corrections
        // land on a 10-points-per-bit grid. 4800 already has 10 samples/bit at 48 kHz, so
        // it needs no interpolation.
        _upsample = sampleRate / baud < 8 ? 2 : 1;
        _dpll = new BitDpll(
            baud, sampleRate * _upsample, bitSink, transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
    }

    /// <summary>Creates the 9600 baud mode — NinoTNC mode 0 (classic AX.25) or 2
    /// (IL2P+CRC), 20 kHz OBW.</summary>
    public static FskModem Fsk9600(int sampleRate, Action<byte[]> frameReceived, FskFraming framing) =>
        new(sampleRate, frameReceived, framing, 9600);

    /// <summary>Creates the 4800 baud mode — NinoTNC mode 4 (IL2P+CRC), 10 kHz OBW.</summary>
    public static FskModem Fsk4800(
        int sampleRate, Action<byte[]> frameReceived, FskFraming framing = FskFraming.Il2pCrc) =>
        new(sampleRate, frameReceived, framing, 4800);

    /// <inheritdoc />
    public string Mode => _framing switch
    {
        FskFraming.ClassicHdlc => $"fsk{_baud}",
        FskFraming.Il2pCrc => $"fsk{_baud}-il2pc",
        _ => $"fsk{_baud}-il2p",
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

            for (int point = 1; point <= _upsample; point++)
            {
                // Linear interpolation between successive filtered samples (point ==
                // _upsample is the sample itself) — see the _upsample ctor note.
                float value = _previousFiltered
                    + (filtered - _previousFiltered) * point / _upsample;

                // Envelope-midpoint slicer (as in the AFSK demod): tracks soundcard DC
                // offset and level without assuming a centred signal.
                _peakHigh += (value - _peakHigh) * (value > _peakHigh ? 0.08f : 0.0002f);
                _peakLow += (value - _peakLow) * (value < _peakLow ? 0.08f : 0.0002f);
                float excess = value - (_peakHigh + _peakLow) * 0.5f;
                int level = excess > 0 ? 1 : 0;

                // NOTE: sub-sample crossing interpolation (a measured win for AFSK/BPSK)
                // is deliberately NOT used here. At 5 samples/bit behind the tight
                // 0.55·baud pulse filter, the crossings carry strong data-dependent ISI
                // offsets, and interpolating them faithfully makes the DPLL chase that
                // jitter into the closed eye for unlucky bit patterns (found by the
                // back-to-back loopback test). Quantised nudges average the ISI out;
                // revisit with matched-filter timing against a real off-air 9600 corpus.
                _previousExcess = excess;
                _dpll.Sample(level);
            }

            _previousFiltered = filtered;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wireBits;
        if (_framing == FskFraming.ClassicHdlc)
        {
            int openingFlags = Math.Max(2, txDelayMilliseconds * _baud / (8 * 1000));
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
            byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _framing == FskFraming.Il2pCrc);
            int preambleBits = Math.Max(16, txDelayMilliseconds * _baud / 1000);
            wireBits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        }

        // ±1 NRZ pulse train through the pulse-shaping low-pass ('1' = positive deviation).
        int samplesPerBit = _sampleRate / _baud;
        var shaper = new FirFilter(FilterDesign.LowPass(0.55 * _baud, _sampleRate, 48 * _sampleRate / 48000));
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
        _previousFiltered = 0;
        _previousExcess = 0;
    }
}
