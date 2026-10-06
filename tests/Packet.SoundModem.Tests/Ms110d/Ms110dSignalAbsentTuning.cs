using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// The tuning instrument behind the signal-absent window and line (issue #553,
/// docs/dev/ms110d/signal-absent.md; <c>MS110D_SIGNAL_ABSENT_TUNE=1</c>, hours on 14 cores).
/// Every case runs with the release switched off, so the receiver behaves as it did before
/// #553, and writes its event trace to <c>MS110D_TUNE_OUT</c>: the probe-presence statistic per
/// frame (plain and floored), each frame read, each burst end and carrier change, in the order
/// they happened. Because the statistic writes no receiver state, any window and line can then
/// be scored offline, exactly, by evidence/2026-10-06-signal-absent-tuning/evaluate.py: a frame
/// read after the point that setting would have let go is a frame it would have lost.
/// <c>MS110D_TUNE_SET</c> picks the set: A slow flat Rayleigh, B scripted 4 to 8 s fades, C weak
/// bursts then noise, D the AWGN and Poor mask points, V the validation seeds for A, E scripted
/// 10 to 18 s fades, K steady carriers on and off the probe lines under a readable burst, T the
/// same carriers after a weak burst (scored with the C set), M two to four carriers under a
/// readable burst and N the same after a weak one (scored with the C set). Existing traces are kept, so an
/// interrupted set resumes.
/// </summary>
public class Ms110dSignalAbsentTuning(ITestOutputHelper output)
{
    private const int Rate = 9600;
    private const double Sigma = 0.05;
    private static readonly Dictionary<int, double> AwgnMask = new() { [1] = -3, [2] = 0, [3] = 3, [4] = 5, [5] = 6, [6] = 9, [7] = 13, [8] = 16, [13] = 6 };
    private static readonly Dictionary<int, double> PoorMask = new() { [1] = 3, [2] = 5, [3] = 7, [4] = 10, [5] = 11, [6] = 14, [7] = 19, [8] = 23, [13] = 11 };

    private static byte[] UiFrame(int seed, int payload)
    {
        byte[] header = [0x9A, 0x86, 0x82, 0xA6, 0xA8, 0x40, 0x60, 0x8E, 0x84, 0x6E, 0xA4, 0x88, 0x8E, 0x61, 0x03, 0xF0];
        var frame = new byte[header.Length + payload];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(header.Length));
        return frame;
    }

    private static float[] Packed(int wn, double seconds)
    {
        var tx = new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = wn })
        {
            Packing = new FramePacking(TimeSpan.FromSeconds(seconds), TimeSpan.Zero),
        };
        int bps = Ms110dMode.Mode3k(wn).Bps;
        int payload = Math.Clamp(bps * 60 / 8 / 20, 40, 200);
        var queue = Enumerable.Range(0, 2000).Select(i => UiFrame(100 + i, payload)).ToList();
        return tx.ModulateFrames(queue.Take(tx.FramesPerBurst(queue)).ToList(), 0);
    }

    private static float[] Noise(Random r, int n)
    {
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - r.NextDouble();
            double u2 = r.NextDouble();
            b[i] = (float)(Sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return b;
    }

    /// <summary>Two, three and four carriers on the probe's lines, two and three off them,
    /// and a mix, for the probe length of <paramref name="wn"/>.</summary>
    private static IEnumerable<(string Name, double[] Tones)> CarrierSets(int wn)
    {
        bool k48 = Ms110dMode.Mode3k(wn).K == 48;
        double[] on = k48 ? [1896, 1992, 1704, 2088] : [1650, 2100, 1950, 1500];
        double[] off = k48 ? [1850, 1944, 2040] : [1875, 2025, 1725];
        yield return ("on2", on[..2]);
        yield return ("on3", on[..3]);
        yield return ("on4", on);
        yield return ("off2", off[..2]);
        yield return ("off3", off);
        yield return ("mix3", [on[0], off[0], on[1]]);
    }

    private static float[] WeakBits(int wn)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
        var random = new Random(4);
        var bits = new byte[4 * tx.Mode.Bps];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)random.Next(2);
        return tx.Modulate(bits);
    }

    private static void AddCarrier(float[] air, int from, double hz, double amplitude)
    {
        for (int i = from; i < air.Length; i++)
        {
            air[i] += (float)(amplitude * Math.Cos(2 * Math.PI * hz * i / Rate));
        }
    }

    private static double Gain(float[] burst, double snr)
    {
        double power = burst.Average(s => (double)s * s);
        return Math.Sqrt(Sigma * Sigma * 3000 / (Rate / 2.0) * Math.Pow(10, snr / 10) / power);
    }

    private static string Trace(string id, int wn, float[] air, long start, long end)
    {
        var sb = new StringBuilder();
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        sb.Append(CultureInfo.InvariantCulture, $"H,{id},{wn},{mode.U + mode.K},{mode.K},{start},{end}\n");
        var rx = new Ms110dModem(Rate, _ => sb.Append("F\n"), rx: new Ms110dDemodOptions { PresenceReleaseOff = true });
        Ms110dDemodulator demod = rx.Receiver;
        demod.PresenceTraced += (t, a, b) => sb.Append(CultureInfo.InvariantCulture, $"P,{t},{a:F3},{b:F3}\n");
        demod.BurstCompleted += b => sb.Append(CultureInfo.InvariantCulture, $"B,{b.Reason}\n");
        bool cd = false;
        for (int at = 0; at < air.Length; at += 960)
        {
            rx.Process(air.AsSpan(at, Math.Min(960, air.Length - at)));
            if (rx.CarrierDetect != cd)
            {
                cd = rx.CarrierDetect;
                sb.Append(CultureInfo.InvariantCulture, $"C,{at + 960},{(cd ? 1 : 0)}\n");
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Trace_A_Tuning_Set()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_SIGNAL_ABSENT_TUNE") != "1",
            "set MS110D_SIGNAL_ABSENT_TUNE=1 (and MS110D_TUNE_OUT, MS110D_TUNE_SET) for the tuning traces");
        string set = Environment.GetEnvironmentVariable("MS110D_TUNE_SET") ?? "B";
        string outDir = Environment.GetEnvironmentVariable("MS110D_TUNE_OUT")
            ?? throw new InvalidOperationException("MS110D_TUNE_OUT names the trace directory");
        int[] wns = (Environment.GetEnvironmentVariable("MS110D_TUNE_WNS") ?? "1,2,3,4,5,6,7,8,13")
            .Split(',').Select(w => int.Parse(w, CultureInfo.InvariantCulture)).ToArray();
        var jobs = new List<(string Id, Func<string> Run)>();
        foreach (int wn in wns)
        {
            if (set is "A" or "V")
            {
                foreach (double spread in set == "A" ? new[] { 0.02, 0.05, 0.1, 0.2 } : new[] { 0.02, 0.05 })
                foreach (double off in new[] { 0.0, 5, 10 })
                foreach (int seed in set == "A" ? Enumerable.Range(1, 5) : Enumerable.Range(6, 5))
                {
                    double snr = AwgnMask[wn] + off;
                    string id = string.Create(CultureInfo.InvariantCulture, $"{set}-wn{wn}-r{spread}-s{snr}-{seed}");
                    jobs.Add((id, () =>
                    {
                        float[] burst = Packed(wn, 60);
                        var ch = new WattersonChannel(Rate, (seed * 7919) + wn + (int)(spread * 1000), new WattersonPath(0, Fading: true, DopplerSpreadHz: spread));
                        float[] air = ch.Apply(burst, snr, leadInSamples: Rate, leadOutSamples: 2 * Rate);
                        return Trace(id, wn, air, Rate, Rate + burst.Length);
                    }));
                }
            }
            else if (set is "B" or "E")
            {
                foreach (double hold in set == "B" ? new[] { 4.0, 6, 8 } : new[] { 10.0, 12, 14, 16, 18 })
                foreach (double depthDb in new[] { -15.0, -30 })
                foreach (double off in new[] { -3.0, 0, 3, 6 })
                foreach (int seed in new[] { 77, 78 })
                {
                    double snr = AwgnMask[wn] + off;
                    string id = string.Create(CultureInfo.InvariantCulture, $"{set}-wn{wn}-h{hold}-d{depthDb}-s{snr}-{seed}");
                    jobs.Add((id, () =>
                    {
                        float[] burst = Packed(wn, 60);
                        double gain = Gain(burst, snr);
                        double depth = Math.Pow(10, depthDb / 20);
                        const double fadeStart = 20, ramp = 0.5;
                        var air = Noise(new Random(seed), Rate + burst.Length + (30 * Rate));
                        for (int i = 0; i < burst.Length; i++)
                        {
                            double t = (i / (double)Rate) - fadeStart;
                            double env = 1;
                            if (t > 0 && t < ramp) env = 1 + ((depth - 1) * 0.5 * (1 - Math.Cos(Math.PI * t / ramp)));
                            else if (t >= ramp && t < ramp + hold) env = depth;
                            else if (t >= ramp + hold && t < (2 * ramp) + hold) env = depth + ((1 - depth) * 0.5 * (1 - Math.Cos(Math.PI * (t - ramp - hold) / ramp)));
                            air[Rate + i] += (float)(burst[i] * gain * env);
                        }

                        return Trace(id, wn, air, Rate, Rate + burst.Length);
                    }));
                }
            }
            else if (set == "C")
            {
                foreach (string chan in new[] { "awgn", "poor" })
                foreach (double snr in new[] { -3.0, -6, -9 })
                foreach (int seed in Enumerable.Range(0, 6))
                {
                    string id = string.Create(CultureInfo.InvariantCulture, $"C-wn{wn}-{chan}-s{snr}-{seed}");
                    jobs.Add((id, () =>
                    {
                        int cs = 10_000 + (wn * 1000) + seed;
                        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
                        var random = new Random(cs);
                        var bits = new byte[4 * tx.Mode.Bps];
                        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)random.Next(2);
                        float[] audio = tx.Modulate(bits);
                        WattersonPath[] paths = chan == "poor" ? WattersonChannel.Poor : [];
                        float[] air = new WattersonChannel(Rate, cs, paths).Apply(audio, snr, leadInSamples: Rate, leadOutSamples: 30 * Rate);
                        return Trace(id, wn, air, Rate, Rate + audio.Length);
                    }));
                }
            }
            else if (set == "K" && wn is 2 or 4 or 6)
            {
                double[] tones = wn == 2 ? [1896, 1992, 1850, 1944] : [1650, 1950, 2100, 1875, 2025];
                foreach (double toneHz in tones)
                foreach (double aboveDb in new[] { -6.0, 0, 2, 5, 8 })
                foreach (int seed in new[] { 77, 78 })
                {
                    double snr = AwgnMask[wn] + 1;
                    string id = string.Create(CultureInfo.InvariantCulture, $"K-wn{wn}-f{toneHz}-c{aboveDb}-s{snr}-{seed}");
                    jobs.Add((id, () =>
                    {
                        float[] burst = Packed(wn, 60);
                        double gain = Gain(burst, snr);
                        var air = Noise(new Random(seed), Rate + burst.Length + (20 * Rate));
                        double signalPower = burst.Average(x => (double)x * x) * gain * gain;
                        double amplitude = Math.Sqrt(2 * signalPower * Math.Pow(10, aboveDb / 10));
                        for (int i = 0; i < burst.Length; i++)
                        {
                            air[Rate + i] += (float)(burst[i] * gain);
                        }

                        AddCarrier(air, 0, toneHz, amplitude);
                        return Trace(id, wn, air, Rate, Rate + burst.Length);
                    }));
                }
            }
            else if (set == "T" && wn is 2 or 4 or 6)
            {
                double[] tones = wn == 2 ? [1896, 1992, 1850] : [1950, 2100, 1875];
                foreach (double toneHz in tones)
                foreach (double toneDb in new[] { -6.0, 0, 6 })
                foreach (int seed in new[] { 5, 6 })
                {
                    string id = string.Create(CultureInfo.InvariantCulture, $"C-wn{wn}-tone{toneHz}-{toneDb}-{seed}");
                    jobs.Add((id, () =>
                    {
                        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
                        var random = new Random(seed);
                        var bits = new byte[4 * tx.Mode.Bps];
                        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)random.Next(2);
                        float[] audio = tx.Modulate(bits);
                        double gain = Gain(audio, -6);
                        var air = Noise(random, Rate + audio.Length + (40 * Rate));
                        for (int i = 0; i < audio.Length; i++) air[Rate + i] += (float)(audio[i] * gain);
                        double amplitude = Math.Sqrt(2 * Sigma * Sigma * 3000 / (Rate / 2.0) * Math.Pow(10, toneDb / 10));
                        AddCarrier(air, Rate + audio.Length, toneHz, amplitude);
                        return Trace(id, wn, air, Rate, Rate + audio.Length);
                    }));
                }
            }
            else if (set is "M" or "N" && wn is 2 or 4 or 6)
            {
                foreach ((string name, double[] tones) in CarrierSets(wn))
                foreach (double toneDb in set == "M" ? new[] { 6.0, 9, 10, 12 } : new[] { -6.0, 0, 6 })
                foreach (double off in set == "M" ? new[] { 1.0, 5 } : new[] { -99.0 })
                {
                    double snr = set == "M" ? AwgnMask[wn] + off : -6;
                    string id = string.Create(CultureInfo.InvariantCulture,
                        $"{(set == "M" ? "M" : "C")}-wn{wn}-{name}-t{toneDb}-s{snr}");
                    jobs.Add((id, () =>
                    {
                        float[] burst = set == "M" ? Packed(wn, 60) : WeakBits(wn);
                        double gain = Gain(burst, snr);
                        var air = Noise(new Random(91), Rate + burst.Length + ((set == "M" ? 10 : 40) * Rate));
                        for (int i = 0; i < burst.Length; i++) air[Rate + i] += (float)(burst[i] * gain);
                        double amplitude = Math.Sqrt(2 * Sigma * Sigma * 3000 / (Rate / 2.0) * Math.Pow(10, toneDb / 10));
                        foreach (double hz in tones)
                        {
                            AddCarrier(air, set == "M" ? 0 : Rate + burst.Length, hz, amplitude);
                        }

                        return Trace(id, wn, air, Rate, Rate + burst.Length);
                    }));
                }
            }
            else if (set == "D")
            {
                foreach (string chan in new[] { "awgn", "poor" })
                foreach (int seed in Enumerable.Range(0, 3))
                {
                    double snr = chan == "poor" ? PoorMask[wn] : AwgnMask[wn];
                    string id = string.Create(CultureInfo.InvariantCulture, $"D-wn{wn}-{chan}-s{snr}-{seed}");
                    jobs.Add((id, () =>
                    {
                        float[] burst = Packed(wn, 60);
                        WattersonPath[] paths = chan == "poor" ? WattersonChannel.Poor : [];
                        float[] air = new WattersonChannel(Rate, 3000 + (wn * 10) + seed, paths).Apply(burst, snr, leadInSamples: Rate, leadOutSamples: 10 * Rate);
                        return Trace(id, wn, air, Rate, Rate + burst.Length);
                    }));
                }
            }
        }

        Directory.CreateDirectory(outDir);
        int done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = int.Parse(Environment.GetEnvironmentVariable("MS110D_TUNE_WORKERS") ?? "12", CultureInfo.InvariantCulture) }, job =>
        {
            string path = Path.Combine(outDir, job.Id + ".csv");
            if (File.Exists(path)) return;
            File.WriteAllText(path + ".tmp", job.Run());
            File.Move(path + ".tmp", path);
            Interlocked.Increment(ref done);
        });
        output.WriteLine($"{done} of {jobs.Count} run");
    }
}
