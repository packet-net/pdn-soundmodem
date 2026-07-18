using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class BpskCarrierOffsetEstimatorTests
{
    private const int SampleRate = 12000;

    private static float[] Signal(int offsetHz, float noiseSigma = 0f, int seed = 3)
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        byte[] wire = Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, 96, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, carrierFrequency: 1500 + offsetHz).Modulate(bits);
        if (noiseSigma > 0)
        {
            var random = new Random(seed);
            for (int i = 0; i < audio.Length; i++)
            {
                double u1 = 1.0 - random.NextDouble(), u2 = random.NextDouble();
                audio[i] += noiseSigma * (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }
        }

        return audio;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-8)]
    [InlineData(25)]
    [InlineData(-25)]
    [InlineData(50)]
    public void Estimates_A_Known_Carrier_Offset(int offsetHz)
    {
        var estimator = new BpskCarrierOffsetEstimator(SampleRate, 1500, 300);
        estimator.Process(Signal(offsetHz));

        estimator.HasEstimate.Should().BeTrue();
        estimator.OffsetHz.Should().BeApproximately(offsetHz, 2.5,
            "the symbol-spaced squaring estimate recovers the carrier offset");
    }

    [Fact]
    public void Recovers_The_Offset_Under_Noise()
    {
        var estimator = new BpskCarrierOffsetEstimator(SampleRate, 1500, 300);
        estimator.Process(Signal(20, noiseSigma: 0.1f));

        estimator.HasEstimate.Should().BeTrue();
        estimator.OffsetHz.Should().BeApproximately(20, 4);
    }

    [Fact]
    public void Reports_No_Confident_Estimate_On_Silence()
    {
        var estimator = new BpskCarrierOffsetEstimator(SampleRate, 1500, 300);
        estimator.Process(new float[SampleRate]); // one second of silence

        estimator.HasEstimate.Should().BeFalse("there is no carrier to measure");
    }

    [Fact]
    public void Matches_The_Winning_Branch_Of_The_Bank()
    {
        // Cross-check: the fine estimate should agree with which multi-modem branch decodes.
        float[] audio = Signal(18);
        var estimator = new BpskCarrierOffsetEstimator(SampleRate, 1500, 300);
        estimator.Process(audio);

        estimator.OffsetHz.Should().BeApproximately(18, 3);
    }
}
