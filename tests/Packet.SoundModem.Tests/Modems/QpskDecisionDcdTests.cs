using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class QpskDecisionDcdTests
{
    private static QpskDecisionDcd Fed(QpskDecisionDcd dcd, int symbols, Func<int, (double I, double Q)> decision)
    {
        for (int k = 0; k < symbols; k++)
        {
            (double i, double q) = decision(k);
            dcd.OnDecision(i, q);
        }

        return dcd;
    }

    /// <summary>Unit phasor at an angle error of <paramref name="radians"/> from the axis.</summary>
    private static (double, double) AtError(double radians) => (Math.Cos(radians), Math.Sin(radians));

    private static double Gaussian(Random random, double sigma)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Fact]
    public void Clean_Decisions_Assert_Within_The_Memory()
    {
        var dcd = new QpskDecisionDcd();
        int assertedAt = -1;
        for (int k = 0; k < 2 * QpskDecisionDcd.MemorySymbols && assertedAt < 0; k++)
        {
            dcd.OnDecision(1, 0);
            if (dcd.Asserted)
            {
                assertedAt = k + 1;
            }
        }

        assertedAt.Should().BePositive("a clean signal is the easy case");
        assertedAt.Should().BeLessThanOrEqualTo(QpskDecisionDcd.MemorySymbols,
            "the average reaches the assert level inside one memory");
    }

    [Fact]
    public void Every_Axis_Scores_As_A_Clean_Decision()
    {
        // The fourth power strips the decided quadrant, so the data pattern cannot matter.
        (double, double)[] axes = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        Fed(new QpskDecisionDcd(), 64, k => axes[(k * 7) & 3]).Asserted.Should().BeTrue();
    }

    [Fact]
    public void Amplitude_Does_Not_Matter()
    {
        Fed(new QpskDecisionDcd(), 64, _ => (1e-6, 0)).Asserted.Should().BeTrue("a weak clean decision is still clean");
        Fed(new QpskDecisionDcd(), 64, _ => (0, -1e6)).Asserted.Should().BeTrue("a loud one too");
    }

    [Fact]
    public void Uniformly_Random_Angles_Never_Assert()
    {
        // Noise: the decision variable sits anywhere. Ten minutes of symbols at 1800 Bd.
        var random = new Random(5);
        var dcd = new QpskDecisionDcd();
        double max = -1;
        for (int k = 0; k < 1_080_000; k++)
        {
            dcd.OnDecision(Gaussian(random, 1), Gaussian(random, 1));
            dcd.Asserted.Should().BeFalse("noise is not a carrier (symbol {0})", k);
            max = Math.Max(max, dcd.Coherence);
        }

        max.Should().BeLessThan(QpskDecisionDcd.AssertLevel - 0.1,
            "the noise average stays well clear of the assert level (its standard deviation is about 0.09)");
    }

    [Fact]
    public void Decisions_At_The_Knee_Assert_And_Hopeless_Ones_Do_Not()
    {
        // 12 degrees rms of angle error is a decision that copies (exp(-8 sigma^2) = 0.70);
        // 30 degrees rms is one that cannot (0.11), a symbol error every few symbols.
        var random = new Random(9);
        Fed(new QpskDecisionDcd(), 400, _ => AtError(Gaussian(random, 12 * Math.PI / 180)))
            .Asserted.Should().BeTrue("12 degrees rms is a dB or so below every QPSK knee");
        Fed(new QpskDecisionDcd(), 400, _ => AtError(Gaussian(random, 30 * Math.PI / 180)))
            .Asserted.Should().BeFalse("30 degrees rms is far past the release level");
    }

    [Fact]
    public void A_Signal_That_Stops_Releases_Within_Three_Memories()
    {
        // Decisions with no coherence at all (an angle error of exactly 22.5 degrees scores
        // zero) are the noise average without the noise's own excursions, so the release is
        // the exponential decay alone: 2.3 memories from near one to the release level. The
        // modem-level QpskDcdTests measure the real thing, noise after a burst, where the
        // floor's excursions stretch that by up to a memory.
        var dcd = Fed(new QpskDecisionDcd(), 100, _ => (1, 0));
        dcd.Asserted.Should().BeTrue();
        int releasedAt = -1;
        for (int k = 0; k < 4 * QpskDecisionDcd.MemorySymbols && releasedAt < 0; k++)
        {
            dcd.OnDecision(Math.Cos(Math.PI / 8), Math.Sin(Math.PI / 8));
            if (!dcd.Asserted)
            {
                releasedAt = k + 1;
            }
        }

        releasedAt.Should().BeInRange(2 * QpskDecisionDcd.MemorySymbols, 3 * QpskDecisionDcd.MemorySymbols,
            "the average falls from near one to the release level in about 2.3 memories");
    }

    [Fact]
    public void Digital_Silence_Releases_Faster_Than_Noise()
    {
        // A squelched radio or a wired bench loop delivers exact zeros, which have no angle:
        // scored as the boundary, not ignored, so DCD cannot latch on for ever.
        var dcd = Fed(new QpskDecisionDcd(), 100, _ => (1, 0));
        int releasedAt = -1;
        for (int k = 0; k < QpskDecisionDcd.MemorySymbols && releasedAt < 0; k++)
        {
            dcd.OnDecision(0, 0);
            if (!dcd.Asserted)
            {
                releasedAt = k + 1;
            }
        }

        releasedAt.Should().BeInRange(1, 24);
    }

    [Fact]
    public void Reset_Drops_Dcd_And_Forgets_The_Average()
    {
        var dcd = Fed(new QpskDecisionDcd(), 100, _ => (1, 0));
        dcd.Reset();
        dcd.Asserted.Should().BeFalse();
        dcd.Coherence.Should().Be(0);
    }
}
