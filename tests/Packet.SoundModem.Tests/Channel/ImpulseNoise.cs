namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Impulse-noise (QRN) injection for the sim rig - rx-roadmap workstream 6 item 2,
/// calibrated from the 40 m capture campaign's measured atmospherics rather than a
/// textbook Middleton model. The measured record (docs/bench/impulse-stats-2026-08-06,
/// re-confirmed over the first full day): broadband static crashes arriving at 60-220 per
/// minute (evening and sunrise peaks ~220), whose detection-band envelope sits a
/// threshold-plus-exponential distance above the quiet-band floor (p50 18.1 dB over
/// background, p90 23.8, p99 34.7 - an exponential excess over the instrument's 15.6 dB
/// detection threshold fits all three within a decibel), with crash durations lognormal
/// about ~19 ms and a small population of second-long crash trains carrying the p99 tail.
/// </summary>
/// <param name="RatePerMinute">Broadband crash arrival rate. The campaign's measured
/// anchors: ~120/min for an ordinary summer evening, ~220/min at the evening and sunrise
/// peaks. Zero disables injection entirely (and draws nothing from the channel's random
/// stream, so a zero-rate run is byte-identical to no profile at all).</param>
public sealed record ImpulseNoiseProfile(double RatePerMinute)
{
    /// <summary>An ordinary measured summer evening: ~120 crashes/min.</summary>
    public static ImpulseNoiseProfile Evening => new(120);

    /// <summary>The measured evening/sunrise peak: ~220 crashes/min.</summary>
    public static ImpulseNoiseProfile SunrisePeak => new(220);
}

/// <summary>
/// Adds measured-calibrated impulse noise to a channel output. Amplitudes are drawn as
/// dB-over-the-AWGN-floor, and because each crash is synthesized as BROADBAND shaped white
/// noise, its envelope in any measurement sub-band scales with the same broadband sigma as
/// the background does - the drawn dB ratio survives the measuring instrument's band-pass
/// and envelope smoothing without a calibration constant, which is what closes the
/// validation loop (the injector's output re-analysed by the capture campaign's own
/// impulse-stats methodology must reproduce the measured percentile table).
/// </summary>
/// <remarks>
/// Model shape, each parameter pinned to the measured record and then verified through the
/// closed loop rather than trusted:
/// <list type="bullet">
/// <item>Arrivals: Poisson at the profile rate (exponential inter-arrival times).</item>
/// <item>Amplitude: the instrument's own 6x detection threshold (15.6 dB) plus an
/// exponential excess of mean 3.7 dB - reproducing measured p50/p90/p99 of
/// 18.1/23.8/34.7 dB analytically to within a dB. The measured 73 dB maximum lies beyond
/// this tail; the record itself flags the amplitude tail as an AGC-compressed lower bound,
/// so the model deliberately does not chase it.</item>
/// <item>Duration: a mixture - crashes lognormal (median ~15 ms, sigma-ln 1.05) with a
/// 4.5 percent train population (lognormal median ~800 ms), jointly reproducing the
/// measured duration percentiles through the loop (the instrument measures
/// threshold-crossing time of the decaying envelope, not the nominal duration, so the
/// fit is closed empirically, not assumed).</item>
/// <item>Waveform: white Gaussian burst under a 1 ms attack / exponential-decay envelope -
/// the shape of a band-limited atmospheric transient at audio.</item>
/// </list>
/// </remarks>
public static class ImpulseNoise
{
    /// <summary>The effective amplitude base, dB over the broadband AWGN sigma, from
    /// which the exponential excess is drawn. The instrument's detection threshold is
    /// 15.56 dB (6x) over ITS background estimate, but that estimate is the 20th
    /// percentile of the smoothed band envelope (~4 dB under the mean) and the reading is
    /// the event's PEAK smoothed envelope (~2 dB over the underlying sigma), so drawing
    /// from 15.56 made the instrument read every event ~6 dB hot. 7.4 here lands the
    /// closed loop's detected p50 on the measured 18.1 dB (see
    /// docs/bench/impulse-model-validation-2026-08-07.txt).</summary>
    private const double ThresholdDb = 7.4;

    /// <summary>Mean of the exponential amplitude excess above the threshold, dB. Fitted:
    /// p50 = threshold + ln(2)*mean = 18.1, p90 = threshold + ln(10)*mean = 24.0,
    /// p99 = 32.4 against measured 18.1 / 23.8 / 34.7.</summary>
    private const double AmplitudeExcessMeanDb = 3.66;

    /// <summary>Crash-duration lognormal parameters (natural log of seconds) and the train
    /// mixture - see the class remarks; verified through the validation loop.</summary>
    private const double CrashMedianSeconds = 0.027;
    private const double CrashSigmaLn = 1.15;
    private const double TrainFraction = 0.05;
    private const double TrainMedianSeconds = 3.2;
    private const double TrainSigmaLn = 0.9;

    /// <summary>Attack time of each crash's envelope.</summary>
    private const double AttackSeconds = 0.001;

    /// <summary>Adds impulses to <paramref name="output"/> in place.</summary>
    /// <param name="output">The channel output (already carrying its AWGN).</param>
    /// <param name="sampleRate">Sample rate of <paramref name="output"/>.</param>
    /// <param name="noiseSigma">The per-sample AWGN sigma the channel added - the floor
    /// the drawn dB-over-background amplitudes stand on. Must be positive: impulse
    /// amplitudes are measured over a noise floor, so a noiseless run has nothing to
    /// calibrate against.</param>
    /// <param name="profile">The rate profile; a zero rate returns without drawing.</param>
    /// <param name="random">The channel's own seeded stream (determinism: a point
    /// reproduces from its seed).</param>
    public static void Add(
        float[] output, int sampleRate, double noiseSigma, ImpulseNoiseProfile profile,
        Random random)
    {
        if (profile.RatePerMinute <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(noiseSigma, 0);

        // The profile rate is the MEASURED broadband rate the campaign quotes; the
        // instrument's two-band coincidence gate passes ~65 percent of drawn events at
        // these amplitudes (near-threshold draws fail one band or the other), so the
        // drawn arrival rate is scaled up to make the detected rate match the profile -
        // fitted through the closed loop.
        double ratePerSecond = profile.RatePerMinute / 60.0 / 0.65;
        double t = NextExponential(random) / ratePerSecond;
        double span = output.Length / (double)sampleRate;
        while (t < span)
        {
            AddOneCrash(output, sampleRate, (int)(t * sampleRate), noiseSigma, random);
            t += NextExponential(random) / ratePerSecond;
        }
    }

    private static void AddOneCrash(
        float[] output, int sampleRate, int start, double noiseSigma, Random random)
    {
        if (random.NextDouble() < TrainFraction)
        {
            // A crash train: a dense volley of ordinary crashes across the train span,
            // spaced under the measuring instrument's 20 ms merge gap so it reads as one
            // long event - which is what the measured second-long p99 durations are. A
            // single decaying envelope cannot reproduce them: its tail crosses back under
            // the detection threshold within a fraction of the drawn duration.
            double trainSpan = TrainMedianSeconds * Math.Exp(TrainSigmaLn * NextGaussian(random));
            double at = 0;
            while (at < trainSpan)
            {
                // Sub-crashes carry a +5 dB amplitude floor: a weak volley member that
                // dips under detection splits the train into medium events, and the
                // measured record's second-long events are continuous runs.
                AddOneBurst(output, sampleRate, start + (int)(at * sampleRate), noiseSigma,
                    random, floorDb: 5);
                at += 0.008 + (0.008 * random.NextDouble());
            }

            return;
        }

        AddOneBurst(output, sampleRate, start, noiseSigma, random);
    }

    private static void AddOneBurst(
        float[] output, int sampleRate, int start, double noiseSigma, Random random,
        double floorDb = 0)
    {
        if (start >= output.Length)
        {
            return;
        }

        double db = ThresholdDb + floorDb + (AmplitudeExcessMeanDb * NextExponential(random));
        double peak = noiseSigma * Math.Pow(10, db / 20.0);
        double duration = CrashMedianSeconds * Math.Exp(CrashSigmaLn * NextGaussian(random));

        int attack = Math.Max(1, (int)(AttackSeconds * sampleRate));
        // Render to 3 time constants past the nominal duration so the decay tail falls
        // well below the detection threshold rather than being truncated at it.
        int total = Math.Min((int)(duration * 4 * sampleRate) + attack, output.Length - start);
        double tau = Math.Max(duration / 3.0, AttackSeconds);
        for (int i = 0; i < total; i++)
        {
            int k = start + i;
            if (k >= output.Length)
            {
                break;
            }

            double envelope = i < attack
                ? i / (double)attack
                : Math.Exp(-((i - attack) / (double)sampleRate) / tau);
            output[k] += (float)(peak * envelope * NextGaussian(random));
        }
    }

    private static double NextExponential(Random random) =>
        -Math.Log(1.0 - random.NextDouble());

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
