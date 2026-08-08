using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// One end-to-end FM link: how the audio a modem transmits comes back out of a receiver's
/// discriminator. Everything an FM mode meets that an SSB mode does not - the threshold effect,
/// the discriminator's rising noise spectrum, pre/de-emphasis, deviation error, the IF filter and
/// flutter - lives here.
/// </summary>
/// <param name="PeakDeviationHz">The deviation the transmit drive is calibrated to, in Hz peak
/// (docs/mode-modulation-reference.md: 3.0 kHz for AFSK 1200, 2.4 kHz for GFSK 9600, 5.0 kHz for
/// C4FSK 19200 and qpsk3600, and so on). The modem's audio peak maps to this.</param>
/// <param name="IfBandwidthHz">Receiver IF filter bandwidth, and the bandwidth the carrier-to-noise
/// ratio is stated in. Roughly 8 kHz on a 12.5 kHz channel, 16 kHz on 25 kHz.</param>
/// <param name="TxAudioLowHz">Transmit audio path low cut. A microphone input rolls off below
/// ~300 Hz; a data port passes down to a few Hz.</param>
/// <param name="TxAudioHighHz">Transmit audio path high cut - the splatter filter. ~3 kHz on a
/// microphone input, several kHz on a data port. This is the limit OFDM-AB's four bandwidth
/// profiles are drawn around.</param>
/// <param name="RxAudioLowHz">Receive audio path low cut (speaker output vs discriminator tap).</param>
/// <param name="RxAudioHighHz">Receive audio path high cut.</param>
/// <param name="PreEmphasisMicroseconds">Transmit pre-emphasis time constant; 0 for a flat data
/// port. Amateur FM uses 750 us.</param>
/// <param name="DeEmphasisMicroseconds">Receive de-emphasis time constant; 0 for a flat
/// discriminator tap.</param>
/// <param name="DeviationErrorDb">Drive calibration error: positive over-deviates, negative under-
/// deviates. 0 is a correctly calibrated transmitter.</param>
/// <param name="FlutterDopplerHz">Two-sigma Doppler of a flat Rayleigh fade on the RF carrier -
/// VHF/UHF flutter. 0 for a static link.</param>
public sealed record FmLinkProfile(
    double PeakDeviationHz,
    double IfBandwidthHz = 8000,
    double TxAudioLowHz = 300,
    double TxAudioHighHz = 3000,
    double RxAudioLowHz = 300,
    double RxAudioHighHz = 3000,
    double PreEmphasisMicroseconds = 750,
    double DeEmphasisMicroseconds = 750,
    double DeviationErrorDb = 0,
    double FlutterDopplerHz = 0)
{
    /// <summary>The path an ordinary handheld or mobile gives you: microphone in, speaker out,
    /// both emphasised and both band-limited to voice. What OFDM-AB calls narrowband, and what
    /// every FM packet station without a data port is using.</summary>
    public static FmLinkProfile MicAndSpeaker(double peakDeviationHz, double ifBandwidthHz = 8000) =>
        new(peakDeviationHz, ifBandwidthHz);

    /// <summary>A radio's data port: flat audio into the modulator, discriminator audio out, no
    /// emphasis either end and a much wider passband. What a 9600 baud packet radio needs, and
    /// what OFDM-AB's wideband profiles assume.</summary>
    public static FmLinkProfile DataPort(
        double peakDeviationHz, double? audioHighHz = null, double ifBandwidthHz = 8000)
    {
        // A discriminator cannot hand back more audio bandwidth than half its IF filter, so the
        // data port's passband follows the channel rather than a constant: about 4 kHz on a
        // 12.5 kHz channel, about 8 kHz on a 25 kHz one. Getting this wrong silently truncates a
        // fast mode's baseband and looks exactly like a mode that does not work.
        double high = audioHighHz ?? (ifBandwidthHz / 2);
        return new(peakDeviationHz, ifBandwidthHz,
            TxAudioLowHz: 20, TxAudioHighHz: high,
            RxAudioLowHz: 20, RxAudioHighHz: high,
            PreEmphasisMicroseconds: 0, DeEmphasisMicroseconds: 0);
    }

    /// <summary>Receiver IF bandwidth for a channel spacing: about 8 kHz on the 12.5 kHz narrow
    /// channels, about 16 kHz on the 25 kHz wide ones. Which spacing a mode belongs on is a
    /// property of the mode, not a preference - see <c>FmModes</c>.</summary>
    public static double IfBandwidthForSpacing(double channelSpacingHz) =>
        channelSpacingHz >= 20000 ? 16000 : 8000;
}

/// <summary>
/// A physical FM link simulator: the modem's audio is really frequency-modulated onto a carrier,
/// really has noise added at a stated carrier-to-noise ratio, and is really put through a limiter
/// and a discriminator. Nothing about the FM impairments is approximated by a curve - they emerge,
/// which is the point.
/// </summary>
/// <remarks>
/// <para><b>Why this is not the Watterson rig.</b> <see cref="WattersonChannel"/> models a linear
/// SSB path: audio in, the same audio plus noise and multipath out, and an SNR that means what it
/// says at the modem's input. An FM link is not linear. Noise is added to the <em>carrier</em>,
/// before a limiter and a discriminator, and three consequences follow that no SSB model can
/// produce:</para>
/// <list type="bullet">
/// <item><description><b>The threshold effect.</b> Well above threshold the discriminator suppresses
/// noise and output SNR beats input CNR by the modulation-index gain; below it, click noise takes
/// over and the output collapses far faster than the input degrades. FM modes do not fade away
/// gracefully, they fall off a cliff, and where that cliff sits is the number that matters.</description></item>
/// <item><description><b>Triangular output noise.</b> Discriminator noise power rises with the
/// square of audio frequency, so a mode's high-frequency content is measurably noisier than its low.
/// This is why pre/de-emphasis exists, and why any wideband audio mode (OFDM-AB above all, whose top
/// subcarriers sit where the noise is worst) cannot be masked honestly against flat AWGN.</description></item>
/// <item><description><b>Emphasis and band limits are the channel.</b> A microphone input's
/// pre-emphasis and 300-3000 Hz passband are not incidental; for a mode designed to work through
/// mic and speaker they define what is transmittable at all.</description></item>
/// </list>
/// <para><b>CNR convention.</b> <c>cnrDb</c> is carrier power over noise power in
/// <see cref="FmLinkProfile.IfBandwidthHz"/> - the receiver's own filter, which is where an FM
/// receiver's threshold is defined. It is deliberately not the SNR3k convention the HF modes use:
/// the two are not comparable and pretending otherwise would be the more dangerous choice.</para>
/// <para><b>Lead-in is carrier, not silence.</b> A real FM data burst keys the transmitter, then
/// modulates, so the receiver sees an unmodulated carrier before the data rather than open-squelch
/// hiss. The padding here does the same, which is what lets a modem's acquisition see the noise
/// floor it will really see.</para>
/// </remarks>
public sealed class FmChannel
{
    private readonly FmLinkProfile _profile;
    private readonly int _audioRate;
    private readonly int _ifRate;
    private readonly int _interpolation;
    private readonly Random _random;

    /// <summary>Interpolation from the modem's audio rate to the rate the carrier is represented
    /// at. The complex envelope of an FM signal is roughly 2*(deviation + audio bandwidth) wide
    /// (Carson), and the phase integration wants headroom beyond that, so the factor is the
    /// smallest power of two that puts the IF rate at four times Carson - which keeps a 12 kHz
    /// mode at 48 kHz and stops a 48 kHz mode running a needless 192 kHz.</summary>
    public int InterpolationFactor => _interpolation;

    /// <summary>Creates the link. <paramref name="seed"/> fixes the noise and fade realisation, so
    /// a measurement reproduces from (mode, profile, CNR, seed).</summary>
    public FmChannel(FmLinkProfile profile, int audioRate, int seed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audioRate, 0);
        _profile = profile;
        _audioRate = audioRate;
        double carson = 2 * (profile.PeakDeviationHz + profile.TxAudioHighHz);
        int factor = 2;
        while (factor < 16 && audioRate * factor < 4 * carson)
        {
            factor *= 2;
        }

        _interpolation = factor;
        _ifRate = audioRate * factor;
        _random = new Random(seed);
    }

    /// <summary>Peak deviation actually applied, after the drive-calibration error.</summary>
    public double AppliedDeviationHz =>
        _profile.PeakDeviationHz * Math.Pow(10, _profile.DeviationErrorDb / 20.0);

    /// <summary>Puts one burst of modem audio through the link.</summary>
    /// <param name="audio">The modem's transmit audio.</param>
    /// <param name="cnrDb">Carrier-to-noise ratio in the IF bandwidth, dB.
    /// <see cref="double.PositiveInfinity"/> for a noiseless link.</param>
    /// <param name="leadInSeconds">Unmodulated carrier before the burst (the key-up).</param>
    /// <param name="leadOutSeconds">Unmodulated carrier after it.</param>
    /// <returns>Discriminator audio at the modem's own rate, scaled so that full deviation is
    /// +/-1.0 - so a correctly driven link returns roughly what went in.</returns>
    public float[] Apply(
        ReadOnlySpan<float> audio, double cnrDb, double leadInSeconds = 0.1,
        double leadOutSeconds = 0.1)
    {
        int leadIn = (int)(leadInSeconds * _audioRate);
        int leadOut = (int)(leadOutSeconds * _audioRate);
        var padded = new float[leadIn + audio.Length + leadOut];
        audio.CopyTo(padded.AsSpan(leadIn));

        float[] shaped = ShapeTransmitAudio(padded);
        float[] deviation = ScaleToDeviation(shaped, leadIn, audio.Length);
        (double[] i, double[] q) = Modulate(deviation);
        if (_profile.FlutterDopplerHz > 0)
        {
            ApplyFlutter(i, q);
        }

        AddNoise(i, q, cnrDb);
        FilterIf(i, q);
        float[] discriminated = Discriminate(i, q);
        return ShapeReceiveAudio(discriminated);
    }

    // The transmit audio path: the microphone or data-port filter, then pre-emphasis. Both are
    // properties of the radio, not the modem, and both are applied before anything reaches the
    // modulator - so a mode whose energy sits where the path rolls off never gets on the air.
    private float[] ShapeTransmitAudio(float[] audio)
    {
        float[] shaped = BandLimit(audio, _audioRate, _profile.TxAudioLowHz, _profile.TxAudioHighHz);
        return _profile.PreEmphasisMicroseconds > 0
            ? PreEmphasise(shaped, _audioRate, _profile.PreEmphasisMicroseconds)
            : shaped;
    }

    // Drive calibration, done the way the bench does it: find the burst's own peak and scale that
    // to the target deviation. Measuring the peak over the burst only (not the silent padding)
    // keeps a long key-up from flattering the drive.
    private float[] ScaleToDeviation(float[] shaped, int burstStart, int burstLength)
    {
        double peak = 0;
        int end = Math.Min(shaped.Length, burstStart + burstLength);
        for (int n = burstStart; n < end; n++)
        {
            peak = Math.Max(peak, Math.Abs(shaped[n]));
        }

        if (peak <= 0)
        {
            return new float[shaped.Length];
        }

        double scale = AppliedDeviationHz / peak;
        var deviation = new float[shaped.Length];
        for (int n = 0; n < shaped.Length; n++)
        {
            deviation[n] = (float)(shaped[n] * scale);
        }

        return deviation;
    }

    // Frequency modulation proper: interpolate to the IF rate, integrate the instantaneous
    // frequency into a phase, and walk the unit circle. The envelope is constant, which is what
    // makes a limiter possible at the far end and what makes flat fading a CNR change rather than
    // an amplitude change.
    private (double[] I, double[] Q) Modulate(float[] deviationHzAtAudioRate)
    {
        float[] fine = Interpolate(deviationHzAtAudioRate, _interpolation, _ifRate);
        var i = new double[fine.Length];
        var q = new double[fine.Length];
        double phase = 0;
        double k = 2 * Math.PI / _ifRate;
        for (int n = 0; n < fine.Length; n++)
        {
            phase += k * fine[n];
            if (phase > Math.PI)
            {
                phase -= 2 * Math.PI;
            }
            else if (phase < -Math.PI)
            {
                phase += 2 * Math.PI;
            }

            i[n] = Math.Cos(phase);
            q[n] = Math.Sin(phase);
        }

        return (i, q);
    }

    // Flat Rayleigh fading on the carrier - VHF/UHF flutter. On a constant-envelope signal this is
    // a CNR excursion rather than an audio-amplitude one, so a deep fade does not make the modem
    // quieter: it drops the link under threshold and fills the output with clicks.
    private void ApplyFlutter(double[] i, double[] q)
    {
        double sigma = _profile.FlutterDopplerHz / 2.0;
        (double[] gi, double[] gq) = FadingGains(i.Length, sigma);
        for (int n = 0; n < i.Length; n++)
        {
            double ri = (i[n] * gi[n]) - (q[n] * gq[n]);
            double rq = (i[n] * gq[n]) + (q[n] * gi[n]);
            i[n] = ri;
            q[n] = rq;
        }
    }

    // Complex white noise at the stated carrier-to-noise ratio. The ratio is defined in the IF
    // bandwidth, so the noise is generated across the whole IF sample rate at the density that
    // leaves exactly that ratio once the IF filter has cut it down.
    private void AddNoise(double[] i, double[] q, double cnrDb)
    {
        if (double.IsPositiveInfinity(cnrDb))
        {
            return;
        }

        double cnr = Math.Pow(10, cnrDb / 10.0);
        // Carrier power is 1 (unit circle). Noise density that yields 1/cnr inside the IF filter.
        double totalNoisePower = _ifRate / (cnr * _profile.IfBandwidthHz);
        double sigma = Math.Sqrt(totalNoisePower / 2.0);
        for (int n = 0; n < i.Length; n++)
        {
            i[n] += sigma * Gaussian();
            q[n] += sigma * Gaussian();
        }
    }

    // The receiver's IF filter. On the complex envelope a channel filter centred on the carrier is
    // just a low-pass on each of I and Q at half the bandwidth.
    private void FilterIf(double[] i, double[] q)
    {
        int taps = 129;
        float[] kernel = FilterDesign.LowPass(_profile.IfBandwidthHz / 2, _ifRate, taps);
        FilterInPlace(i, kernel);
        FilterInPlace(q, kernel);
    }

    // Limiter and discriminator in one step: the angle between successive samples is the
    // instantaneous frequency and knows nothing about amplitude, which is exactly what a limiting
    // FM receiver delivers. Output is normalised so that full deviation reads +/-1.
    private float[] Discriminate(double[] i, double[] q)
    {
        var audio = new float[i.Length];
        double scale = _ifRate / (2 * Math.PI) / _profile.PeakDeviationHz;
        for (int n = 1; n < i.Length; n++)
        {
            double di = (i[n] * i[n - 1]) + (q[n] * q[n - 1]);
            double dq = (q[n] * i[n - 1]) - (i[n] * q[n - 1]);
            audio[n] = (float)(Math.Atan2(dq, di) * scale);
        }

        audio[0] = audio.Length > 1 ? audio[1] : 0;
        return audio;
    }

    // The receive audio path: de-emphasis, the speaker or discriminator-tap filter, and back down
    // to the modem's rate.
    private float[] ShapeReceiveAudio(float[] discriminated)
    {
        float[] shaped = _profile.DeEmphasisMicroseconds > 0
            ? DeEmphasise(discriminated, _ifRate, _profile.DeEmphasisMicroseconds)
            : discriminated;
        float[] atAudioRate = Decimate(shaped, _interpolation, _ifRate);
        return BandLimit(atAudioRate, _audioRate, _profile.RxAudioLowHz, _profile.RxAudioHighHz);
    }

    private static float[] BandLimit(float[] audio, int rate, double lowHz, double highHz)
    {
        double nyquist = rate / 2.0;
        double high = Math.Min(highHz, nyquist * 0.95);
        int taps = 127;
        float[] kernel = lowHz > 20
            ? FilterDesign.BandPass(lowHz, high, rate, taps)
            : FilterDesign.LowPass(high, rate, taps);
        var filter = new FirFilter(kernel);
        var output = new float[audio.Length];
        for (int n = 0; n < audio.Length; n++)
        {
            output[n] = filter.Next(audio[n]);
        }

        return output;
    }

    // Pre-emphasis: a first-order high-frequency lift, 6 dB per octave above 1/(2*pi*tau),
    // normalised to unity gain at 1 kHz so the drive calibration that follows is not chasing the
    // emphasis network's own gain.
    private static float[] PreEmphasise(float[] audio, int rate, double microseconds)
    {
        double tau = microseconds * 1e-6;
        double a = 1.0 / (1.0 + (1.0 / (rate * tau)));
        double reference = Math.Sqrt(1 + Math.Pow(2 * Math.PI * 1000 * tau, 2));
        var output = new float[audio.Length];
        double previousIn = 0;
        double previousOut = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            double y = (a * previousOut) + (audio[n] - previousIn);
            output[n] = (float)(y / reference * (1 / a));
            previousIn = audio[n];
            previousOut = y;
        }

        return output;
    }

    // De-emphasis: the matching single-pole roll-off, which is also what turns the discriminator's
    // rising noise spectrum back into something close to flat.
    private static float[] DeEmphasise(float[] audio, int rate, double microseconds)
    {
        double tau = microseconds * 1e-6;
        double a = 1.0 - Math.Exp(-1.0 / (rate * tau));
        double reference = 1.0 / Math.Sqrt(1 + Math.Pow(2 * Math.PI * 1000 * tau, 2));
        var output = new float[audio.Length];
        double y = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            y += a * (audio[n] - y);
            output[n] = (float)(y / reference);
        }

        return output;
    }

    private static float[] Interpolate(float[] input, int factor, int outputRate)
    {
        var stuffed = new float[input.Length * factor];
        for (int n = 0; n < input.Length; n++)
        {
            stuffed[n * factor] = input[n] * factor;
        }

        float[] kernel = FilterDesign.LowPass(outputRate / (2.0 * factor) * 0.9, outputRate, 127);
        var filter = new FirFilter(kernel);
        for (int n = 0; n < stuffed.Length; n++)
        {
            stuffed[n] = filter.Next(stuffed[n]);
        }

        return stuffed;
    }

    private static float[] Decimate(float[] input, int factor, int inputRate)
    {
        float[] kernel = FilterDesign.LowPass(inputRate / (2.0 * factor) * 0.9, inputRate, 127);
        var filter = new FirFilter(kernel);
        var filtered = new float[input.Length];
        for (int n = 0; n < input.Length; n++)
        {
            filtered[n] = filter.Next(input[n]);
        }

        var output = new float[input.Length / factor];
        for (int n = 0; n < output.Length; n++)
        {
            output[n] = filtered[n * factor];
        }

        return output;
    }

    private static void FilterInPlace(double[] signal, float[] kernel)
    {
        var filter = new FirFilter(kernel);
        for (int n = 0; n < signal.Length; n++)
        {
            signal[n] = filter.Next((float)signal[n]);
        }
    }

    // A flat Rayleigh gain process: complex Gaussian samples at a low rate, shaped to the Doppler
    // spread and interpolated up - the same construction WattersonChannel uses per path, applied
    // here to the single carrier.
    private (double[] I, double[] Q) FadingGains(int count, double sigmaHz)
    {
        const int GainRate = 200;
        int decimation = Math.Max(1, _ifRate / GainRate);
        int lowCount = (count / decimation) + 2;
        var ri = new double[lowCount];
        var rq = new double[lowCount];
        for (int n = 0; n < lowCount; n++)
        {
            ri[n] = Gaussian();
            rq[n] = Gaussian();
        }

        // Gaussian Doppler shaping at the low rate.
        double bandwidth = Math.Max(sigmaHz, 0.01) / GainRate;
        int taps = Math.Min(lowCount, 129);
        var kernel = new double[taps];
        double sum = 0;
        for (int k = 0; k < taps; k++)
        {
            double t = k - ((taps - 1) / 2.0);
            double v = Math.Exp(-2 * Math.PI * Math.PI * bandwidth * bandwidth * t * t);
            kernel[k] = v;
            sum += v;
        }

        for (int k = 0; k < taps; k++)
        {
            kernel[k] /= sum;
        }

        var si = new double[lowCount];
        var sq = new double[lowCount];
        for (int n = 0; n < lowCount; n++)
        {
            double ai = 0;
            double aq = 0;
            for (int k = 0; k < taps; k++)
            {
                int m = n - k + ((taps - 1) / 2);
                if (m >= 0 && m < lowCount)
                {
                    ai += kernel[k] * ri[m];
                    aq += kernel[k] * rq[m];
                }
            }

            si[n] = ai;
            sq[n] = aq;
        }

        // Unit mean power, so flutter moves the CNR about rather than shifting it.
        double power = 0;
        for (int n = 0; n < lowCount; n++)
        {
            power += (si[n] * si[n]) + (sq[n] * sq[n]);
        }

        double norm = power > 0 ? Math.Sqrt(lowCount / power) : 1;
        var gi = new double[count];
        var gq = new double[count];
        for (int n = 0; n < count; n++)
        {
            int lo = n / decimation;
            double f = (n % decimation) / (double)decimation;
            gi[n] = ((si[lo] * (1 - f)) + (si[lo + 1] * f)) * norm;
            gq[n] = ((sq[lo] * (1 - f)) + (sq[lo + 1] * f)) * norm;
        }

        return (gi, gq);
    }

    private double Gaussian()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
