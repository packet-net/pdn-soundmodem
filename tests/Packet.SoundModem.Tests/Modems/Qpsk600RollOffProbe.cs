using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;
using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Bench probe (set <c>ROLLOFF_PROBE=1</c>): the issue #344 instrument, the #340 campaign
/// re-run for qpsk600. That mode's 0.20 roll-off was chosen against the superseded 328 Hz
/// highest-energy-window reading of a NinoTNC's mode-9 transmission, the same figure #340
/// showed reads mostly preamble for bpsk300. Three instruments, mirroring
/// <see cref="RollOffDiscrepancyProbe"/>: occupied bandwidth of our shape at both candidate
/// roll-offs against the mode-9 reference recording measured whole-burst, a TX-vs-RX filter
/// mismatch matrix under calibrated AWGN, and the real NinoTNC capture received through both
/// candidate matched filters. QPSK's detection is the wildcard that keeps BPSK's answer from
/// carrying over automatically, so the receive rows run under the deployed differential
/// detector and the coherent one both.
/// </summary>
/// <remarks>
/// <para>What it measured on 2026-08-22 (the numbers that decided #344; decisive rows at
/// N=400 via <c>ROLLOFF_PROBE_N</c>):</para>
/// <list type="bullet">
///   <item>OBW, whole-burst, like-for-like with the never-wider test: ours at 0.20 =
///   328 Hz, at 0.35 = 352 Hz, the NinoTNC mode-9 reference recording = 398 Hz - the same
///   398 the bpsk300 recording measures, from the same modulator at the same symbol rate.
///   The 328 Hz the July 2026 decision attributed to the TNC was the pre-issue-#2
///   highest-energy-window method reading mostly preamble, so the never-wider rule never
///   forced 0.20 here either.</item>
///   <item>Loopback, deployed differential detector, N=400: matched and cross tx/rx pairs
///   statistically identical at every SNR tried (-1 dB: 307/305/296/300 across the four
///   combinations), so the filter-mismatch penalty is unmeasurable and a mixed 0.20/0.35
///   fleet loses nothing. On the coherent cross-check the TX shape alone decides (its
///   receive low-pass is fixed at 0.75 baud, and both rx columns are bit-identical):
///   tx 0.35 copies 120/219/290 against tx 0.20's 69/176/249 at -1/0/+1 dB, decisively
///   favouring the wider transmission.</item>
///   <item>The real NinoTNC mode-9 burst under AWGN, deployed differential detector,
///   N=400: rx 0.35 = 131 vs rx 0.20 = 81 at -2 dB (about 4 sigma, 1.6x), identical from
///   -1 dB up (305 vs 302, 383 vs 380, 398 vs 397), 6 vs 7 in the noise floor at -3.
///   The wider filter receives a real NinoTNC better exactly where margin matters, the
///   same direction and about the same size as #340's bpsk300 result.</item>
/// </list>
/// <para>nino-bench has passed <c>QpskModulator.DefaultRollOff</c> to the QPSK factories
/// since before the 0.20 decision (its <c>--qpsk-rolloff</c> default), so every mode-9
/// bench validation on record - the 6/6 both-ways run inside the commit that chose 0.20,
/// the TXDELAY survey - transmitted 0.35. The factory's 0.20 was never once measured
/// against a real NinoTNC.</para>
/// </remarks>
public class Qpsk600RollOffProbe(ITestOutputHelper output)
{
    private const int Rate = 12000;

    /// <summary>Trials per point; override with <c>ROLLOFF_PROBE_N</c> to deepen the
    /// decisive rows (seeds are 1..N, so a deeper run strictly extends a shallower one).</summary>
    private static int Trials =>
        int.TryParse(Environment.GetEnvironmentVariable("ROLLOFF_PROBE_N"), out int n) ? n : 200;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }

    private static byte[] BenchFrame()
    {
        // The frame shape the reference recordings carry (nino-bench MakeFrame), so the
        // OBW comparison stays like-for-like with the never-wider test.
        var payload = new byte[40];
        byte[] tag = System.Text.Encoding.UTF8.GetBytes("BENCH qpsk600 #0000 ");
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = i < tag.Length ? tag[i] : (byte)('A' + (i % 26));
        }

        byte[] header = [0x9C, 0x92, 0x9C, 0x9E, 0x40, 0x40, 0xE0, 0x84, 0x8A, 0x9C, 0x86, 0x90, 0x40, 0x63, 0x03, 0xF0];
        return [.. header, .. payload];
    }

    private static float[] Trim(float[] audio)
    {
        int a = 0, b = audio.Length - 1;
        while (a < audio.Length && Math.Abs(audio[a]) < 0.02f)
        {
            a++;
        }

        while (b > a && Math.Abs(audio[b]) < 0.02f)
        {
            b--;
        }

        return audio[a..(b + 1)];
    }

    /// <summary>Same FFT-per-rate convention as the never-wider test: ~12 Hz bins on both
    /// sides, because spectra measured at different resolutions do not compare.</summary>
    private static double WholeBurstObw(float[] burst, int rate) =>
        OccupiedBandwidth.Measure(burst, rate, fftSize: rate == 48000 ? 4096 : 1024).WidthHz;

    [Fact]
    public void Obw_Deployed_Vs_Default_Vs_NinoTnc()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ROLLOFF_PROBE") is null, "bench probe only");

        byte[] frame = BenchFrame();
        foreach (double rollOff in new[] { 0.20, 0.25, 0.30, 0.35 })
        {
            IModem tx = QpskModem.Qpsk600(Rate, static _ => { }, rollOff: rollOff);
            double obw = WholeBurstObw(Trim(tx.Modulate(frame, 300)), Rate);
            output.WriteLine($"ours at roll-off {rollOff:F2}: {obw:F1} Hz");
        }

        string path = Path.Combine(RepoRoot(), "samples", "ninotnc", "qpsk600.wav");
        var (raw, rate48) = WavFile.ReadMono(path);
        float[] reference = FirstBurst(raw, rate48);
        double ninoObw = WholeBurstObw(reference, rate48);
        output.WriteLine($"NinoTNC mode-9 reference recording (whole burst): {ninoObw:F1} Hz");
    }

    private static float[] FirstBurst(float[] raw, int rate)
    {
        int window = rate / 100, start = -1;
        for (int i = 0; i + window < raw.Length; i += window)
        {
            float peak = 0;
            for (int k = i; k < i + window; k++)
            {
                peak = Math.Max(peak, Math.Abs(raw[k]));
            }

            if (peak > 0.03f && start < 0)
            {
                start = i;
            }

            if (peak <= 0.03f && start >= 0)
            {
                if (i - start > rate / 10)
                {
                    return raw[start..i];
                }

                start = -1;
            }
        }

        throw new InvalidOperationException("no burst found");
    }

    private static int RunLoopback(
        double txRollOff, double rxRollOff, double snrDb, int trials, PskDetector detector)
    {
        byte[] frame = BenchFrame();
        var tx = QpskModem.Qpsk600(Rate, static _ => { }, rollOff: txRollOff);
        float[] burst = Trim(tx.Modulate(frame, 0));

        int successes = 0;
        Parallel.For(0, trials, new ParallelOptions { MaxDegreeOfParallelism = 12 }, seed =>
        {
            float[] audio = SimChannel.Apply(burst, Rate, SimChannelKind.Awgn, snrDb, seed + 1);
            bool got = false;
            var rx = QpskModem.Qpsk600(
                Rate, f => got |= f.AsSpan().SequenceEqual(frame),
                rollOff: rxRollOff, detector: detector);
            for (int i = 0; i < audio.Length; i += Rate / 10)
            {
                rx.Process(audio.AsSpan(i, Math.Min(Rate / 10, audio.Length - i)));
            }

            if (got)
            {
                Interlocked.Increment(ref successes);
            }
        });
        return successes;
    }

    [Fact]
    public void Filter_Mismatch_Matrix_Under_Awgn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ROLLOFF_PROBE") is null, "bench probe only");

        int N = Trials;
        foreach (PskDetector detector in new[] { PskDetector.Differential, PskDetector.Coherent })
        {
            foreach (double snr in new[] { -1.0, 0.0, 1.0 })
            {
                foreach ((double txA, double rxA) in new[] { (0.35, 0.35), (0.20, 0.20), (0.20, 0.35), (0.35, 0.20) })
                {
                    int ok = RunLoopback(txA, rxA, snr, N, detector);
                    output.WriteLine(
                        $"{detector} snr {snr:+0;-0} dB  tx {txA:F2} rx {rxA:F2}: {ok}/{N}");
                }
            }
        }
    }

    [Fact]
    public void Real_NinoTnc_Burst_Against_Both_Matched_Filters()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ROLLOFF_PROBE") is null, "bench probe only");

        string path = Path.Combine(RepoRoot(), "samples", "ninotnc", "qpsk600.wav");
        var (raw, rate48) = WavFile.ReadMono(path);
        float[] burst48 = FirstBurst(raw, rate48);
        var decimator = new Decimator(rate48, rate48 / Rate);
        var burst = new float[decimator.MaxOutput(burst48.Length)];
        int produced = decimator.Process(burst48, burst);
        burst = Trim(burst[..produced]);

        // Effectively-clean sanity first (+30 dB through the same channel, which also
        // normalises the recording's ~0.17 capture level and adds the lead-in a cold
        // receiver wants): both candidate matched filters must copy the recording up here
        // before the knee comparison means anything. Fed raw at capture level with no
        // lead-in, no combination copies it; that is a level/run-in artefact of the raw
        // file, not a filter result, which is why this row goes through the channel too.
        foreach (PskDetector detector in new[] { PskDetector.Differential, PskDetector.Coherent })
        {
            foreach (double rxA in new[] { 0.35, 0.20 })
            {
                float[] audio = SimChannel.Apply(burst, Rate, SimChannelKind.Awgn, 30.0, 1);
                bool got = false;
                var rx = QpskModem.Qpsk600(Rate, _ => got = true, rollOff: rxA, detector: detector);
                for (int i = 0; i < audio.Length; i += Rate / 10)
                {
                    rx.Process(audio.AsSpan(i, Math.Min(Rate / 10, audio.Length - i)));
                }

                output.WriteLine($"+30 dB, {detector}, rx {rxA:F2}: {(got ? "copies" : "NO COPY")}");
            }
        }

        int N = Trials;
        foreach (PskDetector detector in new[] { PskDetector.Differential, PskDetector.Coherent })
        {
            foreach (double snr in new[] { -3.0, -2.0, -1.0, 0.0, 1.0 })
            {
                foreach (double rxA in new[] { 0.35, 0.20 })
                {
                    int successes = 0;
                    Parallel.For(0, N, new ParallelOptions { MaxDegreeOfParallelism = 12 }, seed =>
                    {
                        float[] audio = SimChannel.Apply(burst, Rate, SimChannelKind.Awgn, snr, seed + 1);
                        bool got = false;
                        var rx = QpskModem.Qpsk600(
                            Rate, _ => got = true, rollOff: rxA, detector: detector);
                        for (int i = 0; i < audio.Length; i += Rate / 10)
                        {
                            rx.Process(audio.AsSpan(i, Math.Min(Rate / 10, audio.Length - i)));
                        }

                        if (got)
                        {
                            Interlocked.Increment(ref successes);
                        }
                    });
                    output.WriteLine(
                        $"nino burst, {detector}, snr {snr:+0;-0} dB, rx {rxA:F2}: {successes}/{N}");
                }
            }
        }
    }
}
