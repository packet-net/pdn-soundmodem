using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// "Measure my filter" end to end, on synthetic shaped noise rather than a real rig: a sum of
/// many random-phase tones spaced across a chosen band, which is flat in power across that band
/// and silent outside it, the same shape a receive filter leaves on ordinary band noise. No
/// channel or sound card involved - <see cref="MailcastFilterMeasurer.Process"/> is fed directly,
/// the same call a receive tap would make.
/// </summary>
public sealed class MailcastFilterMeasurerTests
{
    private const int Rate = 48000;

    /// <summary>White noise passed through a windowed-sinc FIR bandpass, flat from
    /// <paramref name="lowHz"/> to <paramref name="highHz"/> and rolling off outside it - the
    /// same shape an analogue receive filter leaves on the station's own noise, and genuinely
    /// random rather than a fixed sum of tones (which would leave standing interference ripples
    /// in the averaged spectrum instead of a flat passband).</summary>
    private static float[] ShapedNoise(double lowHz, double highHz, int samples, int seed)
    {
        const int Taps = 801; // odd; about 200 Hz transition either side of the edges at 48 kHz
        int half = Taps / 2;
        var fir = new double[Taps];
        double fLow = lowHz / Rate;
        double fHigh = highHz / Rate;
        for (int n = 0; n < Taps; n++)
        {
            int k = n - half;
            double ideal = LowPass(k, fHigh) - LowPass(k, fLow);
            double window = 0.54 - (0.46 * Math.Cos(2 * Math.PI * n / (Taps - 1))); // Hamming
            fir[n] = ideal * window;
        }

        var random = new Random(seed);
        var white = new double[samples + Taps];
        for (int n = 0; n < white.Length; n++)
        {
            white[n] = (random.NextDouble() * 2) - 1;
        }

        var audio = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                sum += fir[t] * white[n + Taps - 1 - t];
            }

            audio[n] = (float)sum; // the windowed-sinc bandpass is close to unity gain in its passband
        }

        return audio;

        static double LowPass(int k, double fc) => k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
    }

    private static async Task<MailcastFilterMeasurement> MeasureAsync(MailcastFilterMeasurer measurer, float[] audio)
    {
        Task<MailcastFilterMeasurement> task = measurer.MeasureAsync(CancellationToken.None);
        const int Block = Rate / 10;
        for (int at = 0; at < audio.Length; at += Block)
        {
            measurer.Process(audio.AsSpan(at, Math.Min(Block, audio.Length - at)));
        }

        return await task;
    }

    [Fact]
    public async Task A_Sound_Cards_Shaped_Noise_Gives_Back_Roughly_Its_Own_Edges()
    {
        // 700 to 3200 Hz, wide enough for no "narrow filter" note: about 10.5 s so one
        // measurement (10 s) finishes within what is fed.
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => false);
        float[] audio = ShapedNoise(700, 3200, (int)(10.5 * Rate), seed: 1);

        MailcastFilterMeasurement measured = await MeasureAsync(measurer, audio);

        measured.LowHz.Should().NotBeNull();
        measured.LowHz!.Value.Should().BeInRange(600, 800);
        measured.HighHz!.Value.Should().BeInRange(3100, 3300);
        measured.Note.Should().BeNull();
    }

    [Fact]
    public async Task A_Narrow_Bands_Shaped_Noise_Gets_The_Narrow_Filter_Note()
    {
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => false);
        float[] audio = ShapedNoise(900, 2100, (int)(10.5 * Rate), seed: 2); // 1200 Hz wide

        MailcastFilterMeasurement measured = await MeasureAsync(measurer, audio);

        measured.WidthHz.Should().NotBeNull();
        measured.WidthHz!.Value.Should().BeLessThan(MailcastFilterAnalysis.NarrowFilterWidthHz);
        measured.Note.Should().NotBeNull().And.Contain("narrower than about 2.4 kHz");
    }

    [Fact]
    public async Task Audio_Fed_While_Skip_Says_So_Is_Left_Out_Of_The_Average()
    {
        // A wide, flat block (800 to 2600 Hz) that would read as a different, wider passband
        // than the narrow one actually measured, fed while skip() is true throughout a running
        // measurement - if it leaked into the average, the result would come out wider than
        // 900 to 2100.
        bool skipNow = true;
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => skipNow);
        Task<MailcastFilterMeasurement> task = measurer.MeasureAsync(CancellationToken.None);

        float[] distraction = ShapedNoise(800, 2600, (int)(3 * Rate), seed: 3);
        measurer.Process(distraction);

        skipNow = false;
        float[] real = ShapedNoise(900, 2100, (int)(10.5 * Rate), seed: 4);
        const int Block = Rate / 10;
        for (int at = 0; at < real.Length; at += Block)
        {
            measurer.Process(real.AsSpan(at, Math.Min(Block, real.Length - at)));
        }

        MailcastFilterMeasurement measured = await task;

        measured.LowHz!.Value.Should().BeInRange(800, 1000);
        measured.HighHz!.Value.Should().BeInRange(2000, 2200);
    }

    [Fact]
    public async Task A_Second_Measurement_While_One_Runs_Is_Refused()
    {
        var measurer = new MailcastFilterMeasurer(Rate, skip: () => false);
        Task<MailcastFilterMeasurement> first = measurer.MeasureAsync(CancellationToken.None);

        Func<Task> second = () => measurer.MeasureAsync(CancellationToken.None);

        await second.Should().ThrowAsync<InvalidOperationException>();

        // Let the first one finish rather than leaving a timer running past the test.
        measurer.Process(ShapedNoise(700, 3200, (int)(10.5 * Rate), seed: 5));
        await first;
    }
}
