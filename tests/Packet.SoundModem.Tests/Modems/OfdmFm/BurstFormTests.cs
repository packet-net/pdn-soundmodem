using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The burst form as a thing the link negotiates: a receiver that sees follow-on bursts fail
/// where full ones decode asks for full bursts, the ask rides in the header beside the rate
/// recommendation, and the transmitter obeys until the receiver lets follow-on bursts be tried
/// again.
/// </summary>
/// <remarks>
/// Measured on air, 2026-09-19: on the direction whose sound card clock was wobbling within a
/// burst, long follow-on keyups lost three to ten frames in twenty where full bursts on the same
/// link at the same time lost at most three, and which direction that was moved with time. Only
/// the receiving end can see it and only the transmitting end can change it.
/// </remarks>
public class BurstFormTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;
    private static readonly OfdmFmGeometry Acquisition = Small.Geometry;
    private static readonly OfdmFmGeometry Wide = new(6, 44, 6);
    private static readonly OfdmFmGeometryTable Table = new([Acquisition, null, null, null, null, Wide]);

    private static readonly OfdmFmCoding Code = new(OfdmFmFec.Convolutional, 9, 2, 3, true);

    /// <summary>Both ends on the wide layout, adapting, sending follow-on frames inside keyups.
    /// </summary>
    private static readonly OfdmFmParameters Station = Small with
    {
        FirstCarrier = Wide.FirstCarrier,
        DataCarriers = Wide.DataCarriers,
        PilotCarriers = Wide.PilotCarriers,
        GeometryId = 5,
        Coding = Code,
        Constellation = OfdmFmConstellation.Qpsk,
        AdaptiveRate = true,
        FollowOnFrames = true,
        ContiguousBursts = true,
    };

    private static byte[] Frame(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A decoded burst here is a comfortable one on any rung: the pre-FEC figure sits far under
    // every rung's cliff, including the rate-7/8 rung's 0.014, so the rate logic has no reason to
    // move and what the tests see is the form policy alone.
    private static OfdmFmBurst Burst(bool decoded, bool followOn, OfdmFmRate rate, double preFec = 0.0005) =>
        new(decoded ? new byte[16] : null, rate.Constellation, 0, rate.Coding, decoded ? preFec : null,
            FollowOn: followOn);

    [Fact]
    public void The_Ask_For_Full_Bursts_Rides_In_The_Header_Beside_The_Recommendation()
    {
        var codec = new OfdmFmBurstCodec(Station, Table);
        OfdmFmRate rate = OfdmFmRateLadder.Rungs[OfdmFmRateLadder.Slowest];
        byte[] payload = Frame(40, 1);
        int sync = Small.SymbolSamples;

        OfdmFmHeader plain = codec.ReadHeader(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk, 1, rate), sync)!.Value;
        OfdmFmHeader asking = codec.ReadHeader(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk, 1, rate, askFullBursts: true), sync)!.Value;

        plain.Recommendation.Should().NotBeNull();
        plain.AskedFullBursts.Should().BeFalse();
        asking.Recommendation.Should().Be(plain.Recommendation, "the flag does not disturb the rate");
        asking.AskedFullBursts.Should().BeTrue();

        // Without a recommendation there is nothing for the flag to ride on.
        codec.ReadHeader(codec.Modulate(payload, OfdmFmConstellation.Qpsk, 1, askFullBursts: true), sync)!
            .Value.AskedFullBursts.Should().BeFalse();

        // And the one constellation whose value plus eight does not fit the nibble sends its
        // recommendation without the flag rather than not at all.
        var dense = new OfdmFmRate(OfdmFmConstellation.Qam256, Code, double.NaN, double.NaN);
        OfdmFmHeader denseAsk = codec.ReadHeader(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk, 1, dense, askFullBursts: true), sync)!.Value;
        denseAsk.Recommendation!.Constellation.Should().Be(OfdmFmConstellation.Qam256);
        denseAsk.AskedFullBursts.Should().BeFalse();
    }

    [Fact]
    public void Two_Follow_On_Failures_In_A_Window_Ask_For_Full_Bursts_And_Leave_The_Rate_Alone()
    {
        var controller = new OfdmFmRateController(OfdmFmRateLadder.Fastest);
        OfdmFmRate rate = controller.Recommendation;

        controller.Heard(Burst(decoded: true, followOn: true, rate));
        controller.Heard(Burst(decoded: false, followOn: true, rate));
        controller.WantFullBursts.Should().BeFalse("one failure is what a wobble costs now and then");
        controller.Heard(Burst(decoded: true, followOn: true, rate));
        controller.Heard(Burst(decoded: false, followOn: true, rate));

        controller.WantFullBursts.Should().BeTrue();
        controller.Recommendation.Should().Be(rate, "a follow-on failure is about the form, not the rate");
    }

    [Fact]
    public void A_Full_Burst_That_Fails_Still_Steps_The_Rate_Down()
    {
        var controller = new OfdmFmRateController(OfdmFmRateLadder.Fastest);
        OfdmFmRate rate = controller.Recommendation;

        controller.Heard(Burst(decoded: false, followOn: false, rate));

        controller.Recommendation.Should().NotBe(rate);
        controller.WantFullBursts.Should().BeFalse();
    }

    [Fact]
    public void Enough_Clean_Full_Bursts_Allow_Follow_On_Again_And_A_Relapse_Holds_Longer()
    {
        var controller = new OfdmFmRateController(OfdmFmRateLadder.Slowest);
        OfdmFmRate rate = controller.Recommendation;

        controller.Heard(Burst(decoded: false, followOn: true, rate));
        controller.Heard(Burst(decoded: false, followOn: true, rate));
        controller.WantFullBursts.Should().BeTrue();
        controller.FullBurstsWanted.Should().Be(OfdmFmRateController.FullBurstsBeforeFollowOnAgain);

        for (int i = 0; i < OfdmFmRateController.FullBurstsBeforeFollowOnAgain - 1; i++)
        {
            controller.Heard(Burst(decoded: true, followOn: false, rate));
            controller.WantFullBursts.Should().BeTrue("full burst {0} is not yet enough", i + 1);
        }

        controller.Heard(Burst(decoded: true, followOn: false, rate));
        controller.WantFullBursts.Should().BeFalse("enough full bursts have decoded");

        // Follow-on bursts fail again at once: the next hold is twice as long.
        controller.Heard(Burst(decoded: false, followOn: true, rate));
        controller.Heard(Burst(decoded: false, followOn: true, rate));
        controller.WantFullBursts.Should().BeTrue();
        controller.FullBurstsWanted.Should().Be(2 * OfdmFmRateController.FullBurstsBeforeFollowOnAgain);

        controller.Reset();
        controller.WantFullBursts.Should().BeFalse();
        controller.FullBurstsWanted.Should().Be(OfdmFmRateController.FullBurstsBeforeFollowOnAgain);
    }

    [Fact]
    public void A_Station_Whose_Follow_On_Bursts_Fail_Gets_Full_Bursts_Until_It_Says_Otherwise()
    {
        // A sends keyups to B; B's copies of two follow-on payloads are wrecked on the way. B asks
        // for full bursts in its own next burst, A hears it and sends full bursts inside its next
        // keyup, and once B has decoded enough of them it stops asking and A goes back to
        // follow-on bursts.
        var a = new OfdmFmModem("ofdm-fm:a", Station, _ => { }, geometryTable: Table);
        var heardByB = new List<byte[]>();
        var b = new OfdmFmModem("ofdm-fm:b", Station, heardByB.Add, geometryTable: Table);
        var codec = new OfdmFmBurstCodec(Station, Table);
        int silence = Small.SymbolSamples * 12;

        static List<float[]> Keyup(OfdmFmModem sender, int frames, int seed)
        {
            var bursts = new List<float[]>();
            for (int i = 0; i < frames; i++)
            {
                bursts.Add(sender.Modulate(Frame(30 + (i * 5), seed + i), i == 0 ? 300 : OfdmFmModem.HostLeadInWithinKeyupMs));
            }

            return bursts;
        }

        static void Feed(OfdmFmModem receiver, IEnumerable<float[]> pieces)
        {
            float[] all = [.. pieces.SelectMany(p => p)];
            for (int at = 0; at < all.Length; at += 997)
            {
                receiver.Process(all.AsSpan(at, Math.Min(997, all.Length - at)).ToArray());
            }
        }

        // A keyup of five from A: the first full, four follow-on. Wreck the payloads of the
        // second and third follow-on bursts after their header.
        List<float[]> first = Keyup(a, 5, 100);
        first[1].Length.Should().BeLessThan(first[0].Length, "follow-on bursts are shorter");
        foreach (int i in new[] { 2, 3 })
        {
            Array.Clear(first[i], codec.FollowOnHeaderSamples, first[i].Length - codec.FollowOnHeaderSamples);
        }

        Feed(b, [.. first, new float[silence]]);
        b.AskingForFullBursts.Should().BeTrue("two follow-on bursts failed where full ones decoded");
        a.SendingFullBursts.Should().BeFalse("A has not heard B yet");

        // B answers: its burst carries the ask. A hears it.
        Feed(a, [b.Modulate(Frame(20, 200), 300), new float[silence]]);
        a.SendingFullBursts.Should().BeTrue();

        // A's next keyup is full bursts throughout, at whatever rate A is transmitting by now.
        List<float[]> second = Keyup(a, 4, 300);
        OfdmFmRate sending = a.TransmittingAt!;
        second[1].Length.Should().Be(
            codec.Modulate(Frame(35, 301), sending.Constellation, 0, a.AskingFor, sending.Coding, askFullBursts: a.AskingForFullBursts).Length,
            "a full burst, not a follow-on one");

        // B decodes enough full bursts to let follow-on bursts be tried again.
        int decodedBefore = heardByB.Count;
        Feed(b, [.. second, new float[silence]]);
        heardByB.Count.Should().Be(decodedBefore + 4);
        for (int k = 0; k < OfdmFmRateController.FullBurstsBeforeFollowOnAgain; k++)
        {
            if (!b.AskingForFullBursts)
            {
                break;
            }

            Feed(b, [.. Keyup(a, 1, 400 + k), new float[silence]]);
        }

        b.AskingForFullBursts.Should().BeFalse("enough full bursts have decoded");

        Feed(a, [b.Modulate(Frame(20, 500), 300), new float[silence]]);
        a.SendingFullBursts.Should().BeFalse();
        List<float[]> third = Keyup(a, 3, 600);
        sending = a.TransmittingAt!;
        third[1].Length.Should().Be(
            codec.ModulateFollowOn(Frame(35, 601), sending.Constellation, a.AskingFor, sending.Coding, askFullBursts: a.AskingForFullBursts).Length,
            "follow-on bursts again");
    }
}
