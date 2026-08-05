using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The frequency-shift decorator that moves the spec-fixed families (ms110d-*, freedv-*) off
/// their native audio centres. The claims here are the ones the plan gated the feature on:
/// a moved modem still decodes its own bursts (round trip), the translation is clean (image
/// rejection measured, not assumed), and moving costs no decode margin (AWGN parity - a fast
/// smoke in CI, a deeper env-gated ladder for the real verdict).
/// </summary>
public class FrequencyShiftedModemTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static byte[] TestFrame(int length, int seed)
    {
        var random = new Random(seed);
        var frame = new byte[length];
        random.NextBytes(frame);
        return frame;
    }

    private static IModem Create(string mode, double? centre, Action<byte[]> sink) =>
        ModemCatalog.Create(mode, Rate, sink, new ModemOptions(CentreFrequencyHz: centre));

    [Theory]
    [InlineData("ms110d-wn6", 3000)]
    [InlineData("ms110d-wn6", 5000)]
    [InlineData("ms110d-wn2", 3600)]
    [InlineData("freedv-datac3", 2500)]
    public void A_Frame_Round_Trips_Through_A_Moved_Modem(string mode, double centre)
    {
        byte[] frame = TestFrame(60, 11);
        var received = new List<byte[]>();
        IModem modem = Create(mode, centre, received.Add);

        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 50);
        modem.Process(new float[Rate / 10]);
        modem.Process(audio);
        modem.Process(new float[Rate]);

        received.Should().ContainSingle().Which.Should().Equal(frame,
            "the shift out and the shift back must cancel exactly at the decoder");
    }

    [Fact]
    public void The_Image_Of_A_Shifted_Burst_Is_Buried()
    {
        // The analytic-shift method's failure mode is a mirror image: input content at f leaks
        // to shift - f when the Hilbert response is not flat where the signal sits. At a 5000 Hz
        // centre (shift +3200) the whole image band (0-3030 Hz for MS110D's 170-3450 Hz input)
        // sits BELOW the shifted band (3370-6650 Hz), so the two are measurable separately -
        // which is why this test uses the large shift.
        IModem modem = Create("ms110d-wn6", 5000, _ => { });
        float[] burst = modem.Modulate(TestFrame(120, 5), txDelayMilliseconds: 50);

        // Hann-windowed DFT over a window in the middle of the burst.
        const int N = 16384;
        int offset = Math.Max(0, (burst.Length - N) / 2);
        var windowed = new double[N];
        for (int i = 0; i < N && offset + i < burst.Length; i++)
        {
            windowed[i] = burst[offset + i] * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (N - 1))));
        }

        double binHz = Rate / (double)N;
        double BandPower(double lowHz, double highHz)
        {
            double power = 0;
            for (int k = (int)(lowHz / binHz); k <= (int)(highHz / binHz); k++)
            {
                double re = 0, im = 0;
                for (int i = 0; i < N; i++)
                {
                    double angle = -2 * Math.PI * k * i / N;
                    re += windowed[i] * Math.Cos(angle);
                    im += windowed[i] * Math.Sin(angle);
                }

                power += (re * re) + (im * im);
            }

            return power;
        }

        double signal = BandPower(3400, 6650);
        double image = BandPower(200, 3100);
        double rejectionDb = 10 * Math.Log10(signal / image);

        output.WriteLine($"image rejection: {rejectionDb:F1} dB");
        rejectionDb.Should().BeGreaterThan(50,
            "a mirror image under -50 dB is below the mask floor and cannot cost decode margin");
    }

    [Fact]
    public void Moving_The_Modem_Costs_No_Decodes_At_A_Working_Snr()
    {
        // The CI-fast arm of the performance gate: at an SNR a bare ms110d-wn6 decodes cleanly,
        // the moved modem must too - same frames, same seeded noise. This catches gross
        // degradation (a broken Hilbert, a chewed tail); the env-gated ladder below is the
        // instrument for fractions of a dB.
        foreach (double? centre in new double?[] { null, 3000 })
        {
            int decoded = RunAwgnArm("ms110d-wn6", centre, frames: 3, snr3kDb: 16, seed: 400);
            decoded.Should().Be(3, $"centre {centre?.ToString() ?? "native"} at 16 dB SNR(3k)");
        }
    }

    [Fact]
    public void The_Shift_Ladder_Shows_No_Knee_Movement()
    {
        // The deep arm of the performance gate: an A/B/B' ladder around the decode knee. Run
        // with MS110D_SHIFT_GATE=1 (roughly 10-20 minutes). Rungs and count are overridable:
        // MS110D_SHIFT_GATE_RUNGS=8,10,12 MS110D_SHIFT_GATE_N=100 for a finer read.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("MS110D_SHIFT_GATE") == "1",
            "set MS110D_SHIFT_GATE=1 for the shift-decorator BER ladder (slow)");

        // Defaults straddle the measured WN6 knee (native 23/30 at 4 dB, clean by 6).
        double[] rungs = (Environment.GetEnvironmentVariable("MS110D_SHIFT_GATE_RUNGS") ?? "4,5,6,8")
            .Split(',').Select(double.Parse).ToArray();
        int n = int.Parse(Environment.GetEnvironmentVariable("MS110D_SHIFT_GATE_N") ?? "50");
        double?[] arms = [null, 3000, 5000];

        var successes = new Dictionary<(double? Centre, double Rung), int>();
        foreach (double rung in rungs)
        {
            foreach (double? centre in arms)
            {
                int ok = RunAwgnArm("ms110d-wn6", centre, n, rung, seed: 500 + (int)rung);
                successes[(centre, rung)] = ok;
                output.WriteLine($"centre {centre?.ToString() ?? "native"}, {rung} dB: {ok}/{n}");
            }
        }

        // The ladder must actually straddle the knee, or the comparison is vacuous.
        rungs.Any(r => successes[(null, r)] > n / 10 && successes[(null, r)] < n * 9 / 10)
            .Should().BeTrue("at least one rung must sit in the transition; recalibrate the rungs");

        // Per rung: the moved arms may not fall more than the two-sigma binomial wobble below
        // the bare arm. Aggregate: within 5 % overall, which at these counts resolves a knee
        // movement well under the 0.2 dB the plan allows.
        double sigma = Math.Sqrt(0.25 * n) * 2;
        foreach (double rung in rungs)
        {
            foreach (double? centre in arms.Where(c => c is not null))
            {
                successes[(centre, rung)].Should().BeGreaterThanOrEqualTo(
                    (int)(successes[(null, rung)] - sigma),
                    $"centre {centre} at {rung} dB may not fall past statistical wobble below native");
            }
        }

        foreach (double? centre in arms.Where(c => c is not null))
        {
            int moved = rungs.Sum(r => successes[(centre, r)]);
            int native = rungs.Sum(r => successes[(null, r)]);
            moved.Should().BeGreaterThanOrEqualTo(native - (int)(0.05 * n * rungs.Length),
                $"centre {centre} aggregate within 5 % of native");
        }
    }

    /// <summary>
    /// One arm of the AWGN gate: <paramref name="frames"/> bursts at <paramref name="snr3kDb"/>
    /// (noise power referenced to a 3 kHz bandwidth, the HF convention), each decoded by a
    /// fresh modem. Returns how many decoded byte-exact.
    /// </summary>
    private static int RunAwgnArm(string mode, double? centre, int frames, double snr3kDb, int seed)
    {
        int decoded = 0;
        for (int i = 0; i < frames; i++)
        {
            byte[] frame = TestFrame(60, seed + i);
            IModem tx = Create(mode, centre, _ => { });
            float[] burst = tx.Modulate(frame, txDelayMilliseconds: 50);

            // Signal power measured past the TXDELAY silence; noise sigma from the SNR in the
            // 3 kHz reference bandwidth: N0 = P/(3000·snr), sigma² = N0·(rate/2). Absolute
            // calibration cancels in the A/B - both arms are scaled identically.
            int skip = Rate / 20;
            double power = 0;
            for (int s = skip; s < burst.Length; s++)
            {
                power += burst[s] * burst[s];
            }

            power /= burst.Length - skip;
            double sigma = Math.Sqrt(power * (Rate / 2.0) / (3000 * Math.Pow(10, snr3kDb / 10)));

            // Noise covers a lead-in longer than the busy detector's floor-seeding window (the
            // sim harness's measured trap), the burst, and a tail.
            var noisy = new float[(Rate / 2) + burst.Length + (Rate / 4)];
            var noise = new Random(seed * 1000 + i);
            for (int s = 0; s < noisy.Length; s++)
            {
                // Box-Muller; one gaussian per sample.
                double u1 = 1.0 - noise.NextDouble();
                double u2 = noise.NextDouble();
                noisy[s] = (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }

            for (int s = 0; s < burst.Length; s++)
            {
                noisy[(Rate / 2) + s] += burst[s];
            }

            var received = new List<byte[]>();
            IModem rx = Create(mode, centre, received.Add);
            rx.Process(noisy);
            rx.Process(new float[Rate / 2]);
            if (received.Count == 1 && received[0].AsSpan().SequenceEqual(frame))
            {
                decoded++;
            }
        }

        return decoded;
    }
}
