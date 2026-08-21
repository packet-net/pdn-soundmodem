using AwesomeAssertions;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The over-the-air scorer, and the reference bits it grades against.
/// </summary>
/// <remarks>
/// Everything an over-the-air campaign will claim passes through here, so the tests are built to
/// catch the failure that would be invisible in the results: a scorer that is internally
/// consistent but grading against the wrong thing. The reference bits are therefore checked
/// against the <em>demodulator's own first-pass LLRs on a noiseless channel</em>, where the
/// answer must be exactly zero uncoded errors - a wire-order or puncture mismatch shows up as
/// ~50 %, which no amount of "it decoded fine" would reveal.
/// </remarks>
public class BurstScorerTests
{
    private const int Rate = 9600;

    private static Ms110dTxSettings Settings(int wn) => new()
    {
        WaveformNumber = wn,
        Interleaver = Ms110dInterleaverKind.Short,
        ConstraintLength = 7,
        PreambleSuperframes = 3,
    };

    /// <summary>A burst's worth of payload for the given waveform - one whole interleaver block
    /// less the EOM, which is what the OTA schedule sends.</summary>
    private static int PayloadBits(int wn)
        => Ms110dReferenceBits.PayloadBitsForBlocks(wn, Ms110dInterleaverKind.Short, blocks: 1);

    /// <summary>Silence padded around a burst, put through the rig at a stated SNR.</summary>
    private static float[] Transmit(Ms110dReferenceBits reference, double snrDb, int seed,
        int leadIn = 5 * Rate, int leadOut = 2 * Rate)
        => new WattersonChannel(Rate, seed).Apply(
            new Ms110dModulator(reference.Settings).Modulate(reference.PayloadBits),
            snrDb, leadInSamples: leadIn, leadOutSamples: leadOut);

    private static IEnumerable<ReadOnlyMemory<float>> Blocks(float[] audio, int blockSize)
    {
        for (int start = 0; start < audio.Length; start += blockSize)
        {
            yield return audio.AsMemory(start, Math.Min(blockSize, audio.Length - start));
        }
    }

    /// <summary>Grades the demodulator's first-pass LLRs on a noiseless channel against a
    /// reference, position for position.</summary>
    private static (long Bits, long Errors, long ConfidentlyWrong, long Erasures, int LengthMismatches)
        Grade(Ms110dReferenceBits reference, float[] audio)
    {
        long bits = 0, errors = 0, confidentlyWrong = 0, erasures = 0;
        int lengthMismatches = 0;
        var demod = new Ms110dDemodulator();
        demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
        {
            if (blockIndex >= reference.BlockCount)
            {
                return;
            }

            ReadOnlySpan<byte> sent = reference.Block(blockIndex);
            if (llrs.Length != sent.Length)
            {
                lengthMismatches++;
            }

            // A misaligned reference produces errors at every confidence level, because the bit
            // it is compared against is unrelated. A genuinely ambiguous channel bit produces
            // errors only where the soft output is near zero. So the threshold, not the count,
            // is what separates "wrong wire order" from "the demodulator was unsure".
            var magnitudes = llrs.Select(Math.Abs).OrderBy(x => x).ToArray();
            double confident = magnitudes[magnitudes.Length / 2]; // the block's median confidence

            int compare = Math.Min(llrs.Length, sent.Length);
            for (int i = 0; i < compare; i++)
            {
                // An exactly-zero LLR is an erasure, not a decision: the soft-output path
                // expressed no opinion at that position (the Walsh detector still picks a
                // di-bit noncoherently, but the scorer grades sign(LLR), and there is none).
                // The exact match is load-bearing: structural zeros are identically 0f, and
                // any tolerance would start eating genuine low-confidence errors like WN2's.
                // WN0 erases its first channel symbol of every burst by design - the RAKE's
                // decision-directed finger gains start cold, so the MRC statistic is zero for
                // every candidate (Wid0WalshModem.DemodulateRake) - and the > 0 tie-break
                // reads that silence as a hard 1: with both bits of the erased di-bit sent as
                // 0 that is 2/80 = 2.5 % charged errors on a clean Short block (1/80 = 1.25 %
                // at the theory's seed).
                if (llrs[i] == 0)
                {
                    erasures++;
                    continue;
                }

                bits++;
                if ((llrs[i] > 0 ? 0 : 1) != sent[i])
                {
                    errors++;
                    if (Math.Abs(llrs[i]) >= confident)
                    {
                        confidentlyWrong++;
                    }
                }
            }
        };

        demod.Process(new float[2400]);
        demod.Process(audio);
        demod.Process(new float[4800]);
        return (bits, errors, confidentlyWrong, erasures, lengthMismatches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(13)]
    public void The_reference_bits_are_the_bits_the_demodulator_sees_on_a_clean_channel(int wn)
    {
        // The load-bearing test. If the re-encoder's wire order, puncture or interleave differs
        // from the transmitter's by so much as one position, this reports about 50 % uncoded
        // errors while the payload still decodes perfectly - a scorer that grades every OTA
        // result against a fiction and never says so.
        var reference = new Ms110dReferenceBits(Settings(wn), PayloadBits(wn), seed: 500 + wn);
        float[] audio = new Ms110dModulator(Settings(wn)).Modulate(reference.PayloadBits);

        (long bits, long errors, long confidentlyWrong, long erasures, int lengthMismatches) =
            Grade(reference, audio);

        bits.Should().BeGreaterThan(0, "the demodulator must have produced first-pass LLRs");
        lengthMismatches.Should().Be(0,
            "the re-encoded block must be exactly as long as the wire, or the puncture or the "
            + "interleaver size disagrees with the transmitter's");
        confidentlyWrong.Should().Be(0,
            "on a noiseless channel the demodulator may be unsure, but it must never be "
            + "confidently wrong about a bit that was actually transmitted - a misaligned "
            + "reference would put about half its errors above the median confidence, and "
            + "there are {0} errors in {1} bits", errors, bits);
        erasures.Should().Be(wn == 0 ? 2 : 0,
            "WN0's cold-start RAKE erases exactly its first di-bit of a burst and the DFE "
            + "modes erase nothing; any other count is an LLR path gone quiet - or a "
            + "demodulator change that must be reflected here, not skipped green");
        (errors / (double)bits).Should().BeLessThan(0.01,
            "a handful of near-zero-confidence positions is the waveform; a percentage is a bug");
    }

    [Fact]
    public void A_reference_that_was_never_transmitted_still_fails_the_error_checks()
    {
        // The erasure skip must not blind the guard it serves. Grade a WN0 burst - the mode
        // the skip removes positions from - against bits that were never on the air, and the
        // result must look exactly like the de-rigging failure: errors at every confidence
        // level, not just near zero. Four blocks so the random payload dominates the constant
        // EOM tail both references share.
        int payloadBits = Ms110dReferenceBits.PayloadBitsForBlocks(0, Ms110dInterleaverKind.Short, blocks: 4);
        var transmitted = new Ms110dReferenceBits(Settings(0), payloadBits, seed: 500);
        var wrong = new Ms110dReferenceBits(Settings(0), payloadBits, seed: 501);
        float[] audio = new Ms110dModulator(Settings(0)).Modulate(transmitted.PayloadBits);

        (long bits, long errors, long confidentlyWrong, _, _) = Grade(wrong, audio);

        bits.Should().BeGreaterThan(0, "the demodulator must have produced first-pass LLRs");
        confidentlyWrong.Should().BeGreaterThan(0,
            "unrelated reference bits disagree with the demodulator at the median confidence too");
        (errors / (double)bits).Should().BeGreaterThan(0.25,
            "unrelated reference bits disagree about half the time - anything near the clean "
            + "channel's rate would mean the guard can no longer see a wrong reference");
    }

    [Fact]
    public void A_scheduled_wn0_burst_reports_the_erasures_it_excludes()
    {
        // The production counterpart to the theory's erasure pin: the scorer must REPORT the
        // positions it drops, so a quiet LLR path on a real capture shows as a growing count
        // rather than silently flattering the uncoded rate. WN0's cold-start RAKE erases
        // exactly its first di-bit of every burst, whatever the SNR.
        var reference = new Ms110dReferenceBits(Settings(0), PayloadBits(0), seed: 66);
        float[] audio = Transmit(reference, snrDb: 25, seed: 42);
        var schedule = new List<ScheduledBurst> { new(reference, 5.0) };

        CaptureScore score = new BurstScorer(schedule).Score(Blocks(audio, 4096));

        score.Bursts.Should().ContainSingle();
        BurstScore burst = score.Bursts[0];
        burst.WidCorrect.Should().BeTrue();
        burst.UncodedErasures.Should().Be(2, "WN0 erases its first di-bit of every burst by design");
        burst.UncodedBits.Should().Be(78, "the 80 wire bits less the 2 erased positions");
        burst.UncodedErrors.Should().Be(0, "25 dB AWGN is far above WN0's -6 dB operating point");
    }

    [Fact]
    public void The_reference_payload_is_reproducible_from_its_seed_alone()
    {
        // A manifest records the seed and the length, not megabytes of payload. That only works
        // if the draw is the same one the transmitter made.
        var a = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 4242);
        var b = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 4242);
        var c = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 4243);

        a.PayloadBits.Should().Equal(b.PayloadBits);
        a.PayloadBits.Should().NotEqual(c.PayloadBits);
        a.Seed.Should().Be(4242);
    }

    [Fact]
    public void A_capture_of_several_bursts_is_scored_burst_by_burst()
    {
        var schedule = new List<ScheduledBurst>();
        var audio = new List<float>();
        int[] waveforms = [2, 6, 13];
        for (int k = 0; k < waveforms.Length; k++)
        {
            var reference = new Ms110dReferenceBits(Settings(waveforms[k]), PayloadBits(waveforms[k]), seed: 900 + k);
            double at = audio.Count / (double)Rate;
            audio.AddRange(Transmit(reference, snrDb: 25, seed: 70 + k));
            schedule.Add(new ScheduledBurst(reference, at + 5.0)); // 5 s of lead-in noise first
        }

        CaptureScore score = new BurstScorer(schedule).Score(Blocks([.. audio], 4096));

        score.Missed.Should().BeEmpty("every scheduled burst was transmitted");
        score.Bursts.Should().HaveCount(3);
        for (int k = 0; k < 3; k++)
        {
            BurstScore burst = score.Bursts[k];
            burst.Acquired.Should().BeTrue("burst {0} was transmitted at 25 dB SNR", k);
            burst.Scheduled.Should().BeTrue();
            burst.WaveformNumber.Should().Be(waveforms[k], "the WID announces the waveform");
            burst.WidCorrect.Should().BeTrue();
            burst.Reason.Should().Be(Ms110dBurstEndReason.Eom);
            burst.PayloadErrors.Should().Be(0);
            burst.UncodedBits.Should().BeGreaterThan(0);
            burst.UncodedErasures.Should().Be(0, "only WN0 has a designed erasure; the DFE modes erase nothing");
            burst.StartSeconds.Should().BeApproximately(schedule[k].ExpectedSeconds, 1.0);
        }
    }

    [Fact]
    public void A_schedule_whose_clock_drifts_against_the_capture_is_still_matched_in_order()
    {
        // The 2026-07-27 campaign's nit: the schedule's times and the capture's drifted apart by
        // about a slot over a ladder, and the tail scored as unscheduled. Here every slot lands
        // 3 s later than the schedule says, cumulatively - by the fourth burst the error is
        // 12 s against a 5 s match window - and the matcher must follow it from what it has
        // already matched rather than lose the tail.
        var schedule = new List<ScheduledBurst>();
        var audio = new List<float>();
        int[] waveforms = [2, 6, 13, 2, 6];
        const double driftPerSlot = 3.0;
        double nominal = 5.0;
        for (int k = 0; k < waveforms.Length; k++)
        {
            var reference = new Ms110dReferenceBits(Settings(waveforms[k]), PayloadBits(waveforms[k]), seed: 950 + k);
            audio.AddRange(new float[(int)(driftPerSlot * k * Rate)]); // the capture runs slow
            float[] burst = Transmit(reference, snrDb: 25, seed: 80 + k);
            audio.AddRange(burst);
            schedule.Add(new ScheduledBurst(reference, nominal));
            nominal += burst.Length / (double)Rate; // the schedule's own clock: no drift
        }

        CaptureScore score = new BurstScorer(schedule).Score(Blocks([.. audio], 4096));

        score.Missed.Should().BeEmpty("every scheduled burst was transmitted");
        score.Bursts.Should().HaveCount(5);
        score.Bursts.Should().OnlyContain(b => b.Scheduled, "the matcher follows the measured drift");
        for (int k = 0; k < 5; k++)
        {
            score.Bursts[k].WaveformNumber.Should().Be(waveforms[k]);
            score.Bursts[k].PayloadErrors.Should().Be(0, "each burst is scored against its own reference bits");
        }
    }

    [Fact]
    public void A_burst_that_was_never_transmitted_is_reported_as_missed()
    {
        // The metric that matters most and the one a per-slice scorer cannot produce: the
        // receiver simply never heard it.
        var sent = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 11);
        var neverSent = new Ms110dReferenceBits(Settings(6), PayloadBits(6), seed: 12);
        float[] audio = Transmit(sent, snrDb: 25, seed: 33);

        var schedule = new List<ScheduledBurst>
        {
            new(sent, 5.0),
            new(neverSent, audio.Length / (double)Rate + 5.0),
        };

        CaptureScore score = new BurstScorer(schedule).Score(Blocks(audio, 4096));

        score.Bursts.Should().HaveCount(1);
        score.Missed.Should().ContainSingle().Which.Reference.Should().BeSameAs(neverSent);
    }

    [Fact]
    public void The_score_does_not_depend_on_how_the_capture_is_blocked()
    {
        var reference = new Ms110dReferenceBits(Settings(6), PayloadBits(6), seed: 55);
        float[] audio = Transmit(reference, snrDb: 12, seed: 91);
        var schedule = new List<ScheduledBurst> { new(reference, 5.0) };

        CaptureScore small = new BurstScorer(schedule).Score(Blocks(audio, 331));
        CaptureScore large = new BurstScorer(schedule).Score(Blocks(audio, 1 << 16));

        small.Bursts.Should().HaveCount(1);
        large.Bursts.Should().HaveCount(1);
        small.Bursts[0].UncodedErrors.Should().Be(large.Bursts[0].UncodedErrors);
        small.Bursts[0].PayloadErrors.Should().Be(large.Bursts[0].PayloadErrors);
        small.Bursts[0].StartSeconds.Should().Be(large.Bursts[0].StartSeconds);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(15.0)]
    public void The_snr_it_reports_is_the_snr_the_rig_injected(double snrDb)
    {
        // Same discipline as SnrEstimatorTests: the scorer must not quietly re-fit an OTA point
        // to its own bias. Here it is the end-to-end path - the guard the scorer chose for
        // itself, not one a test handed it - that has to come out right.
        var reference = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 61);
        float[] audio = Transmit(reference, snrDb, seed: 17);
        var schedule = new List<ScheduledBurst> { new(reference, 5.0) };

        CaptureScore score = new BurstScorer(schedule).Score(Blocks(audio, 4096));

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Snr.Should().NotBeNull("the 5 s lead-in is ample noise to measure");
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(snrDb, 1.5);
    }

    [Fact]
    public void Uncoded_error_rate_rises_with_noise_where_coded_error_rate_only_saturates()
    {
        // Why the uncoded rate is worth the InternalsVisibleTo: at 25 dB and at 8 dB the payload
        // decodes perfectly both times, so coded BER says the two channels are identical. The
        // uncoded rate is what tells them apart, and it is what an OTA point is compared with
        // the simulation on.
        var reference = new Ms110dReferenceBits(Settings(6), PayloadBits(6), seed: 77);
        var schedule = new List<ScheduledBurst> { new(reference, 5.0) };

        CaptureScore clean = new BurstScorer(schedule)
            .Score(Blocks(Transmit(reference, snrDb: 25, seed: 5), 4096));
        CaptureScore noisy = new BurstScorer(schedule)
            .Score(Blocks(Transmit(reference, snrDb: 8, seed: 5), 4096));

        clean.Bursts.Should().ContainSingle();
        noisy.Bursts.Should().ContainSingle();
        clean.Bursts[0].UncodedBer.Should().BeLessThan(1e-3, "25 dB AWGN is essentially clean");
        noisy.Bursts[0].UncodedBer.Should().BeGreaterThan(clean.Bursts[0].UncodedBer * 10,
            "8 dB must show channel bit errors the coded rate hides");
    }

    [Fact]
    public void An_unscheduled_burst_is_reported_but_not_scored_against_someone_elses_bits()
    {
        // Somebody else's transmission, or one of ours that the schedule does not know about.
        // Scoring it against the next entry would corrupt that entry's result and consume its
        // slot, so it is recorded with Scheduled = false and nothing else claimed.
        var reference = new Ms110dReferenceBits(Settings(2), PayloadBits(2), seed: 88);
        float[] audio = Transmit(reference, snrDb: 25, seed: 44);

        CaptureScore score = new BurstScorer([]).Score(Blocks(audio, 4096));

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Scheduled.Should().BeFalse();
        score.Bursts[0].Acquired.Should().BeTrue();
        score.Bursts[0].UncodedBits.Should().Be(0);
        score.Bursts[0].UncodedErasures.Should().Be(0, "no reference, no grading - erasures included");
        score.Bursts[0].WidCorrect.Should().BeFalse();
    }
}
