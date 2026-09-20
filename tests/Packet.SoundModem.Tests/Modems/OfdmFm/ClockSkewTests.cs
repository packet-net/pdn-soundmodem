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
