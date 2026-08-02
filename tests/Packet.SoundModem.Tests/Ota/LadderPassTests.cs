using AwesomeAssertions;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Tests.Channel;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The §E2 ladder, verified end to end with no radio in the loop.
/// </summary>
/// <remarks>
/// <para>Renders a ladder exactly as it would go to the transmitter, lays it out as a capture
/// would record it, and puts it back through the real receive converter and the real scorer. The
/// only thing between the two ends that is not the production path is the radio itself — so
/// everything except the hardware is proved before any power is applied, which is the whole
/// reason the harness is built this way round.</para>
/// <para>The assertion that matters is that the SNR the scorer <em>measures</em> matches the SNR
/// the rig was <em>asked</em> to inject. If those two disagree, every E2 point is plotted at the
/// wrong place on the comparison curve and the campaign quietly re-fits itself — the failure
/// this project has already paid for once.</para>
/// </remarks>
public class LadderPassTests
{
    private const int CaptureRate = 48000;
    private const double OffsetHz = 2000;

    private static LadderPassOptions Options() => new()
    {
        OutputRate = CaptureRate, // render straight at the capture rate: no radio in this loop
        OffsetHz = OffsetHz,
        PreambleSuperframes = 3,
    };

    /// <summary>Lays rendered points out as a capture would hold them, with quiet between, and
    /// returns the IQ plus the time each burst's noise lead-in began.</summary>
    private static (short[] Iq, double[] LeadInStarts) LayOut(
        IReadOnlyList<RenderedPoint> points, double gapSeconds = 2.0, int seed = 4)
    {
        var random = new Random(seed);
        var samples = new List<short>();
        var starts = new List<double>();

        void Quiet(double seconds)
        {
            int frames = (int)(seconds * CaptureRate);
            for (int k = 0; k < frames; k++)
            {
                // A real capture is never digital silence between transmissions.
                samples.Add((short)random.Next(-40, 40));
                samples.Add((short)random.Next(-40, 40));
            }
        }

        Quiet(gapSeconds);
        foreach (RenderedPoint point in points)
        {
            starts.Add(samples.Count / 2.0 / CaptureRate);
            foreach (float v in point.Iq)
            {
                samples.Add((short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
            }

            Quiet(gapSeconds);
        }

        return ([.. samples], [.. starts]);
    }

    private static IEnumerable<ReadOnlyMemory<float>> Convert(short[] iq)
    {
        var converter = new StreamingIqToAudioConverter(new IqToAudioOptions
        {
            InputRate = CaptureRate,
            OutputRate = 9600,
            DialHz = OffsetHz,
            NormalisePeak = 0f,
        });

        const int block = 1 << 15;
        var output = new float[converter.MaxOutputFor(block) + converter.MaxFlushOutput];
        for (int start = 0; start < iq.Length / 2; start += block)
        {
            int frames = Math.Min(block, (iq.Length / 2) - start);
            int wrote = converter.Process(iq.AsSpan(start * 2, frames * 2), output);
            yield return output.AsMemory(0, wrote);
        }

        yield return output.AsMemory(0, converter.Flush(output));
    }

    [Fact]
    public void The_signal_power_is_identical_at_every_rung_and_only_the_noise_moves()
    {
        // The level policy, asserted directly on what would be transmitted. If a low-SNR point
        // came out quieter, the leakage path's own fixed noise would be a larger share of it —
        // a second, uncalibrated dose of noise exactly where the ladder is most delicate.
        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(
            waveformNumber: 6, snrsDb: [20, 10, 0], repeats: 1);
        IReadOnlyList<RenderedPoint> rendered = new LadderPass(Options()).Render(plan);

        // Total power rises as SNR falls, because noise is being added to a constant signal.
        var powers = rendered.Select(p => Power(p.Iq)).ToArray();
        powers[1].Should().BeGreaterThan(powers[0], "10 dB carries more noise than 20 dB");
        powers[2].Should().BeGreaterThan(powers[1], "0 dB carries more noise still");

        // And the peak of the worst point is what the gain was set from.
        double worstPeak = Math.Sqrt(rendered.Max(p => PeakSquared(p.Iq)));
        worstPeak.Should().BeApproximately(0.9, 0.01,
            "one gain for the pass, taken from the point whose envelope is worst");
        rendered.Take(2).All(p => Math.Sqrt(PeakSquared(p.Iq)) < worstPeak).Should().BeTrue(
            "the quieter points are quieter only because they carry less noise");
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(12.0)]
    [InlineData(6.0)]
    public void The_snr_the_scorer_measures_is_the_snr_the_rig_injected(double snrDb)
    {
        // The load-bearing one. Nominal SNR is never trusted anywhere in this harness: each
        // point carries its own noise lead-in on the air so the receiver can measure what was
        // actually delivered. This proves the two agree through the real converter and scorer.
        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(
            waveformNumber: 6, snrsDb: [snrDb], repeats: 1, firstSeed: 31);
        IReadOnlyList<RenderedPoint> rendered = new LadderPass(Options()).Render(plan);
        (short[] iq, double[] starts) = LayOut(rendered);

        var schedule = rendered
            .Select((p, k) => new ScheduledBurst(p.Reference, starts[k] + p.LeadInSeconds))
            .ToList();

        CaptureScore score = new BurstScorer(schedule).Score(Convert(iq));

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Snr.Should().NotBeNull();
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(snrDb, 1.5,
            "the transmitted noise lead-in exists so the receiver measures the delivered SNR "
            + "rather than trusting the requested one");
    }

    [Fact]
    public void A_whole_ladder_runs_through_the_offline_loop_and_degrades_in_the_right_direction()
    {
        // The rehearsal: several rungs, laid out as a capture, scored in one pass. WN6's AWGN
        // mask gate is 9 dB, so 18 dB must be clean and 3 dB must not be.
        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(
            waveformNumber: 6, snrsDb: [18, 12, 6, 3], repeats: 1, firstSeed: 41);
        IReadOnlyList<RenderedPoint> rendered = new LadderPass(Options()).Render(plan);
        (short[] iq, double[] starts) = LayOut(rendered);

        var schedule = rendered
            .Select((p, k) => new ScheduledBurst(p.Reference, starts[k] + p.LeadInSeconds))
            .ToList();

        CaptureScore score = new BurstScorer(schedule).Score(Convert(iq));

        score.Missed.Should().BeEmpty("every rung down to 3 dB should at least be acquired");
        score.Bursts.Should().HaveCount(4);

        // Measured SNR must track the request, monotonically, across the whole ladder.
        var measured = score.Bursts.Select(b => b.Snr!.SnrDb).ToArray();
        for (int k = 0; k < 4; k++)
        {
            measured[k].Should().BeApproximately(plan[k].SnrDb, 1.5);
        }

        // And the uncoded rate must be the thing that separates the rungs.
        score.Bursts[0].UncodedBer.Should().BeLessThan(score.Bursts[3].UncodedBer,
            "18 dB must show fewer channel bit errors than 3 dB");
        score.Bursts[0].PayloadErrors.Should().Be(0, "18 dB is well above WN6's 9 dB AWGN gate");
    }

    [Fact]
    public void The_plan_visits_every_rung_before_repeating_any()
    {
        // A pass cut short by an operator, a dropped connection or the weather should still
        // cover the ladder rather than the top of it.
        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(
            waveformNumber: 2, snrsDb: [10, 5, 0], repeats: 3);

        plan.Should().HaveCount(9);
        plan.Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Skip(3).Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Select(p => p.Seed).Should().OnlyHaveUniqueItems(
            "a repeated rung must be a different payload and a different channel realisation, "
            + "or repeats measure the same draw over and over");
    }

    [Fact]
    public void The_poor_channel_is_the_mask_suites_own_rig()
    {
        // Not a new implementation of Poor — the same one, so the E2 differential is hardware
        // against simulation rather than one rig against another.
        IReadOnlyList<RenderedPoint> poor = new LadderPass(Options()).Render(
            LadderPass.Plan(6, [10], 1, LadderChannel.Poor, firstSeed: 7));
        IReadOnlyList<RenderedPoint> awgn = new LadderPass(Options()).Render(
            LadderPass.Plan(6, [10], 1, LadderChannel.Awgn, firstSeed: 7));

        typeof(LadderPass).Assembly.Should().BeSameAs(typeof(WattersonChannel).Assembly,
            "the rig is compiled into the OTA tool from the test project's copy — one definition, "
            + "or the comparison measures the gap between two rigs");
        Power(poor[0].Iq).Should().BeGreaterThan(0);
        poor[0].Iq.Should().NotEqual(awgn[0].Iq, "a fading channel is not the ideal path");
    }

    private static double Power(float[] iq)
    {
        double sum = 0;
        for (int k = 0; k < iq.Length; k++)
        {
            sum += iq[k] * (double)iq[k];
        }

        return sum / (iq.Length / 2);
    }

    private static double PeakSquared(float[] iq)
    {
        double peak = 0;
        for (int k = 0; k < iq.Length; k += 2)
        {
            peak = Math.Max(peak, (iq[k] * (double)iq[k]) + (iq[k + 1] * (double)iq[k + 1]));
        }

        return peak;
    }
}
