using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// A burst names its own carrier layout, so a receiver configured for one geometry hears every
/// geometry in its table.
/// </summary>
/// <remarks>
/// <para>Before this, the geometry was the one thing a header could not say: the header is read
/// against it, so both ends had to agree in advance, and a bandwidth change was a change at both
/// stations. Now the sync symbol, preamble and header sit on a fixed acquisition layout that every
/// entry of the table shares, the header carries a 4-bit table index, and a payload on any other
/// entry is preceded by one channel-estimate symbol on its own carriers. These tests are the
/// difference between those two states.</para>
/// <para>Every geometry here is invented, on the synthetic transform. The table is sparse on
/// purpose - ids 1, 5 and 7 with gaps between - because a real table will be.</para>
/// </remarks>
public class GeometrySignallingTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;

    /// <summary>The acquisition layout: the synthetic profile's own, at id 0.</summary>
    private static readonly OfdmFmGeometry Acquisition = Small.Geometry;

    /// <summary>A layout that overlaps the acquisition span from a different first carrier, with
    /// fewer carriers than the header needs in one symbol on the acquisition layout.</summary>
    private static readonly OfdmFmGeometry Narrow = new(8, 12, 2);

    /// <summary>A layout wider than the acquisition span at both ends of it, which is the case
    /// signalling exists for: carriers the preamble never occupied.</summary>
    private static readonly OfdmFmGeometry Wide = new(6, 44, 6);

    /// <summary>A layout a receiver may not hold.</summary>
    private static readonly OfdmFmGeometry Foreign = new(10, 30, 4);

    private static readonly OfdmFmGeometryTable Table =
        new([Acquisition, Narrow, null, null, null, Wide]);

    private static readonly OfdmFmGeometryTable WithForeign =
        new([Acquisition, Narrow, null, null, null, Wide, null, Foreign]);

    private static OfdmFmParameters On(OfdmFmGeometry geometry, int id) => Small with
    {
        FirstCarrier = geometry.FirstCarrier,
        DataCarriers = geometry.DataCarriers,
        PilotCarriers = geometry.PilotCarriers,
        GeometryId = id,
    };

    private static readonly OfdmFmParameters OnAcquisition = On(Acquisition, 0);
    private static readonly OfdmFmParameters OnNarrow = On(Narrow, 1);
    private static readonly OfdmFmParameters OnWide = On(Wide, 5);
    private static readonly OfdmFmParameters OnForeign = On(Foreign, 7);

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static TheoryData<int> PayloadIds => [0, 1, 5];

    [Theory]
    [MemberData(nameof(PayloadIds))]
    public void A_Receiver_On_The_Acquisition_Geometry_Decodes_A_Burst_Sent_On_Any_Entry(int id)
    {
        // The receiver is configured for id 0 and is never told what is coming. Had the header
        // not named the payload's layout, a burst on id 5 would have been read on the wrong
        // carriers and failed its CRC, which is exactly what happened before this existed.
        var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
        var sender = new OfdmFmBurstCodec(On(Table[id]!, id), Table);
        byte[] payload = Payload(40, id);

        OfdmFmBurst? burst = receiver.Demodulate(sender.Modulate(payload, OfdmFmConstellation.Qam16));

        burst.Should().NotBeNull();
        burst!.Geometry.Should().Be(id);
        burst.Payload.Should().Equal(payload);
    }

    [Fact]
    public void A_Receiver_On_A_Wide_Geometry_Decodes_A_Burst_Sent_On_A_Narrow_One()
    {
        // The other direction, which is the same mechanism only if the acquisition layout really
        // is independent of the receiver's own: a receiver on id 5 still looks for the sync symbol
        // on id 0 and reads the header there.
        var receiver = new OfdmFmBurstCodec(OnWide, Table);
        var sender = new OfdmFmBurstCodec(OnNarrow, Table);
        byte[] payload = Payload(24);

        OfdmFmBurst? burst = receiver.Demodulate(sender.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst.Should().NotBeNull();
        burst!.Geometry.Should().Be(1);
        burst.Payload.Should().Equal(payload);
    }

    [Fact]
    public void A_Streaming_Receiver_Follows_A_Sender_Changing_Geometry_Between_Bursts()
    {
        // The case that matters on air: the receiver has to size each burst from its header
        // before it has the burst, and a payload on another layout has a different number of
        // bits per symbol AND an extra symbol in front. Sized from the receiver's own layout it
        // would stop collecting part way through, or hunt for sync inside the payload.
        var delivered = new List<byte[]>();
        var heard = new List<OfdmFmBurst>();
        var receiver = new OfdmFmModem("ofdm-fm:test", OnAcquisition, delivered.Add,
            geometryTable: Table);
        receiver.BurstHeard += heard.Add;

        // QPSK, not QAM-16: uncoded QAM-16 sits at its own peak-reduction cliff by design (the
        // limit is the lowest at which a noiseless loopback still round trips), and on the
        // synthetic layout a few payloads in forty fall off it on main's codec too. This test is
        // about geometry, not about that cliff.
        var sent = new List<(byte[] Payload, int Geometry)>();
        var audio = new List<float>();
        int seed = 1;
        foreach (int id in new[] { 5, 0, 1, 5, 1, 0, 5 })
        {
            var sender = new OfdmFmBurstCodec(On(Table[id]!, id), Table);
            byte[] payload = Payload(24 + (seed * 7), seed++);
            sent.Add((payload, id));
            audio.AddRange(sender.Modulate(payload, OfdmFmConstellation.Qpsk));
        }

        // Fed in blocks that do not line up with burst boundaries.
        float[] stream = [.. audio];
        for (int at = 0; at < stream.Length; at += 997)
        {
            receiver.Process(stream.AsSpan(at, Math.Min(997, stream.Length - at)).ToArray());
        }

        delivered.Should().HaveCount(sent.Count);
        for (int i = 0; i < sent.Count; i++)
        {
            delivered[i].Should().Equal(sent[i].Payload, "burst {0} of the geometry sweep", i);
        }

        heard.Where(b => b.Payload is not null).Select(b => b.Geometry)
            .Should().Equal(sent.Select(s => s.Geometry));
    }

    [Fact]
    public void The_Burst_Length_A_Header_Announces_Is_The_Length_The_Modulator_Produced()
    {
        // The streaming receiver holds exactly this many samples and then resumes hunting past
        // them, so an off-by-one-symbol here - the estimate symbol counted on the wrong side -
        // would put the search inside the next burst's lead-in or this one's last symbol.
        foreach (int id in new[] { 0, 1, 5 })
        {
            var sender = new OfdmFmBurstCodec(On(Table[id]!, id), Table);
            var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
            float[] audio = sender.Modulate(Payload(50, id), OfdmFmConstellation.Qam64, leadInSymbols: 0);

            OfdmFmHeader? header = receiver.ReadHeader(audio, 0);

            header.Should().NotBeNull();
            receiver.BurstSamples(header!.Value).Should().Be(audio.Length, "geometry {0}", id);
            receiver.PayloadOffset(id).Should().Be(
                receiver.HeaderEndOffset + (id == 0 ? 0 : Small.SymbolSamples),
                "only a payload off the acquisition layout carries an estimate symbol");
        }
    }

    [Fact]
    public void A_Burst_On_A_Geometry_The_Receiver_Does_Not_Hold_Is_Refused_And_The_Next_One_Is_Heard()
    {
        // Refused, not guessed: decoding on some other layout would fail the payload CRC and
        // report a bad link. And the refusal must cost only that burst - the receiver goes back
        // to hunting and finds the next burst on a layout it does hold.
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:test", OnAcquisition, delivered.Add,
            geometryTable: Table);
        var foreign = new OfdmFmBurstCodec(OnForeign, WithForeign);
        var known = new OfdmFmBurstCodec(OnWide, WithForeign);
        byte[] wanted = Payload(30, 9);

        var audio = new List<float>();
        audio.AddRange(foreign.Modulate(Payload(30, 8), OfdmFmConstellation.Qpsk));
        audio.AddRange(known.Modulate(wanted, OfdmFmConstellation.Qpsk));
        audio.AddRange(new float[Small.SymbolSamples * 2]);

        receiver.Process([.. audio]);

        delivered.Should().ContainSingle().Which.Should().Equal(wanted);
        new OfdmFmBurstCodec(OnAcquisition, Table)
            .ReadHeader(foreign.Modulate(Payload(30, 8), OfdmFmConstellation.Qpsk, 0), 0)
            .Should().BeNull("a header naming geometry 7 is unreadable to a table without it");
    }

    [Fact]
    public void A_Profile_With_No_Table_Runs_Alone_On_Id_Zero()
    {
        // The arrangement this waveform had before geometry was signalled, and still what a bench
        // with one profile wants: no estimate symbol, every burst says geometry 0, and a receiver
        // built the same way reads it.
        var alone = new OfdmFmBurstCodec(Small);
        byte[] payload = Payload(24);
        float[] audio = alone.Modulate(payload, OfdmFmConstellation.Qpsk, leadInSymbols: 0);

        alone.GeometryId.Should().Be(0);
        alone.Table.Ids.Should().Equal([0]);
        alone.PayloadOffset(0).Should().Be(alone.HeaderEndOffset);

        OfdmFmBurst? burst = alone.Demodulate(audio);
        burst!.Payload.Should().Equal(payload);
        burst.Geometry.Should().Be(0);
    }

    [Fact]
    public void The_Header_Fits_One_Symbol_Down_To_A_Hundred_And_Four_Data_Carriers()
    {
        // 52 header bits are 104 coded, and a header carrier holds one bit, so 104 data carriers
        // on the acquisition layout is the whole budget. This is why the geometry field is 4 bits
        // and the length field lost 4, and it is pinned here so that nobody grows the header past
        // the room that exists without a test telling them. An acquisition layout narrower than
        // this still works; it just spends a second symbol on every burst announcing itself.
        var narrowest = new OfdmFmParameters(48000, 2048, 64, 9, 104, 2);
        new OfdmFmBurstCodec(narrowest).HeaderSymbolCount.Should().Be(1);

        // One carrier fewer and it no longer fits, which is the measure of how little room there is.
        var tooFew = new OfdmFmParameters(48000, 2048, 64, 9, 103, 2);
        new OfdmFmBurstCodec(tooFew).HeaderSymbolCount.Should().Be(2);
    }

    [Fact]
    public void A_Recommended_Geometry_Rides_With_The_Rate_Recommendation()
    {
        var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
        var sender = new OfdmFmBurstCodec(OnWide, Table);
        OfdmFmRate asked = OfdmFmRateLadder.Rungs[3];

        OfdmFmBurst? burst = receiver.Demodulate(sender.Modulate(
            Payload(20), OfdmFmConstellation.Qpsk, recommendation: asked, recommendedGeometry: 1));

        burst!.Recommendation.Should().Be(asked);
        burst.RecommendedGeometry.Should().Be(1);

        // Without a rate to ride with, the geometry says nothing.
        OfdmFmBurst? silent = receiver.Demodulate(sender.Modulate(
            Payload(20), OfdmFmConstellation.Qpsk, recommendedGeometry: 1));
        silent!.Recommendation.Should().BeNull();
        silent.RecommendedGeometry.Should().BeNull();

        // Left out, the recommended geometry is the sender's own: "send me what I send you".
        OfdmFmBurst? own = receiver.Demodulate(sender.Modulate(
            Payload(20), OfdmFmConstellation.Qpsk, recommendation: asked));
        own!.RecommendedGeometry.Should().Be(5);
    }

    [Fact]
    public void A_Recommended_Geometry_This_Station_Does_Not_Hold_Is_Dropped_And_The_Rate_Kept()
    {
        // The rate advice is still good; the geometry advice is one this station cannot take.
        var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
        var sender = new OfdmFmBurstCodec(OnAcquisition, WithForeign);
        OfdmFmRate asked = OfdmFmRateLadder.Rungs[2];

        OfdmFmBurst? burst = receiver.Demodulate(sender.Modulate(
            Payload(20), OfdmFmConstellation.Qpsk, recommendation: asked, recommendedGeometry: 7));

        burst!.Recommendation.Should().Be(asked);
        burst.RecommendedGeometry.Should().BeNull();
    }

    [Fact]
    public void An_Adapting_Station_Transmits_On_The_Geometry_Its_Correspondent_Asks_For()
    {
        // A on the wide layout, B on the acquisition layout, both adapting. A's bursts recommend
        // A's own layout, so once B has heard A it transmits on the wide layout too, without B
        // having been configured for it. And A, hearing B ask for id 0, moves down to it.
        var aFrames = new List<byte[]>();
        var bFrames = new List<byte[]>();
        var a = new OfdmFmModem("ofdm-fm:a", OnWide with { AdaptiveRate = true }, aFrames.Add,
            geometryTable: Table);
        var b = new OfdmFmModem("ofdm-fm:b", OnAcquisition with { AdaptiveRate = true }, bFrames.Add,
            geometryTable: Table);

        b.TransmittingOn.Should().Be(0);
        a.TransmittingOn.Should().Be(5);

        b.Process(a.Modulate(Payload(32, 1), txDelayMilliseconds: 0));
        b.TransmittingOn.Should().Be(5, "B was asked for the wide layout and holds it");

        a.Process(b.Modulate(Payload(32, 2), txDelayMilliseconds: 0));
        a.TransmittingOn.Should().Be(0, "A was asked for the acquisition layout");

        // And what B now transmits really is on id 5, readable by a third station on id 0.
        var heard = new List<OfdmFmBurst>();
        var c = new OfdmFmModem("ofdm-fm:c", OnAcquisition, _ => { }, geometryTable: Table);
        c.BurstHeard += heard.Add;
        c.Process(b.Modulate(Payload(32, 3), txDelayMilliseconds: 0));
        heard.Should().ContainSingle().Which.Geometry.Should().Be(5);
    }

    [Fact]
    public void A_Geometry_Ask_Expires_With_The_Rate_Recommendation()
    {
        var clock = new FakeClock();
        var a = new OfdmFmModem("ofdm-fm:a", OnAcquisition with { AdaptiveRate = true }, _ => { },
            clock, geometryTable: Table);
        var b = new OfdmFmModem("ofdm-fm:b", OnWide with { AdaptiveRate = true }, _ => { },
            geometryTable: Table);

        a.Process(b.Modulate(Payload(32), txDelayMilliseconds: 0));
        a.TransmittingOn.Should().Be(5);

        clock.Advance(TimeSpan.FromSeconds(Small.RecommendationLifeSeconds + 1));
        a.Modulate(Payload(8), txDelayMilliseconds: 0);
        a.TransmittingOn.Should().Be(0, "a stale ask is forgotten with the rate it rode on");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public void A_Clock_Difference_Is_Tracked_Across_A_Payload_On_Another_Geometry(int ppm)
    {
        // The tilt fit reads the header on the acquisition layout and the pilots on the payload
        // layout, and a tilt is so many radians per BIN per symbol. Positions taken as indices
        // into each layout instead of absolute bins would fit the header at one offset and apply
        // the correction at another, which on a wide payload shows up as the uncorrected
        // behaviour: a dense constellation spending its code on nothing but the tilt.
        var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
        var sender = new OfdmFmBurstCodec(OnWide with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
        }, Table);
        byte[] payload = Payload(256, ppm);
        float[] clean = sender.Modulate(payload, OfdmFmConstellation.Qam64, 2);
        var padded = new float[clean.Length + 512];
        clean.CopyTo(padded, 0);

        OfdmFmBurst? burst = receiver.Demodulate(ClockSkewTests.Resample(padded, 1.0 + (ppm * 1e-6)));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload, "{0} ppm on a wide payload", ppm);
        burst.PreFecBitErrorRate.Should().BeLessThan(0.02);
    }

    [Fact]
    public void Per_Carrier_Signal_To_Noise_Tracks_The_Noise_Actually_Added()
    {
        // The figure a bit-loading decision is taken from, so it has to be a measurement of the
        // channel and not of the codec: ten decibels more noise must read as ten decibels less
        // signal to noise, on every carrier, and a clean loopback must read as clean.
        var profile = OnWide with { PeakToAverageLimitDb = 12 };
        var codec = new OfdmFmBurstCodec(profile, Table);
        var receiver = new OfdmFmBurstCodec(OnAcquisition, Table);
        byte[] payload = Payload(200);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam16, 2);

        double MeanSnr(float[] audio)
        {
            OfdmFmBurst? burst = receiver.Demodulate(audio);
            burst!.Payload.Should().Equal(payload);
            burst.CarrierSnrDb.Should().HaveCount(Wide.TotalCarriers);
            burst.CarrierGainDb.Should().HaveCount(Wide.TotalCarriers);
            return burst.CarrierSnrDb!.Average();
        }

        MeanSnr(clean).Should().BeGreaterThan(30, "a noiseless loopback is limited only by the "
            + "peak reducer's distortion, which at 12 dB of crest factor is small");

        double quiet = MeanSnr(Noisy(clean, cnrDb: 30, seed: 1));
        double loud = MeanSnr(Noisy(clean, cnrDb: 20, seed: 2));

        // The carriers hold the burst's power in a fraction of the transform's bins, so the
        // per-carrier figure sits above the time-domain carrier-to-noise ratio by that fraction
        // - and the ten-decibel step between the two must come through as a ten-decibel step.
        (quiet - loud).Should().BeApproximately(10, 2);
        quiet.Should().BeInRange(28, 42);

        OfdmFmBurst? failed = receiver.Demodulate(Noisy(clean, cnrDb: -10, seed: 3));
        (failed?.CarrierSnrDb).Should().BeNull("with no CRC there is no trustworthy reference");
    }

    [Fact]
    public void A_Profile_In_A_Table_Must_Be_The_Entry_It_Claims_To_Be()
    {
        // Two ways to lie, both refused at construction rather than on air. A profile whose id
        // names a different layout would announce one thing and transmit another; a profile with
        // no id in a table has no name for what it transmits.
        Action wrongLayout = () => new OfdmFmBurstCodec(OnWide with { GeometryId = 1 }, Table);
        wrongLayout.Should().Throw<ArgumentException>().WithMessage("*not the table's geometry 1*");

        Action noId = () => new OfdmFmBurstCodec(OnWide with { GeometryId = null }, Table);
        noId.Should().Throw<ArgumentException>().WithMessage("*which entry it is*");

        Action absent = () => new OfdmFmBurstCodec(OnForeign, Table);
        absent.Should().Throw<ArgumentException>().WithMessage("*geometry 7 is not in the table*");

        Action outOfRange = (Small with { GeometryId = 16 }).Validate;
        outOfRange.Should().Throw<InvalidOperationException>().WithMessage("*not a 4-bit id*");
    }

    [Fact]
    public void A_Table_Needs_An_Acquisition_Entry_And_At_Most_Sixteen()
    {
        Action noZero = () => new OfdmFmGeometryTable([null, Narrow]);
        noZero.Should().Throw<ArgumentException>().WithMessage("*entry at id 0*");

        Action tooMany = () => new OfdmFmGeometryTable(Enumerable.Repeat(Acquisition, 17).ToList());
        tooMany.Should().Throw<ArgumentException>().WithMessage("*at most 16*");

        Table.Ids.Should().Equal([0, 1, 5]);
        Table[3].Should().BeNull();
        Table[16].Should().BeNull();
        Table.IdOf(Wide).Should().Be(5);
        Table.IdOf(Foreign).Should().BeNull();
    }

    [Fact]
    public void The_Table_Is_Built_From_The_Profiles_Ids_And_Refuses_What_Cannot_Share_One()
    {
        // Rate variants of one span share an id; a different layout on a taken id, or a different
        // transform, cannot be in the same table and is named as the reason.
        var profiles = new Dictionary<string, OfdmFmParameters>(StringComparer.Ordinal)
        {
            ["acq"] = OnAcquisition,
            ["acq-fast"] = OnAcquisition with { Constellation = OfdmFmConstellation.Qam64 },
            ["wide"] = OnWide,
            ["alone"] = Small with { FirstCarrier = 20 },
            ["zz-squatter"] = OnNarrow with { GeometryId = 5 },
            ["othertransform"] = OnNarrow with { FftSize = 256 },
        };

        OfdmFmGeometryTable? table = OfdmFmParameters.TableOf(
            profiles, out IReadOnlyList<(string Name, string Reason)> rejected);

        table.Should().NotBeNull();
        table!.Ids.Should().Equal([0, 5]);

        // Profiles are taken in name order, so the first name on an id owns it. A hand-edited
        // file with two layouts on one id is a mistake either way; what matters is that the
        // second is refused by name and reason rather than quietly overwriting the first.
        rejected.Select(r => r.Name).Order().Should().Equal("othertransform", "zz-squatter");
        rejected.Single(r => r.Name == "zz-squatter").Reason.Should().Contain("already 'wide'");
        rejected.Single(r => r.Name == "othertransform").Reason.Should().Contain("transform");

        OfdmFmParameters.TableOf(
            new Dictionary<string, OfdmFmParameters> { ["alone"] = Small }, out _)
            .Should().BeNull("no profile declares an id");

        Action noAcquisition = () => OfdmFmParameters.TableOf(
            new Dictionary<string, OfdmFmParameters> { ["wide"] = OnWide }, out _);
        noAcquisition.Should().Throw<InvalidOperationException>().WithMessage("*none declares id 0*");
    }

    [Fact]
    public void The_Catalogue_Runs_Its_Presets_In_One_Shared_Table()
    {
        // Two layouts on different ids in one table, built as the catalogue builds them: a frame
        // sent on one is heard by a receiver configured for the other, neither knowing in advance.
        var profiles = new Dictionary<string, OfdmFmParameters>(StringComparer.Ordinal)
        {
            ["acq"] = OnAcquisition,
            ["wide"] = OnWide,
            ["zz-squatter"] = OnNarrow with { GeometryId = 5 },
        };

        OfdmFmGeometryTable? table = OfdmFmParameters.TableOf(profiles, out var rejected);

        table.Should().NotBeNull();
        table!.Ids.Should().Equal([0, 5]);
        rejected.Should().ContainSingle().Which.Name.Should().Be("zz-squatter");

        var delivered = new List<byte[]>();
        var acq = new OfdmFmModem("acq", OnAcquisition, delivered.Add, null, null, table);
        var wide = new OfdmFmModem("wide", OnWide, _ => { }, null, null, table);
        byte[] frame = Payload(64);

        acq.Process(wide.Modulate(frame, txDelayMilliseconds: 30));

        delivered.Should().ContainSingle().Which.Should().Equal(frame);

        // A profile handed no table runs alone, in its own single-entry one.
        var alone = new OfdmFmModem(
            "alone", Small with { FirstCarrier = 20 }, _ => { });
        alone.GeometryTable.Ids.Should().Equal([0]);
    }

    [Fact]
    public void Every_Shipped_Preset_Is_In_The_Shipped_Table()
    {
        // The table the catalogue hands every mode. Nothing may be silently left out of it: a
        // preset missing from the table would acquire against a layout it does not belong to.
        OfdmFmParameters.TableOf(OfdmFmPresets.ByMode, out var rejected)
            .Should().NotBeNull();

        rejected.Should().BeEmpty(
            "every shipped preset has to make it into the shipped table; rejected: {0}",
            string.Join(", ", rejected.Select(r => $"{r.Name} ({r.Reason})")));

        OfdmFmPresets.Table.Ids.Should().Equal([0, 1, 2]);

        foreach ((string mode, OfdmFmParameters preset) in OfdmFmPresets.ByMode)
        {
            preset.GeometryId.Should().NotBeNull(
                "{0} has to name its layout so a receiver can read its bursts", mode);
            OfdmFmPresets.Table.Ids.Should().Contain(
                preset.GeometryId!.Value, "{0} names a layout the table has to hold", mode);
        }
    }

    private static float[] Noisy(float[] clean, double cnrDb, int seed)
    {
        double power = 0;
        foreach (float sample in clean)
        {
            power += (double)sample * sample;
        }

        double rms = Math.Sqrt(power / clean.Length);
        double sigma = rms / Math.Pow(10, cnrDb / 20);
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

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
