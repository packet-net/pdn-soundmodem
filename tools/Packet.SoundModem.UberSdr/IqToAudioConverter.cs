using M0LTE.Dsp;

namespace Packet.SoundModem.UberSdr;

/// <summary>Knobs for <see cref="IqToAudioConverter"/>.</summary>
public sealed class IqToAudioOptions
{
    /// <summary>IQ sample rate of the capture (iq48 = 48000).</summary>
    public int InputRate { get; init; } = 48000;

    /// <summary>Output audio rate — MS110D native is 9600. Must divide <see cref="InputRate"/>.</summary>
    public int OutputRate { get; init; } = 9600;

    /// <summary>Frequency, within the IQ baseband, of the SSB suppressed carrier (the dial). The
    /// USB band lives at DialHz + [SsbLowHz, SsbHighHz]; set this to (our TX dial − the RX tune
    /// frequency) so the modem lands where the demod expects it. 0 when the RX is tuned exactly
    /// to our dial.</summary>
    public double DialHz { get; init; }

    /// <summary>SSB passband edges above the suppressed carrier. Default 150–3450 Hz clears the
    /// full MS110D occupied band (≈180–3420 Hz about the 1800 Hz carrier — the reference,
    /// RX-truncation-free case); set e.g. 300–2700 to emulate a narrower USB filter for comparison.</summary>
    public double SsbLowHz { get; init; } = 150;
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>Taps in the complex SSB bandpass (odd → linear phase).</summary>
    public int BandpassTaps { get; init; } = 301;

    /// <summary>Peak-normalise the output to this magnitude (0 disables). The demod equalises
    /// level, so this is only cosmetic / clipping insurance.</summary>
    public float NormalisePeak { get; init; } = 0.7f;
}

/// <summary>
/// Turns a captured IQ48 stream (complex baseband centred on the RX tune frequency) into the
/// real audio the MS110D demodulator consumes (9600 Hz, modem on the 1800 Hz sub-carrier). This
/// is the offline SSB demodulator the OTA plan (C2) calls for: because we hold the complex
/// baseband, <em>we</em> choose the passband, so RX-side filter truncation is never a confound.
///
/// <para>Chain: shift by −DialHz (suppressed carrier → 0 Hz) → complex bandpass keeping only the
/// upper sideband +[SsbLow,SsbHigh] (rejecting the LSB image / adjacents / noise on the negative
/// side) → take the real part → decimate to the output rate.</para>
/// </summary>
public sealed class IqToAudioConverter
{
    private readonly IqToAudioOptions _opt;

    public IqToAudioConverter(IqToAudioOptions? options = null) => _opt = options ?? new IqToAudioOptions();

    /// <summary>Convert interleaved 16-bit stereo IQ (I,Q,I,Q…) as read from a capture WAV.</summary>
    public float[] Convert(ReadOnlySpan<short> interleaved)
    {
        int n = interleaved.Length / 2;
        var i = new double[n];
        var q = new double[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = interleaved[2 * k] / 32768.0;
            q[k] = interleaved[2 * k + 1] / 32768.0;
        }
        return Convert(i, q);
    }

    /// <summary>Convert separate I/Q sample arrays (normalised, ~[−1,1]).</summary>
    public float[] Convert(ReadOnlySpan<double> iIn, ReadOnlySpan<double> qIn)
    {
        int n = iIn.Length;
        if (qIn.Length != n)
        {
            throw new ArgumentException("I and Q must be the same length");
        }
        if (_opt.InputRate % _opt.OutputRate != 0)
        {
            throw new ArgumentException($"InputRate {_opt.InputRate} must be a multiple of OutputRate {_opt.OutputRate}");
        }

        // 1. NCO: shift by −DialHz so the suppressed carrier sits at 0 Hz (incremental phasor).
        var reS = new double[n];
        var imS = new double[n];
        double dPhi = -2.0 * Math.PI * _opt.DialHz / _opt.InputRate;
        double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
        double pr = 1.0, pi = 0.0;
        for (int k = 0; k < n; k++)
        {
            reS[k] = iIn[k] * pr - qIn[k] * pi;
            imS[k] = iIn[k] * pi + qIn[k] * pr;
            double npr = pr * wr - pi * wi;
            pi = pr * wi + pi * wr;
            pr = npr;
            if ((k & 0x3FFF) == 0x3FFF) // renormalise the phasor periodically against drift
            {
                double m = Math.Sqrt(pr * pr + pi * pi);
                pr /= m;
                pi /= m;
            }
        }

        // 2. Complex SSB bandpass = real low-pass prototype (half the passband width) modulated up
        //    to the passband centre, so it keeps ONLY the upper sideband.
        double centre = 0.5 * (_opt.SsbHighHz + _opt.SsbLowHz);
        double halfWidth = 0.5 * (_opt.SsbHighHz - _opt.SsbLowHz);
        float[] proto = FilterDesign.LowPass(halfWidth, _opt.InputRate, _opt.BandpassTaps);
        int taps = proto.Length;
        int mid = (taps - 1) / 2;
        var hre = new double[taps];
        var him = new double[taps];
        for (int m = 0; m < taps; m++)
        {
            double ph = 2.0 * Math.PI * centre * (m - mid) / _opt.InputRate;
            hre[m] = proto[m] * Math.Cos(ph);

            // Negated: the convolution below is written y[k] = sum_m h[m]*x[k-mid+m], which
            // reverses the kernel and so realises a response centred at MINUS the modulation
            // frequency. Without this the filter selects the LOWER sideband while claiming to
            // keep the upper one. It went unnoticed because the loopback test synthesised its
            // input with the same convention, and the one real capture it was tried on was
            // only checked for "energy in the SSB band" — which an inverted selection can
            // still satisfy. The transmit-side upconverter, whose band placement is asserted
            // against absolute frequencies, is what exposed it.
            him[m] = -proto[m] * Math.Sin(ph);
        }

        // Convolve (complex), keep the real part only. Linear-phase filter → compensate its
        // group delay of `mid` samples so output aligns with input time.
        var real = new double[n];
        for (int k = 0; k < n; k++)
        {
            double acc = 0.0; // real part of sum_m h[m] * x[k - mid + m]
            int baseIdx = k - mid;
            int mLo = Math.Max(0, -baseIdx);
            int mHi = Math.Min(taps, n - baseIdx);
            for (int m = mLo; m < mHi; m++)
            {
                int xi = baseIdx + m;
                acc += hre[m] * reS[xi] - him[m] * imS[xi];
            }
            real[k] = acc;
        }

        // 3. Anti-alias low-pass then decimate to the output rate.
        int factor = _opt.InputRate / _opt.OutputRate;
        var lp = new FirFilter(FilterDesign.LowPass(0.45 * _opt.OutputRate, _opt.InputRate, 8 * factor + 1));
        int outN = n / factor;
        var outp = new float[outN];
        int oi = 0;
        for (int k = 0; k < n; k++)
        {
            float y = lp.Next((float)real[k]);
            if (k % factor == 0 && oi < outN)
            {
                outp[oi++] = y;
            }
        }

        if (_opt.NormalisePeak > 0)
        {
            float peak = 0f;
            for (int k = 0; k < oi; k++)
            {
                peak = Math.Max(peak, Math.Abs(outp[k]));
            }
            if (peak > 1e-9f)
            {
                float g = _opt.NormalisePeak / peak;
                for (int k = 0; k < oi; k++)
                {
                    outp[k] *= g;
                }
            }
        }

        return outp;
    }
}
