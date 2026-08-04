using M0LTE.Dsp;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Ota;

/// <summary>Knobs for <see cref="Ms110dIqUpconverter"/>.</summary>
public sealed class Ms110dIqUpconverterOptions
{
    /// <summary>Audio rate in. MS110D is native 9600 Hz with the modem on a 1800 Hz
    /// sub-carrier.</summary>
    public int InputRate { get; init; } = 9600;

    /// <summary>Complex output rate. 24 kHz is the Flex waveform stream's rate.</summary>
    public int OutputRate { get; init; } = 24000;

    /// <summary>
    /// Where the suppressed carrier lands relative to the transmitter's band reference
    /// (<c>FlexTransmitterOptions.FrequencyMHz</c>, where baseband 0 Hz lands on air), in Hz.
    /// </summary>
    /// <remarks>Non-zero by default so the occupied band sits clear of baseband 0 Hz, where
    /// any DC term in our own IQ ends up on air. The radio's LO/carrier leakage (measured at
    /// −27 dBc on a real 6500) lands at the <em>derived slice</em> - the top edge of the
    /// placed band, above the modem's passband - so both artefacts stay outside the occupied
    /// band.</remarks>
    public double OffsetHz { get; init; } = 2000;

    /// <summary>SSB passband edges above the suppressed carrier. The default clears the full
    /// MS110D occupied band (≈180-3420 Hz about the 1800 Hz sub-carrier).</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>Upper passband edge.</summary>
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>Taps in the complex bandpass (odd → linear phase).</summary>
    public int BandpassTaps { get; init; } = 401;

    /// <summary>Peak magnitude of the emitted IQ. Kept near full scale so radiated power is
    /// set by the radio's <c>rfpower</c> rather than by drive level - the two multiply.</summary>
    public double Amplitude { get; init; } = 0.9;

    /// <summary>
    /// Scale the result so its peak is <see cref="Amplitude"/>. True for a single burst, where
    /// the modem's own level is arbitrary and only the dynamic range matters.
    /// </summary>
    /// <remarks><b>Set false for a §E2 ladder.</b> Peak-normalising each point separately would
    /// make the transmitted <em>signal</em> power vary with the injected noise - a low-SNR point
    /// is mostly noise, so normalising its peak turns the signal down. The path contributes its
    /// own noise at a fixed absolute level, so a signal that quietens at the bottom of the ladder
    /// gets a second, uncalibrated dose of noise exactly where the measurement is most delicate.
    /// The ladder therefore takes the natural scale here and applies one gain across the whole
    /// pass, chosen from the worst point.</remarks>
    public bool PeakNormalise { get; init; } = true;
}

/// <summary>
/// Turns MS110D transmit audio into the complex baseband the Flex waveform path transmits:
/// the mirror of the capture tool's <c>SsbDemodulator</c>, and the transmit half of the
/// OTA chain.
/// </summary>
/// <remarks>
/// <para>Chain: resample 9600 → 24000 Hz (×5 then ÷2, both integer), then a complex bandpass
/// keeping only the upper sideband, which is what makes the result analytic. Taking a real
/// audio signal and keeping only its positive frequencies synthesises an ideal SSB
/// transmitter - no SSB filter, no ALC, no uncharacterised TX DSP. That is the point: it
/// removes every impairment except our own, so the first bring-up measures the harness
/// rather than the radio's modulator (ota-execution-plan §T1).</para>
/// <para><b>This shares no code with the receive-side converter, deliberately.</b> A shared
/// filter kernel would cancel its own errors in a loopback - recover the payload perfectly
/// while both ends were wrong together - so the two are independent implementations and the
/// tests check this one against analytic expectations (image rejection, band placement,
/// flatness) as well as against round-trip recovery.</para>
/// </remarks>
public sealed class Ms110dIqUpconverter
{
    private readonly Ms110dIqUpconverterOptions _opt;

    /// <summary>Creates the upconverter.</summary>
    public Ms110dIqUpconverter(Ms110dIqUpconverterOptions? options = null)
    {
        _opt = options ?? new Ms110dIqUpconverterOptions();
        if (_opt.OutputRate % 2 != 0 || _opt.OutputRate * 2 % _opt.InputRate != 0)
        {
            throw new ArgumentException(
                $"no integer path from {_opt.InputRate} Hz to {_opt.OutputRate} Hz", nameof(options));
        }
    }

    /// <summary>The intermediate rate the ×N/÷2 pair passes through.</summary>
    public int IntermediateRate => _opt.OutputRate * 2;

    /// <summary>
    /// Converts real MS110D audio to interleaved <c>I, Q</c> at
    /// <see cref="Ms110dIqUpconverterOptions.OutputRate"/>.
    /// </summary>
    public float[] Convert(ReadOnlySpan<float> audio)
    {
        // 1. Resample to the waveform rate through an integer path: 9600 ×5 → 48000 ÷2 → 24000.
        int up = IntermediateRate / _opt.InputRate;
        var upsampler = new Upsampler(IntermediateRate, up, _opt.BandpassTaps);
        var intermediate = new float[upsampler.OutputLength(audio.Length)];
        upsampler.Process(audio, intermediate);

        var decimator = new Decimator(IntermediateRate, 2, _opt.BandpassTaps);
        var real = new float[decimator.MaxOutput(intermediate.Length)];
        int n = decimator.Process(intermediate, real);

        // 2. Complex bandpass: a real low-pass prototype of half the passband width, modulated
        //    up to the passband centre. Applied to a real signal it keeps only the positive
        //    frequencies - the analytic signal - which is precisely single-sideband.
        //
        //    The filter is centred on where the audio ACTUALLY sits, not where we want it to
        //    end up: a bandpass selects, it does not translate. Centring it on
        //    signal-plus-offset merely cuts most of the signal away, which is what the
        //    spectral tests caught. The move happens in step 3.
        double centre = 0.5 * (_opt.SsbHighHz + _opt.SsbLowHz);
        double halfWidth = 0.5 * (_opt.SsbHighHz - _opt.SsbLowHz);
        float[] proto = FilterDesign.LowPass(halfWidth, _opt.OutputRate, _opt.BandpassTaps);
        int taps = proto.Length;
        int mid = (taps - 1) / 2;
        var hre = new double[taps];
        var him = new double[taps];
        for (int m = 0; m < taps; m++)
        {
            double ph = 2.0 * Math.PI * centre * (m - mid) / _opt.OutputRate;
            hre[m] = proto[m] * Math.Cos(ph);

            // Negated, and this is load-bearing. The convolution below is written as
            // y[k] = sum_m h[m]*x[k-mid+m], which reverses the kernel: substituting u = mid-m
            // gives an effective response centred at MINUS the modulation frequency. Taking
            // the obvious sign here selects the lower sideband instead of the upper, and the
            // signal ends up near DC rather than at the requested offset.
            //
            // Worth recording how this was found: the round-trip test passed regardless,
            // because the receive-side converter uses the same indexing convention and the
            // two inversions cancelled. Only the spectral assertions - measured against where
            // the energy is supposed to be, not against the other converter - caught it. That
            // is the entire reason those tests exist.
            him[m] = -proto[m] * Math.Sin(ph);
        }

        var iq = new float[2 * n];
        double peak = 0;
        for (int k = 0; k < n; k++)
        {
            double accRe = 0, accIm = 0;
            int baseIdx = k - mid;
            int mLo = Math.Max(0, -baseIdx);
            int mHi = Math.Min(taps, n - baseIdx);
            for (int m = mLo; m < mHi; m++)
            {
                float x = real[baseIdx + m];
                accRe += hre[m] * x;
                accIm += him[m] * x;
            }

            iq[2 * k] = (float)accRe;
            iq[(2 * k) + 1] = (float)accIm;
            peak = Math.Max(peak, (accRe * accRe) + (accIm * accIm));
        }

        // 3. NCO: translate the now-analytic signal to its transmit offset. Multiplying a
        //    single-sideband signal by e^{j2*pi*offset*t} slides it bodily up or down the
        //    band without creating an image, which is the whole reason for making it analytic
        //    before moving it.
        if (Math.Abs(_opt.OffsetHz) > 1e-9)
        {
            double dPhi = 2.0 * Math.PI * _opt.OffsetHz / _opt.OutputRate;
            double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
            double pr = 1.0, pi = 0.0;
            peak = 0;
            for (int k = 0; k < n; k++)
            {
                double re = iq[2 * k], im = iq[(2 * k) + 1];
                double outRe = (re * pr) - (im * pi);
                double outIm = (re * pi) + (im * pr);
                iq[2 * k] = (float)outRe;
                iq[(2 * k) + 1] = (float)outIm;
                peak = Math.Max(peak, (outRe * outRe) + (outIm * outIm));

                double npr = (pr * wr) - (pi * wi);
                pi = (pr * wi) + (pi * wr);
                pr = npr;
                if ((k & 0x3FFF) == 0x3FFF) // renormalise against accumulated drift
                {
                    double m = Math.Sqrt((pr * pr) + (pi * pi));
                    pr /= m;
                    pi /= m;
                }
            }
        }

        // 4. Scale to the requested peak. The modem's own level is arbitrary and the
        //    demodulator equalises it, so the only thing that matters here is using the
        //    waveform's dynamic range without clipping. A ladder turns this off and scales the
        //    whole pass by one gain instead - see PeakNormalise.
        peak = Math.Sqrt(peak);
        if (_opt.PeakNormalise && peak > 1e-12)
        {
            float g = (float)(_opt.Amplitude / peak);
            for (int k = 0; k < iq.Length; k++)
            {
                iq[k] *= g;
            }
        }

        return iq;
    }
}
