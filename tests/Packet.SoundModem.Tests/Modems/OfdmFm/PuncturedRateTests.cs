using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The punctured rates above 3/4 on the K=7 code, and the proof that adding them moved nothing
/// below them.
/// </summary>
/// <remarks>
/// <para>Rates 5/6 and 7/8 (packet-net/pdn-ofdm-fm#10) are a table entry and two coding ids, which
/// is exactly why they need these tests: the table is shared with 1/2, 2/3 and 3/4, whose coded
/// output is a wire format every deployed station decodes, and the ids sit in a header field whose
/// order is also a wire format. Three claims, each pinned to something outside the code under
/// test: the old rates' output is byte-for-byte what it was before the change (captured from the
/// encoder before the table was touched), the new rates apply the published patterns (written out
/// by hand here, not read back from the table), and the header names them as 8 and 9 and nothing
/// else.</para>
/// </remarks>
public class PuncturedRateTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;

    /// <summary>The 50-bit payload every golden string below was captured from.</summary>
    private const string GoldenPayload = "10010011010010001011000111010111000110111000011001";

    private static byte[] Bits(int count, int seed)
    {
        var rng = new Random(seed);
        var bits = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bits[i] = (byte)rng.Next(2);
        }

        return bits;
    }

    private static string Text(ReadOnlySpan<byte> bits)
    {
        var text = new char[bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            text[i] = bits[i] == 0 ? '0' : '1';
        }

        return new string(text);
    }

    /// <summary>
    /// Captured from the encoder at 39b43ac, before the puncture table gained 5/6 and 7/8: the
    /// 50-bit payload above through every existing convolutional coding, with and without the
    /// interleaver.
    /// </summary>
    public static TheoryData<int, int, int, bool, string> GoldenCodedOutput => new()
    {
        { 7, 1, 2, false, "1110111110101010010000110010011011011110011111100001001101101101000111111101010110101111110000110010" },
        { 7, 1, 2, true, "1010100000111011011111011100110000111011011111111000000011011011011111100010000101100111011101111010" },
        { 7, 2, 3, false, "111111101101010001001011110111011111000001011110000111110010101111110001001" },
        { 7, 2, 3, true, "110110001011010110010001001110011001111011011101111110010101001110110111101" },
        { 7, 3, 4, false, "1101110110000001000010101100111100101111111001111110010111110001000" },
        { 7, 3, 4, true, "1111001111010011010011000011011001010011011101000010001110111011110" },
        { 9, 1, 2, false, "0101111010010101000100100010001101001011010110010111011011000010110001100110111110001101111011000010" },
        { 9, 1, 2, true, "0110010000111100011100010010111100011100001000111111101100111001101110010110010001010000101111011000" },
        { 9, 2, 3, false, "010111100010000001001001010101010100011011110001110011011111100110111110001" },
        { 9, 2, 3, true, "000001111010011010101000101110011011101010001111011111100100010001100101011" },
        { 9, 3, 4, false, "0111100001100101000011101010010001101010000100110101110011111010000" },
        { 9, 3, 4, true, "0100000110011001000100010111101010110011111100100100110110100011000" },
    };

    [Theory]
    [MemberData(nameof(GoldenCodedOutput))]
    public void The_Existing_Rates_Code_Exactly_What_They_Did_Before_The_Table_Grew(
        int constraintLength, int numerator, int denominator, bool interleave, string expected)
    {
        // The payload is regenerated rather than stored as bits so the generator is under test
        // too: if it ever drifted, this would fail on the payload line, not on a coded string
        // that no longer means anything.
        byte[] payload = Bits(50, 42);
        Text(payload).Should().Be(GoldenPayload);

        var codec = new OfdmFmCodec(
            new OfdmFmCoding(OfdmFmFec.Convolutional, constraintLength, numerator, denominator, interleave));

        Text(codec.Encode(payload)).Should().Be(
            expected, "K={0} rate {1}/{2} interleave {3} is a wire format",
            constraintLength, numerator, denominator, interleave);
    }

    /// <summary>The published patterns, A on the 0o133 output and B on the 0o171 output, written
    /// out here independently of the table under test.</summary>
    public static TheoryData<int, int, int[], int[]> PublishedPatterns => new()
    {
        // 802.11n, rate 5/6, period 5.
        { 5, 6, new[] { 1, 0, 1, 0, 1 }, new[] { 1, 1, 0, 1, 0 } },

        // DVB-S (EN 300 421 table 3), rate 7/8, period 7. The standard lists the 0o171 output
        // first; these rows are on the polynomials they were published for.
        { 7, 8, new[] { 1, 1, 1, 1, 0, 1, 0 }, new[] { 1, 0, 0, 0, 1, 0, 1 } },
    };

    [Theory]
    [MemberData(nameof(PublishedPatterns))]
    public void The_New_Rates_Apply_The_Published_Puncture_Pattern(
        int numerator, int denominator, int[] keepA, int[] keepB)
    {
        // Rate 1/2 is the whole mother lattice, two bits per payload bit. A punctured rate must be
        // exactly that lattice with the pattern's zeros struck out, in order - applied here by
        // hand so the test does not merely read the table back to itself.
        byte[] payload = Bits(105, 7);
        var mother = new OfdmFmCodec(new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, false))
            .Encode(payload);
        var expected = new List<byte>();
        for (int i = 0; i < payload.Length; i++)
        {
            int p = i % keepA.Length;
            if (keepA[p] == 1)
            {
                expected.Add(mother[i * 2]);
            }

            if (keepB[p] == 1)
            {
                expected.Add(mother[(i * 2) + 1]);
            }
        }

        var punctured = new OfdmFmCodec(
            new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, false));

        punctured.Encode(payload).Should().Equal(expected);
        expected.Count.Should().Be(
            payload.Length * denominator / numerator,
            "105 payload bits is a whole number of periods at both rates, so the rate is exact");
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(7, 8)]
    public void The_Coded_Length_Follows_The_Pattern_Into_A_Partial_Period(int numerator, int denominator)
    {
        // A payload is rarely a multiple of 5 or 7 bits, so the last period is cut short and the
        // coded length depends on which columns of the pattern the cut leaves in. Counted by hand
        // from the pattern for every remainder, and checked against both what the codec claims
        // and what it actually emits. From seven bits, the shortest block the tail-biting encoder
        // takes, up through three whole periods so every remainder is met more than once.
        (int[] keepA, int[] keepB) = numerator == 5
            ? (new[] { 1, 0, 1, 0, 1 }, new[] { 1, 1, 0, 1, 0 })
            : (new[] { 1, 1, 1, 1, 0, 1, 0 }, new[] { 1, 0, 0, 0, 1, 0, 1 });
        var codec = new OfdmFmCodec(
            new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, false));

        for (int bits = 7; bits <= 7 + (3 * numerator); bits++)
        {
            int expected = 0;
            for (int i = 0; i < bits; i++)
            {
                expected += keepA[i % keepA.Length] + keepB[i % keepB.Length];
            }

            codec.CodedBits(bits).Should().Be(expected, "{0} payload bits", bits);
            codec.Encode(Bits(bits, bits)).Length.Should().Be(expected, "{0} payload bits", bits);
        }
    }

    [Theory]
    [InlineData(5, 6, false)]
    [InlineData(5, 6, true)]
    [InlineData(7, 8, false)]
    [InlineData(7, 8, true)]
    public void The_Decoder_Depunctures_The_New_Rates_At_Every_Remainder(
        int numerator, int denominator, bool interleave)
    {
        // Straight through the coding chain with hard metrics and no noise, at every length a
        // burst can frame - whole bytes, from a one-byte payload and its two CRC bytes upward,
        // far enough to meet every remainder against both periods - and then two long ones.
        // What this proves is that the depuncturer puts every kept bit back in the slot the
        // encoder took it from, whatever the remainder: one slot out and the Viterbi walk lands
        // on the wrong path.
        //
        // Whole bytes from 24 bits, and not every bit count from 7, for a reason worth knowing:
        // at 7/8 the tail-biting decoder in M0LTE.Fec fails about half of all payloads at a few
        // odd lengths below 35 bits with no noise at all, although the coding is injective there
        // (checked exhaustively at 19 bits), so it is the decoder and not the pattern. No frame
        // is that short and every frame is whole bytes, at which it was swept clean to 520 bits;
        // the finding is recorded in docs/dev/ofdm-fm/receiver-findings.md rather than fixed
        // here because the decoder is not this repository's.
        var codec = new OfdmFmCodec(
            new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, interleave));
        int[] lengths = [.. Enumerable.Range(3, 12).Select(bytes => bytes * 8), 1000, 2056];

        foreach (int bits in lengths)
        {
            byte[] payload = Bits(bits, bits);
            byte[] coded = codec.Encode(payload);
            var llrs = new float[coded.Length];
            for (int i = 0; i < coded.Length; i++)
            {
                llrs[i] = coded[i] == 0 ? 1f : -1f;
            }

            codec.Decode(llrs, bits).Should().Equal(
                payload, "{0} payload bits at {1}/{2} interleave {3}", bits, numerator, denominator, interleave);
        }
    }

    [Fact]
    public void The_New_Rates_Take_Coding_Ids_8_And_9_On_The_K7_Code_Only()
    {
        // Ids 0 to 7 were pinned before this change and are pinned by RateSignallingTests; these
        // are the two that were appended. K=9 at the same rates has no id on purpose: 10 to 15
        // are held for the LDPC families and the table is not free to spend them twice.
        OfdmFmBurstCodec.CodingId(new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 5, 6, true)).Should().Be(8);
        OfdmFmBurstCodec.CodingId(new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 7, 8, true)).Should().Be(9);

        foreach ((int numerator, int denominator) in (ReadOnlySpan<(int, int)>)[(5, 6), (7, 8)])
        {
            Action name = () => OfdmFmBurstCodec.CodingId(
                new OfdmFmCoding(OfdmFmFec.Convolutional, 9, numerator, denominator, true));

            name.Should().Throw<ArgumentException>()
                .WithMessage("*names 10 codings*", "K=9 rate {0}/{1} has no id", numerator, denominator);
        }
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(7, 8)]
    public void A_Profile_At_The_New_Rates_Validates_On_K7_And_Is_Refused_On_K9(
        int numerator, int denominator)
    {
        // Validate is what a profile set is checked with before a station is offered it, so this
        // is the check that a profile naming the new rates is accepted before it reaches a
        // station, and that one naming them on the code that cannot signal them is refused there
        // rather than on the first transmit.
        Action k7 = (Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, true),
        }).Validate;
        k7.Should().NotThrow();

        Action k9 = (Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, numerator, denominator, true),
        }).Validate;
        k9.Should().Throw<InvalidOperationException>().WithMessage("*cannot be signalled*");
    }

    [Fact]
    public void The_Ladder_Carries_The_K7_Rungs_At_Qam64_In_Cliff_Order()
    {
        // The deployed 8 kHz preset transmits K=7 2/3 at QAM-64, and a profile that is not on the
        // ladder opens every link at the bottom rung and crawls: that was seen on air, and it is
        // the first thing these rungs fix. The rest is the shape the controller depends on - one
        // rung per code the header can name at QAM-64 on K=7, every rung calibrated, and the
        // whole ladder in cliff order, because the controller steps one index at a time and
        // reads "up" as "needs more signal".
        foreach ((int numerator, int denominator) in
            (ReadOnlySpan<(int, int)>)[(2, 3), (3, 4), (5, 6), (7, 8)])
        {
            var coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, true);
            int rung = OfdmFmRateLadder.IndexOf(OfdmFmConstellation.Qam64, coding);

            rung.Should().BeGreaterThan(
                OfdmFmRateLadder.Slowest, "K=7 {0}/{1} at QAM-64 is a rung", numerator, denominator);
            OfdmFmRate rate = OfdmFmRateLadder.Rungs[rung];
            rate.CliffCarrierToNoiseDb.Should().BePositive();
            rate.GoodputBitsPerSecond.Should().BePositive();
            rate.ErrorRateFactorPerDb.Should().BeInRange(0, 1, "the controller needs a measured slope");
            OfdmFmRateLadder.CliffPreFecErrorRate(coding).Should().BePositive();
        }

        for (int i = 1; i < OfdmFmRateLadder.Rungs.Count; i++)
        {
            OfdmFmRateLadder.Rungs[i].CliffCarrierToNoiseDb.Should().BeGreaterThan(
                OfdmFmRateLadder.Rungs[i - 1].CliffCarrierToNoiseDb, "rung {0} is above rung {1}", i, i - 1);
        }
    }

    [Fact]
    public void A_Rate_Outside_The_Table_Is_Still_Refused()
    {
        // The table grew; the door did not open. 4/5 sits between the old rates and the new ones
        // and has no pattern here, and a profile naming it must fail the way it always did.
        Action rate = () => new OfdmFmCodec(new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 4, 5, true));

        rate.Should().Throw<InvalidOperationException>().WithMessage("*not punctured here*");
    }
}
