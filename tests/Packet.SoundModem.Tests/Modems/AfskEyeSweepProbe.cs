using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Bench probe (set <c>AFSK_EYE=1</c>): where the recovered clock instant sits in the eye, and
/// how wide the eye is, by sweeping the decision instant plus or minus 15 samples and scoring a
/// known bit stream at each. This is the measurement that says how much timing diversity can be
/// worth on a mode before any of it is built, and it is what explains the 2026-08-21 (later5)
/// result: at 300 baud (40 samples per bit) the error count is flat across a ten-sample
/// plateau, so phases 1 to 3 samples either side usually decide the same bit; at 1200 baud (10
/// samples per bit) the window is about [-2, 0] samples and one sample late nearly doubles the
/// bit errors.
/// </summary>
/// <remarks>
/// Reconstructs the slicer's input from <see cref="AfskDemodulator.DiagnosticTap"/> (one call
/// per sample, so its index is the demodulator's sample clock) and the clock instants from
/// <see cref="AfskDemodulator.PhaseTap"/>, which is the only thing that ties a recovered bit
/// back to the sample it was read at.
/// </remarks>
public class AfskEyeSweepProbe
{
    private const int SampleRate = 12000;
    private const int Reach = 15;

    [Fact]
    public void Where_The_Clock_Instant_Sits_In_The_Eye()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("AFSK_EYE") is null, "bench probe only");

        var report = new List<string>();
        foreach ((string name, int baud, double shift, double half, double low, double[] sigmas) in
                 new (string, int, double, double, double, double[])[]
                 {
                     ("afsk300", 300, 100, 300, 300, [0.8, 1.0, 1.2]),
                     ("afsk1200", 1200, 500, 700, 650, [0.5, 0.8, 1.1]),
                 })
        {
            foreach (double sigma in sigmas)
            {
                var totals = new int[(2 * Reach) + 1];
                for (int seed = 1; seed <= 3; seed++)
                {
                    (List<float> excess, List<long> instants, byte[] sent) =
                        Capture(baud, shift, half, low, sigma, seed);
                    for (int offset = -Reach; offset <= Reach; offset++)
                    {
                        totals[offset + Reach] += ErrorsAt(excess, instants, sent, offset);
                    }
                }

                int best = 0;
                for (int i = 0; i < totals.Length; i++)
                {
                    if (totals[i] < totals[best])
                    {
                        best = i;
                    }
                }

                string counts = string.Join(",", totals);
                report.Add(FormattableString.Invariant(
                    $"{name} sigma {sigma}: best offset {best - Reach} samples, errors by offset -{Reach}..+{Reach}: {counts}"));
            }
        }

        Assert.Fail(string.Join("\n", report));
    }

    private static (List<float> Excess, List<long> Instants, byte[] Sent) Capture(
        int baud, double toneShift, double halfWidth, double lowPass, double sigma, int seed)
    {
        const double Centre = 1700;
        var random = new Random(seed);
        var sent = new byte[64 + 1024];
        for (int i = 0; i < 64; i++)
        {
            sent[i] = (byte)(i & 1);
        }

        for (int i = 64; i < sent.Length; i++)
        {
            sent[i] = (byte)random.Next(2);
        }

        var modulator = new AfskModulator(SampleRate, baud, Centre - toneShift, Centre + toneShift);
        float[] audio = modulator.ModulateLevels(sent);
        int pad = SampleRate / 5;
        var channel = new float[audio.Length + (2 * pad)];
        audio.CopyTo(channel, pad);
        for (int i = 0; i < channel.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            channel[i] += (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        var excess = new List<float>();
        var instants = new List<long>();
        var demodulator = new AfskDemodulator(
            SampleRate, static _ => { }, Centre, baud,
            bandPassHalfWidth: halfWidth, lowPassCutoff: lowPass, toneShift: toneShift,
            phaseBitSink: static (_, _) => { })
        {
            DiagnosticTap = (discriminator, high, low, _) =>
                excess.Add(discriminator - ((high + low) * 0.5f)),
        };
        demodulator.PhaseTap = (phase, instant, _, _) =>
        {
            if (phase == 0)
            {
                instants.Add(instant);
            }
        };

        int block = SampleRate / 10;
        for (int position = 0; position < channel.Length; position += block)
        {
            demodulator.Process(channel.AsSpan(position, Math.Min(block, channel.Length - position)));
        }

        return (excess, instants, sent);
    }

    /// <summary>Bits wrong against what was sent, deciding every bit <paramref name="offset"/>
    /// samples from the clock instant, at the alignment and polarity that suit that offset
    /// best.</summary>
    private static int ErrorsAt(List<float> excess, List<long> instants, byte[] sent, int offset)
    {
        var bits = new List<int>(instants.Count);
        foreach (long instant in instants)
        {
            long at = instant + offset;
            bits.Add(at >= 0 && at < excess.Count && excess[(int)at] > 0 ? 1 : 0);
        }

        int best = int.MaxValue;
        for (int align = 0; align + sent.Length <= bits.Count; align++)
        {
            for (int invert = 0; invert < 2; invert++)
            {
                int wrong = 0;
                for (int i = 0; i < sent.Length; i++)
                {
                    if ((bits[align + i] ^ invert) != sent[i])
                    {
                        wrong++;
                    }
                }

                best = Math.Min(best, wrong);
            }
        }

        return best;
    }
}
