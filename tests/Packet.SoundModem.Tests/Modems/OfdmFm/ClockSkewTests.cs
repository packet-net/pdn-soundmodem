using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The two ends of a link keep time with two different soundcards, and no two soundcards agree.
/// </summary>
/// <remarks>
/// <para>An FM audio path has no carrier to offset, so the only clock error left is the sample
/// clocks', and it is not exotic: consumer USB audio is routinely tens of ppm off nominal, and two
/// of them 100 ppm apart is an ordinary thing to own. A difference slides the receiver's transform
/// window slowly through the burst, which paints a phase tilt across the band that grows symbol by
/// symbol - the per-symbol pilot correction sees only its average, and what remains lands on the
/// band edges of the late symbols, densest constellations first.</para>
/// <para>Nothing in the FM link model moves the clocks, so no ladder ever measured this; before
/// the receiver tracked the tilt, 100 ppm alone - no noise, no channel - spent 77 % of QAM-256
/// 2/3's correcting power on a narrow profile, and 200 ppm failed it outright. These tests pin the
/// tracking with the skew applied the way the real fault arrives: the transmitted audio resampled
/// by the offset, nothing else in the path.</para>
/// </remarks>
public class ClockSkewTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public void A_Clock_Difference_Ordinary_For_Two_Soundcards_Does_Not_Cost_The_Payload(int ppm)
    {
        var profile = OfdmFmParameters.Synthetic with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
        };
        var codec = new OfdmFmBurstCodec(profile);
        var payload = new byte[256];
        new Random(1000).NextBytes(payload);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam256, 2);

        // Silence after the burst, as a real channel always has: the resampler shortens what it
        // is given, and the tail it takes must not be the burst's own last symbol.
        var padded = new float[clean.Length + 512];
        clean.CopyTo(padded, 0);

        OfdmFmBurst? burst = codec.Demodulate(Resample(padded, 1.0 + (ppm * 1e-6)));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(
            payload, "a {0} ppm clock difference should be tracked out, not survived", ppm);

        // Corrected, not merely absorbed by the code: on a noiseless loopback any spending at all
        // is tilt the tracker left behind, and more than a few per cent of it here would be the
        // uncorrected behaviour coming back.
        burst.PreFecBitErrorRate.Should().BeLessThan(0.01);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(3000)]
    public void A_Clock_Whose_Rate_Changes_Within_A_Long_Burst_Is_Tracked_Symbol_By_Symbol(int bytes)
    {
        // The burst-wide fit models one drift per burst, and a long burst on air does not get
        // one: on 2026-09-19 half the 4000-byte frames failed on a span where every 1024-byte one
        // decoded, the survivors read 3 to 5 dB below the short bursts, and reading each symbol's
        // own tilt from its pilots on top of the fit took the captured cell from 7 decoded to 17.
        // What the one-line model cannot follow is a rate that CHANGES across the burst, so that
        // is what this applies: the clock difference swings through a slow cycle over the burst,
        // large enough that the fitted average leaves a tilt at the ends QAM-64 cannot take. A
        // fast small wobble, which is what the tone captures showed, moves the sampling point by
        // thousandths of a sample and is not the case; it was tried first and told nothing.
        var wide = OfdmFmParameters.Synthetic with
        {
            FirstCarrier = 4,
            DataCarriers = 50,
            PilotCarriers = 8,
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
        };
        var codec = new OfdmFmBurstCodec(wide);
        var payload = new byte[bytes];
        new Random(bytes).NextBytes(payload);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam64, 2);
        var padded = new float[clean.Length + 1024];
        clean.CopyTo(padded, 0);

        // 6 ppm of constant difference, as the bench has, plus 80 ppm swinging at 0.6 Hz: about
        // a fifth of a sample of timing excursion over the burst, a tilt of some 12 degrees at
        // the band edge that a straight line through it halves at best.
        float[] heard = ResampleWandering(padded, wide.SampleRate, 6, 80, 0.6);

        OfdmFmBurst? burst = codec.Demodulate(heard);

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload, "{0} bytes through a clock whose rate changes", bytes);
        burst.PreFecBitErrorRate.Should().BeLessThan(0.02);
    }

    /// <summary>
    /// The shipped 8 kHz preset, in the shipped table, read through a clock difference far
    /// outside what the pilot fits were built for.
    /// </summary>
    /// <remarks>
    /// Measured on the two-Tait bench in November 2026: a CM108B's playback clock has been seen
    /// more than 600 ppm off for the first half second of a transmission, and a board without its
    /// "MIC PLL Adjust" EEPROM bit captures 520 ppm away from every other board's playback. The
    /// pilot fits lose the default preset by 300 ppm. The clock-tracked read measures each
    /// symbol's delay from all of its carriers and resamples, so the rate is simply a number it
    /// reports.
    /// </remarks>
    [Theory]
    [InlineData(520)]
    [InlineData(-520)]
    [InlineData(1000)]
    [InlineData(-1000)]
    public void A_Clock_Hundreds_Of_Parts_Per_Million_Out_Is_Measured_And_Taken_Out(int ppm)
    {
        var codec = new OfdmFmBurstCodec(OfdmFmPresets.Default8k, OfdmFmPresets.Table);
        var payload = new byte[1024];
        new Random(520).NextBytes(payload);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam64, 2);
        var padded = new float[clean.Length + 4096];
        clean.CopyTo(padded, 2048);

        OfdmFmBurst? burst = codec.Demodulate(Resample(padded, 1.0 + (ppm * 1e-6)));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
        burst.Reading.Should().Be("tracked");
        burst.PreFecBitErrorRate.Should().BeLessThan(0.002);

        // A receiver clock running slow by so many ppm takes fewer samples than were played, so
        // the burst arrives compressed and the measured rate is the negative of the resampling
        // ratio's excess.
        burst.Clock.Should().NotBeNull();
        burst.Clock!.RatePpm.Should().BeApproximately(-ppm, 15);
        burst.Clock.WanderMicroseconds.Should().BeLessThan(2);
    }

    /// <summary>
    /// A clock that changes rate sharply and repeatedly inside one burst, which is what a
    /// CM108B's does: measured against the samples that were played, its rate steps by several
    /// hundred ppm every few tens of milliseconds. One rate per burst does not describe that and
    /// a prediction from the symbol before is wrong by half a sample at every turn, which is why
    /// each symbol's delay is searched for and not only refined.
    /// </summary>
    [Fact]
    public void A_Clock_That_Zig_Zags_Inside_A_Burst_Is_Followed_Symbol_By_Symbol()
    {
        var codec = new OfdmFmBurstCodec(OfdmFmPresets.Default8k, OfdmFmPresets.Table);
        var payload = new byte[1024];
        new Random(300).NextBytes(payload);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam64, 2);
        var padded = new float[clean.Length + 4096];
        clean.CopyTo(padded, 2048);

        // 100 ppm mean, switching 300 ppm either side of it every 60 ms.
        float[] heard = ResampleAlong(
            padded, 48000, seconds => 100 + (((int)(seconds / 0.06) % 2) == 0 ? 300 : -300));
        OfdmFmBurst? burst = codec.Demodulate(heard);

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
        burst.Reading.Should().Be("tracked");
        burst.PreFecBitErrorRate.Should().BeLessThan(0.01);
        burst.Clock!.WanderMicroseconds.Should().BeGreaterThan(5);
    }

    /// <summary>
    /// A burst that arrives stretched, through the streaming receiver fed one sample at a time,
    /// so that it could decode at the very instant the burst's announced length has arrived.
    /// </summary>
    /// <remarks>
    /// The tail of a stretched burst's last symbol lies past its announced end: fifty-odd samples
    /// at the 1930 ppm two CM108B cards were measured apart with their "MIC PLL Adjust" bit
    /// clear. A receiver that reads the burst before those samples have arrived reads zeros for
    /// them, which costs the last symbol and, at a rate with little to spare, the burst. So it
    /// waits for them, and this is the block size at which not waiting shows every time.
    /// </remarks>
    [Theory]
    [InlineData(-1000)]
    [InlineData(-1930)]
    public void A_Stretched_Burst_Is_Not_Read_Until_Its_Tail_Has_Arrived(int ppm)
    {
        var payload = new byte[1900];
        new Random(1900).NextBytes(payload);
        var codec = new OfdmFmBurstCodec(OfdmFmPresets.Edge8k, OfdmFmPresets.Table);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam64, 2);
        var padded = new float[clean.Length + 8192];
        clean.CopyTo(padded, 2048);
        float[] heard = Resample(padded, 1.0 + (ppm * 1e-6));

        var delivered = new List<byte[]>();
        var bursts = new List<OfdmFmBurst>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Edge8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        receiver.BurstHeard += bursts.Add;
        var one = new float[1];
        foreach (float sample in heard)
        {
            one[0] = sample;
            receiver.Process(one);
        }

        delivered.Should().ContainSingle().Which.Should().Equal(payload);
        bursts.Should().ContainSingle();
        bursts[0].PreFecBitErrorRate.Should().BeLessThan(0.001);
    }

    /// <summary>A burst as a receiver would capture it if its clock ran at a rate, in parts per
    /// million against the sender's, given as a function of time.</summary>
    internal static float[] ResampleAlong(float[] audio, int sampleRate, Func<double, double> ppmAt)
    {
        const int HalfTaps = 16;
        var output = new List<float>(audio.Length);
        double position = HalfTaps + 4;
        int i = 0;
        while (position < audio.Length - HalfTaps - 4)
        {
            int centre = (int)Math.Floor(position);
            double fraction = position - centre;
            double sum = 0;
            for (int t = -HalfTaps; t <= HalfTaps; t++)
            {
                double x = t - fraction;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = 0.5 + (0.5 * Math.Cos(Math.PI * x / (HalfTaps + 1)));
                sum += sinc * window * audio[centre + t];
            }

            output.Add((float)sum);
            position += 1.0 + (ppmAt(i / (double)sampleRate) * 1e-6);
            i++;
        }

        return [.. output];
    }

    /// <summary>
    /// The same resampler with the clock ratio varying along the burst: a constant part in ppm
    /// and a sinusoidal wobble on top, which is what the bench's soundcards were measured doing.
    /// </summary>
    internal static float[] ResampleWandering(
        float[] audio, int sampleRate, double ppm, double wobblePpm, double wobbleHz)
    {
        const int HalfTaps = 16;
        var output = new List<float>(audio.Length);
        double position = HalfTaps + 4;
        int i = 0;
        while (position < audio.Length - HalfTaps - 4)
        {
            int centre = (int)Math.Floor(position);
            double fraction = position - centre;
            double sum = 0;
            double weightSum = 0;
            for (int t = -HalfTaps; t <= HalfTaps; t++)
            {
                int index = centre + t;
                if (index < 0 || index >= audio.Length)
                {
                    continue;
                }

                double x = t - fraction;
                double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = 0.5 + (0.5 * Math.Cos(Math.PI * x / (HalfTaps + 1)));
                double weight = sinc * window;
                sum += weight * audio[index];
                weightSum += weight;
            }

            output.Add((float)(weightSum == 0 ? 0 : sum / weightSum));
            double seconds = i / (double)sampleRate;
            double ratio = 1.0 + ((ppm + (wobblePpm * Math.Sin(2 * Math.PI * wobbleHz * seconds))) * 1e-6);
            position += ratio;
            i++;
        }

        return [.. output];
    }

    /// <summary>
    /// Windowed-sinc fractional resampler. Transparent to far below the distortion any assertion
    /// here could see, so what the tests measure is the clock difference and not the interpolator.
    /// Kept here rather than taken from the link model's ReceiverClockOffsetPpm deliberately:
    /// these tests skew a bare loopback with no channel in the path at all, which is what
    /// isolates the receiver's own tracking from everything the model adds.
    /// </summary>
    internal static float[] Resample(float[] audio, double ratio)
    {
        const int HalfTaps = 16;
        int length = (int)(audio.Length / ratio) - (2 * HalfTaps) - 8;
        var output = new float[Math.Max(length, 0)];
        for (int i = 0; i < output.Length; i++)
        {
            double position = (i * ratio) + HalfTaps + 4;
            int centre = (int)Math.Floor(position);
            double fraction = position - centre;
            double sum = 0;
            double weightSum = 0;
            for (int t = -HalfTaps; t <= HalfTaps; t++)
            {
                int index = centre + t;
                if (index < 0 || index >= audio.Length)
                {
                    continue;
                }

                double x = t - fraction;
                double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = 0.5 * (1 + Math.Cos(Math.PI * x / (HalfTaps + 1)));
                double tap = sinc * window;
                sum += audio[index] * tap;
                weightSum += tap;
            }

            output[i] = (float)(weightSum > 0 ? sum / weightSum : 0);
        }

        return output;
    }
}
