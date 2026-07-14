using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Bell 202 AFSK demodulator following the UZ7HO SoundModem chain (QtSoundModem
/// ax25_demod.c, Mux3/decode_stream_FSK): input band-pass filter → complex mix to
/// baseband at the channel centre → I/Q low-pass → differentiate-and-cross-multiply FM
/// discriminator → DC-tracking slicer → DPLL bit clock. Emits NRZI line levels once per
/// bit; chain through <see cref="Hdlc.NrziDecoder"/> and <see cref="Hdlc.HdlcDeframer"/>.
/// </summary>
public sealed class Afsk1200Demodulator
{
    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;

    private float _i0, _i1, _i2;
    private float _q0, _q1, _q2;
    private float _peakHigh;
    private float _peakLow;

    /// <summary>Creates a demodulator delivering NRZI line levels (one per bit) to
    /// <paramref name="bitSink"/>.</summary>
    /// <param name="sampleRate">Input sample rate; the QtSoundModem-style filters are
    /// designed for 12000 Hz but scale with the rate.</param>
    /// <param name="bitSink">Receives the recovered line level at each bit instant.</param>
    /// <param name="centerFrequency">Channel centre (midpoint of mark/space), 1700 Hz for
    /// standard tones.</param>
    public Afsk1200Demodulator(int sampleRate, Action<int> bitSink, double centerFrequency = 1700)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        // QtSoundModem's 1200-baud filter set: 1400 Hz-wide band-pass around the centre,
        // 650 Hz I/Q low-pass (UZ7HOStuff.h MODEM_1200 constants), 256/128 taps at 12 kHz.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            centerFrequency - 700, centerFrequency + 700, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(650, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(650, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * centerFrequency / sampleRate;
        _dpll = new BitDpll(1200, sampleRate, bitSink);
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _bandPass.Next(sample);

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
            float discriminator = ((_i0 - _i2) * _q1 - (_q0 - _q2) * _i1) / (power + 1e-12f);

            // During silence the normalisation divides noise by near-zero power, producing
            // huge garbage that would blow up the envelope thresholds below and deafen the
            // slicer for the next transmission. The legitimate range is set by the tone
            // deviation (|disc| ≈ 2π·500/rate·2 ≪ 1), so clamp hard.
            discriminator = Math.Clamp(discriminator, -1f, 1f);

            // Envelope-based slicer threshold (fast attack, slow decay), midway between the
            // mark and space extremes. A mean tracker fails here: an HDLC flag preamble is
            // 87.5 % mark (seven hold bits per flag), which drags a mean far off centre —
            // QtSoundModem's adaptive min/max thresholds exist for the same reason.
            _peakHigh += (discriminator - _peakHigh) * (discriminator > _peakHigh ? 0.08f : 0.0008f);
            _peakLow += (discriminator - _peakLow) * (discriminator < _peakLow ? 0.08f : 0.0008f);
            int level = discriminator > (_peakHigh + _peakLow) * 0.5f ? 1 : 0;

            _dpll.Sample(level);
        }
    }
}
