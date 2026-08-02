using AwesomeAssertions;
using Packet.SoundModem.Ota;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The FreeDV datac OFDM §E2 ladder, verified end to end with no radio in the loop — the OFDM
/// counterpart of <see cref="LadderPassTests"/>.
/// </summary>
/// <remarks>
/// <para>Renders a datac ladder exactly as it would go to the DAX transmitter, lays it out as an IQ
/// capture would record it, puts it back through the real receive converter and scores it with the
/// datac receiver. The only thing between the two ends that is not the production path is the radio
/// itself, so everything except the hardware is proved before any power is applied.</para>
/// <para>The load-bearing assertion is the same one the MS110D ladder rests on: the SNR the scorer
/// <em>measures</em> from each burst's own transmitted noise lead-in matches the SNR the rig was
/// <em>asked</em> to inject. If those disagree, every point is plotted at the wrong place on the
/// comparison curve.</para>
/// </remarks>
public class OfdmLadderPassTests
{
    private const int CaptureRate = 48000;

    private static OfdmLadderPassOptions Options() => new()
    {
        // Render straight at the capture rate (no radio in this loop). OffsetHz stays at its 0
        // default — the DAX route places the datac band at its native audio centre.
        OutputRate = CaptureRate,
    };

    /// <summary>Lays rendered points out as a capture would hold them, with quiet between, converts
    /// the IQ to the datac engine's 8 kHz audio and returns it with each burst's active-start time.</summary>
    private static (float[] Audio, double[] BurstStarts) LayOutAndConvert(
        IReadOnlyList<OfdmRenderedPoint> points, double gapSeconds = 2.0, int seed = 4)
    {
        var random = new Random(seed);
        var iq = new List<short>();
        var starts = new List<double>();

        void Quiet(double seconds)
        {
            int frames = (int)(seconds * CaptureRate);
            for (int k = 0; k < frames; k++)
            {
                iq.Add((short)random.Next(-40, 40));
                iq.Add((short)random.Next(-40, 40));
            }
        }

        Quiet(gapSeconds);
        foreach (OfdmRenderedPoint point in points)
        {
            // The active burst starts after its noise lead-in — the time the scorer windows on.
            starts.Add((iq.Count / 2.0 / CaptureRate) + point.LeadInSeconds);
            foreach (float v in point.Iq)
            {
                iq.Add((short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
            }

            Quiet(gapSeconds);
        }

        var converter = new StreamingSsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = CaptureRate,
            OutputRate = OfdmBurstScorer.NativeRate,
            DialHz = 0,
            NormalisePeak = 0f,
        });

        short[] samples = [.. iq];
        var audio = new List<float>();
        const int block = 1 << 15;
        var output = new float[converter.MaxOutputFor(block) + converter.MaxFlushOutput];
        for (int start = 0; start < samples.Length / 2; start += block)
        {
            int frames = Math.Min(block, (samples.Length / 2) - start);
            int wrote = converter.Process(samples.AsSpan(start * 2, frames * 2), output);
            audio.AddRange(output.AsSpan(0, wrote));
        }

        audio.AddRange(output.AsSpan(0, converter.Flush(output)));
        return ([.. audio], [.. starts]);
    }

    private static OfdmCampaignManifest Manifest(
        string mode, IReadOnlyList<OfdmLadderPoint> plan,
        IReadOnlyList<OfdmRenderedPoint> rendered, double[] starts) =>
        new(
            Name: "test",
            Mode: mode,
            OffsetHz: 0,
            CaptureRate: CaptureRate,
            Bursts:
            [
                .. plan.Select((p, k) => new OfdmCampaignBurst(
                    p.Mode, p.Seed, p.SnrDb, p.Channel, starts[k], rendered[k].BurstSeconds)),
            ],
            ModemRevision: "test",
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: "none (test)",
            FrequencyMHz: "18.106500",
            RfPower: null,
            PassAudioGain: 1,
            DialCorrectionHz: 0);

    [Theory]
    [InlineData("freedv-datac3", 12.0)]
    [InlineData("freedv-datac3", 6.0)]
    [InlineData("freedv-datac0", 10.0)]
    [InlineData("freedv-datac0", 4.0)]
    public void The_snr_the_scorer_measures_is_the_snr_the_rig_injected(string mode, double snrDb)
    {
        // The load-bearing one. Each rung carries its own noise lead-in on the air so the receiver
        // measures what was delivered rather than trusting the request; this proves the two agree
        // through the real SSB round-trip, converter and OFDM scorer.
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan(mode, [snrDb], repeats: 1, firstSeed: 31);
        IReadOnlyList<OfdmRenderedPoint> rendered = new OfdmLadderPass(Options()).Render(plan);
        (float[] audio, double[] starts) = LayOutAndConvert(rendered);

        OfdmCaptureScore score = new OfdmBurstScorer(audio).Score(Manifest(mode, plan, rendered, starts));

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Snr.Should().NotBeNull();
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(snrDb, 1.2,
            "the transmitted noise lead-in exists so the receiver measures the delivered SNR "
            + "rather than trusting the requested one");
    }

    [Theory]
    [InlineData("freedv-datac3")]
    [InlineData("freedv-datac0")]
    public void A_whole_datac_ladder_decodes_clean_in_its_working_range(string mode)
    {
        // The rehearsal: several healthy rungs, laid out as a capture, scored in one pass. Both modes
        // decode comfortably at these SNRs, so every burst must acquire and pass CRC with zero coded
        // BER, and the measured SNR must track the request across the whole ladder.
        double[] snrs = [12, 9, 6];
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan(mode, snrs, repeats: 1, firstSeed: 41);
        IReadOnlyList<OfdmRenderedPoint> rendered = new OfdmLadderPass(Options()).Render(plan);
        (float[] audio, double[] starts) = LayOutAndConvert(rendered);

        OfdmCaptureScore score = new OfdmBurstScorer(audio).Score(Manifest(mode, plan, rendered, starts));

        score.Bursts.Should().HaveCount(snrs.Length);
        score.Bursts.Should().OnlyContain(b => b.Acquired, "every healthy rung must acquire");
        score.Bursts.Should().OnlyContain(b => b.CrcOk, "every healthy rung must pass CRC");
        score.Bursts.Should().OnlyContain(b => b.CodedBer == 0, "coded BER is zero above the cliff");
        for (int k = 0; k < snrs.Length; k++)
        {
            score.Bursts[k].Snr!.SnrDb.Should().BeApproximately(snrs[k], 1.2,
                "measured SNR must track the request across the whole ladder");
        }
    }

    [Fact]
    public void The_scorer_reports_the_cliff_rather_than_papering_over_it()
    {
        // A scorer that always passes is worthless. datac0 falls off its AWGN cliff a few dB below
        // 0 dB, so a rung deep below it must be caught: no acquisition, and every payload bit wrong.
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan(
            "freedv-datac0", [12, -12], repeats: 1, firstSeed: 55);
        IReadOnlyList<OfdmRenderedPoint> rendered = new OfdmLadderPass(Options()).Render(plan);
        (float[] audio, double[] starts) = LayOutAndConvert(rendered);

        OfdmCaptureScore score = new OfdmBurstScorer(audio).Score(
            Manifest("freedv-datac0", plan, rendered, starts));

        score.Bursts[0].CrcOk.Should().BeTrue("12 dB is well above datac0's AWGN cliff");
        score.Bursts[0].CodedBer.Should().Be(0);
        score.Bursts[1].Acquired.Should().BeFalse("−12 dB is well below the cliff — the burst is lost");
        score.Bursts[1].CodedBer.Should().Be(1.0, "a lost packet counts every payload bit wrong");
    }

    [Fact]
    public void The_signal_power_is_identical_at_every_rung_and_only_the_noise_moves()
    {
        // The level policy, asserted on the transmitted audio. A low-SNR rung must not come out
        // quieter, or the leakage path's own fixed noise would be a larger, uncalibrated share of it.
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan("freedv-datac3", [20, 10, 0], repeats: 1);
        IReadOnlyList<OfdmRenderedPoint> rendered = new OfdmLadderPass(Options()).Render(plan);

        double[] powers = rendered.Select(p => Power(p.Iq)).ToArray();
        powers[1].Should().BeGreaterThan(powers[0], "10 dB carries more noise than 20 dB");
        powers[2].Should().BeGreaterThan(powers[1], "0 dB carries more noise still");

        double worstPeak = Math.Sqrt(rendered.Max(p => PeakSquared(p.Iq)));
        worstPeak.Should().BeApproximately(0.9, 0.01,
            "one gain for the pass, taken from the point whose envelope is worst");
    }

    [Fact]
    public void The_plan_visits_every_rung_before_repeating_any_with_unique_seeds()
    {
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan("freedv-datac3", [10, 5, 0], repeats: 3);

        plan.Should().HaveCount(9);
        plan.Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Skip(3).Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Select(p => p.Seed).Should().OnlyHaveUniqueItems(
            "a repeated rung must be a different payload and channel realisation");
    }

    [Fact]
    public void An_unknown_mode_is_rejected_before_any_rendering()
    {
        Action plan = () => OfdmLadderPass.Plan("ms110d-wn6", [10], repeats: 1);
        plan.Should().Throw<ArgumentException>("the OFDM ladder only renders FreeDV datac modes");
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
