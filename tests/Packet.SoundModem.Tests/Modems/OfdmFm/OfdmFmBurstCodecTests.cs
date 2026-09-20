using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The audio-band OFDM machinery, exercised on synthetic geometry.
/// </summary>
/// <remarks>
/// Every profile here is the small synthetic one (see <see cref="OfdmFmParameters.Synthetic"/>),
/// which is deliberate: these tests prove the machinery rather than any particular waveform, and
/// a few carriers exercise it as thoroughly as a few hundred while running in a fraction of the
/// time. That a symbol survives its own transform, that the constellations map and demap, that
/// timing is found, that the channel estimate corrects a distorted path and that a bad CRC is
/// refused are all true of any carrier layout. The shipped layouts are measured in the ladders
/// and the probes, which say which one they ran on.
/// </remarks>
public class OfdmFmBurstCodecTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData(OfdmFmConstellation.Bpsk)]
    [InlineData(OfdmFmConstellation.Qpsk)]
    [InlineData(OfdmFmConstellation.Psk8)]
    [InlineData(OfdmFmConstellation.Qam16)]
    [InlineData(OfdmFmConstellation.Qam32)]
    [InlineData(OfdmFmConstellation.Qam64)]
    [InlineData(OfdmFmConstellation.Qam128)]
    [InlineData(OfdmFmConstellation.Qam256)]
    public void A_Burst_Round_Trips_At_Every_Constellation(OfdmFmConstellation constellation)
    {
        var modem = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(32);

        float[] audio = modem.Modulate(payload, constellation);
        OfdmFmBurst? burst = modem.Demodulate(audio);

        burst.Should().NotBeNull();
        burst!.Constellation.Should().Be(constellation);
        burst.Payload.Should().Equal(payload);
    }

    [Fact]
    public void Every_Constellation_Maps_And_Demaps_Every_Symbol_Value()
    {
        foreach (OfdmFmConstellation constellation in Enum.GetValues<OfdmFmConstellation>())
        {
            (float I, float Q)[] points = OfdmFmMapper.Points(constellation);
            points.Should().HaveCount(1 << constellation.BitsPerCarrier());

            for (int value = 0; value < points.Length; value++)
            {
                OfdmFmMapper.Demap(points, points[value].I, points[value].Q)
                    .Should().Be(value, "{0} point {1} must demap to itself", constellation, value);
            }

            // Unit mean power, so a constellation change does not change the drive level.
            double power = points.Sum(p => ((double)p.I * p.I) + ((double)p.Q * p.Q)) / points.Length;
            power.Should().BeApproximately(1.0, 0.02);
        }
    }

    [Fact]
    public void The_Burst_Is_Found_Wherever_It_Sits_In_The_Audio()
    {
        var modem = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        float[] burstAudio = modem.Modulate(payload, OfdmFmConstellation.Qpsk);

        foreach (int offset in new[] { 0, 7, 133, 1000 })
        {
            var padded = new float[offset + burstAudio.Length + 500];
            burstAudio.CopyTo(padded, offset);

            OfdmFmBurst? burst = modem.Demodulate(padded);

            burst.Should().NotBeNull("a burst offset by {0} samples must still be found", offset);
            burst!.Payload.Should().Equal(payload);
        }
    }

    [Fact]
    public void A_Distorted_Channel_Is_Equalised_By_The_Preamble_Estimate()
    {
        // The whole point of OFDM with a channel estimate: a path that tilts and delays the audio
        // is corrected per subcarrier, and a mode with no equaliser could not read this.
        var modem = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        float[] audio = modem.Modulate(payload, OfdmFmConstellation.Qam16);

        var distorted = new float[audio.Length];
        float previous = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            // A one-pole tilt plus an echo: frequency-selective, which is exactly what a
            // per-subcarrier estimate handles and a flat correction does not.
            float shaped = (0.6f * audio[n]) + (0.4f * previous);
            previous = audio[n];
            distorted[n] = shaped + (n >= 3 ? 0.25f * audio[n - 3] : 0f);
        }

        OfdmFmBurst? burst = modem.Demodulate(distorted);

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
    }

    [Fact]
    public void A_Robust_Constellation_Tolerates_More_Noise_Than_A_Dense_One()
    {
        // Not a performance claim - the geometry is invented - but the ordering is real and a
        // modem that got it the other way round would be broken. Measured as "how much noise does
        // each still decode through", which is robust to where the absolute levels happen to fall.
        var modem = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(16);

        static float[] Noisy(float[] clean, double sigma, int seed)
        {
            var random = new Random(seed);
            var noisy = new float[clean.Length];
            for (int n = 0; n < clean.Length; n++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                noisy[n] = (float)(clean[n] + (sigma * gauss));
            }

            return noisy;
        }

        double Tolerance(OfdmFmConstellation constellation)
        {
            float[] clean = modem.Modulate(payload, constellation);
            double best = 0;
            foreach (double sigma in new[] { 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1 })
            {
                bool allSeeds = true;
                for (int seed = 1; seed <= 3 && allSeeds; seed++)
                {
                    byte[]? got = modem.Demodulate(Noisy(clean, sigma, seed))?.Payload;
                    allSeeds = got is not null && got.AsSpan().SequenceEqual(payload);
                }

                if (allSeeds)
                {
                    best = sigma;
                }
            }

            return best;
        }

        double bpsk = Tolerance(OfdmFmConstellation.Bpsk);
        double qam256 = Tolerance(OfdmFmConstellation.Qam256);

        bpsk.Should().BeGreaterThan(qam256,
            "one bit per carrier must ride out noise that eight bits per carrier cannot");
    }

    [Fact]
    public void A_Corrupted_Payload_Is_Refused_Rather_Than_Delivered()
    {
        var modem = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        float[] audio = modem.Modulate(payload, OfdmFmConstellation.Qpsk);

        // Wreck a run of samples inside the payload symbols, past preamble and header. Derived
        // from the codec rather than counted by hand: the header is more than one symbol, and how
        // many depends on the profile, so a hard-coded index silently wrecked the header instead
        // and passed for the wrong reason.
        int from = Small.SymbolSamples + modem.HeaderEndOffset;
        for (int n = from; n < Math.Min(from + Small.SymbolSamples, audio.Length); n++)
        {
            audio[n] = 0f;
        }

        OfdmFmBurst? burst = modem.Demodulate(audio);

        // Either the burst is rejected outright or its payload is refused by the CRC, but a
        // wrong payload must never be handed up as good.
        (burst?.Payload).Should().BeNull();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(13)]
    [InlineData(14)]
    public void A_Profile_Works_Whatever_The_Parity_Of_Its_First_Carrier(int firstCarrier)
    {
        // A regression guard for a bug the committed synthetic profile could never have caught.
        // The sync symbol repeats over half a symbol only on EVEN absolute bins; an odd one
        // anti-repeats, so an odd first carrier used to produce two sign-flipped halves and a
        // correlation peaking at -1 where the search looked for +1. The signal looked perfectly
        // healthy and decoded to nothing, and it only appeared when the geometry changed under a
        // profile whose first carrier happened to be even.
        var modem = new OfdmFmBurstCodec(Small with { FirstCarrier = firstCarrier });
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = modem.Demodulate(modem.Modulate(payload, OfdmFmConstellation.Qam16));

        burst.Should().NotBeNull("first carrier {0}", firstCarrier);
        burst!.Payload.Should().Equal(payload);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    [InlineData(7, 8)]
    public void A_Coded_Burst_Round_Trips_At_Every_Rate(int numerator, int denominator)
    {
        var coded = Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, true),
        };
        var modem = new OfdmFmBurstCodec(coded);
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = modem.Demodulate(modem.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
    }

    public static TheoryData<OfdmFmConstellation, int, int> NewRatesAtEveryConstellation
    {
        get
        {
            var data = new TheoryData<OfdmFmConstellation, int, int>();
            foreach (OfdmFmConstellation constellation in Enum.GetValues<OfdmFmConstellation>())
            {
                data.Add(constellation, 5, 6);
                data.Add(constellation, 7, 8);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NewRatesAtEveryConstellation))]
    public void A_Burst_At_The_New_Rates_Round_Trips_At_Every_Constellation_And_Payload_Size(
        OfdmFmConstellation constellation, int numerator, int denominator)
    {
        // The puncture periods are 5 and 7, which divide neither a byte nor a symbol, so the
        // coded length of a burst falls wherever it falls against the last symbol. The sizes
        // here are spread so that at every constellation at least one of them leaves that symbol
        // part filled, and the test says so rather than trusting the choice: the padding of a
        // partial symbol is the path a fixed size could miss.
        var coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, numerator, denominator, true);
        var modem = new OfdmFmBurstCodec(Small with { Coding = coding });
        var codec = new OfdmFmCodec(coding);
        int bitsPerSymbol = Small.DataCarriers * (int)constellation;
        bool partial = false;

        foreach (int size in (ReadOnlySpan<int>)[1, 2, 5, 24, 61, 128, 255])
        {
            byte[] payload = Payload(size, seed: size);

            OfdmFmBurst? burst = modem.Demodulate(modem.Modulate(payload, constellation));

            burst.Should().NotBeNull(
                "{0} bytes at {1} rate {2}/{3}", size, constellation, numerator, denominator);
            burst!.Payload.Should().Equal(payload, "{0} bytes at {1}", size, constellation);
            burst.Coding.Should().Be(coding, "the header names the rate the burst was sent at");

            // The framed payload is the payload plus its two CRC bytes, and what reaches the
            // subcarriers is that, coded.
            partial |= codec.CodedBits((size + 2) * 8) % bitsPerSymbol != 0;
        }

        partial.Should().BeTrue(
            "the sizes must include one that does not fill the last symbol at {0} rate {1}/{2}",
            constellation, numerator, denominator);
    }

    [Fact]
    public void A_Bit_Loaded_Burst_Round_Trips()
    {
        // Bits spread unevenly across the band, as a channel whose noise rises with frequency
        // wants. Same 20 carriers, same total per symbol, different distribution.
        var loaded = Small with
        {
            BitLoading = [new OfdmFmBitLoadingTier(8, 4), new OfdmFmBitLoadingTier(12, 2)],
        };
        var modem = new OfdmFmBurstCodec(loaded);
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = modem.Demodulate(modem.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
    }

    [Fact]
    public void Bit_Loading_Tiers_Must_Cover_Exactly_The_Data_Carriers()
    {
        var short_ = Small with { BitLoading = [new OfdmFmBitLoadingTier(5, 4)] };

        Action build = () => short_.BitsPerDataCarrier(OfdmFmConstellation.Qpsk);

        build.Should().Throw<InvalidOperationException>().WithMessage("*cover 5 carriers*");
    }

    [Fact]
    public void Bit_Loading_Tiers_Covering_Too_Many_Carriers_Are_Refused_Rather_Than_Truncated()
    {
        // The other direction, which used to be undetectable: filling and then checking where the
        // cursor stopped cannot see it, because the fill clamps at the end of the array and the
        // cursor lands on exactly DataCarriers however much the tiers asked for. A profile with a
        // tier set describing a wider band than it has would have run silently truncated.
        var wide = Small with
        {
            BitLoading = [new OfdmFmBitLoadingTier(Small.DataCarriers + 4, 2)],
        };

        Action build = () => wide.BitsPerDataCarrier(OfdmFmConstellation.Qpsk);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*cover {Small.DataCarriers + 4} carriers*");
    }

    [Fact]
    public void Validate_Reaches_The_Coding_And_The_Bit_Loading_Too()
    {
        // Both are only touched when a burst is built. A validator that checked the geometry alone
        // would pass a profile that then threw out of IModem.Process on the receive thread, which
        // is the worst possible place to find out.
        Action tiers = (Small with { BitLoading = [new OfdmFmBitLoadingTier(5, 4)] }).Validate;
        tiers.Should().Throw<InvalidOperationException>().WithMessage("*cover 5 carriers*");

        Action code = (Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, ConstraintLength: 5),
        }).Validate;
        code.Should().Throw<InvalidOperationException>().WithMessage("*no mother code*");

        Action rate = (Small with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 4, 5),
        }).Validate;
        rate.Should().Throw<InvalidOperationException>().WithMessage("*not punctured here*");

        Action good = Small.Validate;
        good.Should().NotThrow();
    }

    [Theory]
    [InlineData(OfdmFmFec.None, 0)]
    [InlineData(OfdmFmFec.Convolutional, 12)]
    public void The_Codec_Corrects_What_Its_Scheme_Should(OfdmFmFec scheme, int correctable)
    {
        // The coding layer on its own, away from the burst: flip bits in the coded stream and see
        // what comes back. Measured at the burst level the payload code cannot show, because the
        // HEADER is uncoded BPSK and fails first - a real finding, and a design item: whatever
        // codes the payload should cover the header too, or the header becomes the burst's floor.
        var codec = new OfdmFmCodec(new OfdmFmCoding(scheme, Interleave: true));
        var random = new Random(9);
        var bits = new byte[160];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        byte[] coded = codec.Encode(bits);
        var llrs = new float[coded.Length];
        for (int i = 0; i < coded.Length; i++)
        {
            llrs[i] = coded[i] == 0 ? 4f : -4f;
        }

        // Corrupt a scattered handful of coded bits by flipping their metrics outright.
        for (int k = 0; k < correctable + 1; k++)
        {
            int at = (k * 13) % llrs.Length;
            llrs[at] = -llrs[at];
        }

        byte[] back = codec.Decode(llrs, bits.Length);
        int wrong = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            wrong += bits[i] == back[i] ? 0 : 1;
        }

        if (scheme == OfdmFmFec.None)
        {
            wrong.Should().BeGreaterThan(0, "an uncoded stream cannot correct anything");
        }
        else
        {
            wrong.Should().Be(0, "a rate-1/2 K=7 code must absorb {0} scattered bit flips", correctable);
        }
    }

    [Fact]
    public void The_Interleaver_Is_Its_Own_Inverse_Pairing()
    {
        // Guards the interleave/deinterleave pairing end to end. Worth having because getting it
        // wrong is silent: every coded burst decodes to noise and nothing says why. It caught a
        // real mistake here, though not the one first diagnosed - GpInterleaver's third parameter
        // is the ELEMENT COUNT, and passing ChooseB(n) there instead permutes a prefix of the
        // block with the wrong stride. The library was correct; the call was not.
        var codec = new OfdmFmCodec(new OfdmFmCoding(OfdmFmFec.None, Interleave: true));
        var bits = new byte[257];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)(i % 2);
        }

        byte[] spread = codec.Encode(bits);
        var llrs = new float[spread.Length];
        for (int i = 0; i < spread.Length; i++)
        {
            llrs[i] = spread[i] == 0 ? 4f : -4f;
        }

        codec.Decode(llrs, bits.Length).Should().Equal(bits);
    }

    [Fact]
    public void Silence_Decodes_To_Nothing()
    {
        var modem = new OfdmFmBurstCodec(Small);

        modem.Demodulate(new float[Small.SymbolSamples * 6]).Should().BeNull();
    }

    [Fact]
    public void A_Profile_That_Does_Not_Fit_Its_Transform_Is_Refused_With_A_Reason()
    {
        var tooMany = Small with { DataCarriers = Small.FftSize };

        Action build = () => new OfdmFmBurstCodec(tooMany);

        build.Should().Throw<InvalidOperationException>().WithMessage("*do not fit*");
    }

    [Fact]
    public void The_Fallback_Profile_Is_A_Small_One_A_Station_Could_Still_Run()
    {
        // The profile every test above runs on, pinned so that nobody quietly grows it: it is here
        // to be cheap, and a fallback that drifted towards a real layout would slow the whole
        // suite down for no more coverage. The 12000 is not a waveform choice at all: it is one of
        // the two rates a pdn-soundmodem channel runs at, chosen so that even this profile is one
        // a station could be pointed at.
        Small.SampleRate.Should().Be(12000);
        Small.FftSize.Should().Be(128);
        OfdmFmParameters.LoadLocal(Path.Combine(Path.GetTempPath(), "no-such-file.json"))
            .Should().BeNull("a missing local profile file is normal, not an error");
    }

    [Fact]
    public void Every_Seeded_Uncoded_Payload_Survives_The_Clean_Whole_Buffer_Path()
    {
        // The uncoded path's own crest-factor limiter was clipping a few payloads in a hundred
        // hard enough to flip a subcarrier's decoded bit, on audio with no noise added at all -
        // see PeakLimitFor's remarks. 64, 90 and 128 bytes are the sizes that first found it; a
        // few hundred seeds at each, on a channel with nothing else in it, is what proves the
        // fix rather than one payload that happens to round trip.
        var failed = new List<(int Length, int Seed)>();
        foreach (int length in new[] { 64, 90, 128 })
        {
            for (int seed = 0; seed < 300; seed++)
            {
                var modem = new OfdmFmBurstCodec(Small);
                byte[] payload = Payload(length, seed);

                float[] audio = modem.Modulate(payload, Small.Constellation, 1);
                OfdmFmBurst? burst = modem.Demodulate(audio);

                if (burst?.Payload is null || !burst.Payload.SequenceEqual(payload))
                {
                    failed.Add((length, seed));
                }
            }
        }

        failed.Should().BeEmpty(
            "a noiseless burst has nothing to blame but our own transmit chain, and {0} pairs of "
            + "(length, seed) did not round trip", failed.Count);
    }
}
