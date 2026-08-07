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
}
