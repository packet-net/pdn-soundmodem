using System.Text;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Env-gated probe (temporary instrument, 2026-07-31): reproduce the exact AX.25 frames
/// the studybox corpus driver transmitted per TXDELAY tier and run them through our own
/// c4fsk9600 modulate→demodulate loopback. Discriminates content-dependent decode failure
/// (fails here too) from a capture/TNC-side defect (passes here) for the two failing
/// corpus files (0011 txd50 0/3, txd120 2/3). Set C4FSK_CONTENT_PROBE=1 and
/// C4FSK_CONTENT_REPORT=&lt;file&gt; to run.
/// </summary>
public class C4fskContentProbeTests
{
    private static byte[] Ui(string info)
    {
        static byte[] Addr(string call, int ssid, bool last)
        {
            var b = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            b[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
            return b;
        }

        return
        [
            .. Addr("QST", 0, last: false),
            .. Addr("M0LTE", 1, last: true),
            0x03, 0xF0,
            .. Encoding.ASCII.GetBytes(info),
        ];
    }

    // Byte-exact mirror of nino_capture.py frames_for() for dip 0011 (long_cap 180).
    private static byte[][] CorpusFrames(int txd, string tier)
    {
        string tag = $"NinoTNC corpus 0011 txd={txd}ms {tier} PERSIST=255 MIC-range AGC-off ";
        string filler = string.Concat(
            Enumerable.Repeat("pdn-soundmodem wiki corpus payload ", 6))[..180];
        return
        [
            Ui(tag + "S"),
            Ui(tag + "M " + "the quick brown fox jumps over the lazy dog 0123456789 "),
            Ui(tag + "L " + filler),
        ];
    }

    [Fact]
    public void Demod_Fix_Candidates_On_Real_Corpus()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".variants");

        int[] dibitToLevel = [2, 3, 1, 0];
        string[] variants = ["baseline", "avg3", "ffe3g", "ffe5g", "avg3+ffe3g", "avg3+ffe5g"];
        foreach (string variant in variants)
        {
            var totals = new List<string>();
            foreach ((string tier, int txd) in new[] { ("txd50ms", 50), ("txd120ms", 120), ("txd250ms", 250) })
            {
                string wav = $"/tmp/nino-corpus/0011-9600c4fsk-il2pc-{tier}.wav";
                var (samples, _) = Packet.SoundModem.Audio.WavFile.ReadMono(wav);
                List<int> starts = BurstStarts(samples);
                byte[][] frames = CorpusFrames(txd, txd switch { 50 => "aggressive", 120 => "standard", _ => "conservative" });
                for (int burst = 0; burst < Math.Min(3, starts.Count); burst++)
                {
                    int errors = RunVariant(variant, samples, starts[burst], txd,
                        M0LTE.Il2p.Il2pCodec.Encode(frames[burst], appendCrc: true), dibitToLevel,
                        variant == "ffe5g" ? report : null);
                    totals.Add($"{tier[3..].TrimEnd('m', 's')}b{burst + 1}:{errors}");
                }
            }

            report.WriteLine($"{variant,-15} sym errors  {string.Join("  ", totals)}");
        }
    }

    private static List<int> BurstStarts(float[] samples)
    {
        const int win = 480;
        int nw = samples.Length / win;
        var env = new float[nw];
        float peak = 0;
        for (int w = 0; w < nw; w++)
        {
            float mx = 0;
            for (int i = w * win; i < (w + 1) * win; i++) { mx = Math.Max(mx, Math.Abs(samples[i])); }
            env[w] = mx;
            peak = Math.Max(peak, mx);
        }

        var starts = new List<int>();
        int? cur = null;
        int quiet = 0;
        for (int w = 0; w < nw; w++)
        {
            if (env[w] > 0.05f * peak && cur is null) { cur = w; quiet = 0; }
            else if (env[w] <= 0.05f * peak && cur is not null)
            {
                quiet++;
                if (quiet > 8) { starts.Add(cur.Value * win); cur = null; quiet = 0; }
            }
            else { quiet = 0; }
        }

        if (cur is not null) { starts.Add(cur.Value * win); }
        return starts;
    }

    private static int RunVariant(
        string variant, float[] samples, int burstStart, int txd, byte[] wire, int[] dibitToLevel,
        StreamWriter? positionsTo = null)
    {
        var expectedLevels = new List<int>();
        foreach (byte b in wire)
        {
            for (int k = 6; k >= 0; k -= 2) { expectedLevels.Add(dibitToLevel[(b >> k) & 3]); }
        }

        int ffeTaps = variant.Contains("ffe5g") ? 5 : variant.Contains("ffe3g") ? 3 : 0;
        bool avg3 = variant.Contains("avg3");

        var rx = new M0LTE.Dsp.FirFilter(M0LTE.Dsp.FilterDesign.LowPass(1.5 * 4800, 48000, 48));
        float peakHigh = 0, peakLow = 0;
        double clockPhase = 0;
        int lastSign = 0;
        // FFE: symbol-spaced taps on decision-instant samples, decision-directed NLMS,
        // centre-tap identity start; adaptation gated on a live envelope so silence
        // never trains it.
        double[] w = new double[Math.Max(1, ffeTaps)];
        if (ffeTaps > 0) { w[ffeTaps / 2] = 1; }
        float prev1 = 0, prev2 = 0;
        var sampled = new List<float>();
        var decided = new List<int>();
        int from = Math.Max(0, burstStart - 4800);
        int to = Math.Min(samples.Length, burstStart + (txd + 800) * 48);
        for (int i = from; i < to; i++)
        {
            float filtered = rx.Next(samples[i]);
            peakHigh += (filtered - peakHigh) * (filtered > peakHigh ? 0.08f : 0.00001f);
            peakLow += (filtered - peakLow) * (filtered < peakLow ? 0.08f : 0.00001f);
            float mid = (peakHigh + peakLow) * 0.5f;
            float half = Math.Max((peakHigh - peakLow) * 0.5f, 1e-6f);
            float norm = (filtered - mid) / half;
            clockPhase += 4800.0 / 48000;
            if (clockPhase >= 0.5)
            {
                clockPhase -= 1.0;
                // decision-instant value: raw sample or 3-sample centre average
                float v = avg3 ? (prev2 + prev1 + norm) / 3f : norm;
                sampled.Add(v);
                bool live = half > 0.05f;
                if (ffeTaps == 0)
                {
                    decided.Add(v switch { < -2f / 3f => 0, < 0f => 1, < 2f / 3f => 2, _ => 3 });
                }
                else if (sampled.Count >= ffeTaps)
                {
                    double y = 0, p = 1e-6;
                    for (int t = 0; t < ffeTaps; t++)
                    {
                        double xt = sampled[^(ffeTaps - t)];
                        y += w[t] * xt;
                        p += xt * xt;
                    }

                    int lv = y switch { < -2.0 / 3 => 0, < 0 => 1, < 2.0 / 3 => 2, _ => 3 };
                    decided.Add(lv);
                    if (live)
                    {
                        double target = lv switch { 0 => -1, 1 => -1.0 / 3, 2 => 1.0 / 3, _ => 1 };
                        double mu = 0.05 / p * (target - y);
                        for (int t = 0; t < ffeTaps; t++) { w[t] += mu * sampled[^(ffeTaps - t)]; }
                    }
                }
                else
                {
                    decided.Add(v switch { < -2f / 3f => 0, < 0f => 1, < 2f / 3f => 2, _ => 3 });
                }
            }

            prev2 = prev1;
            prev1 = norm;
            int sign = norm > 0 ? 1 : 0;
            if (sign != lastSign)
            {
                lastSign = sign;
                clockPhase *= 0.74;
            }
        }

        // align decided against expected
        int best = -1, bestErr = int.MaxValue;
        for (int off = 0; off < decided.Count - 60; off++)
        {
            int err = 0;
            for (int k = 0; k < 60; k++) { if (decided[off + k] != expectedLevels[k]) { err++; } }
            if (err < bestErr) { bestErr = err; best = off; }
            if (err == 0) { break; }
        }

        if (bestErr > 10) { return 9999; }
        int n = Math.Min(expectedLevels.Count, decided.Count - best);
        int errors = 0;
        var positions = new List<int>();
        for (int k = 0; k < n; k++)
        {
            if (decided[best + k] != expectedLevels[k]) { errors++; positions.Add(k); }
        }

        if (positions.Count > 0)
        {
            positionsTo?.WriteLine(
                $"      txd{txd} start{burstStart}: err syms [{string.Join(",", positions)}] (bytes [{string.Join(",", positions.Select(p => p / 4).Distinct())}])");
        }

        return errors;
    }

    [Fact]
    public void Trace_Real_Demod_Decisions_Against_Expected_Symbols()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".trace");

        // Replica of C4fskModem.Process (c4fsk9600 at 48k: upsample 1) with full
        // observability, run over each burst of a corpus file, decisions diffed against
        // the expected wire symbols.
        int[] dibitToLevel = [2, 3, 1, 0];

        // Instrument calibration: replica + reference against OUR OWN modulator's
        // rendering of the same frame. Align err and sym errors must be ~0 here or the
        // instrument (not the audio) is at fault.
        {
            byte[] calFrame = CorpusFrames(250, "conservative")[0];
            IModem calTx = C4fskModem.C4fsk9600(48000, _ => { });
            float[] calAudio = calTx.Modulate(calFrame, 250);
            var cal = new float[48000 / 2 + calAudio.Length + 48000 / 2];
            calAudio.CopyTo(cal, 48000 / 2);
            TraceOne(report, "CAL-selfTX", cal, 24000, 250,
                M0LTE.Il2p.Il2pCodec.Encode(calFrame, appendCrc: true), dibitToLevel);
        }
        foreach ((string tier, int txd) in new[] { ("txd50ms", 50), ("txd120ms", 120), ("txd250ms", 250) })
        {
            string wav = $"/tmp/nino-corpus/0011-9600c4fsk-il2pc-{tier}.wav";
            var (samples, _) = Packet.SoundModem.Audio.WavFile.ReadMono(wav);

            // 10 ms envelope split at 5% of peak with >=80 ms quiet gaps — the same
            // detector every python instrument in this investigation used.
            const int win = 480;
            int nw = samples.Length / win;
            var env = new float[nw];
            float peak = 0;
            for (int w = 0; w < nw; w++)
            {
                float mx = 0;
                for (int i = w * win; i < (w + 1) * win; i++) { mx = Math.Max(mx, Math.Abs(samples[i])); }
                env[w] = mx;
                peak = Math.Max(peak, mx);
            }

            var starts = new List<int>();
            int? cur = null;
            int quiet = 0;
            for (int w = 0; w < nw; w++)
            {
                if (env[w] > 0.05f * peak && cur is null) { cur = w; quiet = 0; }
                else if (env[w] <= 0.05f * peak && cur is not null)
                {
                    quiet++;
                    if (quiet > 8) { starts.Add(cur.Value * win); cur = null; quiet = 0; }
                }
                else { quiet = 0; }
            }

            if (cur is not null) { starts.Add(cur.Value * win); }
            report.WriteLine($"{tier}: burst starts at [{string.Join(",", starts.Select(s => s / 48))}] ms");

            byte[][] frames = CorpusFrames(txd, txd switch { 50 => "aggressive", 120 => "standard", _ => "conservative" });
            for (int burst = 0; burst < Math.Min(3, starts.Count); burst++)
            {
                TraceOne(report, $"{tier} b{burst + 1}", samples, starts[burst], txd,
                    M0LTE.Il2p.Il2pCodec.Encode(frames[burst], appendCrc: true), dibitToLevel);
            }
        }
    }

    private static void TraceOne(
        StreamWriter report, string label, float[] samples, int burstStart, int txd,
        byte[] wire, int[] dibitToLevel)
    {
        var expectedLevels = new List<int>();
        foreach (byte b in wire)
        {
            for (int k = 6; k >= 0; k -= 2)
            {
                expectedLevels.Add(dibitToLevel[(b >> k) & 3]);
            }
        }

        var rx = new M0LTE.Dsp.FirFilter(M0LTE.Dsp.FilterDesign.LowPass(1.5 * 4800, 48000, 48));
        float peakHigh = 0, peakLow = 0;
        double clockPhase = 0;
        int lastSign = 0;
        var decided = new List<(int Level, float Norm, float High, float Low, double PhaseAtWrap, int Pos)>();
        int from = Math.Max(0, burstStart - 4800);
        int to = Math.Min(samples.Length, burstStart + (txd + 800) * 48);
        for (int i = from; i < to; i++)
        {
            float filtered = rx.Next(samples[i]);
            peakHigh += (filtered - peakHigh) * (filtered > peakHigh ? 0.08f : 0.00001f);
            peakLow += (filtered - peakLow) * (filtered < peakLow ? 0.08f : 0.00001f);
            float mid = (peakHigh + peakLow) * 0.5f;
            float half = Math.Max((peakHigh - peakLow) * 0.5f, 1e-6f);
            float norm = (filtered - mid) / half;
            int level = norm switch { < -2f / 3f => 0, < 0f => 1, < 2f / 3f => 2, _ => 3 };
            clockPhase += 4800.0 / 48000;
            if (clockPhase >= 0.5)
            {
                clockPhase -= 1.0;
                decided.Add((level, norm, peakHigh, peakLow, clockPhase, i));
            }

            int sign = norm > 0 ? 1 : 0;
            if (sign != lastSign) { lastSign = sign; clockPhase *= 0.74; }
        }

        int best = -1, bestErr = int.MaxValue;
        for (int off = 0; off < decided.Count - 60; off++)
        {
            int err = 0;
            for (int k = 0; k < 60; k++) { if (decided[off + k].Level != expectedLevels[k]) { err++; } }
            if (err < bestErr) { bestErr = err; best = off; }
            if (err == 0) { break; }
        }

        int n = Math.Min(expectedLevels.Count, decided.Count - best);
        var errs = new List<int>();
        for (int k = 0; k < n; k++) { if (decided[best + k].Level != expectedLevels[k]) { errs.Add(k); } }
        report.WriteLine(
            $"{label}: align err {bestErr}/60, wire syms {n}, sym errors {errs.Count} " +
            $"at [{string.Join(",", errs.Take(20))}{(errs.Count > 20 ? "..." : "")}]");

        // Eye statistics: |norm| distribution at decision instants conditioned on the
        // expected level class, plus the worst outer and worst inner. The 2/3 slicing
        // boundary is healthy only if outer P1 clears it and inner P99 stays under it.
        var outerAbs = new List<float>();
        var innerAbs = new List<float>();
        for (int k = 0; k < n; k++)
        {
            float av = Math.Abs(decided[best + k].Norm);
            if (expectedLevels[k] is 0 or 3) { outerAbs.Add(av); } else { innerAbs.Add(av); }
        }

        outerAbs.Sort();
        innerAbs.Sort();
        static float Pct(List<float> xs, double p) => xs[(int)(p / 100 * (xs.Count - 1))];
        report.WriteLine(FormattableString.Invariant(
            $"   eye: outer P0/P1/P5/P50 {Pct(outerAbs, 0):F3}/{Pct(outerAbs, 1):F3}/{Pct(outerAbs, 5):F3}/{Pct(outerAbs, 50):F3}  inner P50/P95/P99/P100 {Pct(innerAbs, 50):F3}/{Pct(innerAbs, 95):F3}/{Pct(innerAbs, 99):F3}/{Pct(innerAbs, 100):F3}"));

        foreach (int k in errs.Take(8))
        {
            (int lv, float nm, float hi, float lo, double ph, int pos) = decided[best + k];
            string ctx = string.Join("", Enumerable.Range(Math.Max(0, k - 3), Math.Min(7, n - Math.Max(0, k - 3)))
                .Select(j => expectedLevels[j].ToString()));
            report.WriteLine(FormattableString.Invariant(
                $"   sym {k} (wire byte {k / 4}) t={pos / 48.0:F1}ms: decided {lv} expected {expectedLevels[k]} norm {nm:F3} phase {ph:F3} ctx {ctx}"));
        }
    }

    [Fact]
    public void Emit_Expected_Wire_Bytes()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".wire");

        foreach ((int txd, string tier) in new[] { (50, "aggressive"), (120, "standard"), (250, "conservative") })
        {
            byte[][] frames = CorpusFrames(txd, tier);
            for (int i = 0; i < frames.Length; i++)
            {
                byte[] wire = M0LTE.Il2p.Il2pCodec.Encode(frames[i], appendCrc: true);
                report.WriteLine($"txd{txd} burst{i + 1} {Convert.ToHexString(wire)}");
            }
        }
    }

    [Fact]
    public void Failing_Corpus_Files_Without_Crc_Enforcement()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".nocrc");

        // crc:false makes the deframer collect four fewer wire bytes and emit frames
        // without CRC enforcement — frames appearing here but not in the normal QC means
        // the bursts demodulate and RS-decode fine and die at the CRC gate; nothing
        // appearing means they die earlier (sync hunt / RS).
        foreach (string tier in new[] { "txd50ms", "txd120ms", "txd250ms" })
        {
            string wav = $"/tmp/nino-corpus/0011-9600c4fsk-il2pc-{tier}.wav";
            if (!File.Exists(wav))
            {
                continue;
            }

            var (samples, _) = Packet.SoundModem.Audio.WavFile.ReadMono(wav);
            var got = new List<string>();
            var rx = new C4fskModem(48000, _ => { }, symbolRate: 4800, crc: false);
            rx.FrameDecoded += (f, q) => got.Add(
                $"len {f.Length} corrected {q.CorrectedBytes}");
            rx.Process(samples);
            report.WriteLine($"{tier}: {got.Count} frames [{string.Join("; ", got)}]");
        }
    }

    [Fact]
    public void Crc_Convention_Probe()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".crc");

        // (a) codec-level roundtrip of a both-C-bits-clear UI frame: is CrcValid false?
        byte[] frame = Ui("hello");
        byte[] wire = M0LTE.Il2p.Il2pCodec.Encode(frame, appendCrc: true);
        bool ok = M0LTE.Il2p.Il2pCodec.TryDecode(
            wire, hasTrailingCrc: true, out byte[] back, out M0LTE.Il2p.Il2pDecodeInfo info);
        report.WriteLine(
            $"codec roundtrip C=0/0: ok={ok} crcValid={info.CrcValid} " +
            $"sent[6]=0x{frame[6]:X2} sent[13]=0x{frame[13]:X2} " +
            $"back[6]=0x{(back.Length > 13 ? back[6] : 0):X2} back[13]=0x{(back.Length > 13 ? back[13] : 0):X2}");

        // (b) real TNC capture: what do decoded frames' C bits look like, and is CRC valid?
        string wav = "/tmp/nino-corpus/0011-9600c4fsk-il2pc-txd250ms.wav";
        if (File.Exists(wav))
        {
            var (samples, _) = Packet.SoundModem.Audio.WavFile.ReadMono(wav);
            var decoded = new List<(byte B6, byte B13, bool? Crc)>();
            var rx = C4fskModem.C4fsk9600(48000, _ => { });
            rx.FrameDecoded += (f, q) => decoded.Add((f[6], f[13], q.CrcValid));
            rx.Process(samples);
            foreach ((byte b6, byte b13, bool? crc) in decoded)
            {
                report.WriteLine($"TNC frame: [6]=0x{b6:X2} [13]=0x{b13:X2} crcValid={crc}");
            }
        }
    }

    [Fact]
    public void C4fsk_Loopback_Header_Mutations()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".hdr");

        static byte[] Addr(string call, int ssid, bool last, int command)
        {
            var b = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            b[6] = (byte)((command << 7) | 0x60 | (ssid << 1) | (last ? 1 : 0));
            return b;
        }

        byte[] payload = Encoding.ASCII.GetBytes(new string('x', 20));
        (string Label, byte[] Dest, byte[] Src)[] cases =
        [
            ("baseline TEST(c)/Q0AAA-0", Addr("TEST", 0, false, 1), Addr("Q0AAA", 0, true, 0)),
            ("dest QST(c)", Addr("QST", 0, false, 1), Addr("Q0AAA", 0, true, 0)),
            ("src M0LTE-0", Addr("TEST", 0, false, 1), Addr("M0LTE", 0, true, 0)),
            ("src Q0AAA-1 (ssid 1)", Addr("TEST", 0, false, 1), Addr("Q0AAA", 1, true, 0)),
            ("no C bit on dest", Addr("TEST", 0, false, 0), Addr("Q0AAA", 0, true, 0)),
            ("mine + C bit (QST(c)/M0LTE-1)", Addr("QST", 0, false, 1), Addr("M0LTE", 1, true, 0)),
            ("full mine (QST/M0LTE-1)", Addr("QST", 0, false, 0), Addr("M0LTE", 1, true, 0)),
        ];
        const int rate = 48000;
        foreach ((string label, byte[] dest, byte[] src) in cases)
        {
            byte[] frame = [.. dest, .. src, 0x03, 0xF0, .. payload];
            IModem tx = C4fskModem.C4fsk9600(rate, _ => { });
            var audio = new List<float>();
            audio.AddRange(new float[rate / 2]);
            audio.AddRange(tx.Modulate(frame, 0));
            audio.AddRange(new float[rate / 2]);

            int decoded = 0;
            IModem rx = C4fskModem.C4fsk9600(rate, _ => decoded++);
            rx.Process(audio.ToArray());
            report.WriteLine($"{label,-32}: {decoded}/1");
        }
    }

    [Fact]
    public void C4fsk_Loopback_Component_Swap()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".swap");

        static byte[] ParityAddr(string call, bool last, int command)
        {
            var b = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            b[6] = (byte)((command << 7) | 0x60 | (last ? 1 : 0));
            return b;
        }

        byte[] parityPayload = new byte[40];
        byte[] tag = Encoding.ASCII.GetBytes("PDN RX00 ");
        for (int i = 0; i < parityPayload.Length; i++)
        {
            parityPayload[i] = i < tag.Length ? tag[i] : (byte)('A' + ((i - tag.Length) % 26));
        }

        byte[] parityHeader = [.. ParityAddr("TEST", false, 1), .. ParityAddr("Q0AAA", true, 0), 0x03, 0xF0];
        byte[] myHeader = Ui("")[..16];
        byte[] xs20 = Encoding.ASCII.GetBytes(new string('x', 20));
        byte[] xs40 = Encoding.ASCII.GetBytes(new string('x', 40));

        (string Label, byte[] Frame)[] cases =
        [
            ("parity-exact (TEST/Q0AAA + PDN40)", [.. parityHeader, .. parityPayload]),
            ("parity-hdr + x20", [.. parityHeader, .. xs20]),
            ("parity-hdr + x40", [.. parityHeader, .. xs40]),
            ("QST/M0LTE + PDN40", [.. myHeader, .. parityPayload]),
            ("QST/M0LTE + x20", [.. myHeader, .. xs20]),
        ];
        const int rate = 48000;
        foreach ((string label, byte[] frame) in cases)
        {
            IModem tx = C4fskModem.C4fsk9600(rate, _ => { });
            var audio = new List<float>();
            audio.AddRange(new float[rate / 2]);
            audio.AddRange(tx.Modulate(frame, 0));
            audio.AddRange(new float[rate / 2]);

            int decoded = 0;
            IModem rx = C4fskModem.C4fsk9600(rate, _ => decoded++);
            rx.Process(audio.ToArray());
            report.WriteLine($"{label,-36}: {decoded}/1");
        }
    }

    [Fact]
    public void C4fsk_Loopback_Txdelay_Sweep()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".txdelays");

        const int rate = 48000;
        foreach (int txd in new[] { 0, 5, 10, 15, 20, 25, 30, 40, 50, 80, 120, 250 })
        {
            byte[] frame = Ui(new string('x', 20));
            IModem tx = C4fskModem.C4fsk9600(rate, _ => { });
            var audio = new List<float>();
            audio.AddRange(new float[rate / 2]);
            audio.AddRange(tx.Modulate(frame, txd));
            audio.AddRange(new float[rate / 2]);

            int decoded = 0;
            IModem rx = C4fskModem.C4fsk9600(rate, _ => decoded++);
            rx.Process(audio.ToArray());
            report.WriteLine($"txdelay {txd,3} ms: {decoded}/1");
        }
    }

    [Fact]
    public void C4fsk_Loopback_Length_Sweep()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            (Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
             ?? "/tmp/c4fsk-content-probe.txt") + ".lengths");

        const int rate = 48000;
        foreach (int infoLen in new[] { 4, 10, 20, 30, 40, 50, 60, 75, 100, 131, 180, 256 })
        {
            byte[] frame = Ui(new string('x', infoLen));
            IModem tx = C4fskModem.C4fsk9600(rate, _ => { });
            var audio = new List<float>();
            audio.AddRange(new float[rate / 2]);
            audio.AddRange(tx.Modulate(frame, 250));
            audio.AddRange(new float[rate / 2]);

            int decoded = 0;
            IModem rx = C4fskModem.C4fsk9600(rate, _ => decoded++);
            rx.Process(audio.ToArray());
            report.WriteLine($"info {infoLen,3} B (frame {frame.Length,3} B): {decoded}/1");
        }
    }

    [Fact]
    public void C4fsk_Loopback_Decodes_Each_Corpus_Tiers_Content()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_PROBE") is null,
            "set C4FSK_CONTENT_PROBE=1 to run the content probe");
        using var report = new StreamWriter(
            Environment.GetEnvironmentVariable("C4FSK_CONTENT_REPORT")
            ?? "/tmp/c4fsk-content-probe.txt");

        const int rate = 48000;
        foreach ((int txd, string tier, int preambleMs) in new (int, string, int)[]
                 {
                     (250, "conservative", 250),
                     (120, "standard", 120),
                     (50, "aggressive", 50),
                     (50, "aggressive", 250),   // 50-tier content, long preamble
                     (250, "conservative", 50), // 250-tier content, short preamble
                 })
        {
            IModem tx = C4fskModem.C4fsk9600(rate, _ => { });
            var audio = new List<float>();
            audio.AddRange(new float[rate / 2]);
            foreach (byte[] f in CorpusFrames(txd, tier))
            {
                audio.AddRange(tx.Modulate(f, preambleMs));
                audio.AddRange(new float[(int)(0.8 * rate)]);
            }

            var sizes = new List<int>();
            IModem rx = C4fskModem.C4fsk9600(rate, f => sizes.Add(f.Length));
            rx.Process(audio.ToArray());
            report.WriteLine(
                $"tier-txd{txd,-3} content, {preambleMs,3} ms preamble: " +
                $"{sizes.Count}/3 sizes [{string.Join(",", sizes)}]");
        }
    }
}
