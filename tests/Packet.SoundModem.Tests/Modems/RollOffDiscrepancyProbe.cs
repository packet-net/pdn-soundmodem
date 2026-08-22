using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;
using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Bench probe (set <c>ROLLOFF_PROBE=1</c>): the issue #340 instrument. Measures what the
/// bpsk300 factory/bank roll-off discrepancy actually cost, on three axes: occupied
/// bandwidth of each transmit path against the NinoTNC reference recording, a TX-vs-RX
/// filter mismatch matrix under calibrated AWGN, and the real NinoTNC capture received
/// through both candidate matched filters.
/// </summary>
/// <remarks>
/// <para>What it measured on 2026-08-22 (the numbers that decided #340, decisive rows
/// re-run at N=400):</para>
/// <list type="bullet">
///   <item>OBW, whole-burst, like-for-like with the never-wider test: factory at 0.20 =
///   328 Hz, bank at 0.35 = 352 Hz, the NinoTNC reference recording = 398 Hz. Both of ours
///   are narrower than the TNC; the 328 Hz the July 2026 roll-off decision attributed to
///   the TNC was the pre-issue-#2 highest-energy-window method reading mostly preamble.</item>
///   <item>Loopback at the -5 dB knee, N=400: matched 0.35 = 264, matched 0.20 = 268,
///   statistically identical. The cross terms (tx 0.20 into rx 0.35 = 246, tx 0.35 into
///   rx 0.20 = 258) put the mismatch penalty at roughly 0.1 to 0.2 dB, at the edge of
///   resolution.</item>
///   <item>The real NinoTNC burst under AWGN, N=400: rx 0.35 = 248 vs rx 0.20 = 210 at
///   -5 dB (about 4 sigma, roughly 0.4 dB), 36 vs 14 at -6 dB. The deployed 0.35 branch
///   filter receives a real NinoTNC better than the factory's 0.20 would have.</item>
/// </list>
/// <para>The capture's spectrum explains the receive result: flat to about plus or minus
/// 150 Hz around the carrier, then steep skirts, -27 dB at 250 Hz out and -44 dB at 300.
/// That is wider than either of our shapes, and closer to 0.35's passband than 0.20's.</para>
/// </remarks>
public class RollOffDiscrepancyProbe(ITestOutputHelper output)
{
    private const int Rate = 12000;

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
        byte[] tag = System.Text.Encoding.UTF8.GetBytes("BENCH bpsk300 #0000 ");
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
    public void Obw_Factory_Vs_Bank_Vs_NinoTnc()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ROLLOFF_PROBE") is null, "bench probe only");

        byte[] frame = BenchFrame();
        IModem factory = BpskModem.Bpsk300(Rate, static _ => { });
        IModem bank = BpskMultiModem.Bpsk300(Rate, static _ => { });
        double factoryObw = WholeBurstObw(Trim(factory.Modulate(frame, 300)), Rate);
        double bankObw = WholeBurstObw(Trim(bank.Modulate(frame, 300)), Rate);

        string path = Path.Combine(RepoRoot(), "samples", "ninotnc", "bpsk300.wav");
        var (raw, rate48) = WavFile.ReadMono(path);
        float[] reference = FirstBurst(raw, rate48);
        double ninoObw = WholeBurstObw(reference, rate48);

        output.WriteLine($"factory (BpskModem.Bpsk300):   {factoryObw:F1} Hz");
        output.WriteLine($"bank (BpskMultiModem.Bpsk300): {bankObw:F1} Hz");
        output.WriteLine($"NinoTNC reference recording:   {ninoObw:F1} Hz");
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

    private static int RunLoopback(double txRollOff, double rxRollOff, double snrDb, int trials)
    {
        byte[] frame = BenchFrame();
        var tx = new BpskModem(Rate, static _ => { }, crc: true, 1500, 300, txRollOff);
        float[] burst = Trim(tx.Modulate(frame, 0));

        int successes = 0;
        Parallel.For(0, trials, new ParallelOptions { MaxDegreeOfParallelism = 6 }, seed =>
        {
            float[] audio = SimChannel.Apply(burst, Rate, SimChannelKind.Awgn, snrDb, seed + 1);
            bool got = false;
            var rx = new BpskModem(Rate, f => got |= f.AsSpan().SequenceEqual(frame), crc: true, 1500, 300, rxRollOff);
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

        const int N = 200;
        foreach (double snr in new[] { -6.0, -5.0, -4.0 })
        {
            foreach ((double txA, double rxA) in new[] { (0.35, 0.35), (0.20, 0.20), (0.20, 0.35), (0.35, 0.20) })
            {
                int ok = RunLoopback(txA, rxA, snr, N);
                output.WriteLine($"snr {snr:+0;-0} dB  tx {txA:F2} rx {rxA:F2}: {ok}/{N}");
            }
        }
    }

    [Fact]
    public void Real_NinoTnc_Burst_Against_Both_Matched_Filters()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ROLLOFF_PROBE") is null, "bench probe only");

        string path = Path.Combine(RepoRoot(), "samples", "ninotnc", "bpsk300.wav");
        var (raw, rate48) = WavFile.ReadMono(path);
        float[] burst48 = FirstBurst(raw, rate48);
        var decimator = new Decimator(rate48, rate48 / Rate);
        var burst = new float[decimator.MaxOutput(burst48.Length)];
        int produced = decimator.Process(burst48, burst);
        burst = Trim(burst[..produced]);

        const int N = 200;
        foreach (double snr in new[] { -6.0, -5.0, -4.0, -3.0 })
        {
            foreach (double rxA in new[] { 0.35, 0.20 })
            {
                int successes = 0;
                Parallel.For(0, N, new ParallelOptions { MaxDegreeOfParallelism = 6 }, seed =>
                {
                    float[] audio = SimChannel.Apply(burst, Rate, SimChannelKind.Awgn, snr, seed + 1);
                    bool got = false;
                    var rx = new BpskModem(Rate, _ => got = true, crc: true, 1500, 300, rxA);
                    for (int i = 0; i < audio.Length; i += Rate / 10)
                    {
                        rx.Process(audio.AsSpan(i, Math.Min(Rate / 10, audio.Length - i)));
                    }

                    if (got)
                    {
                        Interlocked.Increment(ref successes);
                    }
                });
                output.WriteLine($"nino burst, snr {snr:+0;-0} dB, rx {rxA:F2}: {successes}/{N}");
            }
        }
    }
}
