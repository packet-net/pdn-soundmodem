using M0LTE.Dsp;
using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Coherent 4-level FSK baseband ("C4FSK") — the NinoTNC modes 1 (19200 bps, 9600 sym/s)
/// and 3 (9600 bps, 4800 sym/s), both IL2P+CRC, added upstream in firmware 3/4.42. Like
/// the GFSK modes this is a direct baseband for an FM radio's 9600 port: a 4-PAM pulse
/// train (levels ±1, ±⅓) carrying two bits per symbol, shaped to 20 kHz / 10 kHz OBW.
/// </summary>
/// <remarks>
/// <para>
/// Wire truth from a real NinoTNC (firmware 3.44, CM108 loop, 2026-07-16): symbol-instant
/// k-means of its mode-3 transmission shows four levels at ratios −0.99/−0.37/+0.42/+1.00
/// with near-equal occupancy, and an outer-pair preamble (+1,+1,−1,−1 repeating). The
/// dibit→level mapping was established by brute force against those recordings — every
/// candidate mapping was run over captured bursts with known content and the IL2P
/// CRC arbitrated; see the C4FSK mapping test.
/// </para>
/// <para>
/// RX follows the proven FskModem chain (LPF → adaptive slicer → shared DPLL →
/// Il2pDeframer), widened to four levels: the outer envelope is tracked the same way and
/// the three slice thresholds sit at 0 and ±⅔ of it. Mode 1 at 48 kHz has only 5 samples
/// per symbol, so it takes the same ×2 interpolation the 9600 GFSK RX needs.
/// </para>
/// </remarks>
public sealed class C4fskModem : IModem
{
    private readonly int _sampleRate;
    private readonly int _symbolRate;
    private readonly bool _crc;
    private readonly FirFilter _rxFilter;
    private readonly Il2pDeframer _deframer;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly int _upsample;
    private readonly double _clockIncrement;
    private double _clockPhase;
    private int _lastSign;
    private bool _previousEnergyBusy;
    private float _peakHigh;
    private float _peakLow;
    private float _previousFiltered;

    /// <summary>
    /// MMDVM-TNC "Mode 2" sync, 4 bytes chosen to be outer-symbol-only: 0x5D 0x57 0xDF
    /// 0x7F. The deframer hunts 24 bits, so it takes the low three bytes; the first byte
    /// acts as extra preamble. Verified symbol-for-symbol against a NinoTNC mode-3
    /// recording (16 outer symbols +,+,−,+, +,+,+,−, −,+,−,−, +,−,−,−).
    /// </summary>
    private const int SyncWord = 0x57DF7F;

    private static readonly byte[] SyncBytes = [0x5D, 0x57, 0xDF, 0x7F];

    /// <summary>Preamble byte, per MMDVM-TNC Mode2Defines: 0x77 = dibits 01 11 01 11 =
    /// +3 −3 +3 −3, a symbol-rate outer alternation.</summary>
    private const byte PreambleByte = 0x77;

    /// <summary>Dibit → PAM level (0 = −outer … 3 = +outer), from MMDVM-TNC Mode2Defines'
    /// sync table as observed on the wire: 01→+3, 00→+1, 10→−1, 11→−3. (Mode2TX.cpp's
    /// writeByte is the opposite polarity; the on-air sense depends on the radio chain,
    /// and the deframer hunts both sync polarities so an inverted path still decodes.)</summary>
    private static readonly int[] DibitToLevel = [2, 3, 1, 0];

    private static readonly int[] LevelToDibit = BuildInverse();

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Sample rate; must be a multiple of the symbol rate
    /// (48000 typical).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="symbolRate">4800 (mode 3, 9600 bps) or 9600 (mode 1, 19200 bps).</param>
    /// <param name="crc">IL2P+CRC (both NinoTNC C4FSK modes run CRC).</param>
    public C4fskModem(int sampleRate, Action<byte[]> frameReceived, int symbolRate = 4800, bool crc = true)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(symbolRate, 0);
        if (sampleRate % symbolRate != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {symbolRate}", nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _symbolRate = symbolRate;
        _crc = crc;
        // 1.5× the symbol rate, NOT the 0.55× the binary FSK modes use: a 4-level eye is
        // three times tighter, and the extra ISI of a tight low-pass on the already
        // Gaussian-shaped signal collapses the inner levels — measured on real NinoTNC
        // recordings as 0/8 at 0.55×, 7-8/8 from 1.0× up. 1.5× keeps some noise rejection.
        _rxFilter = new FirFilter(FilterDesign.LowPass(1.5 * symbolRate, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate);

        _deframer = new Il2pDeframer(
            (frame, info) =>
            {
                frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid));
            },
            crcMode: crc, syncWord: SyncWord);

        _upsample = sampleRate / symbolRate < 8 ? 2 : 1;
        _clockIncrement = (double)symbolRate / (sampleRate * _upsample);
    }

    /// <summary>Creates the 9600 bps mode — NinoTNC mode 3 (4800 sym/s, 10 kHz OBW).</summary>
    public static C4fskModem C4fsk9600(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, 4800);

    /// <summary>Creates the 19200 bps mode — NinoTNC mode 1 (9600 sym/s, 20 kHz OBW).</summary>
    public static C4fskModem C4fsk19200(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, 9600);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"c4fsk{_symbolRate * 2}{(_crc ? "-il2pc" : "-il2p")}";

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

            // No signal, no bits. On silence this slicer saturates to the outer levels
            // (the envelope collapses and the normalised value rails), producing 1-heavy
            // garbage — and the Mode-2 sync is 18/24 ones, so the deframer false-locks
            // continuously between bursts and is mid-garbage-frame when a real sync
            // arrives (measured: ~12k near-sync hits in one recording's silence). The
            // 2-level modes survive the same silence by statistics — balanced garbage
            // rarely matches a balanced sync — but here the bit stream must simply stop
            // when the channel is idle. The energy detector's hold keeps bits flowing
            // through the tail of a burst, so nothing real is lost.
            if (!_energyBusy.Busy)
            {
                // Reset the deframer on the energy-gate falling edge: if it was
                // mid-collection when the carrier stopped, abandon the phantom frame so
                // the next burst's sync word is not consumed as payload.
                if (_previousEnergyBusy)
                {
                    _deframer.Reset();
                }

                _previousEnergyBusy = false;
                continue;
            }

            _previousEnergyBusy = true;

            for (int point = 1; point <= _upsample; point++)
            {
                float value = _previousFiltered + ((filtered - _previousFiltered) * point / _upsample);

                // Track the outer envelope exactly as the 2-level FSK slicer does; the
                // four levels sit at ±1 and ±⅓ of it, so the three thresholds are 0 and
                // ±⅔ of the half-swing around the midpoint.
                // Decay is 20× slower than the binary modes': the envelope's job here is
                // to HOLD the outer reference through inner-heavy scrambled stretches, not
                // to track — at 2e-4 it sagged toward the inner levels mid-frame and the
                // ⅔ threshold ate them (5/8; 7-8/8 at 1e-5, measured on the same bursts).
                _peakHigh += (value - _peakHigh) * (value > _peakHigh ? 0.08f : 0.00001f);
                _peakLow += (value - _peakLow) * (value < _peakLow ? 0.08f : 0.00001f);
                float mid = (_peakHigh + _peakLow) * 0.5f;
                float half = Math.Max((_peakHigh - _peakLow) * 0.5f, 1e-6f);
                float normalised = (value - mid) / half;

                int level = normalised switch
                {
                    < -2f / 3f => 0,
                    < 0f => 1,
                    < 2f / 3f => 2,
                    _ => 3,
                };

                // Symbol clock. The shared BitDpll cannot be used as-is for 4-PAM: it
                // nudges on EVERY level change, and an outer-to-outer transition sweeps
                // through the inner thresholds mid-flight, injecting nudges half a symbol
                // off — measured as total clock collapse (0 frames from a recording that
                // decodes with 1 symbol error in 316 at a fixed phase). Only the middle
                // threshold's crossings — sign changes — land at symbol boundaries, so
                // only they steer the clock; the 4-level decision is sampled at the wrap.
                _clockPhase += _clockIncrement;
                if (_clockPhase >= 0.5)
                {
                    _clockPhase -= 1.0;
                    int dibit = LevelToDibit[level];
                    _deframer.PushBit((dibit >> 1) & 1);
                    _deframer.PushBit(dibit & 1);
                    _packetDcd.OnSymbol();
                }

                int sign = normalised > 0 ? 1 : 0;
                if (sign != _lastSign)
                {
                    _lastSign = sign;
                    _packetDcd.OnTransition(_clockPhase);
                    _clockPhase *= 0.74;   // Dire Wolf's locked inertia, as BitDpll
                }
            }

            _previousFiltered = filtered;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);

        // MMDVM-TNC Mode 2 wire layout: 0x77 preamble bytes for the TXDELAY, the 4-byte
        // outer-only sync, then the IL2P bytes.
        int bitRate = _symbolRate * 2;

        // Floor of ~20 ms of preamble regardless of TXDELAY: the receive side's energy
        // gate takes a block or two to assert from silence, and bits emitted before it
        // opens never reach the deframer. At 2 bytes (8 symbols ≈ 2 ms) the entire
        // preamble died inside that latency — cold acquisition failed at TXDELAY 0 while
        // 20 ms decodes 10/10. The NinoTNC's own C4FSK floor is one 16-bit word, but it
        // has no such gate; ours buys silence immunity (12k false sync locks per
        // recording without it) at the price of this minimum.
        int preambleBytes = Math.Max(_symbolRate / 50 / 4, txDelayMilliseconds * bitRate / 1000 / 8);
        var stream = new byte[preambleBytes + SyncBytes.Length + wire.Length];
        Array.Fill(stream, PreambleByte, 0, preambleBytes);
        SyncBytes.CopyTo(stream, preambleBytes);
        wire.CopyTo(stream, preambleBytes + SyncBytes.Length);

        var bits = new byte[stream.Length * 8];
        for (int i = 0; i < stream.Length; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                bits[(i * 8) + k] = (byte)((stream[i] >> (7 - k)) & 1);
            }
        }

        // Dibits → 4-PAM levels → the same pulse-shaping low-pass the receiver assumes,
        // run past the end to flush the filter's group delay (the FskModem trailer lesson).
        // Same eye physics as receive: 0.55× shaping would hand the far receiver a
        // collapsed 4-level eye. 1.0× approximates the Gaussian BT 0.6 pulse MMDVM-TNC
        // transmits (bench-validated against a real NinoTNC rather than matched exactly).
        int samplesPerSymbol = _sampleRate / _symbolRate;
        int taps = 48 * _sampleRate / 48000;
        var shaper = new FirFilter(FilterDesign.LowPass(1.0 * _symbolRate, _sampleRate, taps));
        ReadOnlySpan<float> amplitudes = [-0.8f, -0.8f / 3f, 0.8f / 3f, 0.8f];
        var samples = new float[(bits.Length / 2 * samplesPerSymbol) + taps];
        int position = 0;
        for (int i = 0; i + 1 < bits.Length; i += 2)
        {
            int dibit = ((bits[i] & 1) << 1) | (bits[i + 1] & 1);
            float amplitude = amplitudes[DibitToLevel[dibit]];
            for (int k = 0; k < samplesPerSymbol; k++)
            {
                samples[position++] = shaper.Next(amplitude);
            }
        }

        while (position < samples.Length)
        {
            samples[position++] = shaper.Next(0f);
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
    }

    private static int[] BuildInverse()
    {
        var inverse = new int[4];
        for (int dibit = 0; dibit < 4; dibit++)
        {
            inverse[DibitToLevel[dibit]] = dibit;
        }

        return inverse;
    }
}
