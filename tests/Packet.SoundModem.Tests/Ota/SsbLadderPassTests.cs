using AwesomeAssertions;
using Packet.SoundModem.Ota;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The SSB audio-carrier §E2 ladder (AFSK 300, BPSK, QPSK over DIGU SSB), verified end to end with no
/// radio in the loop — the audio-carrier counterpart of <see cref="OfdmLadderPassTests"/> and
/// <see cref="LadderPassTests"/>.
/// </summary>
/// <remarks>
/// <para>Renders a ladder exactly as it would go to the DAX transmitter, lays it out as an IQ capture
/// would record it, puts it back through the real receive converter and scores it with the mode's own
/// demodulator through the <c>IModem</c> seam. The only thing between the two ends that is not the
/// production path is the radio itself, so everything except the hardware is proved before any power is
/// applied.</para>
/// <para>The load-bearing assertion is the same one the MS110D and OFDM ladders rest on: the SNR the
/// scorer <em>measures</em> from each burst's own transmitted noise lead-in matches the SNR the rig was
/// <em>asked</em> to inject. If those disagree, every point is plotted at the wrong place on the
/// comparison curve.</para>
/// </remarks>
public class SsbLadderPassTests
{
    private const int NativeRate = 12000; // every SSB audio-carrier mode runs at 12 kHz
    private const int CaptureRate = 48000;

    private static SsbLadderPassOptions Options(double offsetHz = 0) => new()
    {
        // Render straight at the capture rate (no radio in this loop). OffsetHz is 0 for the DAX-style
        // placement (carrier at its native audio centre) and 2000 for the IQ route's software SSB.
        OutputRate = CaptureRate,
        OffsetHz = offsetHz,
    };

    /// <summary>Lays rendered points out as a capture would hold them, with quiet between, converts the
    /// IQ back to the mode's 12 kHz audio (down-shifting by <paramref name="dialHz"/>, which must equal
    /// the pass offset) and returns it with each burst's active-start time.</summary>
    private static (float[] Audio, double[] BurstStarts) LayOutAndConvert(
        IReadOnlyList<SsbRenderedPoint> points, double dialHz = 0, double gapSeconds = 2.0, int seed = 4)
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
        foreach (SsbRenderedPoint point in points)
        {
            // The active burst starts after its noise lead-in — the time the scorer windows on.
            starts.Add((iq.Count / 2.0 / CaptureRate) + point.LeadInSeconds);
            foreach (float v in point.Iq)
            {
                iq.Add((short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
            }

            Quiet(gapSeconds);
        }

        var converter = new StreamingIqToAudioConverter(new IqToAudioOptions
        {
            InputRate = CaptureRate,
            OutputRate = NativeRate,
            DialHz = dialHz,
            SsbLowHz = 150,
            SsbHighHz = 3450,
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

    private static SsbCampaignManifest Manifest(
        string mode, IReadOnlyList<SsbLadderPoint> plan,
        IReadOnlyList<SsbRenderedPoint> rendered, double[] starts) =>
        new(
            Name: "test",
            Mode: mode,
            OffsetHz: 0,
            CaptureRate: CaptureRate,
            Bursts:
            [
                .. plan.Select((p, k) => new SsbCampaignBurst(
                    p.Mode, p.Seed, p.FrameBytes, p.SnrDb, p.Channel, starts[k], rendered[k].BurstSeconds)),
            ],
            ModemRevision: "test",
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: "none (test)",
            FrequencyMHz: "18.106500",
            RfPower: null,
            PassGain: 1,
            DialCorrectionHz: 0);

    private static SsbCaptureScore RenderAndScore(
        string mode, double[] snrs, int firstSeed, double offsetHz = 0)
    {
        IReadOnlyList<SsbLadderPoint> plan = SsbLadderPass.Plan(mode, snrs, repeats: 1, firstSeed: firstSeed);
        IReadOnlyList<SsbRenderedPoint> rendered = new SsbLadderPass(Options(offsetHz)).Render(plan);
        (float[] audio, double[] starts) = LayOutAndConvert(rendered, dialHz: offsetHz);
        return new SsbBurstScorer(audio, NativeRate).Score(Manifest(mode, plan, rendered, starts));
    }

    [Theory]
    [InlineData("bpsk1200", 18.0)]
    [InlineData("bpsk1200", 10.0)]
    [InlineData("qpsk600", 18.0)]
    [InlineData("qpsk600", 8.0)]
    [InlineData("afsk300", 18.0)]
    [InlineData("afsk300-il2pc", 14.0)]
    public void The_snr_the_scorer_measures_is_the_snr_the_rig_injected(string mode, double snrDb)
    {
        // The load-bearing one. Each rung carries its own noise lead-in on the air so the receiver
        // measures what was delivered rather than trusting the request; this proves the two agree
        // through the real SSB round-trip, converter and the mode's own demodulator.
        SsbCaptureScore score = RenderAndScore(mode, [snrDb], firstSeed: 31);

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Snr.Should().NotBeNull();
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(snrDb, 1.5,
            "the transmitted noise lead-in exists so the receiver measures the delivered SNR "
            + "rather than trusting the requested one");
        score.Bursts[0].Decoded.Should().BeTrue("every one of these rungs is inside the mode's working range");
    }

    [Theory]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("afsk300")]
    public void The_iq_route_offset_round_trips(string mode)
    {
        // The IQ route (the default) places the carrier at +offset in the software SSB and the scorer
        // down-shifts by the same offset the manifest records; a mismatch would misplace every burst by
        // the offset. Prove the 2 kHz offset round-trips: render at +2000, convert back with DialHz 2000,
        // and the bursts still decode with the SNR tracking. This is the plumbing the on-air IQ pass
        // (which recovers the DIGU headroom loss) rests on.
        SsbCaptureScore score = RenderAndScore(mode, [18, 10], firstSeed: 71, offsetHz: 2000);

        score.Bursts.Should().HaveCount(2);
        score.Bursts.Should().OnlyContain(b => b.Decoded, "the 2 kHz IQ offset must round-trip cleanly");
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(18, 1.5,
            "the delivered SNR must still track the request through the offset up/down-shift");
    }

    [Theory]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("afsk300")]
    public void A_whole_ssb_ladder_decodes_clean_in_its_working_range(string mode)
    {
        // The rehearsal: several healthy rungs, laid out as a capture, scored in one pass. Every burst
        // must acquire and decode with zero coded BER, and the measured SNR must track the request
        // across the whole ladder.
        double[] snrs = [20, 14, 8];
        SsbCaptureScore score = RenderAndScore(mode, snrs, firstSeed: 41);

        score.Bursts.Should().HaveCount(snrs.Length);
        score.Bursts.Should().OnlyContain(b => b.Acquired, "every healthy rung must acquire");
        score.Bursts.Should().OnlyContain(b => b.Decoded, "every healthy rung must decode");
        score.Bursts.Should().OnlyContain(b => b.CodedBer == 0, "coded BER is zero above the cliff");
        for (int k = 0; k < snrs.Length; k++)
        {
            score.Bursts[k].Snr!.SnrDb.Should().BeApproximately(snrs[k], 1.5,
                "measured SNR must track the request across the whole ladder");
        }
    }

    [Fact]
    public void The_scorer_reports_the_cliff_rather_than_papering_over_it()
    {
        // A scorer that always passes is worthless. qpsk600 is comfortable at 20 dB and lost at 0 dB in
        // AWGN, so a two-rung pass must catch the difference: the healthy rung decodes, the sub-cliff
        // rung is not acquired and counts every payload bit wrong.
        SsbCaptureScore score = RenderAndScore("qpsk600", [20, 0], firstSeed: 55);

        score.Bursts[0].Decoded.Should().BeTrue("20 dB is well above qpsk600's AWGN cliff");
        score.Bursts[0].CodedBer.Should().Be(0);
        score.Bursts[1].Decoded.Should().BeFalse("0 dB is below the cliff — the frame is lost");
        score.Bursts[1].Acquired.Should().BeFalse("nothing to lock onto at 0 dB for this mode");
        score.Bursts[1].CodedBer.Should().Be(1.0, "a lost frame counts every payload bit wrong");
    }

    [Fact]
    public void The_signal_power_is_identical_at_every_rung_and_only_the_noise_moves()
    {
        // The level policy, asserted on the transmitted IQ. A low-SNR rung must not come out quieter, or
        // the leakage path's own fixed noise would be a larger, uncalibrated share of it.
        IReadOnlyList<SsbLadderPoint> plan = SsbLadderPass.Plan("bpsk1200", [20, 10, 0], repeats: 1);
        IReadOnlyList<SsbRenderedPoint> rendered = new SsbLadderPass(Options()).Render(plan);

        double[] powers = rendered.Select(p => Power(p.Iq)).ToArray();
        powers[1].Should().BeGreaterThan(powers[0], "10 dB carries more noise than 20 dB");
        powers[2].Should().BeGreaterThan(powers[1], "0 dB carries more noise still");

        double worstPeak = Math.Sqrt(rendered.Max(p => PeakSquared(p.Iq)));
        worstPeak.Should().BeApproximately(0.9, 0.01,
            "one gain for the pass, taken from the point whose envelope is worst");
    }

    [Fact]
    public void The_dax_audio_gain_holds_signal_power_constant_across_the_pass()
    {
        // The DAX route applies AudioGain to the natural-scale audio at transmit time; the peak of the
        // worst point's audio is what sets it, so the loudest rung lands at the target amplitude.
        var options = new SsbLadderPassOptions { RenderIq = false, AudioAmplitude = 0.8 };
        IReadOnlyList<SsbLadderPoint> plan = SsbLadderPass.Plan("bpsk1200", [20, 10, 2], repeats: 1);
        var pass = new SsbLadderPass(options);
        IReadOnlyList<SsbRenderedPoint> rendered = pass.Render(plan);

        double worstAudioPeak = rendered.Max(p => p.Audio.Max(Math.Abs));
        (worstAudioPeak * pass.AudioGain).Should().BeApproximately(0.8, 0.01,
            "the pass audio gain scales the worst point's peak to the requested amplitude");
    }

    [Fact]
    public void The_plan_visits_every_rung_before_repeating_any_with_unique_seeds()
    {
        IReadOnlyList<SsbLadderPoint> plan = SsbLadderPass.Plan("bpsk1200", [10, 5, 0], repeats: 3);

        plan.Should().HaveCount(9);
        plan.Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Skip(3).Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Select(p => p.Seed).Should().OnlyHaveUniqueItems(
            "a repeated rung must be a different payload and channel realisation");
    }

    [Theory]
    [InlineData("qpsk3600")]     // an FM mode — belongs to the FM coverage path, not this ladder
    [InlineData("freedv-datac0")] // the OFDM ladder's job
    [InlineData("ms110d-wn6")]    // the MS110D ladder's job
    [InlineData("afsk1200")]      // the classic VHF-FM mode, not an SSB HF mode
    [InlineData("fsk9600")]       // a baseband mode, no audio carrier to place
    public void A_non_ssb_mode_is_rejected_before_any_rendering(string mode)
    {
        SsbLadderPass.IsSsbMode(mode).Should().BeFalse();
        Action plan = () => SsbLadderPass.Plan(mode, [10], repeats: 1);
        plan.Should().Throw<ArgumentException>("the SSB ladder only renders the SSB audio-carrier modes");
    }

    [Theory]
    [InlineData("afsk300")]
    [InlineData("afsk300-il2p")]
    [InlineData("afsk300-il2pc")]
    [InlineData("bpsk300")]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    public void The_ssb_audio_carrier_modes_are_accepted(string mode) =>
        SsbLadderPass.IsSsbMode(mode).Should().BeTrue();

    [Fact]
    public void A_null_mode_is_not_an_ssb_mode() => SsbLadderPass.IsSsbMode(null).Should().BeFalse();

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
