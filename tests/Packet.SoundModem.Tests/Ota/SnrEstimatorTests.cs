using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Audits the over-the-air SNR estimator against the Watterson rig itself.
/// </summary>
/// <remarks>
/// <para>This is the blocking test the plan calls §S3. Every over-the-air result is a
/// comparison against the rig's prediction <em>at a stated SNR</em>, so an estimator with a
/// bias would shift every point along the curve and quietly re-fit the whole campaign to
/// itself - the campaign-audit failure this project has already paid for once. The only
/// defence is to generate signals at SNRs the rig chose and require them back.</para>
/// <para>The rig's convention (<c>WattersonChannel.AddNoise</c>) is mean burst power against
/// noise in a 3 kHz bandwidth, so that is what is asserted. Nothing here compares the
/// estimator to another estimator.</para>
/// </remarks>
public class SnrEstimatorTests
{
    private const int Rate = 9600;

    /// <summary>A real MS110D burst, put through the rig at a known SNR, with a matching
    /// stretch of noise-only audio from the same channel realisation.</summary>
    private static (float[] Burst, float[] Noise) RigBurst(int wn, double snrDb, int seed)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Short,
            ConstraintLength = 7,
            PreambleSuperframes = 3,
        });

        var random = new Random(seed);
        var payload = new byte[Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Short).InputBits - 32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] clean = tx.Modulate(payload);

        // The rig pads with noise-only lead-in/lead-out, which is exactly the guard interval a
        // scheduled OTA burst provides - so the estimator is handed the same shape of data it
        // will see on the air.
        const int guard = 4 * Rate;
        float[] noisy = new WattersonChannel(Rate, seed + 1)
            .Apply(clean, snrDb, leadInSamples: guard, leadOutSamples: guard);

        var burst = new float[clean.Length];
        Array.Copy(noisy, guard, burst, 0, clean.Length);

        // Take the guard from the far end, well clear of any filter tail from the burst.
        var noise = new float[guard - 1000];
        Array.Copy(noisy, noisy.Length - noise.Length, noise, 0, noise.Length);

        return (burst, noise);
    }

    [Theory]
    [InlineData(2, 0.0)]
    [InlineData(2, 5.0)]
    [InlineData(2, 10.0)]
    [InlineData(2, 20.0)]
    [InlineData(6, 5.0)]
    [InlineData(6, 15.0)]
    [InlineData(13, 10.0)]
    public void The_estimator_recovers_the_snr_the_rig_injected(int wn, double snrDb)
    {
        (float[] burst, float[] noise) = RigBurst(wn, snrDb, seed: 1000 + wn);

        SnrEstimate? estimate = SnrEstimator.Estimate(burst, noise, Rate);

        estimate.Should().NotBeNull("a burst put through the rig at a real SNR must measure back");
        estimate!.SnrDb.Should().BeApproximately(snrDb, 1.0,
            "an SNR estimate that disagrees with the rig would shift every OTA point along "
            + "the comparison curve");
    }

    [Fact]
    public void The_estimate_is_quoted_in_the_three_kilohertz_reference_bandwidth()
    {
        // The rig's convention, and the one the gate table is written in. Widening the
        // reference bandwidth must lower the reported SNR by exactly the bandwidth ratio.
        (float[] burst, float[] noise) = RigBurst(wn: 2, snrDb: 10.0, seed: 77);

        SnrEstimate? at3k = SnrEstimator.Estimate(burst, noise, Rate, 3000);
        SnrEstimate? at6k = SnrEstimator.Estimate(burst, noise, Rate, 6000);

        at3k.Should().NotBeNull();
        at6k.Should().NotBeNull();
        (at3k!.SnrDb - at6k!.SnrDb).Should().BeApproximately(10 * Math.Log10(2.0), 0.01);
        at3k!.BandwidthHz.Should().Be(3000);
    }

    [Fact]
    public void The_estimate_does_not_depend_on_the_recording_level()
    {
        // The property that matters for this chain specifically. The dummy-load-to-loop
        // coupling is physical and uncalibrated, and the receiver's gain is not ours to set,
        // so a capture's absolute level carries no information - move a cable and it changes.
        // An SNR estimate must be invariant to it, or session-to-session comparisons measure
        // the room rather than the modem.
        //
        // Note this scales BOTH the burst and the guard: scaling only the burst also scales
        // the noise riding on it, which is not "more signal" and would not be 10 dB of SNR.
        (float[] burst, float[] noise) = RigBurst(wn: 2, snrDb: 5.0, seed: 21);
        var quietBurst = new float[burst.Length];
        var quietNoise = new float[noise.Length];
        const float scale = 0.1f; // −20 dB on the whole recording
        for (int k = 0; k < burst.Length; k++)
        {
            quietBurst[k] = burst[k] * scale;
        }

        for (int k = 0; k < noise.Length; k++)
        {
            quietNoise[k] = noise[k] * scale;
        }

        SnrEstimate? loud = SnrEstimator.Estimate(burst, noise, Rate);
        SnrEstimate? quiet = SnrEstimator.Estimate(quietBurst, quietNoise, Rate);

        loud.Should().NotBeNull();
        quiet.Should().NotBeNull();
        quiet!.SnrDb.Should().BeApproximately(loud!.SnrDb, 0.05,
            "SNR is a ratio; the recording level must not appear in it");
    }

    [Fact]
    public void A_noise_estimate_at_or_above_the_burst_power_is_reported_as_unmeasurable()
    {
        // The first-burst warm-up failure from issue #121. The noise lead-in sits in the capture's
        // settling region (rx_sdr/RSP1/DC), so the measured noise floor is momentarily louder than
        // the burst. There is no signal left after removing that noise - a FAILED measurement, which
        // must come back as null (no reading), NOT as the ≈ −250 dB delivered SNR a signal floored to
        // an epsilon would produce and which then drags the per-run self-cal aggregate to tens of dB.
        var random = new Random(4242);
        var quietBurst = new float[4096];
        var loudNoise = new float[4096];
        for (int k = 0; k < quietBurst.Length; k++)
        {
            quietBurst[k] = (float)((random.NextDouble() - 0.5) * 0.01);
        }

        for (int k = 0; k < loudNoise.Length; k++)
        {
            loudNoise[k] = (float)((random.NextDouble() - 0.5) * 2.0);
        }

        SnrEstimator.Estimate(quietBurst, loudNoise, Rate).Should().BeNull(
            "a noise floor at or above the burst power is a failed measurement, not a −250 dB SNR");
    }

    [Fact]
    public void A_carrier_sitting_in_the_guard_interval_does_not_wreck_the_noise_estimate()
    {
        // Real bands are not empty. The median-of-bins approach exists so that an interferer
        // in the gap moves one bin rather than the whole floor; a mean would follow it and
        // report the burst as far worse than it is.
        (float[] burst, float[] noise) = RigBurst(wn: 2, snrDb: 10.0, seed: 5);
        SnrEstimate? clean = SnrEstimator.Estimate(burst, noise, Rate);
        clean.Should().NotBeNull();

        var polluted = new float[noise.Length];
        noise.CopyTo(polluted, 0);
        double rms = Math.Sqrt(clean!.NoiseDensity * (Rate / 2.0));
        for (int k = 0; k < polluted.Length; k++)
        {
            polluted[k] += (float)(20 * rms * Math.Sin(2 * Math.PI * 2500 * k / Rate));
        }

        SnrEstimate? withCarrier = SnrEstimator.Estimate(burst, polluted, Rate);

        withCarrier.Should().NotBeNull();
        (withCarrier!.SnrDb - clean!.SnrDb).Should().BeApproximately(0, 1.0,
            "a strong carrier in the guard interval must not move the noise floor estimate");
    }

    [Fact]
    public void Noise_shorter_than_a_transform_is_refused_rather_than_guessed_at()
    {
        var burst = new float[9600];
        var tiny = new float[16];

        Action estimate = () => SnrEstimator.Estimate(burst, tiny, Rate);

        estimate.Should().Throw<ArgumentException>();
    }
}
