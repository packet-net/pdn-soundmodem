using M0LTE.Ofdm;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>One propagation path of a <see cref="WattersonChannel"/>.</summary>
/// <param name="DelayMs">Path delay in milliseconds.</param>
/// <param name="Fading">True for a Rayleigh-fading tap gain; false for a static (constant)
/// tap. Static taps carry a fixed seed-derived phase (the spec's static tests fix the
/// geometry, not the phases).</param>
/// <param name="DopplerSpreadHz">Two-sigma Doppler (frequency) spread of the fading process
/// - ITU-R F.1487 "Poor" = 1 Hz. Ignored for static taps.</param>
/// <param name="DopplerShiftHz">Constant per-path Doppler shift. Rarely used; 0 default.</param>
public sealed record WattersonPath(
    double DelayMs, bool Fading = false, double DopplerSpreadHz = 1.0, double DopplerShiftHz = 0);

/// <summary>
/// Watterson HF channel simulator per MIL-STD-188-110D Appendix E: a tapped delay line on the
/// complex envelope with per-tap independent complex-Gaussian gains filtered to a Gaussian
/// Doppler spectrum (E.5.3/E.5.4), plus AWGN calibrated as SNR in a 3 kHz noise bandwidth.
/// Runs at the modem's native 9600 Hz - ≥4× the 2400 Bd symbol rate as E.5.1 requires, so
/// Rung-2 tests are resampler-free (design §4.3/§5.1). Equal-power paths (the D-LXIV/D-LXV
/// test geometries); total mean output power is normalized to the input's.
/// </summary>
/// <remarks>
/// "Poor" (D.6.1 / ITU-R F.1487 Mid-Latitude Disturbed): two independent equal-power Rayleigh
/// paths, 2 ms apart, 1 Hz two-sigma fade - <see cref="Poor"/>. The D-LXV 3 kHz static rigs:
/// WID 2 → (0, 3, 9 ms), WID 10 → (0, 2, 4.5), WID 12 → (0, 1.5), equal power, non-fading.
/// </remarks>
public sealed class WattersonChannel
{
    /// <summary>The audio centre the complex envelope is formed about - 1800 Hz, the App D
    /// sub-carrier, unless the signal under test has been moved (a FrequencyShiftedModem at
    /// the GB7RDG tenant's 3750 Hz, say). The rig demodulates to this centre, low-passes at
    /// 2.2 kHz, fades, and remodulates, so a signal centred elsewhere is simply cut in half
    /// by the low-pass (G3 of the Poor-gate successor program measured 4/12 at 3750 Hz and
    /// 0/12 at 5000 Hz, SNR-independent, before this knob existed).</summary>
    public double CentreHz { get; init; } = 1800;
    private const int GainRate = 96; // fading-gain sample rate, Hz (9600 / 100)

    /// <summary>Sample rate of the recorded <see cref="LastPathGains"/> trajectories.</summary>
    public const int GainSampleRateHz = GainRate;

    private readonly int _sampleRate;
    private readonly WattersonPath[] _paths;
    private readonly Random _random;

    /// <summary>When set before <see cref="Apply"/>, the per-path tap-gain trajectory of
    /// the run is kept in <see cref="LastPathGains"/> - the fade envelope for
    /// error-correlation telemetry and the channel-truth genie (phase-b-plan §B0).</summary>
    public bool RecordGains { get; set; }

    /// <summary>Per-path gain trajectory of the last <see cref="Apply"/> when
    /// <see cref="RecordGains"/> was set: fading paths at <see cref="GainSampleRateHz"/>
    /// (aligned to the channel span, lead-in excluded), static paths a single constant.</summary>
    public IReadOnlyList<Cf[]>? LastPathGains { get; private set; }

    /// <summary>Creates the channel. No paths = the ideal direct path (AWGN-only, passband
    /// passthrough - the D-LXIV AWGN channel).</summary>
    public WattersonChannel(int sampleRate, int seed, params WattersonPath[] paths)
    {
        _sampleRate = sampleRate;
        _paths = paths;
        _random = new Random(seed);
    }

    /// <summary>The D.6.1 "Poor" channel: 2 equal-power Rayleigh paths, 2 ms, 1 Hz spread.</summary>
    public static WattersonPath[] Poor =>
    [
        new WattersonPath(0, Fading: true, DopplerSpreadHz: 1),
        new WattersonPath(2, Fading: true, DopplerSpreadHz: 1),
    ];

    /// <summary>Applies the channel and noise. <paramref name="snrDb"/> is signal-to-noise
    /// with the noise measured in <paramref name="noiseBandwidthHz"/> (3 kHz per D.6.2);
    /// pass <see cref="double.PositiveInfinity"/> for a noiseless run.
    /// <paramref name="leadInSamples"/>/<paramref name="leadOutSamples"/> prepend/append
    /// noise-only padding (so acquisition sees realistic noise, not digital silence).
    /// <paramref name="frequencyOffsetHz"/> applies a carrier offset (the D.6.4 rig).
    /// <paramref name="impulses"/> adds measured-calibrated QRN on top of the AWGN (see
    /// <see cref="ImpulseNoise"/>); impulse amplitudes stand on the AWGN floor, so a
    /// noiseless run cannot take a non-zero rate.</summary>
    public float[] Apply(
        ReadOnlySpan<float> input,
        double snrDb,
        double noiseBandwidthHz = 3000,
        int leadInSamples = 0,
        int leadOutSamples = 0,
        double frequencyOffsetHz = 0,
        ImpulseNoiseProfile? impulses = null)
    {
        if (impulses is { RatePerMinute: > 0 } && double.IsPositiveInfinity(snrDb))
        {
            throw new ArgumentException(
                "impulse amplitudes are calibrated over the AWGN floor; give a finite SNR",
                nameof(impulses));
        }

        if (_paths.Length == 0 && frequencyOffsetHz == 0)
        {
            // Ideal path: passband passthrough plus calibrated noise.
            LastPathGains = RecordGains ? [] : null;
            var direct = new float[leadInSamples + input.Length + leadOutSamples];
            input.CopyTo(direct.AsSpan(leadInSamples));
            double directSigma = AddNoise(direct, input, snrDb, noiseBandwidthHz);
            if (impulses is not null)
            {
                ImpulseNoise.Add(direct, _sampleRate, directSigma, impulses, _random);
            }

            return direct;
        }

        // Analytic/complex envelope about the 1800 Hz sub-carrier.
        Cf[] envelope = ToEnvelope(input);

        // Tapped delay line with per-tap gains; equal-power normalization.
        int n = envelope.Length;
        var summed = new Cf[n];
        WattersonPath[] paths = _paths.Length == 0 ? [new WattersonPath(0)] : _paths;
        float pathScale = (float)(1.0 / Math.Sqrt(paths.Length));
        List<Cf[]>? recorded = RecordGains ? new List<Cf[]>(paths.Length) : null;
        foreach (WattersonPath path in paths)
        {
            Cf[] delayed = FractionalDelay(envelope, path.DelayMs * _sampleRate / 1000.0);
            if (path.DopplerShiftHz != 0)
            {
                if (ReferenceEquals(delayed, envelope))
                {
                    delayed = (Cf[])envelope.Clone();
                }

                ApplyShift(delayed, path.DopplerShiftHz);
            }

            if (path.Fading)
            {
                Cf[] gains = FadingGains(n, path.DopplerSpreadHz / 2.0);
                if (recorded is not null)
                {
                    // Every decimation-th full-rate gain is an exact low-rate process
                    // sample (the interpolation fraction is zero there).
                    int decimation = _sampleRate / GainRate;
                    var trajectory = new Cf[((n - 1) / decimation) + 1];
                    for (int i = 0; i < trajectory.Length; i++)
                    {
                        trajectory[i] = gains[i * decimation];
                    }

                    recorded.Add(trajectory);
                }

                for (int i = 0; i < n; i++)
                {
                    summed[i] += delayed[i] * gains[i] * pathScale;
                }
            }
            else
            {
                var phase = Cf.Cmplx((float)(_random.NextDouble() * 2 * Math.PI));
                recorded?.Add([phase]);
                for (int i = 0; i < n; i++)
                {
                    summed[i] += delayed[i] * phase * pathScale;
                }
            }
        }

        LastPathGains = recorded;

        if (frequencyOffsetHz != 0)
        {
            ApplyShift(summed, frequencyOffsetHz);
        }

        // Back to a real passband signal.
        var output = new float[leadInSamples + n + leadOutSamples];
        for (int i = 0; i < n; i++)
        {
            double theta = 2.0 * Math.PI * CentreHz * i / _sampleRate;
            output[leadInSamples + i] =
                (float)((summed[i].Re * Math.Cos(theta)) - (summed[i].Im * Math.Sin(theta)));
        }

        double sigma = AddNoise(output, input, snrDb, noiseBandwidthHz);
        if (impulses is not null)
        {
            ImpulseNoise.Add(output, _sampleRate, sigma, impulses, _random);
        }

        return output;
    }

    /// <summary>Adds the calibrated AWGN and returns its per-sample sigma (0 for a
    /// noiseless run) - the floor the impulse injector's amplitudes stand on.</summary>
    private double AddNoise(float[] output, ReadOnlySpan<float> input, double snrDb, double noiseBandwidthHz)
    {
        // Noise calibrated against the mean power of the clean input burst.
        if (double.IsPositiveInfinity(snrDb))
        {
            return 0;
        }

        double signalPower = 0;
        foreach (float s in input)
        {
            signalPower += (double)s * s;
        }

        signalPower /= input.Length;
        double snr = Math.Pow(10, snrDb / 10.0);
        double noisePower = signalPower / snr * (_sampleRate / 2.0) / noiseBandwidthHz;
        double sigma = Math.Sqrt(noisePower);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] += (float)(sigma * Gaussian());
        }

        return sigma;
    }

    /// <summary>Generates a fading-gain sequence (unit mean-square, Gaussian Doppler
    /// spectrum of standard deviation <paramref name="sigmaHz"/>) at the output rate -
    /// exposed for the channel self-tests.</summary>
    internal Cf[] FadingGains(int count, double sigmaHz)
    {
        // White complex Gaussian at the low gain rate, filtered by the Gaussian-shaped
        // filter h(t) ∝ exp(−4π²σ²t²) (|H(f)|² = Gaussian PSD), then linearly
        // interpolated to the sample rate - the process changes on ~1/σ second scales.
        double sigmaT = 1.0 / (2.0 * Math.PI * sigmaHz * Math.Sqrt(2.0));
        int half = (int)Math.Ceiling(4 * sigmaT * GainRate);
        var filter = new double[(2 * half) + 1];
        double energy = 0;
        for (int i = 0; i < filter.Length; i++)
        {
            double t = (i - half) / (double)GainRate;
            filter[i] = Math.Exp(-4.0 * Math.PI * Math.PI * sigmaHz * sigmaHz * t * t);
            energy += filter[i] * filter[i];
        }

        double norm = 1.0 / Math.Sqrt(energy);

        int decimation = _sampleRate / GainRate;
        int lowRateCount = (count / decimation) + 2;
        var white = new Cf[lowRateCount + filter.Length];
        for (int i = 0; i < white.Length; i++)
        {
            white[i] = new Cf((float)(Gaussian() / Math.Sqrt(2)), (float)(Gaussian() / Math.Sqrt(2)));
        }

        var low = new Cf[lowRateCount];
        for (int i = 0; i < lowRateCount; i++)
        {
            var acc = Cf.Zero;
            for (int j = 0; j < filter.Length; j++)
            {
                acc += white[i + j] * (float)(filter[j] * norm);
            }

            low[i] = acc;
        }

        var gains = new Cf[count];
        for (int i = 0; i < count; i++)
        {
            int i0 = i / decimation;
            float frac = (i - (i0 * decimation)) / (float)decimation;
            gains[i] = (low[i0] * (1 - frac)) + (low[i0 + 1] * frac);
        }

        return gains;
    }

    private Cf[] ToEnvelope(ReadOnlySpan<float> input)
    {
        // Mix down and low-pass (windowed sinc, cutoff 2.2 kHz) to reject the −2·1800 Hz
        // image; gain 2 restores the envelope amplitude. Delay-compensated (linear phase).
        // 129 taps at the rig's native 9600 Hz (unchanged there, so every banked battery
        // digit stands); scaled with the sample rate so the transition band stays ~150 Hz
        // at 48 kHz, where 129 taps would leave the -2*centre image barely attenuated.
        int taps = (129 * _sampleRate / 9600) | 1;
        int half = taps / 2;
        var h = new double[taps];
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - half;
            double cutoff = 2200.0 / _sampleRate;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)));
            h[i] = sinc * window;
            sum += h[i];
        }

        for (int i = 0; i < taps; i++)
        {
            h[i] /= sum;
        }

        int n = input.Length;
        var mixed = new Cf[n];
        for (int i = 0; i < n; i++)
        {
            double theta = 2.0 * Math.PI * CentreHz * i / _sampleRate;
            mixed[i] = new Cf(
                (float)(input[i] * Math.Cos(theta)),
                (float)(-input[i] * Math.Sin(theta)));
        }

        var envelope = new Cf[n];
        for (int i = 0; i < n; i++)
        {
            var acc = Cf.Zero;
            for (int j = 0; j < taps; j++)
            {
                int k = i + half - j;
                if (k >= 0 && k < n)
                {
                    acc += mixed[k] * (float)h[j];
                }
            }

            envelope[i] = acc * 2f;
        }

        return envelope;
    }

    private static Cf[] FractionalDelay(Cf[] input, double delaySamples)
    {
        if (delaySamples == 0)
        {
            return input;
        }

        int whole = (int)Math.Floor(delaySamples);
        double frac = delaySamples - whole;
        const int half = 16;
        var output = new Cf[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            var acc = Cf.Zero;
            for (int j = -half + 1; j <= half; j++)
            {
                int k = i - whole - j;
                if (k < 0 || k >= input.Length)
                {
                    continue;
                }

                double u = j - frac;
                double w = Math.Abs(u) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / half)));
                acc += input[k] * (float)w;
            }

            output[i] = acc;
        }

        return output;
    }

    private void ApplyShift(Cf[] signal, double shiftHz)
    {
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] *= Cf.Cmplx((float)(2.0 * Math.PI * shiftHz * i / _sampleRate));
        }
    }

    private double Gaussian()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
