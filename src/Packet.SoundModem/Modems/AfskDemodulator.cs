using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// AFSK demodulator following the UZ7HO SoundModem chain (QtSoundModem ax25_demod.c,
/// Mux3/decode_stream_FSK): input band-pass filter → complex mix to baseband at the
/// channel centre → I/Q low-pass → differentiate-and-cross-multiply FM discriminator →
/// DC-tracking slicer → DPLL bit clock. Emits line levels once per bit; chain through
/// <see cref="Hdlc.NrziDecoder"/> + <see cref="Hdlc.HdlcDeframer"/> for AX.25, or
/// straight into <see cref="M0LTE.Il2p.Il2pDeframer"/> for IL2P.
/// </summary>
/// <remarks>
/// The discriminator is plain quadrature FM, so it does not care what the tone shift is —
/// only the filters and the bit clock are per-mode. Covers Bell 202 VHF tones (1200 baud,
/// 1200/2200 Hz — NinoTNC modes 6/7) and Nino's HF tones (300 baud, 1600/1800 Hz — modes
/// 12/13/14).
/// </remarks>
public sealed class AfskDemodulator
{
    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;

    private float _i0, _i1, _i2;
    private float _q0, _q1, _q2;
    private readonly float _envelopeAttack;
    private readonly float _envelopeDecay;
    /// <summary>Diagnostic tap for bench tooling: (discriminator, peakHigh, peakLow,
    /// power) per sample point. Not for production paths.</summary>
    internal Action<float, float, float, float>? DiagnosticTap { get; set; }
    private readonly float _discriminatorLimit;
    private float _peakHigh;
    private float _peakLow;
    private float _previousExcess;

    /// <summary>Creates a demodulator delivering NRZI line levels (one per bit) to
    /// <paramref name="bitSink"/>.</summary>
    /// <param name="sampleRate">Input sample rate; the QtSoundModem-style filters are
    /// designed for 12000 Hz but scale with the rate.</param>
    /// <param name="bitSink">Receives the recovered line level at each bit instant.</param>
    /// <param name="centerFrequency">Channel centre (midpoint of mark/space), 1700 Hz for
    /// both the Bell 202 tones and Nino's HF tones.</param>
    /// <param name="baud">Bit rate: 1200 (Bell 202) or 300 (HF).</param>
    /// <param name="bandPassHalfWidth">Half-width of the input band-pass around the
    /// centre. Per-mode rather than derived — QtSoundModem carries per-mode filter tables
    /// for the same reason. 700 Hz is UZ7HOStuff.h's MODEM_1200 value; 250 Hz matches the
    /// 500 Hz OBW Nino filters the HF modes to.</param>
    /// <param name="lowPassCutoff">I/Q low-pass cutoff: must pass the tones (±shift) plus
    /// the modulation, and nothing else. 650 Hz is MODEM_1200's, and it embodies a real
    /// trade measured on both sides: a wider 750 Hz filter settles faster and takes a
    /// NinoTNC's shortest flag fill (TXDELAY 20, one word) from 6/10 to 10/10 — but costs
    /// weak-signal margin, dropping WA8LMF Track 2 from 472 to 410. The default backs the
    /// weak-signal case: short-fill peers are a rare configuration (fills of ≥100 ms
    /// decode 10/10 either way), weak signals are the daily reality. A port that knows its
    /// peer runs a one-word fill can pass 750 here and accept the noise cost.</param>
    /// <param name="bandPassTaps">Band-pass length at 12 kHz (scaled with the rate). A FIR
    /// this long has a transition width of roughly 3.3·rate/taps, so a narrow filter needs
    /// a long one: 256 suits the wide Bell 202 band-pass, the 300 baud modes want ~4x
    /// that or the "filter" is mostly transition band.</param>
    /// <param name="lowPassTaps">I/Q low-pass length at 12 kHz (scaled with the rate).</param>
    /// <param name="toneShift">Deviation of each tone from the centre — 500 Hz for Bell
    /// 202 (1200/2200), 100 Hz for the HF tones (1600/1800). Sets the discriminator's
    /// legitimate output range, and so the clamp that keeps silence from deafening the
    /// slicer.</param>
    public AfskDemodulator(
        int sampleRate, Action<int> bitSink, double centerFrequency = 1700, int baud = 1200,
        double bandPassHalfWidth = 700, double lowPassCutoff = 650,
        int bandPassTaps = 256, int lowPassTaps = 128, double toneShift = 500)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        _bandPass = new FirFilter(FilterDesign.BandPass(
            centerFrequency - bandPassHalfWidth, centerFrequency + bandPassHalfWidth,
            sampleRate, bandPassTaps * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(lowPassCutoff, sampleRate, lowPassTaps * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(lowPassCutoff, sampleRate, lowPassTaps * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * centerFrequency / sampleRate;
        _dpll = new BitDpll(baud, sampleRate, bitSink, transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
        _energyBusy = new EnergyBusyDetector(sampleRate);

        // The envelope tracker below runs per sample, but what it must keep up with is the
        // eye, which is per bit. Scale its rates so every mode tracks at the same rate per
        // bit as the proven Bell 202 case (10 samples/bit at 12 kHz) — without this, 300
        // baud (40 samples/bit) decays the opposite peak ~4x too fast between transitions
        // and drags the slice point off centre.
        float rateScale = 10f / (sampleRate / (float)baud);
        _envelopeAttack = 0.08f * rateScale;
        _envelopeDecay = 0.0008f * rateScale;

        // Full deviation puts the discriminator at sin(2·2π·shift/rate) — everything
        // beyond that is not a frequency this mode can carry. 1.4x leaves headroom for
        // filter overshoot at transitions. See the clamp in Process for why this must
        // track the mode rather than be a constant.
        _discriminatorLimit = 1.4f * (float)Math.Sin(2 * 2 * Math.PI * toneShift / sampleRate);
    }

    /// <summary>True while DPLL transition timing indicates a coherent packet signal.</summary>
    public bool CarrierDetect => _packetDcd.Asserted;

    /// <summary>Channel-busy for carrier sense: packet DCD or any significant in-band
    /// energy (a carrier, voice, another mode).</summary>
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <summary>Clears carrier state, e.g. while the channel's own transmitter is keyed.</summary>
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _bandPass.Next(sample);
            _energyBusy.Process(filtered);

            _oscillatorPhase += _oscillatorStep;
            if (_oscillatorPhase > 2 * Math.PI)
            {
                _oscillatorPhase -= 2 * Math.PI;
            }

            float i = _lowPassI.Next(filtered * (float)Math.Sin(_oscillatorPhase));
            float q = _lowPassQ.Next(filtered * (float)Math.Cos(_oscillatorPhase));

            _i2 = _i1; _i1 = _i0; _i0 = i;
            _q2 = _q1; _q1 = _q0; _q0 = q;

            // Quadrature FM discriminator (QtSoundModem Mux3): instantaneous frequency
            // relative to the centre, sign selects mark vs space. Normalising by the
            // instantaneous power stands in for QtSoundModem's AGC stage — without it the
            // output scales with amplitude², and band-edge attenuation of the 1200 Hz mark
            // tone skews the slicer duty cycle badly.
            float power = _i1 * _i1 + _q1 * _q1;

            // The normalisation floor is NOT a numerical nicety — it sets what happens
            // when there is no signal. With a 1e-12 floor, the leading edge of a burst
            // (the ~19 bits it takes the 256+128-tap filters to fill) divides near-zero
            // by near-zero and emits full-scale garbage that the envelope trackers then
            // train on: observed slice midpoint 0.65 against a real eye of [0.2, 0.65],
            // which is why bare-HDLC AFSK needed ~100 ms of preamble while IL2P (which
            // sync-hunts past the damage) ran at 16 bits. At 1e-5 — about -50 dB below
            // nominal in-band power (~0.15 here) — sub-signal inputs produce sub-eye
            // output instead: attenuated, honest, and ignorable. Real signal is barely
            // touched (>= 90 % of full output from -40 dBFS up), and channel noise on
            // real captures (WA8LMF floor) sits far above it, so steady-state behaviour
            // is unchanged.
            float discriminator = ((_i0 - _i2) * _q1 - (_q0 - _q2) * _i1) / (power + 1e-5f);

            // Clamp to what this mode can physically produce: full deviation puts the
            // discriminator at sin(2·2π·shift/rate), and no real frequency it carries goes
            // beyond that. The limit has to track the tone shift — a fixed ±1 is only ~2x
            // the legitimate ±0.5 of Bell 202's ±500 Hz, but 10x the ±0.105 of the ±100 Hz
            // HF modes.
            discriminator = Math.Clamp(discriminator, -_discriminatorLimit, _discriminatorLimit);

            // NEGATIVE RESULT — do not re-add a silence squelch here. Zeroing the
            // discriminator when in-band power falls below a floor is intuitive (it stops
            // the trackers learning the garbage the normalisation makes out of near-zero
            // power) but measured worthless once the clamp above is correct: WA8LMF Track
            // 2 at 12 kHz scored 269 unclamped, 426 clamped, 270 squelched-only and 427
            // with both — the clamp is the entire win. An earlier variant that gated on
            // power *relative* to a tracked peak was actively catastrophic (972 → 65),
            // because that track's whole point is dynamic range: one loud frame parks the
            // tracker and squelches every weaker frame after it.

            // Envelope-based slicer threshold (fast attack, slow decay), midway between the
            // mark and space extremes. A mean tracker fails here: an HDLC flag preamble is
            // 87.5 % mark (seven hold bits per flag), which drags a mean far off centre —
            // QtSoundModem's adaptive min/max thresholds exist for the same reason.
            //
            // NEGATIVE RESULT — do not add a cold-start "warm-up" that runs both legs at
            // attack speed. It was tried for fast acquisition and it converts this min/max
            // tracker into a mean-follower: during a flag's six-mark run both peaks chase
            // the mark level and the midpoint loses all discrimination — the exact failure
            // the asymmetric rates exist to avoid. With the normalisation floor above
            // keeping filter-fill garbage out of the trackers, cold start needs no help:
            // from zero, the midpoint sits at half the first tone's level, which the
            // ±full-swing eye clears comfortably.
            _peakHigh += (discriminator - _peakHigh)
                * (discriminator > _peakHigh ? _envelopeAttack : _envelopeDecay);
            _peakLow += (discriminator - _peakLow)
                * (discriminator < _peakLow ? _envelopeAttack : _envelopeDecay);
            float excess = discriminator - (_peakHigh + _peakLow) * 0.5f;
            int level = excess > 0 ? 1 : 0;

            // Sub-sample transition timing: linear zero-crossing interpolation of the
            // slicer input between the previous and current sample.
            double crossing = 0;
            if ((excess > 0) != (_previousExcess > 0) && excess != _previousExcess)
            {
                crossing = Math.Clamp(excess / (double)(excess - _previousExcess), 0, 0.999);
            }

            _previousExcess = excess;
            DiagnosticTap?.Invoke(discriminator, _peakHigh, _peakLow, power);
            _dpll.Sample(level, crossing);
        }
    }
}
