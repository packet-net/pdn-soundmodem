using System.Globalization;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// Regression rig for issue #101 — the WN2 (BPSK r1/4, K=48) DFE dead-init edge. Over a
/// low absolute receive level (no AGC in this front end), the K=48 init LS solve's ridge
/// (<c>initRidge = 1.0</c>, scaled by the mean Gram diagonal — which is dominated by the
/// fixed-magnitude feedback regressors) over-shrinks the feed-forward taps, so the solve
/// returns a near-dead equalizer (init gain ≈ 0) the anchored (<c>trackRidge = 8</c>)
/// tracker cannot recover from → the burst ends <see cref="Ms110dBurstEndReason.SignalLost"/>.
/// The reproduction is the issue's own controlled one: build a WN2 Poor burst that decodes
/// cleanly at full level, then scale the received samples down (the −20 dB nudge the real
/// path applied) and watch the equalizer initialise dead.
/// </summary>
public class Ms110dDeadInitTests
{
    // A deterministic WN2 Poor burst, built exactly as Ms110dMaskTests.RunPointWorker /
    // Ms110dTailAutopsy build theirs (Long interleaver, K=7, 20-super-frame preamble). The
    // seed is bespoke (outside the gated mask seed families) — this is a robustness fixture,
    // not a mask point.
    private const int FixtureSeed = 424_242;
    private const double Wn2PoorSnrDb = 5.0;

    private static (float[] Rx, byte[] Payload) BuildWn2PoorBurst(int payloadSeed, int channelSeed, double scale)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = 2,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(2, Ms110dInterleaverKind.Long);
        double blockSeconds = il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;

        var random = new Random(payloadSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);
        var channel = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor);
        float[] rx = channel.Apply(audio, Wn2PoorSnrDb, leadInSamples: 2400, leadOutSamples: 2400);
        if (scale != 1.0)
        {
            for (int i = 0; i < rx.Length; i++)
            {
                rx[i] = (float)(rx[i] * scale);
            }
        }

        return (rx, payload);
    }

    private sealed record DecodeResult(
        Ms110dBurstEndReason? Reason, int Decoded, long CodedErrors, double InitRefGain);

    private static DecodeResult RunBurst(
        float[] rx, byte[] payload, Ms110dDemodOptions? options = null, Action<string>? frameSink = null)
    {
        var demod = new Ms110dDemodulator(options ?? new Ms110dDemodOptions());
        var decoded = new List<byte>(payload.Length + 64);
        Ms110dBurstEndReason? reason = null;
        double initRefGain = double.NaN;
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.BurstCompleted += bu => reason = bu.Reason;
        if (frameSink is not null)
        {
            demod.FrameDiagnostics += frameSink;
        }

        demod.FrameDiagnostics += line =>
        {
            // The FIRST frame diagnostic reports ref=<init gain> (InitializeDfe seeds
            // _probeGainRef, and ProcessFrame prints it before the healthy-probe update).
            if (double.IsNaN(initRefGain))
            {
                int at = line.IndexOf("ref=", StringComparison.Ordinal);
                if (at >= 0)
                {
                    int start = at + 4;
                    int end = line.IndexOf(' ', start);
                    initRefGain = double.Parse(
                        line[start..(end < 0 ? line.Length : end)], CultureInfo.InvariantCulture);
                }
            }
        };
        demod.Process(rx);

        long codedErrors = 0;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i])
            {
                codedErrors++;
            }
        }

        codedErrors += payload.Length - compared; // truncated decode counts as errors
        return new DecodeResult(reason, decoded.Count, codedErrors, initRefGain);
    }

    // The red test (issue #101): a WN2 Poor burst that decodes cleanly at full receive level
    // dies SignalLost with no coded output once the level is scaled down ~−20 dB — the K=48
    // init LS solve returns a dead equalizer (init gain 0.081 → 0.014, matching the real-RF
    // capture) that the anchored tracker cannot rebuild. On current `main` this asserts RED
    // (reason SignalLost, init gain ≈ 0.014). With the dead-init guard the softer re-solve
    // hands tracking a live filter and the burst decodes — same SNR as full level, so clean.
    [Fact]
    public void Wn2_Poor_Dead_Init_Recovers_At_Low_Receive_Level()
    {
        // Env-gated reproduction (issue #101 investigation): RED on main (SignalLost), GREEN with
        // the FF-scaled cold-restart lever — but the lever regresses the WN2 disjoint sim mask
        // (12 → 31 = 1.02E-5), so it is NOT shipped (see the evidence README). Kept as an on-
        // demand reproduction, not a normal-suite gate.
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 — #101 investigation reproduction (lever not shipped)");

        // −20 dB — the issue's own controlled reproduction (real path level nudge).
        (float[] rx, byte[] payload) = BuildWn2PoorBurst(FixtureSeed, FixtureSeed + 1, scale: 0.1);
        DecodeResult r = RunBurst(rx, payload);

        r.Reason.Should().Be(
            Ms110dBurstEndReason.Eom,
            "the dead-init guard must keep the equalizer alive so the burst decodes to EOM " +
            "instead of dying SignalLost (init gain must not collapse to ~0)");
        r.InitRefGain.Should().BeGreaterThan(
            0.030,
            "the re-solved init gain must clear the dead floor (was ≈ 0.014 dead on main)");
        r.CodedErrors.Should().Be(
            0,
            "scaling is level-only (SNR unchanged), so a live equalizer decodes it as cleanly " +
            "as the full-level burst (0 coded errors)");
    }

    // The full-level control: the SAME burst at nominal level decodes cleanly on main and must
    // stay byte-clean under the fix — the guard is a no-op above the dead floor.
    [Fact]
    public void Wn2_Poor_Full_Level_Burst_Decodes_Clean()
    {
        // Env-gated reproduction (issue #101): the full-level control. On main this is 0 errors;
        // WITH the lever the freshSolve dead-restart guard fires on a full-level fade and injects
        // 3 coded errors — the same mechanism that regresses the disjoint mask. This test FAILS
        // under the lever by design, documenting that perturbation; on-demand only.
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 — #101 investigation reproduction (lever not shipped)");

        (float[] rx, byte[] payload) = BuildWn2PoorBurst(FixtureSeed, FixtureSeed + 1, scale: 1.0);
        DecodeResult r = RunBurst(rx, payload);

        r.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        r.CodedErrors.Should().Be(0);
        r.InitRefGain.Should().BeGreaterThan(
            0.030, "the full-level init is healthy and the guard must not fire on it");
    }

    // Calibration: sweep the receive-level scale and print the init gain / reason so the
    // dead-init flip point is visible. Env-gated so it never runs in the normal suite.
    [Fact]
    public void Dead_Init_Scale_Calibration()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 for the dead-init scale calibration");

        foreach (double scale in new[] { 1.0, 0.5, 0.25, 0.1, 0.05, 0.03, 0.02, 0.01 })
        {
            (float[] rx, byte[] payload) = BuildWn2PoorBurst(FixtureSeed, FixtureSeed + 1, scale);
            DecodeResult r = RunBurst(rx, payload);
            Console.Error.WriteLine(
                $"scale={scale:F3} ({20 * Math.Log10(scale):+0.0;-0.0} dB): reason={r.Reason} " +
                $"decoded={r.Decoded}/{payload.Length} codedErrs={r.CodedErrors} initRef={r.InitRefGain:F4}");
        }
    }

    // Trace: dump the first frames' diagnostics at scale 0.10 (default guard) to see where a
    // globally-weak burst dies. Env-gated.
    [Fact]
    public void Dead_Init_Frame_Trace()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 for the dead-init frame trace");

        (float[] rx, byte[] payload) = BuildWn2PoorBurst(FixtureSeed, FixtureSeed + 1, scale: 0.1);
        int n = 0;
        DecodeResult r = RunBurst(rx, payload, frameSink: line =>
        {
            if (line.StartsWith("frame@", StringComparison.Ordinal) && n++ < 24)
            {
                Console.Error.WriteLine(line);
            }
        });
        Console.Error.WriteLine($"RESULT reason={r.Reason} decoded={r.Decoded} codedErrs={r.CodedErrors}");
    }

    // Calibration: force the dead-init guard (floor 0.5, always fires) and sweep the
    // FF-scaled re-solve ridge at several receive-level scales, so the re-solve ridge that
    // rescues the dead cases to a clean decode is visible. Env-gated.
    [Fact]
    public void Dead_Init_Ridge_Sweep()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 for the dead-init ridge sweep");

        foreach (double scale in new[] { 0.5, 0.25, 0.1, 0.05 })
        {
            foreach (float ridge in new[] { 2.0f, 1.0f, 0.5f, 0.25f, 0.1f, 0.03f })
            {
                (float[] rx, byte[] payload) = BuildWn2PoorBurst(FixtureSeed, FixtureSeed + 1, scale);
                DecodeResult r = RunBurst(rx, payload, new Ms110dDemodOptions
                {
                    DeadInitFloor = 0.5f,
                    DeadInitRidge = ridge,
                });
                Console.Error.WriteLine(
                    $"scale={scale:F3} ridge={ridge:F2}: reason={r.Reason} " +
                    $"decoded={r.Decoded}/{payload.Length} codedErrs={r.CodedErrors} initRef={r.InitRefGain:F4}");
            }
        }
    }

    // Full-level init-gain census over the gated WN2 Poor mask seed families (canonical
    // 502 + disjoint 10502, workers 0..3, bursts 0..61 = the 6M budget). Reports the
    // MINIMUM init gain among these SURVIVING bursts — the dead-init floor must sit strictly
    // below it, so the guard never fires on a mask burst (byte-identity of the mask census).
    // Init gain depends on the channel realization + fixed preamble, not the payload.
    [Fact]
    public void Mask_Family_Init_Gain_Census()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 for the mask-family init-gain census");

        foreach (int baseSeed in new[] { 502, 10_502 })
        {
            double min = double.PositiveInfinity;
            int minWorker = -1, minBurst = -1, signalLost = 0;
            for (int worker = 0; worker < 4; worker++)
            {
                int workerSeed = baseSeed + (worker * 1_000_000);
                for (int burst = 0; burst < 62; burst++)
                {
                    int channelSeed = workerSeed + (1000 * burst) + 1;
                    (float[] rx, byte[] payload) = BuildWn2PoorBurst(workerSeed, channelSeed, 1.0);
                    DecodeResult r = RunBurst(rx, payload);
                    if (r.Reason == Ms110dBurstEndReason.SignalLost)
                    {
                        signalLost++;
                    }

                    if (r.InitRefGain < min)
                    {
                        min = r.InitRefGain;
                        minWorker = worker;
                        minBurst = burst;
                    }
                }
            }

            Console.Error.WriteLine(
                $"family {baseSeed}: min initRef={min:F4} at w{minWorker}/b{minBurst}, " +
                $"SignalLost bursts={signalLost}/248");
        }
    }
}
