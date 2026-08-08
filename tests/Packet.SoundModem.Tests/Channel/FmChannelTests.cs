namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The FM link model, checked against the behaviour that makes FM different from a linear channel.
/// </summary>
/// <remarks>
/// None of these effects is programmed in: the model frequency-modulates a carrier, adds noise to
/// it, and takes the angle between successive samples. The threshold, the rising noise spectrum and
/// the emphasis interaction all fall out of that, and if they did not, the model would be
/// reproducing its author's assumptions instead of FM. That is what these tests are for.
/// </remarks>
public class FmChannelTests
{
    private const int AudioRate = 12000;

    private static float[] Tone(double hz, double seconds, int rate = AudioRate, double amplitude = 1.0)
    {
        var samples = new float[(int)(seconds * rate)];
        for (int n = 0; n < samples.Length; n++)
        {
            samples[n] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * n / rate));
        }

        return samples;
    }

    /// <summary>Power of <paramref name="audio"/> within a band, by direct correlation against a
    /// tone pair - enough to compare bands without pulling in an FFT.</summary>
    private static double BandPower(float[] audio, double hz, int rate = AudioRate)
    {
        double re = 0;
        double im = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            double theta = 2 * Math.PI * hz * n / rate;
            re += audio[n] * Math.Cos(theta);
            im += audio[n] * Math.Sin(theta);
        }

        return ((re * re) + (im * im)) / ((double)audio.Length * audio.Length);
    }

    private static double TotalPower(float[] audio, int from, int to)
    {
        double sum = 0;
        for (int n = from; n < to; n++)
        {
            sum += (double)audio[n] * audio[n];
        }

        return sum / Math.Max(1, to - from);
    }

    /// <summary>Output SNR on a tone: the recovered tone's power over everything else.</summary>
    private static double OutputSnrDb(float[] output, double toneHz, int from, int to)
    {
        var window = output[from..to];
        double signal = BandPower(window, toneHz) * 2;
        double total = TotalPower(window, 0, window.Length);
        double noise = Math.Max(total - signal, 1e-12);
        return 10 * Math.Log10(signal / noise);
    }

    [Fact]
    public void A_Correctly_Driven_Link_Returns_What_Went_In()
    {
        var channel = new FmChannel(FmLinkProfile.DataPort(2400), AudioRate, seed: 1);
        float[] audio = Tone(1000, 0.2);

        float[] output = channel.Apply(audio, double.PositiveInfinity);

        // The output is deviation-normalised, so a correctly driven link hands back what it was
        // given: a full-deviation tone comes out at full scale.
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.1 * AudioRate);
        double peak = 0;
        for (int n = start; n < end; n++)
        {
            peak = Math.Max(peak, Math.Abs(output[n]));
        }

        peak.Should().BeApproximately(1.0, 0.1);
    }

    [Fact]
    public void A_Narrow_If_Filter_Truncates_Sidebands_And_Distorts()
    {
        // Not a defect, and worth a test of its own because it bounds what any dense constellation
        // can do on a narrow channel. Full deviation on a low tone spreads significant Bessel
        // sidebands to roughly 2*(beta+2)*fm - about 8.8 kHz at 2.4 kHz deviation and 1 kHz - so a
        // 12.5 kHz channel's 8 kHz IF filter really does cut them, and symmetric truncation of an
        // FM spectrum comes back as odd-order distortion. Measured here: third harmonic at about
        // -25 dB on an 8 kHz filter, falling away as the filter widens. A noiseless link is
        // therefore NOT arbitrarily clean, which is exactly the sort of ceiling a QAM-256 mode
        // meets first.
        float[] audio = Tone(1000, 0.3);
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.2 * AudioRate);

        static double Snr(double ifBandwidth, float[] audio, int start, int end)
        {
            var profile = FmLinkProfile.DataPort(2400) with { IfBandwidthHz = ifBandwidth };
            float[] output = new FmChannel(profile, AudioRate, 1).Apply(audio, double.PositiveInfinity);
            return OutputSnrDb(output, 1000, start, end);
        }

        double narrow = Snr(8000, audio, start, end);
        double wide = Snr(16000, audio, start, end);

        narrow.Should().BeLessThan(35, "an 8 kHz filter cuts sidebands a 2.4 kHz deviation puts outside it");
        wide.Should().BeGreaterThan(narrow + 20, "widening the filter must recover the truncated sidebands");
    }

    [Fact]
    public void The_Discriminator_Beats_Its_Input_Cnr_Above_Threshold()
    {
        // FM's whole bargain: spend bandwidth, get output SNR above the carrier-to-noise ratio you
        // paid for. Measured on a 25 kHz-channel radio (16 kHz IF), where the sidebands survive and
        // the improvement is not masked by the truncation distortion above.
        var profile = FmLinkProfile.DataPort(2400) with { IfBandwidthHz = 16000 };
        var channel = new FmChannel(profile, AudioRate, seed: 7);
        float[] audio = Tone(1000, 0.3);

        float[] output = channel.Apply(audio, cnrDb: 20);

        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.2 * AudioRate);
        OutputSnrDb(output, 1000, start, end).Should().BeGreaterThan(20,
            "a modulation index of 2.4 should return more audio SNR than the carrier-to-noise "
            + "ratio that bought it");
    }

    [Fact]
    public void Output_Snr_Collapses_Below_Threshold_Faster_Than_The_Input_Does()
    {
        // The threshold effect, which is the single most important thing about an FM link and the
        // reason FM modes do not degrade gracefully: 6 dB off the carrier costs far more than 6 dB
        // of audio once the clicks start.
        var profile = FmLinkProfile.DataPort(2400);
        float[] audio = Tone(1000, 0.3);
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.2 * AudioRate);

        double high = OutputSnrDb(new FmChannel(profile, AudioRate, 11).Apply(audio, 18), 1000, start, end);
        double middle = OutputSnrDb(new FmChannel(profile, AudioRate, 11).Apply(audio, 12), 1000, start, end);
        double low = OutputSnrDb(new FmChannel(profile, AudioRate, 11).Apply(audio, 4), 1000, start, end);

        double gentleSlope = high - middle;
        double cliffSlope = middle - low;
        high.Should().BeGreaterThan(middle);
        middle.Should().BeGreaterThan(low);
        (cliffSlope / Math.Max(gentleSlope, 0.5)).Should().BeGreaterThan(1.5,
            "below threshold the output must fall away faster per dB than it does above it");
    }

    [Fact]
    public void Discriminator_Noise_Rises_With_Audio_Frequency()
    {
        // Triangular noise: a discriminator's output noise power grows with the square of audio
        // frequency. This is why pre/de-emphasis exists at all, and it is the reason a wideband
        // audio mode cannot be masked honestly against flat AWGN - its top subcarriers live where
        // the noise is worst.
        var flat = FmLinkProfile.DataPort(2400) with { RxAudioHighHz = 5000, TxAudioHighHz = 5000 };
        var channel = new FmChannel(flat, AudioRate, seed: 3);

        // No modulation at all: whatever comes out is the discriminator's own noise.
        float[] silence = new float[(int)(0.4 * AudioRate)];
        float[] noise = channel.Apply(silence, cnrDb: 25);

        int start = (int)(0.2 * AudioRate);
        var window = noise[start..(start + (int)(0.15 * AudioRate))];
        double lowBand = 0;
        double highBand = 0;
        for (double hz = 400; hz <= 800; hz += 50)
        {
            lowBand += BandPower(window, hz);
        }

        for (double hz = 3200; hz <= 3600; hz += 50)
        {
            highBand += BandPower(window, hz);
        }

        (highBand / lowBand).Should().BeGreaterThan(4,
            "noise power should rise roughly as the square of frequency, so a 4x higher band "
            + "carries far more of it");
    }

    [Fact]
    public void De_Emphasis_Flattens_The_Noise_It_Rises_By()
    {
        var flat = FmLinkProfile.DataPort(2400) with { RxAudioHighHz = 5000, TxAudioHighHz = 5000 };
        var emphasised = flat with { PreEmphasisMicroseconds = 750, DeEmphasisMicroseconds = 750 };
        float[] silence = new float[(int)(0.4 * AudioRate)];
        int start = (int)(0.2 * AudioRate);

        static double Tilt(float[] noise, int start)
        {
            var window = noise[start..(start + (int)(0.15 * AudioRate))];
            double low = 0;
            double high = 0;
            for (double hz = 400; hz <= 800; hz += 50)
            {
                low += BandPower(window, hz);
            }

            for (double hz = 3200; hz <= 3600; hz += 50)
            {
                high += BandPower(window, hz);
            }

            return high / low;
        }

        double flatTilt = Tilt(new FmChannel(flat, AudioRate, 5).Apply(silence, 25), start);
        double deEmphasisedTilt = Tilt(new FmChannel(emphasised, AudioRate, 5).Apply(silence, 25), start);

        deEmphasisedTilt.Should().BeLessThan(flatTilt,
            "de-emphasis exists precisely to take the tilt out of discriminator noise");
    }

    [Fact]
    public void Under_Deviation_Costs_Output_Signal_To_Noise()
    {
        // Drive calibration is not cosmetic: half the deviation is half the modulation index, and
        // the output SNR follows. This is the sim's answer to "does it matter if the drive is
        // wrong", and it is why the deviation table in mode-modulation-reference.md exists.
        var correct = FmLinkProfile.DataPort(2400);
        var under = correct with { DeviationErrorDb = -6 };
        float[] audio = Tone(1000, 0.3);
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.2 * AudioRate);

        double right = OutputSnrDb(new FmChannel(correct, AudioRate, 9).Apply(audio, 14), 1000, start, end);
        double wrong = OutputSnrDb(new FmChannel(under, AudioRate, 9).Apply(audio, 14), 1000, start, end);

        wrong.Should().BeLessThan(right - 2, "under-deviating throws away modulation index");
    }

    [Fact]
    public void A_Microphone_Path_Removes_What_A_Data_Port_Passes()
    {
        // The reason OFDM-AB has four bandwidth profiles rather than one: a microphone input simply
        // does not carry 4 kHz, however good the link is.
        float[] high = Tone(4000, 0.2);
        var mic = new FmChannel(FmLinkProfile.MicAndSpeaker(3000), AudioRate, seed: 2);
        var data = new FmChannel(FmLinkProfile.DataPort(3000, audioHighHz: 5000), AudioRate, seed: 2);
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.1 * AudioRate);

        float[] throughMic = mic.Apply(high, double.PositiveInfinity);
        float[] throughData = data.Apply(high, double.PositiveInfinity);

        double micPower = BandPower(throughMic[start..end], 4000);
        double dataPower = BandPower(throughData[start..end], 4000);
        (dataPower / Math.Max(micPower, 1e-15)).Should().BeGreaterThan(100,
            "a 300-3000 Hz microphone path should not deliver a 4 kHz tone");
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Link()
    {
        float[] audio = Tone(1000, 0.1);

        float[] first = new FmChannel(FmLinkProfile.DataPort(2400), AudioRate, 42).Apply(audio, 12);
        float[] second = new FmChannel(FmLinkProfile.DataPort(2400), AudioRate, 42).Apply(audio, 12);

        second.Should().Equal(first);
    }

    [Fact]
    public void Flutter_Drags_The_Link_Under_Threshold()
    {
        // Flat fading on a constant-envelope carrier does not make the audio quieter - it moves the
        // carrier-to-noise ratio about, and the fades that go under threshold fill the output with
        // clicks. So a fluttering link at a given mean CNR is worse than a static one at the same
        // CNR, which is not true of a linear channel.
        var stat = FmLinkProfile.DataPort(2400);
        var fluttering = stat with { FlutterDopplerHz = 5 };
        float[] audio = Tone(1000, 0.5);
        int start = (int)(0.15 * AudioRate);
        int end = start + (int)(0.3 * AudioRate);

        // Pooled over several fade realisations on purpose: the gain process is normalised to unit
        // mean power, so any single burst can draw a kind one and a one-seed comparison is a coin
        // flip (measured: 0.01 dB apart on seed 21). The cost of flutter is a property of the
        // distribution, not of a burst.
        double steady = 0;
        double flutter = 0;
        const int Seeds = 6;
        for (int seed = 21; seed < 21 + Seeds; seed++)
        {
            steady += OutputSnrDb(new FmChannel(stat, AudioRate, seed).Apply(audio, 14), 1000, start, end);
            flutter += OutputSnrDb(new FmChannel(fluttering, AudioRate, seed).Apply(audio, 14), 1000, start, end);
        }

        (flutter / Seeds).Should().BeLessThan(steady / Seeds);
    }
}
