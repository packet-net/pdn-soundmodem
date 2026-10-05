using System.Diagnostics;
using System.Globalization;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// Issue #553: a burst too weak to read, whose preamble the receiver still acquires, must not
/// leave it locked on the noise that follows. The receiver lets go once its mini-probes have
/// shown no signal for a whole window (docs/dev/ms110d/signal-absent.md), and never during a
/// burst it can read. Everything here is seeded and counted in samples.
/// </summary>
public class Ms110dSignalAbsentTests(ITestOutputHelper output)
{
    private const double NoiseSigma = 0.05;

    /// <summary>The release bound the note states: the 4 s window, plus up to two frames
    /// (240 ms at U = 256) for the last frame to be read, rounded up.</summary>
    private const double ReleaseBoundSeconds = 4.25;

    /// <summary>Seeded white noise at <see cref="NoiseSigma"/>, with bursts laid on it at a
    /// stated SNR in 3 kHz - the pdn-mailcast reproduction's air (its PR #22).</summary>
    private sealed class Air(int sampleRate, int seed)
    {
        private readonly Random _random = new(seed);

        public float[] Noise(int samples)
        {
            var block = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                double u1 = 1.0 - _random.NextDouble();
                double u2 = _random.NextDouble();
                block[i] = (float)(NoiseSigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }

            return block;
        }

        public float[] Burst(float[] burst, double snrDb)
        {
            double power = burst.Average(s => (double)s * s);
            double noiseIn3k = NoiseSigma * NoiseSigma * 3000 / (sampleRate / 2.0);
            double gain = Math.Sqrt(noiseIn3k * Math.Pow(10, snrDb / 10) / power);
            float[] air = Noise(burst.Length);
            for (int i = 0; i < air.Length; i++)
            {
                air[i] += (float)(burst[i] * gain);
            }

            return air;
        }
    }

    private static byte[] UiFrame(int seed, int payload)
    {
        byte[] header = [0x9A, 0x86, 0x82, 0xA6, 0xA8, 0x40, 0x60, 0x8E, 0x84, 0x6E, 0xA4, 0x88, 0x8E, 0x61, 0x03, 0xF0];
        var frame = new byte[header.Length + payload];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(header.Length));
        return frame;
    }

    /// <summary>Feeds in 0.1 s blocks, as the daemon does, and returns how many samples in
    /// the receiver let go (CarrierDetect fell), or -1 if it never did.</summary>
    private static long FeedUntilReleased(Ms110dModem modem, Func<float[]> nextBlock, long maxSamples)
    {
        long fed = 0;
        while (fed < maxSamples)
        {
            float[] block = nextBlock();
            modem.Process(block);
            fed += block.Length;
            if (!modem.CarrierDetect)
            {
                return fed;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData(4, -6.0, 2026)]
    [InlineData(4, -3.0, 7)]
    [InlineData(6, -9.0, 31)]
    [InlineData(3, -6.0, 5)]
    public void A_Weak_Burst_Is_Let_Go_Soon_After_It_Ends_And_The_Next_Burst_Is_Heard(
        int wn, double snrDb, int seed)
    {
        // The issue's path: the daemon's 48 kHz, 0.1 s blocks, the catalogue's WN defaults.
        const int rate = 48000;
        const int block = rate / 10;
        var air = new Air(rate, seed);
        int frames = 0;
        var journal = new List<string>();
        var modem = new Ms110dModem(rate, _ => frames++);
        modem.LockReleased += journal.Add;
        var tx = new Ms110dModem(rate, _ => { }, new Ms110dTxSettings { WaveformNumber = wn });

        modem.Process(air.Noise(rate));
        modem.CarrierDetect.Should().BeFalse();

        float[] weak = air.Burst(tx.Modulate(UiFrame(seed, 200), 0), snrDb);
        bool heard = false;
        for (int at = 0; at < weak.Length; at += block)
        {
            modem.Process(weak.AsSpan(at, Math.Min(block, weak.Length - at)));
            heard |= modem.CarrierDetect;
        }

        heard.Should().BeTrue("the weak burst's preamble is acquired - that is the case in hand");
        frames.Should().Be(0, "at this SNR the burst cannot be read");

        long releasedAfter = modem.CarrierDetect
            ? FeedUntilReleased(modem, () => air.Noise(block), 60L * rate)
            : 0;
        output.WriteLine($"wn{wn} at {snrDb} dB: let go {releasedAfter / (double)rate:F2} s after the burst ended");
        releasedAfter.Should().BeGreaterThanOrEqualTo(0, "the receiver must not stay locked on noise");
        releasedAfter.Should().BeLessThanOrEqualTo((long)(ReleaseBoundSeconds * rate) + block);
        modem.CarrierDetect.Should().BeFalse();
        modem.LocksReleased.Should().Be(1);
        journal.Should().ContainSingle().Which.Should().Contain($"wn{wn} lock").And.Contain("listening afresh");
        journal[0].Should().MatchRegex("^[ -~]+$", "a journal line is plain ASCII");

        // Listening afresh: quiet on noise, and the next real burst is read.
        modem.Process(air.Noise(2 * rate));
        modem.CarrierDetect.Should().BeFalse();
        modem.Process(air.Burst(tx.Modulate(UiFrame(seed + 1, 200), 0), 10));
        modem.Process(air.Noise(2 * rate));
        frames.Should().Be(1);
        modem.CarrierDetect.Should().BeFalse();
        modem.LocksReleased.Should().Be(1, "a burst that ends with its EOM is not a release");
    }

    public static TheoryData<int, string, double> LongBurstPoints() => new()
    {
        // The Poor-channel mask points (Ms110dMaskTests.PoorMasks) of the K = 32 modes the
        // catalogue defaults to, and the lowest-SNR mask point of all on the K = 48 probe.
        { 4, "poor", 10 },
        { 6, "poor", 14 },
        { 1, "awgn", -3 },
    };

    [Theory]
    [MemberData(nameof(LongBurstPoints))]
    public void A_Two_Minute_Packed_Burst_At_A_Decodable_Snr_Is_Never_Let_Go_Early(
        int wn, string channel, double snrDb)
    {
        const int rate = Ms110dModulator.NativeRate;
        var tx = new Ms110dModem(rate, _ => { }, new Ms110dTxSettings { WaveformNumber = wn })
        {
            Packing = new FramePacking(TimeSpan.FromSeconds(120), TimeSpan.Zero),
        };

        int payload = wn == 1 ? 120 : 960;
        var queue = Enumerable.Range(0, 400).Select(i => UiFrame(100 + i, payload)).ToList();
        List<byte[]> frames = queue.Take(tx.FramesPerBurst(queue)).ToList();
        float[] burst = tx.ModulateFrames(frames, 0);
        (burst.Length / (double)rate).Should().BeInRange(110, 120, "the burst fills its two minutes");

        WattersonPath[] paths = channel == "poor" ? WattersonChannel.Poor : [];
        const int leadIn = rate;
        float[] onAir = new WattersonChannel(rate, seed: 553 + wn, paths).Apply(
            burst, snrDb, leadInSamples: leadIn, leadOutSamples: 2 * rate);

        var received = new List<byte[]>();
        var rx = new Ms110dModem(rate, received.Add);
        long locked = -1, dropped = -1;
        for (int at = 0; at < onAir.Length; at += rate / 10)
        {
            rx.Process(onAir.AsSpan(at, Math.Min(rate / 10, onAir.Length - at)));
            long fed = Math.Min(onAir.Length, at + (rate / 10));
            if (locked < 0 && rx.CarrierDetect)
            {
                locked = fed;
            }
            else if (locked >= 0 && dropped < 0 && !rx.CarrierDetect)
            {
                dropped = fed;
            }
        }

        long end = leadIn + burst.Length;
        output.WriteLine(
            $"wn{wn} {channel} {snrDb} dB: {burst.Length / (double)rate:F1} s burst, {frames.Count} frames sent, " +
            $"{received.Count} read, carrier held {(dropped - locked) / (double)rate:F1} s, dropped " +
            $"{(dropped - end) / (double)rate:F2} s after the burst's end");
        rx.LocksReleased.Should().Be(0);
        locked.Should().BeInRange(leadIn, leadIn + (3 * rate), "the preamble is acquired");
        dropped.Should().BeGreaterThan(end - rate, "the lock is held to the burst's EOM");
        received.Should().NotBeEmpty();
        if (channel == "awgn")
        {
            received.Should().HaveCount(frames.Count, "at the AWGN mask point every frame is read");
        }
    }

    [Fact]
    public void A_Release_Ends_The_Burst_With_Its_Own_Reason_And_Count()
    {
        const int rate = Ms110dModulator.NativeRate;
        var air = new Air(rate, 11);
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 5 });
        var bits = new byte[1000];
        new Random(11).NextBytes(bits);
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] &= 1;
        }

        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.Process(air.Noise(rate));
        demod.Process(air.Burst(tx.Modulate(bits), -6));
        demod.CarrierDetect.Should().BeTrue();
        for (int s = 0; s < 10 && demod.CarrierDetect; s++)
        {
            demod.Process(air.Noise(rate));
        }

        bursts.Should().ContainSingle();
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.SignalAbsent);
        bursts[0].Lock.Should().NotBeNull();
        bursts[0].Lock!.WaveformNumber.Should().Be(5);
        demod.SignalAbsentReleases.Should().Be(1);
        demod.State.Should().Be(Ms110dRxState.Searching);
        demod.Reset();
        demod.SignalAbsentReleases.Should().Be(0, "the counter is since construction or Reset");
    }

    [Fact]
    public void The_Release_Does_Not_Depend_On_How_The_Input_Is_Cut_Into_Blocks()
    {
        const int rate = Ms110dModulator.NativeRate;
        var air = new Air(rate, 12);
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4 });
        var bits = new byte[1200];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)((i * 7) % 3 == 0 ? 1 : 0);
        }

        float[] audio = [.. air.Noise(rate), .. air.Burst(tx.Modulate(bits), -6), .. air.Noise(8 * rate)];
        var seen = new List<(int Frames, Ms110dBurstEndReason Reason, int Blocks)>();
        foreach (int block in new[] { 37, 960, 4801, audio.Length })
        {
            var demod = new Ms110dDemodulator();
            int frames = 0;
            demod.FrameDiagnostics += _ => frames++;
            var ended = new List<(int, Ms110dBurstEndReason, int)>();
            demod.BurstCompleted += b => ended.Add((frames, b.Reason, b.Blocks));
            for (int at = 0; at < audio.Length; at += block)
            {
                demod.Process(audio.AsSpan(at, Math.Min(block, audio.Length - at)));
            }

            ended.Should().ContainSingle($"one burst, fed {block} samples at a time");
            seen.Add(ended[0]);
        }

        seen[0].Reason.Should().Be(Ms110dBurstEndReason.SignalAbsent);
        seen.Should().AllBeEquivalentTo(seen[0], "the frame the receiver lets go at is a property of the air, not of the block size");
    }

    // ------------------------------------------------------------------ evidence instruments

    /// <summary>
    /// Release-latency census for the note (<c>MS110D_SIGNAL_ABSENT_CENSUS=1</c>; minutes):
    /// weak bursts at every DFE waveform over AWGN and Poor, many seeds each, and the time from
    /// each burst's last sample to the release. A burst that is read to its EOM is counted as
    /// such, not as a release. <c>MS110D_CENSUS_SEEDS</c> sets the seeds per cell (default 40).
    /// </summary>
    [Fact]
    public void Release_Latency_Census()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_SIGNAL_ABSENT_CENSUS") != "1",
            "set MS110D_SIGNAL_ABSENT_CENSUS=1 for the release-latency census");
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("MS110D_CENSUS_SEEDS"), out int n) ? n : 40;
        const int rate = Ms110dModulator.NativeRate;
        int[] wns = [1, 2, 3, 4, 5, 6, 7, 8, 13];
        (string Channel, double Snr)[] cells =
        [
            ("awgn", -3), ("awgn", -6), ("awgn", -9), ("awgn", -12),
            ("poor", -3), ("poor", -6), ("poor", -9),
        ];
        var work = (from wn in wns from cell in cells from s in Enumerable.Range(0, seeds) select (wn, cell, s)).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<(int Wn, string Channel, double Snr, string Outcome, double Latency)>();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Workers() }, w =>
        {
            int seed = 10_000 + (w.wn * 1000) + w.s;
            var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = w.wn });
            var random = new Random(seed);
            var bits = new byte[(int)(4 * tx.Mode.Bps)]; // a few seconds of data behind the preamble
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = (byte)random.Next(2);
            }

            float[] audio = tx.Modulate(bits);
            WattersonPath[] paths = w.cell.Channel == "poor" ? WattersonChannel.Poor : [];
            const int tail = 30 * rate;
            float[] onAir = new WattersonChannel(rate, seed, paths).Apply(
                audio, w.cell.Snr, leadInSamples: rate, leadOutSamples: tail);
            long end = rate + audio.Length;

            var demod = new Ms110dDemodulator();
            Ms110dBurstEndReason? reason = null;
            long endedAt = -1;
            long fed = 0;
            bool locked = false;
            demod.BurstCompleted += b =>
            {
                reason ??= b.Reason;
                if (endedAt < 0)
                {
                    endedAt = fed;
                }
            };
            for (int at = 0; at < onAir.Length && endedAt < 0; at += 960)
            {
                int len = Math.Min(960, onAir.Length - at);
                demod.Process(onAir.AsSpan(at, len));
                fed = at + len;
                locked |= demod.CarrierDetect;
            }

            // CarrierDetect also covers reading the preamble: a burst whose WID fails goes back
            // to searching with no burst to end, which is not a lock.
            string outcome = reason is { } ended ? ended.ToString()
                : demod.State == Ms110dRxState.Tracking ? "still-locked"
                : locked ? "preamble-only"
                : "not-acquired";
            results.Add((w.wn, w.cell.Channel, w.cell.Snr, outcome, (endedAt - end) / (double)rate));
        });

        foreach (var group in results.GroupBy(r => (r.Wn, r.Channel, r.Snr)).OrderBy(g => g.Key))
        {
            var outcomes = group.GroupBy(r => r.Outcome).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()}");
            var released = group.Where(r => r.Outcome is "SignalAbsent" or "SignalLost")
                .Select(r => r.Latency).OrderBy(x => x).ToList();
            string latency = released.Count == 0 ? "" :
                string.Create(CultureInfo.InvariantCulture,
                    $" | let go after the end: min {released[0]:F2} median {released[released.Count / 2]:F2} " +
                    $"p95 {released[(int)(0.95 * (released.Count - 1))]:F2} max {released[^1]:F2} s");
            output.WriteLine($"wn{group.Key.Wn} {group.Key.Channel} {group.Key.Snr} dB: {string.Join(", ", outcomes)}{latency}");
        }

        results.Should().NotContain(r => r.Outcome == "still-locked");
        results.Where(r => r.Outcome == "SignalAbsent").Should().OnlyContain(r => r.Latency <= ReleaseBoundSeconds);
    }

    /// <summary>
    /// Margin census for the note (<c>MS110D_SIGNAL_ABSENT_MARGIN=1</c>; tens of minutes): the
    /// lowest 4 s window of the probe-presence statistic, as a multiple of the release line,
    /// over 90 s bursts at every Poor and AWGN mask point. <c>MS110D_CENSUS_SEEDS</c> seeds per
    /// point (default 10).
    /// </summary>
    [Fact]
    public void Presence_Margin_At_The_Mask_Points()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_SIGNAL_ABSENT_MARGIN") != "1",
            "set MS110D_SIGNAL_ABSENT_MARGIN=1 for the margin census");
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("MS110D_CENSUS_SEEDS"), out int n) ? n : 10;
        const int rate = Ms110dModulator.NativeRate;
        (int Wn, string Channel, double Snr)[] points =
        [
            (1, "poor", 3), (2, "poor", 5), (3, "poor", 7), (4, "poor", 10), (5, "poor", 11),
            (6, "poor", 14), (7, "poor", 19), (8, "poor", 23), (13, "poor", 11),
            (1, "awgn", -3), (2, "awgn", 0), (3, "awgn", 3), (4, "awgn", 5), (5, "awgn", 6),
            (6, "awgn", 9), (7, "awgn", 13), (8, "awgn", 16), (13, "awgn", 6),
        ];
        var work = (from p in points from s in Enumerable.Range(0, seeds) select (p, s)).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<(int Wn, string Channel, double Snr, double MinRatio, string Reason)>();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Workers() }, w =>
        {
            int seed = 20_000 + (w.p.Wn * 1000) + w.s + (w.p.Channel == "poor" ? 500 : 0);
            var tx = new Ms110dModulator(new Ms110dTxSettings
            {
                WaveformNumber = w.p.Wn, Interleaver = Ms110dInterleaverKind.Long, PreambleSuperframes = 20,
            });
            var random = new Random(seed);
            var bits = new byte[90 * tx.Mode.Bps];
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = (byte)random.Next(2);
            }

            WattersonPath[] paths = w.p.Channel == "poor" ? WattersonChannel.Poor : [];
            float[] onAir = new WattersonChannel(rate, seed, paths).Apply(
                tx.Modulate(bits), w.p.Snr, leadInSamples: 2400, leadOutSamples: 2400);
            var demod = new Ms110dDemodulator();
            var window = new Queue<double>();
            int frames = (int)Math.Ceiling(Ms110dDemodulator.PresenceWindowSeconds * 2400 / (tx.Mode.U + tx.Mode.K));
            double sum = 0, threshold = double.NaN, minRatio = double.PositiveInfinity;
            demod.FrameDiagnostics += line =>
            {
                int at = line.IndexOf("presence=", StringComparison.Ordinal);
                if (at < 0)
                {
                    return;
                }

                string[] parts = line[(at + 9)..].Split('/');
                double presence = double.Parse(parts[0], CultureInfo.InvariantCulture);
                threshold = double.Parse(parts[1], CultureInfo.InvariantCulture);
                window.Enqueue(presence);
                sum += presence;
                if (window.Count > frames)
                {
                    sum -= window.Dequeue();
                }

                if (window.Count == frames)
                {
                    minRatio = Math.Min(minRatio, sum / frames / threshold);
                }
            };
            string reason = "none";
            demod.BurstCompleted += b => reason = b.Reason.ToString();
            demod.Process(onAir);
            results.Add((w.p.Wn, w.p.Channel, w.p.Snr, minRatio, reason));
        });

        foreach (var group in results.GroupBy(r => (r.Channel, r.Wn, r.Snr)).OrderBy(g => g.Key))
        {
            var reasons = group.GroupBy(r => r.Reason).Select(g => $"{g.Key} {g.Count()}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{group.Key.Channel} wn{group.Key.Wn} {group.Key.Snr} dB: lowest 4 s window " +
                $"{group.Min(r => r.MinRatio):F1}x the release line (median of bursts " +
                $"{group.Select(r => r.MinRatio).OrderBy(x => x).ElementAt(group.Count() / 2):F1}x); ended {string.Join(", ", reasons)}"));
        }

        results.Should().NotContain(r => r.Reason == "SignalAbsent");
    }

    /// <summary>
    /// The issue's CPU measurement (<c>MS110D_LOCK_CPU=1</c>; an hour of wall clock or more):
    /// at the daemon's 48 kHz, one weak WN4 burst then <c>MS110D_LOCK_CPU_HOURS</c> (default
    /// 3) simulated hours of noise, and the same hours of noise with no burst as the idle
    /// baseline. Reports process CPU time spent inside <see cref="Ms110dModem.Process"/> per
    /// simulated hour. A measurement, never an assertion: it depends on the box.
    /// </summary>
    [Fact]
    public void Cpu_Per_Simulated_Hour_After_A_Weak_Burst()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_LOCK_CPU") != "1",
            "set MS110D_LOCK_CPU=1 for the CPU measurement");
        double hours = double.TryParse(Environment.GetEnvironmentVariable("MS110D_LOCK_CPU_HOURS"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 3;
        string which = Environment.GetEnvironmentVariable("MS110D_LOCK_CPU_RUN") ?? "both";
        if (which is "both" or "weak")
        {
            Measure(weakBurst: true);
        }

        if (which is "both" or "idle")
        {
            Measure(weakBurst: false);
        }

        void Measure(bool weakBurst)
        {
            const int rate = 48000;
            const int block = rate / 10;
            var air = new Air(rate, 2026);
            var modem = new Ms110dModem(rate, _ => { });
            var process = Process.GetCurrentProcess();
            TimeSpan cpu = TimeSpan.Zero;
            long lockedFor = 0;

            // Timed a minute of air at a time (process CPU ticks are 10 ms; a 0.1 s block's
            // share is far below that), with the noise for it made before the clock starts.
            void Feed(float[] samples)
            {
                process.Refresh();
                TimeSpan before = process.TotalProcessorTime;
                for (int at = 0; at < samples.Length; at += block)
                {
                    modem.Process(samples.AsSpan(at, Math.Min(block, samples.Length - at)));
                    if (modem.CarrierDetect)
                    {
                        lockedFor += Math.Min(block, samples.Length - at);
                    }
                }

                process.Refresh();
                cpu += process.TotalProcessorTime - before;
            }

            Feed(air.Noise(rate));
            if (weakBurst)
            {
                var tx = new Ms110dModem(rate, _ => { }, new Ms110dTxSettings { WaveformNumber = 4 });
                Feed(air.Burst(tx.Modulate(UiFrame(1, 200), 0), -6));
            }

            cpu = TimeSpan.Zero;
            lockedFor = 0;
            long total = (long)(hours * 3600 * rate);
            for (long fed = 0; fed < total; fed += 60 * rate)
            {
                Feed(air.Noise((int)Math.Min(60L * rate, total - fed)));
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(weakBurst ? "after one weak WN4 burst" : "noise only (idle baseline)")}: " +
                $"{cpu.TotalSeconds / hours:F1} s of CPU per simulated hour ({100 * cpu.TotalSeconds / (hours * 3600):F2}% of a core), " +
                $"carrier held {lockedFor / (double)rate:F1} s of {hours:F1} h, locks released {modem.LocksReleased}"));
        }
    }

    private static int Workers() =>
        int.TryParse(Environment.GetEnvironmentVariable("MS110D_CENSUS_WORKERS"), out int w) ? Math.Max(1, w) : 4;
}
