using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// A burst carries its own rate, so one end can be set and the other end follows.
/// </summary>
/// <remarks>
/// <para>The constellation was always in the header and the receiver always used it. The coding was
/// not: the receiver deinterleaved and Viterbi-decoded with whatever its own profile said, so two
/// stations had to be configured to the same rate in advance and a mismatch looked exactly like a
/// bad path. These tests are the difference between those two states.</para>
/// <para>The geometry stays a matter of configuration and is not signalled. Sample rate, transform
/// size, guard length and which bins are occupied have to be agreed before a receiver can find a
/// burst at all, so there is nowhere to put them; what is signalled is everything that changes the
/// rate within one geometry.</para>
/// </remarks>
public class RateSignallingTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;

    /// <summary>Every coding a header can name, in wire order.</summary>
    private static readonly OfdmFmCoding[] WireOrder =
    [
        new OfdmFmCoding(),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 2, 3, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 3, 4, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 1, 2, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 3, 4, true),
        new OfdmFmCoding(OfdmFmFec.Ldpc),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 5, 6, true),
        new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 7, 8, true),
    ];

    public static TheoryData<OfdmFmCoding> Codings => [.. WireOrder];

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Codings))]
    public void A_Receiver_Decodes_A_Sender_Coded_Differently_From_Itself(OfdmFmCoding sending)
    {
        // The receiver is deliberately on the far end of the ladder from most of the senders: K=9
        // rate 3/4, which expands payload bits by a different factor from every other entry, so a
        // receiver that fell back on its own profile would size the burst wrongly and fail rather
        // than quietly get away with it.
        var receiver = new OfdmFmBurstCodec(
            Small with { Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 3, 4, true) });
        var sender = new OfdmFmBurstCodec(Small with { Coding = sending });
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = receiver.Demodulate(sender.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
    }

    [Fact]
    public void A_Receiver_Follows_A_Sender_Changing_Rate_Between_Bursts()
    {
        // The streaming case, which is the one that matters on air and the one that could break
        // without the whole-buffer case noticing: the receiver has to work out how long each burst
        // is from its header before it has the burst, so that it knows when the next one may start.
        // Sized from the receiver's own coding instead, it would stop collecting part way through a
        // burst sent at a different rate and then hunt for sync inside the remainder of it.
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:test", Small, delivered.Add);

        var sent = new List<byte[]>();
        var audio = new List<float>();
        int seed = 1;
        foreach (OfdmFmCoding coding in WireOrder)
        {
            foreach (OfdmFmConstellation constellation in
                new[] { OfdmFmConstellation.Bpsk, OfdmFmConstellation.Qam16 })
            {
                var sender = new OfdmFmBurstCodec(
                    Small with { Coding = coding, Constellation = constellation });
                byte[] payload = Payload(24, seed++);
                sent.Add(payload);
                audio.AddRange(sender.Modulate(payload, constellation));
            }
        }

        // Fed in blocks that do not line up with burst boundaries, because a receiver that only
        // ever saw whole bursts arrive would not exercise the sizing at all.
        float[] stream = [.. audio];
        for (int at = 0; at < stream.Length; at += 997)
        {
            receiver.Process(stream.AsSpan(at, Math.Min(997, stream.Length - at)).ToArray());
        }

        delivered.Should().HaveCount(sent.Count);
        for (int i = 0; i < sent.Count; i++)
        {
            delivered[i].Should().Equal(sent[i], "burst {0} of the rate sweep", i);
        }
    }

    [Fact]
    public void The_Coding_Index_Is_A_Wire_Format_And_Not_A_Lookup_Order()
    {
        // Pinned so that appending stays easy and renumbering stays hard. A receiver on older
        // firmware would decode a renumbered burst with the wrong code, fail its payload CRC and
        // report a bad link, which is about the least diagnosable way for this to go wrong.
        for (int id = 0; id < WireOrder.Length; id++)
        {
            OfdmFmBurstCodec.CodingId(WireOrder[id]).Should().Be(id);
        }
    }

    [Fact]
    public void A_Coding_With_No_Name_On_The_Wire_Is_Refused_When_The_Profile_Is_Validated()
    {
        // Not on the first transmit, which is where it would otherwise land: a profile like this
        // builds, encodes, decodes against itself and passes any test that does not modulate.
        // Building a codec validates, so the same fault comes out of both, as one exception type.
        Action uninterleaved = (Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, false),
        }).Validate;

        uninterleaved.Should().Throw<InvalidOperationException>()
            .WithMessage("*only interleaved codings*");

        Action build = () => new OfdmFmBurstCodec(Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, false),
        });

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*only interleaved codings*");
    }

    [Fact]
    public void An_Absent_Code_Is_Named_Whatever_Its_Unused_Fields_Say()
    {
        // Constraint length and rate sit in the record and count towards its equality even when
        // there is no code for them to describe. Matching the whole record would refuse a profile
        // that says "no coding, rate 3/4" - an identical waveform - over two numbers nothing reads.
        OfdmFmBurstCodec.CodingId(new OfdmFmCoding(OfdmFmFec.None, 9, 3, 4)).Should().Be(0);
    }

    [Fact]
    public void A_Payload_Too_Long_For_The_Length_Field_Is_Refused()
    {
        // The field is 12 bits. Written modulo 4096 it would size the burst wrongly at the far
        // end, and the symptom would be a payload CRC failure rather than anything pointing here.
        var modem = new OfdmFmBurstCodec(Small);

        Action tooLong = () => modem.Modulate(
            new byte[OfdmFmBurstCodec.MaxPayloadBytes + 1], OfdmFmConstellation.Qpsk);

        tooLong.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*4095 payload bytes*");
    }
}
