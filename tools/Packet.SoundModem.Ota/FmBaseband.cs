using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>Knobs for <see cref="FmModulator"/>.</summary>
public sealed class FmModulatorOptions
{
    /// <summary>Audio rate in - the mode's DSP rate (12000 or 48000).</summary>
    public int InputRate { get; init; } = 48000;

    /// <summary>Complex baseband rate out. Must be an integer multiple of <see cref="InputRate"/>
    /// (the modulator upsamples the audio first), and wide enough for the FM signal's Carson
    /// bandwidth - the noise lead-in's deviation excursions run well past the signal's, and if the
    /// rate is too low they alias. 48 kHz clears every catalogue FM mode at its target deviation;
    /// the RSP1 captures at 96 kHz.</summary>
    public int OutputRate { get; init; } = 48000;

    /// <summary>
    /// The peak frequency deviation, in Hz, that <see cref="ReferenceAmplitude"/> of audio maps to.
    /// This is the mode's <b>Tgt Dev</b> from <c>docs/mode-modulation-reference.md</c> - the whole
    /// point of the FM path is that the drive is calibrated so the achieved peak deviation of the
    /// signal is this value, not the channel spacing.
    /// </summary>
    public double PeakDeviationHz { get; init; } = 2400;

    /// <summary>The audio amplitude that maps to <see cref="PeakDeviationHz"/>. The FM ladder sets
    /// this to the worst point's clean-signal peak so the <em>signal</em> deviates to exactly the
    /// target and the injected noise rides on top - the FM counterpart of the SSB ladder's one
    /// gain per pass. 1.0 for a stand-alone modulate-a-normalised-tone round trip.</summary>
    public double ReferenceAmplitude { get; init; } = 1.0;

    /// <summary>Constant-envelope IQ magnitude. FM carries no amplitude information, so this is
    /// only headroom against the capture's s16 clamp; the demodulator ignores it entirely.</summary>
    public double Amplitude { get; init; } = 0.9;

    /// <summary>Taps in the audio upsampler's anti-image filter (odd → linear phase).</summary>
    public int ResampleTaps { get; init; } = 401;
}

/// <summary>
/// A software FM modulator: real audio in, complex FM baseband out, at a <b>settable peak
/// deviation</b>. The dry-run counterpart of the radio's FM modulator, and the transmit half of
/// the FM OTA chain - it lets the whole FM modem chain (render → FM-mod at Tgt Dev → FM-demod →
/// decode) be proved offline before any power is applied.
/// </summary>
/// <remarks>
/// <para>Chain: upsample the audio to <see cref="FmModulatorOptions.OutputRate"/> through an
/// integer path (so the FM signal's Carson bandwidth is not aliased), then integrate it into an
/// instantaneous phase and emit <c>Amplitude·e^{jφ}</c>. The phase increment per output sample is
/// <c>2π·kf·a[n]/fs</c> with <c>kf = PeakDeviationHz / ReferenceAmplitude</c> Hz per unit
/// amplitude, so audio at <see cref="FmModulatorOptions.ReferenceAmplitude"/> deviates the carrier
/// by exactly <see cref="FmModulatorOptions.PeakDeviationHz"/>.</para>
/// <para><b>Mod then discriminate is an identity on the audio</b> (up to filtering) when no RF
/// noise is added - the phase difference the discriminator forms is exactly the phase increment
/// this modulator accumulated. That is what makes the FM ladder self-calibrating: the noise is
/// injected as audio <em>before</em> modulation (exactly as the on-air ladder transmits its noise
/// lead-in through the radio's FM modulator), rides through the mod/demod round trip unchanged, and
/// the scorer recovers the delivered SNR from it.</para>
/// </remarks>
public sealed class FmModulator
{
    private readonly FmModulatorOptions _opt;

    public FmModulator(FmModulatorOptions? options = null)
    {
        _opt = options ?? new FmModulatorOptions();
        if (_opt.InputRate <= 0 || _opt.OutputRate <= 0 || _opt.OutputRate % _opt.InputRate != 0)
        {
            throw new ArgumentException(
                $"OutputRate ({_opt.OutputRate}) must be a positive integer multiple of InputRate "
                + $"({_opt.InputRate})", nameof(options));
        }

        if (_opt.ReferenceAmplitude <= 0)
        {
            throw new ArgumentException("ReferenceAmplitude must be positive", nameof(options));
        }
    }

    /// <summary>Hz of deviation per unit of input audio amplitude - <c>PeakDeviationHz /
    /// ReferenceAmplitude</c>, the constant the phase accumulator uses.</summary>
    public double DeviationGainHzPerUnit => _opt.PeakDeviationHz / _opt.ReferenceAmplitude;

    /// <summary>Converts real audio to interleaved <c>I, Q</c> at
    /// <see cref="FmModulatorOptions.OutputRate"/>.</summary>
    public float[] Modulate(ReadOnlySpan<float> audio)
    {
        // 1. Upsample the audio to the baseband rate so the FM spectrum has room. A ×1 factor
        //    (audio already at the output rate - every 48 kHz mode) needs no filtering at all.
        int factor = _opt.OutputRate / _opt.InputRate;
        float[] wide;
        if (factor == 1)
        {
            wide = audio.ToArray();
        }
        else
        {
            var up = new Upsampler(_opt.OutputRate, factor, _opt.ResampleTaps);
            wide = new float[up.OutputLength(audio.Length)];
            up.Process(audio, wide);
        }

        // 2. Integrate audio → phase, emit the constant-envelope phasor. The per-sample phase step
        //    is 2π·kf·a/fs, so the instantaneous frequency is exactly kf·a and the peak deviation of
        //    audio at the reference amplitude is PeakDeviationHz. The discriminator forms this same
        //    difference back, which is why the round trip is an identity absent RF noise.
        double kf = DeviationGainHzPerUnit;
        double step = 2.0 * Math.PI * kf / _opt.OutputRate;
        double amp = _opt.Amplitude;
        var iq = new float[2 * wide.Length];
        double phi = 0.0;
        for (int n = 0; n < wide.Length; n++)
        {
            phi += step * wide[n];
            // Keep the phase bounded so cos/sin stay accurate over long bursts.
            if (phi > Math.PI)
            {
                phi -= 2.0 * Math.PI;
            }
            else if (phi < -Math.PI)
            {
                phi += 2.0 * Math.PI;
            }

            iq[2 * n] = (float)(amp * Math.Cos(phi));
            iq[(2 * n) + 1] = (float)(amp * Math.Sin(phi));
        }

        return iq;
    }
}

/// <summary>A measured FM deviation, in Hz.</summary>
/// <param name="PeakHz">Peak instantaneous-frequency excursion - the number that must match the
/// mode's Tgt Dev after calibration.</param>
/// <param name="RmsHz">RMS deviation over the window (the modulating waveform's shape shows here -
/// a two-tone AFSK signal has a lower RMS/peak ratio than white noise).</param>
/// <param name="MeanHz">Mean instantaneous frequency - the residual carrier offset. Non-zero means
/// the capture is not tuned to the carrier (subtract it from Peak/RMS by passing the right DialHz),
/// which on a live capture is exactly the dial error the calibration has to null.</param>
public readonly record struct FmDeviation(double PeakHz, double RmsHz, double MeanHz);

/// <summary>
/// Measures the achieved <b>peak frequency deviation</b> of a complex FM signal - the instrument the
/// on-air calibration uses to set each mode's drive so the deviation hits its Tgt Dev.
/// </summary>
/// <remarks>
/// <para>The deviation is the instantaneous-frequency excursion, and the instantaneous frequency of
/// a complex baseband is the phase derivative <c>arg(z[n]·conj(z[n-1]))·fs/(2π)</c>. FM is
/// constant-envelope, so every sample carries the same weight and the phase difference is a clean
/// estimate; on a noisy capture, restrict the window to the keyed burst (the <c>from</c>/<c>length</c>
/// on the command) so guard-interval noise does not inflate the peak.</para>
/// <para>For a single-tone drive this peak is the primary check; the classic Bessel-null cross-check
/// (the carrier component J₀ nulls at modulation index β = 2.4048, i.e. peak dev = 2.4048·f_tone)
/// falls straight out of a spectrum of the same capture and agrees with the excursion measured here.</para>
/// </remarks>
public static class FmDeviationMeter
{
    /// <summary>Measures deviation over interleaved <c>I, Q</c> (the capture WAV's layout).</summary>
    public static FmDeviation Measure(ReadOnlySpan<float> iqInterleaved, int rate)
    {
        int n = iqInterleaved.Length / 2;
        double peak = 0, sumSq = 0, sum = 0;
        int count = 0;
        double prevI = n > 0 ? iqInterleaved[0] : 0;
        double prevQ = n > 0 ? iqInterleaved[1] : 0;
        double scale = rate / (2.0 * Math.PI);
        for (int k = 1; k < n; k++)
        {
            double i = iqInterleaved[2 * k];
            double q = iqInterleaved[(2 * k) + 1];
            // arg(z[k] · conj(z[k-1])) - the phase advanced over one sample.
            double dphi = Math.Atan2((q * prevI) - (i * prevQ), (i * prevI) + (q * prevQ));
            double f = dphi * scale;
            peak = Math.Max(peak, Math.Abs(f));
            sumSq += f * f;
            sum += f;
            count++;
            prevI = i;
            prevQ = q;
        }

        double mean = count > 0 ? sum / count : 0;
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        return new FmDeviation(peak, rms, mean);
    }

    /// <summary>Measures deviation over separate I/Q arrays.</summary>
    public static FmDeviation Measure(ReadOnlySpan<double> i, ReadOnlySpan<double> q, int rate)
    {
        int n = Math.Min(i.Length, q.Length);
        double peak = 0, sumSq = 0, sum = 0;
        int count = 0;
        double prevI = n > 0 ? i[0] : 0;
        double prevQ = n > 0 ? q[0] : 0;
        double scale = rate / (2.0 * Math.PI);
        for (int k = 1; k < n; k++)
        {
            double dphi = Math.Atan2((q[k] * prevI) - (i[k] * prevQ), (i[k] * prevI) + (q[k] * prevQ));
            double f = dphi * scale;
            peak = Math.Max(peak, Math.Abs(f));
            sumSq += f * f;
            sum += f;
            count++;
            prevI = i[k];
            prevQ = q[k];
        }

        double mean = count > 0 ? sum / count : 0;
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        return new FmDeviation(peak, rms, mean);
    }
}
