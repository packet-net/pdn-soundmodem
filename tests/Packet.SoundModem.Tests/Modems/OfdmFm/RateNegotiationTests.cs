using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The two halves of negotiation: getting a recommendation across the link, and deciding what to
/// recommend.
/// </summary>
public class RateNegotiationTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic with
    {
        Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
    };

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static int At(OfdmFmRateController controller) => OfdmFmRateLadder.IndexOf(
        controller.Recommendation.Constellation, controller.Recommendation.Coding);

    private static OfdmFmBurst Burst(int rung, double spentFraction, bool copied = true)
    {
        OfdmFmRate rate = OfdmFmRateLadder.Rungs[rung];
        return new OfdmFmBurst(
            copied ? [1, 2, 3] : null,
            rate.Constellation,
            0,
            rate.Coding,
            spentFraction * OfdmFmRateLadder.CliffPreFecErrorRate(rate.Coding));
    }

    [Fact]
    public void A_Recommendation_Crosses_The_Link()
    {
        var codec = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        OfdmFmRate wanted = OfdmFmRateLadder.Rungs[4];

        OfdmFmBurst? burst = codec.Demodulate(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk, recommendation: wanted));

        burst!.Payload.Should().Equal(payload);
        burst.Recommendation.Should().NotBeNull();
        burst.Recommendation!.Constellation.Should().Be(wanted.Constellation);
        burst.Recommendation.Coding.Should().Be(wanted.Coding);
    }

    [Fact]
    public void A_Station_With_No_Opinion_Says_So_Rather_Than_Recommending_Something()
    {
        // Null, not the slowest rate. "I have not heard enough to say" and "please slow right down"
        // are different messages and a link that confused them would crawl at every first contact.
        var codec = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = codec.Demodulate(codec.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst!.Payload.Should().Equal(payload);
        burst.Recommendation.Should().BeNull();
    }

    [Fact]
    public void A_Recommendation_For_A_Rate_That_Is_Not_On_The_Ladder_Still_Crosses()
    {
        // 8PSK at rate 1/2 is a real, transmittable rate that the ladder leaves out because
        // something else is as fast and needs less signal. The far end is entitled to want it, so
        // the wire carries it; what cannot come with it is the measured cost and speed, because
        // nothing here has measured this station's ladder for a rung that is not on it.
        var codec = new OfdmFmBurstCodec(Small);
        var offLadder = new OfdmFmRate(
            OfdmFmConstellation.Psk8,
            new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 1, 2, true),
            double.NaN,
            double.NaN);

        OfdmFmBurst? burst = codec.Demodulate(
            codec.Modulate(Payload(24), OfdmFmConstellation.Qpsk, recommendation: offLadder));

        burst!.Recommendation!.Constellation.Should().Be(OfdmFmConstellation.Psk8);
        burst.Recommendation.CliffCarrierToNoiseDb.Should().Be(double.NaN);
        OfdmFmRateLadder
            .IndexOf(burst.Recommendation.Constellation, burst.Recommendation.Coding)
            .Should().Be(-1);
    }

    [Fact]
    public void Losing_The_Advice_Must_Never_Cost_The_Payload_It_Rode_On()
    {
        // A coding the wire cannot name is refused when a PROFILE asks for it, because that would
        // transmit a burst nobody can decode. A recommendation is different: it is advice about a
        // direction this burst is not even carrying, so an unnameable one is dropped and the burst
        // goes out with no recommendation rather than not going out.
        var codec = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        var unnameable = new OfdmFmRate(
            OfdmFmConstellation.Qpsk,
            new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, false),
            0,
            0);

        OfdmFmBurst? burst = codec.Demodulate(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk, recommendation: unnameable));

        burst!.Payload.Should().Equal(payload);
        burst.Recommendation.Should().BeNull();
    }

    [Fact]
    public void A_Link_With_No_History_Starts_At_The_Most_Robust_Rate()
    {
        var controller = new OfdmFmRateController();

        controller.Recommendation.Should().Be(OfdmFmRateLadder.Rungs[OfdmFmRateLadder.Slowest]);
        controller.Transmit.Should().Be(OfdmFmRateLadder.Rungs[OfdmFmRateLadder.Slowest]);
    }

    [Fact]
    public void What_A_Station_Transmits_At_Is_What_The_Far_End_Asked_For()
    {
        // Not its own decision at all. It cannot measure the path its own signal takes.
        var controller = new OfdmFmRateController();
        OfdmFmRate asked = OfdmFmRateLadder.Rungs[5];

        controller.Heard(Burst(0, 0.1) with { Recommendation = asked });

        controller.Transmit.Should().Be(asked);
    }

    [Fact]
    public void Advice_Arrives_Even_On_A_Burst_Whose_Payload_Did_Not_Decode()
    {
        // The header has its own CRC and is coded harder than the payload, so it survives where the
        // payload does not - and a burst that failed is exactly when the far end most needs to be
        // told to slow down.
        var controller = new OfdmFmRateController();
        OfdmFmRate asked = OfdmFmRateLadder.Rungs[3];

        controller.Heard(Burst(0, 0.9, copied: false) with { Recommendation = asked });

        controller.Transmit.Should().Be(asked);
    }

    [Fact]
    public void One_Bad_Burst_Is_Enough_To_Ask_For_Something_Slower()
    {
        var controller = new OfdmFmRateController();
        while (At(controller) < 3)
        {
            controller.Heard(Burst(At(controller), 0.0));
        }

        int before = At(controller);
        before.Should().BeGreaterThan(0, "it should have climbed on a perfect path");

        controller.Heard(Burst(before, 0.0, copied: false));

        At(controller).Should().Be(before - 1);
    }

    [Fact]
    public void Stepping_Up_Waits_Until_There_Are_Enough_Bursts_To_Judge_On()
    {
        var controller = new OfdmFmRateController();

        for (int i = 0; i < OfdmFmRateController.BurstsToJudgeAStepOn - 1; i++)
        {
            controller.Heard(Burst(0, 0.0));
        }

        controller.Recommendation.Should().Be(
            OfdmFmRateLadder.Rungs[0], "one short of a judgement is not a judgement");

        controller.Heard(Burst(0, 0.0));

        controller.Recommendation.Should().Be(OfdmFmRateLadder.Rungs[1]);
    }

    [Fact]
    public void The_Typical_Burst_Decides_A_Step_Up_Rather_Than_The_Best_Or_The_Worst_One()
    {
        // This is where an earlier version of the policy lost 5 to 10 % of throughput. Requiring a
        // RUN of clean bursts means every burst in it has to clear the bar, so on a link whose
        // typical burst sits right at the bar the step almost never fires and the loop settles a
        // rung low. What "several bursts" was always meant to say is that the typical one decides.
        //
        // Rungs 0 and 1 sit 0.4 dB apart on the one-instrument ladder (was 0.9 dB), so a burst
        // that clears the bar today has to be a much better one than before. 0.65 of the way to
        // the cliff is about 0.46 dB by the margin estimate, which leaves rung 1's own expected
        // goodput under rung 0's by hysteresis even though the gap alone is smaller than that
        // margin: so a burst at 0.65 is one that does NOT clear the bar.
        var one = new OfdmFmRateController();
        one.Heard(Burst(0, 0.65));
        for (int i = 0; i < OfdmFmRateController.BurstsToJudgeAStepOn - 1; i++)
        {
            one.Heard(Burst(0, 0.0));
        }

        one.Recommendation.Should().Be(
            OfdmFmRateLadder.Rungs[1], "one poor burst among good ones is not a poor link");

        var half = new OfdmFmRateController();
        for (int i = 0; i < OfdmFmRateController.BurstsToJudgeAStepOn; i++)
        {
            half.Heard(Burst(0, i < 2 ? 0.0 : 0.65));
        }

        half.Recommendation.Should().Be(
            OfdmFmRateLadder.Rungs[0],
            "half the bursts failing to clear the bar is a link that has not got the margin");
    }

    [Fact]
    public void A_Step_Up_That_Fails_Makes_The_Next_Attempt_At_It_Harder()
    {
        // The safety net under the margin estimate. It is a rule of thumb and it will be wrong
        // about some links; when it is, this has to stop trying rather than oscillate.
        var controller = new OfdmFmRateController();
        int wanted = controller.BurstsWanted;

        for (int i = 0; i < wanted; i++)
        {
            controller.Heard(Burst(0, 0.0));
        }

        controller.Recommendation.Should().Be(OfdmFmRateLadder.Rungs[1]);

        controller.Heard(Burst(1, 0.0, copied: false));

        controller.Recommendation.Should().Be(OfdmFmRateLadder.Rungs[0]);
        controller.BurstsWanted.Should().Be(
            wanted * 2, "the step that broke it has to become harder to take again");
    }

    [Fact]
    public void A_Rate_Losing_Some_Frames_Is_Kept_While_It_Still_Beats_The_One_Below()
    {
        // The heart of the policy, and the thing a margin-only rule gets wrong. QAM-16 3/4 delivers
        // 4107 bit/s against QPSK 3/4's 2317, so it can afford to lose a fair share of its frames
        // and still be the better choice. A controller that stepped down whenever margin looked
        // thin would give that away, and measurably did: the earlier version of this reached 78 %
        // of the best fixed rate where this one reaches 93 %.
        var controller = new OfdmFmRateController();
        while (At(controller) < 4)
        {
            controller.Heard(Burst(At(controller), 0.0));
        }

        At(controller).Should().Be(4, "a clean path should have taken it up the ladder");

        // 0.7 of the way to the cliff, which by the fitted success curve is about one frame in
        // seven lost: 4107 bit/s at 85 % against 2962 at very nearly all of them, so this rung is
        // still ahead by about a fifth even while it is visibly losing traffic.
        for (int i = 0; i < 8; i++)
        {
            controller.Heard(Burst(At(controller), 0.7));
        }

        At(controller).Should().Be(4, "losing frames is not the same as being the wrong rate");
    }

    [Fact]
    public void The_Success_Estimate_Falls_As_More_Of_The_Code_Is_Spent()
    {
        // A fitted curve, so what is pinned is its shape and its ends rather than its values.
        OfdmFmRateController.EstimatedFrameSuccess(0.0)
            .Should().BeGreaterThan(OfdmFmRateController.EstimatedFrameSuccess(0.8));
        OfdmFmRateController.EstimatedFrameSuccess(0.8)
            .Should().BeGreaterThan(OfdmFmRateController.EstimatedFrameSuccess(1.2));
        OfdmFmRateController.EstimatedFrameSuccess(1.0).Should().BeApproximately(
            0.5, 0.01, "the cliff is defined as where half the frames land");
        OfdmFmRateController.EstimatedFrameSuccess(2.0)
            .Should().BeLessThan(0.1, "well past the cliff almost nothing gets through");
    }

    [Fact]
    public void The_Margin_Estimate_Falls_As_More_Of_The_Code_Is_Spent()
    {
        // Not a calibration check - the slope is a fitted rule of thumb. What must hold is the
        // ordering and the ends, because a policy reads inequalities off this and nothing else.
        OfdmFmRateController.EstimatedMarginDb(0, OfdmFmRateLadder.Rungs[1]).Should().Be(double.PositiveInfinity);
        OfdmFmRateController.EstimatedMarginDb(1, OfdmFmRateLadder.Rungs[1]).Should().Be(0);
        OfdmFmRateController.EstimatedMarginDb(0.1, OfdmFmRateLadder.Rungs[1])
            .Should().BeGreaterThan(OfdmFmRateController.EstimatedMarginDb(0.5, OfdmFmRateLadder.Rungs[1]));
        OfdmFmRateController.EstimatedMarginDb(0.5, OfdmFmRateLadder.Rungs[1])
            .Should().BeGreaterThan(OfdmFmRateController.EstimatedMarginDb(0.9, OfdmFmRateLadder.Rungs[1]));
    }
}
