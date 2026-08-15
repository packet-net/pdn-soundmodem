using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The dead-feed watch (born of the 2026-08-07 capture incident: a dead Flex VITA stream
/// pads exact silence at full rate, and the daemon recorded 6.8 hours of zeros without a
/// word). Sample-counted, so every case here is exact.
/// </summary>
public class DeadFeedWatchTests
{
    private const int Rate = 12000;

    private static float[] Silence(int samples) => new float[samples];

    private static float[] Noise(int samples)
    {
        var block = new float[samples];
        var random = new Random(7);
        for (int i = 0; i < samples; i++)
        {
            block[i] = (float)((random.NextDouble() - 0.5) * 0.04);
        }

        return block;
    }

    [Fact]
    public void Fires_Exactly_Once_When_The_Silent_Run_Crosses_The_Threshold()
    {
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 30);
        int block = Rate / 10;
        int blocksToThreshold = 30 * 10;

        int fired = 0;
        for (int i = 0; i < blocksToThreshold + 50; i++)
        {
            if (watch.Observe(Silence(block)))
            {
                fired++;
                (i + 1).Should().Be(blocksToThreshold,
                    "the fire must land on the block where the run first reaches 30 s");
            }
        }

        fired.Should().Be(1, "a latched watch must not spam the journal every block");
    }

    [Fact]
    public void A_Live_Sample_Rearms_The_Watch()
    {
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);

        for (int i = 0; i < 9; i++)
        {
            watch.Observe(Silence(Rate / 10)).Should().BeFalse();
        }

        watch.Observe(Noise(Rate / 10)).Should().BeFalse("noise is a live feed");

        // The run restarts from zero: nine more silent blocks stay quiet, the tenth fires.
        for (int i = 0; i < 9; i++)
        {
            watch.Observe(Silence(Rate / 10)).Should().BeFalse();
        }

        watch.Observe(Silence(Rate / 10)).Should().BeTrue();
    }

    [Fact]
    public void A_Quiet_Band_Noise_Floor_Never_Reads_As_Dead()
    {
        // RMS ~0.02 is the healthy capture's measured floor; even one sample per block at
        // a fraction of that keeps the feed alive.
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);
        var faint = Silence(Rate / 10);
        faint[^1] = 0.001f;

        for (int i = 0; i < 100; i++)
        {
            watch.Observe(faint).Should().BeFalse();
        }
    }

    [Fact]
    public void After_Firing_A_Recovered_Feed_Rearms_For_The_Next_Death()
    {
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);

        watch.Observe(Silence(Rate)).Should().BeTrue();
        watch.Observe(Noise(Rate / 10)).Should().BeFalse();
        watch.Observe(Silence(Rate)).Should().BeTrue(
            "a second death after recovery must be reported again");
    }

    [Fact]
    public void Silence_While_The_Station_Is_Transmitting_Is_Not_Evidence_Of_Death()
    {
        // The bug this pins took the GB7RDG node off the air on 2026-08-15: a keyed Flex is not
        // receiving and its DAX stream delivers exact zeros, so a FreeDV run pacing frames every
        // 8 s accumulated 30 s of "unbroken digital silence" across its own transmissions and
        // restarted the production daemon mid-campaign.
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);

        for (int block = 0; block < 40; block++)
        {
            watch.Observe(Silence(Rate / 10), transmitting: true)
                .Should().BeFalse("the radio is keyed, so of course the receive feed is silent");
        }
    }

    [Fact]
    public void A_Transmission_Rearms_The_Watch_Rather_Than_Merely_Pausing_It()
    {
        // Re-arming is what covers the unkey transition: the receive path takes a moment to
        // resume real audio afterwards, and counting that as evidence of death would be the same
        // mistake one block later.
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);

        for (int block = 0; block < 9; block++)
        {
            watch.Observe(Silence(Rate / 10)).Should().BeFalse("0.9 s, just under the threshold");
        }

        watch.Observe(Silence(Rate / 10), transmitting: true).Should().BeFalse();

        for (int block = 0; block < 9; block++)
        {
            watch.Observe(Silence(Rate / 10)).Should().BeFalse(
                "the run restarted at the transmission, so this is 0.9 s again, not 1.9 s");
        }

        watch.Observe(Silence(Rate / 10)).Should().BeTrue("a full second of idle silence since");
    }

    [Fact]
    public void A_Receive_Only_Station_Is_Unaffected_By_The_Transmit_Exemption()
    {
        // The incident the watch was built for was a capture station that never transmits, so
        // the exemption must not weaken it at all: same threshold, same firing block.
        var watch = new DeadFeedWatch(Rate, thresholdSeconds: 1);
        int fired = 0;

        for (int block = 0; block < 20; block++)
        {
            if (watch.Observe(Silence(Rate / 10), transmitting: false))
            {
                fired = block + 1;
            }
        }

        fired.Should().Be(10, "ten 100 ms blocks is one second, exactly as before the change");
    }
}
