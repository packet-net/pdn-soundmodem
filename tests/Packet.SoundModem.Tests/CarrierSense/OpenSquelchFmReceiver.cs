using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// Audio as it comes out of an FM receiver with the squelch open, which is how every packet FM
/// station runs: <b>louder when nobody is transmitting than when somebody is</b>.
/// </summary>
/// <remarks>
/// <para><b>Why this fixture exists at all.</b> Every other audio fixture in this repository puts
/// the noise BELOW the signal. The loopback tests have digital silence between bursts; the AWGN
/// and Watterson ladders inject their lead-in noise at the burst's own signal-to-noise ratio, so
/// the burst is always the loud part. That is right for SSB and for a wired loop and it is
/// backwards for FM, and it is the reason an inverted assumption about carrier sense survived
/// every test in the suite and was only found on air (packet-net/pdn-soundmodem#522). A detector
/// cannot be scored against a channel model that cannot express the failure.</para>
/// <para><b>The physics.</b> With no carrier, an FM discriminator's output is noise filling the
/// whole detection bandwidth. An arriving carrier captures the discriminator and replaces that
/// noise with the modulation, so the received level FALLS, and it falls hardest well above the
/// signal's own band where the modulation puts nothing back.</para>
/// <para><b>It is one measured table and nothing else.</b> The 24 rows below are the per-kilohertz
/// spectrum of <c>ninorx.wav</c>, a 48 kHz capture off radio1's discriminator of a NinoTNC
/// transmitting C4FSK 19k2 against a real idle channel, averaged over 1985 idle blocks and 94
/// whole-burst blocks of 1024 samples. <b>Everything anybody quotes about this channel falls out
/// of it</b>, which is why it is stored as a spectrum rather than as a handful of derived numbers:
/// in-band 0 to 14 kHz reads -15.90 dBFS idle against -18.88 keyed, a 2.98 dB drop; the hiss from
/// 16 to 23.5 kHz collapses by 18.3 dB; and the ratio of power below 8 kHz to power above it,
/// which is what <see cref="Packet.SoundModem.CarrierSense.FmShapeBusyDetector"/> keys off, rises
/// from 7.55 dB to 30.57 dB.</para>
/// <para><b>The shape is the interesting part and a two-band model cannot carry it.</b> Quieting
/// is not uniform: it is -1.1 dB in the bottom kilohertz, where the modulation replaces what the
/// carrier removed, and 27.9 dB at 9 to 10 kHz, just above what a 9600 symbol per second signal
/// occupies. Idle hiss FALLS across the band here, by 37 dB from the bottom to the top, which is
/// this receive path's own response rather than FM theory: textbook post-detection noise rises.
/// That is exactly the station-dependent property that makes an absolute threshold on any band
/// ratio unsafe, and it is why the detector under test compares against the station's own idle.
/// </para>
/// <para><b>What this fixture is not.</b> The burst is band-shaped noise and not a real waveform,
/// so nothing here decodes and nothing here should be used to measure decode performance. Keyups
/// are instantaneous, so assert LATENCY has to be measured against a recording
/// (<see cref="FmShapeBusyDetectorRecordingTests"/>), not against this. What this is for is the
/// steady-state question of which way round the levels and the shape are, which is what every
/// other fixture in the suite gets wrong.</para>
/// </remarks>
internal static class OpenSquelchFmReceiver
{
    /// <summary>The rate a station's sound card captures at, and what the recordings are.</summary>
    public const int SampleRate = 48000;

    /// <summary>Top of the signal's own band: 1.5 x the 9600 symbols a second of c4fsk19200,
    /// which is the widest thing this bench transmits.</summary>
    public const double SignalTopHz = 14400;

    /// <summary>The hiss band, above anything the signal occupies.</summary>
    public const double HissFromHz = 16000;

    /// <summary>Upper edge of the hiss band, short of Nyquist and of the capture path's own
    /// anti-alias corner.</summary>
    public const double HissToHz = 23500;

    /// <summary>What the table gives for 0 to 14 kHz with nobody transmitting.</summary>
    public const double IdleInBandDbfs = -15.90;

    /// <summary>The same band with a far end transmitting. BELOW idle, which is the whole
    /// point.</summary>
    public const double KeyedInBandDbfs = -18.88;

    /// <summary>Measured power per kilohertz band with nobody transmitting, dB, 0 to 24 kHz.</summary>
    private static readonly double[] IdleBandDb =
    [
        -24.55, -24.67, -24.84, -25.19, -25.79, -26.05, -26.77, -27.96,
        -29.02, -30.37, -32.20, -33.73, -35.65, -38.08, -40.19, -42.43,
        -44.66, -46.30, -48.00, -50.12, -52.05, -54.66, -58.23, -61.66,
    ];

    /// <summary>The same bands with a far end transmitting.</summary>
    private static readonly double[] KeyedBandDb =
    [
        -23.47, -24.68, -26.08, -28.36, -31.53, -35.47, -40.38, -47.90,
        -54.93, -58.27, -59.60, -59.66, -60.01, -60.82, -61.65, -62.73,
        -64.41, -65.40, -66.01, -67.32, -68.76, -69.82, -73.13, -76.38,
    ];

    private const double BandWidthHz = 1000;

    /// <summary>Open-squelch idle hiss: the LOUD state.</summary>
    public static float[] Idle(double seconds, int seed, int rate = SampleRate) =>
        Render(seconds, seed, IdleBandDb, rate);

    /// <summary>A far end transmitting: quieter in band, and much quieter above it.</summary>
    public static float[] Keyed(double seconds, int seed, int rate = SampleRate) =>
        Render(seconds, seed, KeyedBandDb, rate);

    /// <summary>
    /// The same channel through a different station's audio path: a one-pole low pass, which is
    /// what a sound card's own response looks like, applied to idle and keyed alike.
    /// </summary>
    /// <remarks>
    /// <b>The regression for the failure that silenced a bench station.</b> Two nominally
    /// identical stations measured 22 dB apart in absolute terms, and the threshold set from one
    /// stopped the other transmitting. A detector whose decision is a ratio against the station's
    /// own idle should not care about any of this; one with an absolute threshold in it will.
    /// </remarks>
    public static float[] IdleThrough(double seconds, int seed, double cornerHz) =>
        Render(seconds, seed, RolledOff(IdleBandDb, cornerHz), SampleRate);

    /// <inheritdoc cref="IdleThrough"/>
    public static float[] KeyedThrough(double seconds, int seed, double cornerHz) =>
        Render(seconds, seed, RolledOff(KeyedBandDb, cornerHz), SampleRate);

    private static double[] RolledOff(double[] bandDb, double cornerHz)
    {
        var shaped = new double[bandDb.Length];
        for (int b = 0; b < bandDb.Length; b++)
        {
            double centre = (b + 0.5) * BandWidthHz;
            shaped[b] = bandDb[b] - (10 * Math.Log10(1 + ((centre / cornerHz) * (centre / cornerHz))));
        }

        return shaped;
    }

    /// <summary>
    /// Idle, then a transmission, then idle again: what a station hears across somebody else's
    /// whole over.
    /// </summary>
    public static float[] IdleThenKeyedThenIdle(double idleSeconds, double keyedSeconds, int seed)
    {
        float[] before = Idle(idleSeconds, seed);
        float[] during = Keyed(keyedSeconds, seed + 1);
        float[] after = Idle(idleSeconds, seed + 2);
        var all = new float[before.Length + during.Length + after.Length];
        before.CopyTo(all, 0);
        during.CopyTo(all, before.Length);
        after.CopyTo(all, before.Length + during.Length);
        return all;
    }

    /// <summary>The sample index a <see cref="IdleThenKeyedThenIdle"/> transmission starts at.</summary>
    public static int KeyupSample(double idleSeconds) => (int)(idleSeconds * SampleRate);

    /// <summary>Band power in dBFS over a span, for a test to check its own fixture with.</summary>
    public static double BandPowerDbfs(
        ReadOnlySpan<float> samples, double fromHz, double toHz, int from, int to)
    {
        int n = 1024;
        var window = new double[n];
        double windowPower = 0;
        for (int i = 0; i < n; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / n));
            windowPower += window[i] * window[i];
        }

        windowPower /= n;
        double binHz = (double)SampleRate / n;
        int k0 = Math.Max(1, (int)Math.Ceiling(fromHz / binHz));
        int k1 = Math.Min((n / 2) - 1, (int)Math.Floor(toHz / binHz));

        var re = new double[n];
        var im = new double[n];
        double total = 0;
        int blocks = 0;
        for (int at = from; at + n <= to && at + n <= samples.Length; at += n)
        {
            for (int i = 0; i < n; i++)
            {
                re[i] = samples[at + i] * window[i];
                im[i] = 0;
            }

            RealFft.Forward(re, im);
            double acc = 0;
            for (int k = k0; k <= k1; k++)
            {
                acc += 2 * ((re[k] * re[k]) + (im[k] * im[k]));
            }

            total += acc / ((double)n * n * windowPower);
            blocks++;
        }

        return 10 * Math.Log10(Math.Max(total / Math.Max(blocks, 1), 1e-30));
    }

    /// <summary>
    /// Synthesises noise with the given per-kilohertz spectrum, by building the spectrum directly
    /// and transforming it.
    /// </summary>
    /// <remarks>
    /// Each bin gets an independent complex Gaussian, so the result is genuine Gaussian noise
    /// rather than a sum of tones with random phase, and each band's realised power lands on the
    /// figure it was given: at these lengths a band holds thousands of bins, so the chi-square
    /// scatter on its total is a small fraction of a decibel. Done in the frequency domain because
    /// the alternative, 24 band-pass filters over every sample, is both slower and less exact
    /// about what it produced.
    /// </remarks>
    private static float[] Render(double seconds, int seed, double[] bandDb, int rate)
    {
        int count = (int)(seconds * rate);
        int n = 1;
        while (n < count)
        {
            n <<= 1;
        }

        double binHz = (double)rate / n;
        var random = new Random(seed);
        var re = new double[n];
        var im = new double[n];

        for (int k = 1; k < n / 2; k++)
        {
            int band = (int)(k * binHz / BandWidthHz);
            if (band >= bandDb.Length)
            {
                continue;
            }

            // Band power P is spread over the bins inside it. For a real signal built from bins
            // 1..n/2-1 with an inverse transform scaled by 1/n, Parseval gives
            // mean square = (2 / n^2) * sum |c_k|^2, so a band of m bins wants |c_k|^2 = P n^2/2m.
            double binsInBand = BandWidthHz / binHz;
            double power = Math.Pow(10, bandDb[band] / 10);
            double sigma = Math.Sqrt(power * (double)n * n / (2 * binsInBand));

            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));
            double real = magnitude * Math.Cos(2 * Math.PI * u2) / Math.Sqrt(2);
            double imaginary = magnitude * Math.Sin(2 * Math.PI * u2) / Math.Sqrt(2);

            re[k] = sigma * real;
            im[k] = sigma * imaginary;
            re[n - k] = re[k];
            im[n - k] = -im[k];
        }

        RealFft.Inverse(re, im);

        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)re[i];
        }

        return samples;
    }
}
