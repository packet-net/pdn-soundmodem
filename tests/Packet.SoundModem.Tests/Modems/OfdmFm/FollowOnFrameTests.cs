using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// A frame that follows another in the same keyup carries only its header and payload, and the
/// receiver reads it with the timing and channel it kept from the frame before.
/// </summary>
/// <remarks>
/// Every frame used to pay sync, preamble, header and estimate symbol before any payload: four
/// symbols, a third of a 1024-byte burst on the 8 kHz preset, for an acquisition the receiver had
/// already done a frame earlier. These tests are the difference between a keyup of full bursts
/// and a keyup of one full burst followed by follow-on ones, on the synthetic transform with a
/// wide payload layout in a table, the way the real stations run.
/// </remarks>
public class FollowOnFrameTests
{
    private static readonly OfdmFmParameters Small = OfdmFmParameters.Synthetic;
    private static readonly OfdmFmGeometry Acquisition = Small.Geometry;
    private static readonly OfdmFmGeometry Wide = new(6, 44, 6);
    private static readonly OfdmFmGeometryTable Table = new([Acquisition, null, null, null, null, Wide]);

    private static OfdmFmParameters On(OfdmFmGeometry geometry, int id) => Small with
    {
        FirstCarrier = geometry.FirstCarrier,
        DataCarriers = geometry.DataCarriers,
        PilotCarriers = geometry.PilotCarriers,
        GeometryId = id,
        Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
    };

    private static readonly OfdmFmParameters Sender = On(Wide, 5) with
    {
        FollowOnFrames = true,
        ContiguousBursts = true,
        Constellation = OfdmFmConstellation.Qam16,
    };

    private static readonly OfdmFmParameters Receiver = On(Acquisition, 0);

    private static byte[] Frame(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A keyup as the host presents it: the full lead-in before the first frame, the
    /// within-keyup figure before each one after.</summary>
    private static List<float[]> Keyup(OfdmFmModem sender, params byte[][] frames)
    {
        var bursts = new List<float[]>();
        for (int i = 0; i < frames.Length; i++)
        {
            bursts.Add(sender.Modulate(frames[i], i == 0 ? 300 : OfdmFmModem.HostLeadInWithinKeyupMs));
        }

        return bursts;
    }

    private static void Stream(OfdmFmModem receiver, IEnumerable<float[]> pieces, int block = 997)
    {
        float[] all = [.. pieces.SelectMany(p => p)];
        for (int at = 0; at < all.Length; at += block)
        {
            receiver.Process(all.AsSpan(at, Math.Min(block, all.Length - at)).ToArray());
        }
    }

    [Fact]
    public void A_Follow_On_Burst_Is_The_Full_Burst_Less_Sync_Preamble_And_Estimate()
    {
        var codec = new OfdmFmBurstCodec(Sender, Table);
        byte[] payload = Frame(40, 1);

        float[] full = codec.Modulate(payload, OfdmFmConstellation.Qam16, leadInSymbols: 0);
        float[] followOn = codec.ModulateFollowOn(payload, OfdmFmConstellation.Qam16);

        // Sync, preamble and, off the acquisition layout, the estimate symbol: three symbols.
        followOn.Length.Should().Be(full.Length - (3 * Small.SymbolSamples));
        followOn.Length.Should().Be(codec.FollowOnBurstSamples(codec.ReadHeader(full, 0)!.Value));
        codec.FollowOnHeaderSamples.Should().Be(codec.HeaderSymbolCount * Small.SymbolSamples);

        // On the acquisition layout there is no estimate symbol to save: two symbols.
        var narrow = new OfdmFmBurstCodec(Receiver, Table);
        narrow.ModulateFollowOn(payload, OfdmFmConstellation.Qpsk).Length.Should().Be(
            narrow.Modulate(payload, OfdmFmConstellation.Qpsk, 0).Length - (2 * Small.SymbolSamples));
    }

    [Fact]
    public void A_Keyup_Of_Follow_On_Frames_Comes_Out_Of_The_Streaming_Receiver_In_Order()
    {
        // The receiver is on the acquisition layout and has no idea a wide payload is coming,
        // let alone one with no sync symbol: the first burst tells it the layout and gives it the
        // channel, and each frame after that refreshes what the next one is read with.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] frames = [Frame(60, 1), Frame(31, 2), Frame(90, 3), Frame(45, 4), Frame(120, 5), Frame(20, 6)];

        List<float[]> bursts = Keyup(sender, frames);
        int fullLength = bursts[0].Length;
        for (int i = 1; i < bursts.Count; i++)
        {
            bursts[i].Length.Should().BeLessThan(fullLength, "frame {0} is a follow-on", i);
        }

        Stream(receiver, [.. bursts, new float[Small.SymbolSamples * 4]]);

        delivered.Should().HaveCount(frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i], "frame {0} of the keyup", i);
        }
    }

    [Fact]
    public void A_Second_Keyup_After_Silence_Is_Acquired_Afresh()
    {
        // The follow-on expectation at the end of a keyup finds no header in the silence, and
        // that must cost nothing: the hunt resumes and the next keyup's sync symbol is found.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] first = [Frame(50, 11), Frame(70, 12), Frame(30, 13)];
        byte[][] second = [Frame(40, 21), Frame(80, 22), Frame(25, 23)];

        var pieces = new List<float[]>();
        pieces.AddRange(Keyup(sender, first));
        pieces.Add(new float[Small.SymbolSamples * 9]);
        pieces.AddRange(Keyup(sender, second));
        pieces.Add(new float[Small.SymbolSamples * 4]);

        Stream(receiver, pieces, block: 611);

        delivered.Should().HaveCount(6);
        byte[][] expected = [.. first, .. second];
        for (int i = 0; i < expected.Length; i++)
        {
            delivered[i].Should().Equal(expected[i], "frame {0} across the two keyups", i);
        }
    }

    [Fact]
    public void A_Follow_On_Header_That_Fails_Costs_The_Keyup_And_Not_The_Next_One()
    {
        // The trade the option makes explicit: with no sync symbol to find a follow-on frame by,
        // a header that does not read loses that frame and whatever follows it in the keyup.
        // What it must not lose is the receiver: the hunt resumes and the next keyup is heard.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] first = [Frame(50, 31), Frame(70, 32), Frame(30, 33)];
        byte[][] second = [Frame(40, 41), Frame(80, 42)];

        List<float[]> keyup = Keyup(sender, first);

        // The second frame's header, wrecked: every header symbol of it, because the header is
        // coded and spread over several symbols on this narrow acquisition layout and survives
        // losing a couple of them.
        Array.Clear(keyup[1], 0, new OfdmFmBurstCodec(Receiver, Table).FollowOnHeaderSamples);
        var pieces = new List<float[]>(keyup)
        {
            new float[Small.SymbolSamples * 9],
        };
        pieces.AddRange(Keyup(sender, second));
        pieces.Add(new float[Small.SymbolSamples * 4]);

        Stream(receiver, pieces);

        delivered.Should().HaveCount(3);
        delivered[0].Should().Equal(first[0]);
        delivered[1].Should().Equal(second[0]);
        delivered[2].Should().Equal(second[1]);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public void A_Keyup_Of_Follow_On_Frames_Survives_A_Clock_Difference(int ppm)
    {
        // The channel a follow-on payload is read against is refreshed from the frame before,
        // so the accumulated timing drift over a keyup lands on that estimate one frame at a time
        // rather than all at once, and the header's estimate is refreshed from each header. A
        // keyup long enough for the drift to reach a sample, at a dense constellation.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender with
        {
            Constellation = OfdmFmConstellation.Qam64,
        }, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] frames = Enumerable.Range(0, 8).Select(i => Frame(100 + (i * 13), 50 + i)).ToArray();

        float[] keyup = [.. Keyup(sender, frames).SelectMany(b => b), .. new float[Small.SymbolSamples * 6]];
        float[] heard = ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6));

        Stream(receiver, [heard]);

        delivered.Should().HaveCount(frames.Length, "{0} ppm across a keyup of {1} frames", ppm, frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i]);
        }
    }

    /// <summary>
    /// A keyup between two stations on the follow-on preset, through clock differences from the
    /// ordinary to the 1930 ppm of two CM108B cards without their "MIC PLL Adjust" bit, in
    /// noise.
    /// </summary>
    /// <remarks>
    /// A follow-on frame has no sync symbol, no preamble and no estimate symbol: it is found
    /// where the frame before ended and read against the channel that frame handed on. Both
    /// depend on the clock. The frame before ends tens of samples away from where its announced
    /// length says, further than the search for the next header was built to look, and the
    /// channel has to be at the last symbol's timing. The clock-tracked read measures both and
    /// hands them on (see <c>OfdmFmKeyupState.ClockShift</c>); before it did, the pilot-fitted
    /// reads lost every follow-on frame from 500 ppm.
    /// </remarks>
    [Theory]
    [InlineData(100)]
    [InlineData(-500)]
    [InlineData(1000)]
    [InlineData(1930)]
    [InlineData(-1930)]
    [InlineData(2500)]
    [InlineData(-2500)]
    public void A_Follow_On_Keyup_Is_Read_By_The_Tracked_Read_Whatever_The_Clock(int ppm)
    {
        var sender = new OfdmFmModem(
            "ofdm-fm:tx", OfdmFmPresets.Follow8k, _ => { }, geometryTable: OfdmFmPresets.Table);
        var delivered = new List<byte[]>();
        var readings = new List<string>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Follow8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        receiver.BurstHeard += burst => readings.Add((burst.FollowOn ? "follow-on " : "full ") + burst.Reading);
        byte[][] frames = Enumerable.Range(0, 8).Select(i => Frame(1024, 800 + i)).ToArray();

        float[] clean = [.. Keyup(sender, frames).SelectMany(b => b)];
        float[] keyup = [.. new float[4096], .. clean, .. new float[OfdmFmPresets.Follow8k.SymbolSamples * 6]];
        float[] heard = ClockSkewTests.WithNoise(
            ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6)), clean, 28, 99);

        Stream(receiver, [heard], block: 960);

        delivered.Should().HaveCount(frames.Length, "{0} ppm across a keyup of {1} frames", ppm, frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i]);
        }

        readings.Count(r => r.StartsWith("follow-on ", StringComparison.Ordinal)).Should().Be(frames.Length - 1);
        readings.Should().OnlyContain(r => r.EndsWith("tracked", StringComparison.Ordinal)
            || r.EndsWith("polished", StringComparison.Ordinal));
    }

    /// <summary>
    /// A long keyup of follow-on frames is read as well at its end as at its start.
    /// </summary>
    /// <remarks>
    /// Everything a follow-on frame is read with comes from the frame before, so anything the
    /// hand-on gets slightly wrong is wrong again at the next frame, and a keyup is as long as
    /// the queue behind it. Two things did add up, both found on air with twenty frames to the
    /// keyup and neither visible in eight. The grid was moved on by half a symbol's drift at
    /// every frame where it was owed once, which at 1600 ppm walked the read window out of the
    /// cyclic prefix by the fifteenth frame. And the channel refinement, applied to its own
    /// result frame after frame, smoothed across carriers with a flat run whose response is
    /// negative for some ripple, so that ripple grew by a fifth a frame whatever the clock.
    /// Together they took the raw error rate from 0.1% at the first frame to 5% at the
    /// eighteenth.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(1930)]
    [InlineData(-1930)]
    public void A_Long_Follow_On_Keyup_Ends_As_Clean_As_It_Starts(int ppm)
    {
        var sender = new OfdmFmModem(
            "ofdm-fm:tx", OfdmFmPresets.Follow8k, _ => { }, geometryTable: OfdmFmPresets.Table);
        var delivered = new List<byte[]>();
        var errors = new List<double>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Follow8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        receiver.BurstHeard += burst => errors.Add(burst.PreFecBitErrorRate ?? 1);
        byte[][] frames = Enumerable.Range(0, 30).Select(i => Frame(1024, 900 + i)).ToArray();

        float[] clean = [.. Keyup(sender, frames).SelectMany(b => b)];
        float[] keyup = [.. new float[4096], .. clean, .. new float[OfdmFmPresets.Follow8k.SymbolSamples * 6]];
        float[] heard = ClockSkewTests.WithNoise(
            ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6)), clean, 28, 77);

        Stream(receiver, [heard], block: 960);

        delivered.Should().HaveCount(frames.Length, "{0} ppm across a keyup of {1} frames", ppm, frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i]);
        }

        errors.Should().HaveCount(frames.Length);
        double start = errors.Take(5).Average();
        double end = errors.TakeLast(5).Average();
        end.Should().BeLessThan(
            (2 * start) + 0.002, "the last frames of the keyup ({0:P2} raw errors) against the first ({1:P2})", end, start);
    }

    /// <summary>
    /// A keyup whose frames have silence between them, as a host that drains its sound card
    /// between frames plays them, heard through a clock difference.
    /// </summary>
    /// <remarks>
    /// <para>A frame that is not where the one before ended is found by its header a few samples
    /// off, or by its symbols' cyclic prefixes, and then has to be read on the grid the frame
    /// before handed its channel on for. Four things were wrong with that on the 8 kHz preset,
    /// the first two with no clock difference at all (2 frames of 6 delivered before this read
    /// existed):</para>
    /// <para>the alignment the prefixes are referred to was measured over a burst's last symbols,
    /// where a receiver that reads a burst the moment it has arrived has no audio to search into,
    /// and came out tens of samples short; a frame so found is placed to within eight samples or
    /// so, and its first symbol was looked for over four; the grid has been moved by what the
    /// clock measured and the frame was put on the unmoved one, which lost frames from 100 ppm;
    /// and between cards far apart a prefix's copy is not a transform later in the audio but
    /// some samples short of it, so the prefixes found nothing at all from about 1000 ppm.</para>
    /// </remarks>
    [Theory]
    [InlineData(0, 1500)]
    [InlineData(100, 1500)]
    [InlineData(300, 1500)]
    [InlineData(-300, 1500)]
    [InlineData(1000, 1500)]
    [InlineData(1930, 1500)]
    [InlineData(-1930, 1500)]
    [InlineData(0, 10)]
    [InlineData(300, 10)]
    [InlineData(50, 17)]
    [InlineData(-300, 40)]
    [InlineData(0, 4200)]
    public void A_Keyup_With_Silence_Between_Its_Frames_Is_Read_Through_A_Clock_Difference(int ppm, int gap)
    {
        var sender = new OfdmFmModem(
            "ofdm-fm:tx", OfdmFmPresets.Follow8k, _ => { }, geometryTable: OfdmFmPresets.Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Follow8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        byte[][] frames = Enumerable.Range(0, 6).Select(i => Frame(1024, 700 + i)).ToArray();

        float[] clean = [.. KeyupWithGaps(sender, frames, _ => gap).SelectMany(b => b)];
        float[] keyup = [.. new float[4096], .. clean, .. new float[OfdmFmPresets.Follow8k.SymbolSamples * 6]];
        float[] heard = ClockSkewTests.WithNoise(
            ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6)), clean, 28, 55);

        Stream(receiver, [heard], block: 960);

        delivered.Should().HaveCount(frames.Length, "{0} ppm, {1} samples of silence between frames", ppm, gap);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i]);
        }
    }

    /// <summary>
    /// A follow-on frame that is lost, after a frame of another length, costs that frame and not
    /// the rest of the keyup.
    /// </summary>
    /// <remarks>
    /// The grid the next frame is read on is moved by what the clock did over the frame before.
    /// A frame whose payload fails measured nothing to move it by, and left alone the grid kept
    /// the move the last good frame earned, a long one's where a short one's was owed. Every
    /// frame after it was then tens of samples off its channel.
    /// </remarks>
    [Theory]
    [InlineData(300)]
    [InlineData(800)]
    [InlineData(1930)]
    [InlineData(-1930)]
    public void A_Lost_Follow_On_Frame_Of_Another_Length_Costs_Only_Itself(int ppm)
    {
        var sender = new OfdmFmModem(
            "ofdm-fm:tx", OfdmFmPresets.Follow8k, _ => { }, geometryTable: OfdmFmPresets.Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Follow8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        byte[][] frames =
        [
            Frame(1900, 600), Frame(300, 601), Frame(300, 602), Frame(300, 603), Frame(300, 604), Frame(300, 605),
        ];

        // The second frame's payload, wrecked from its second symbol on; its header is left.
        List<float[]> bursts = Keyup(sender, frames);
        int header = new OfdmFmBurstCodec(OfdmFmPresets.Follow8k, OfdmFmPresets.Table).FollowOnHeaderSamples;
        int from = header + OfdmFmPresets.Follow8k.SymbolSamples;
        Array.Clear(bursts[1], from, bursts[1].Length - from);

        float[] clean = [.. bursts.SelectMany(b => b)];
        float[] keyup = [.. new float[4096], .. clean, .. new float[OfdmFmPresets.Follow8k.SymbolSamples * 6]];
        float[] heard = ClockSkewTests.WithNoise(
            ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6)), clean, 28, 56);

        Stream(receiver, [heard], block: 960);

        delivered.Should().HaveCount(frames.Length - 1, "{0} ppm, the second frame of six lost", ppm);
        delivered[0].Should().Equal(frames[0]);
        for (int i = 2; i < frames.Length; i++)
        {
            delivered[i - 1].Should().Equal(frames[i]);
        }
    }

    /// <summary>
    /// A station on the default 8 kHz preset, which sends no follow-on frames of its own, hearing
    /// a keyup from one on the follow-on preset through a clock difference.
    /// </summary>
    /// <remarks>
    /// The two presets differ only in that flag, so on the air this is an ordinary pairing. The
    /// first frame of the keyup is a full burst and is read by the clock-tracked read, whose
    /// symbols come from resampled audio with their delays already out. The channel handed on
    /// for the follow-on frames has to be put back at the last symbol's timing in the audio as it
    /// arrived; left at the estimate symbol's, every follow-on frame of the keyup was lost from
    /// 200 ppm (6 of 24 frames delivered where the pilot-fitted reads delivered 24).
    /// </remarks>
    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(-300)]
    public void A_Station_That_Sends_No_Follow_On_Frames_Still_Reads_Them_Through_A_Clock_Difference(int ppm)
    {
        var sender = new OfdmFmModem(
            "ofdm-fm:tx", OfdmFmPresets.Follow8k, _ => { }, geometryTable: OfdmFmPresets.Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem(
            "ofdm-fm:rx", OfdmFmPresets.Default8k, delivered.Add, geometryTable: OfdmFmPresets.Table);
        byte[][] frames = Enumerable.Range(0, 4).Select(i => Frame(1024, 800 + i)).ToArray();

        float[] keyup =
        [
            .. Keyup(sender, frames).SelectMany(b => b),
            .. new float[OfdmFmPresets.Default8k.SymbolSamples * 6],
        ];
        float[] heard = ClockSkewTests.Resample(keyup, 1.0 + (ppm * 1e-6));

        Stream(receiver, [heard], block: 960);

        delivered.Should().HaveCount(frames.Length, "{0} ppm across a keyup of {1} frames", ppm, frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i]);
        }
    }

    [Fact]
    public void Without_The_Option_Every_Frame_Of_A_Keyup_Is_A_Full_Burst()
    {
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender with { FollowOnFrames = false }, _ => { },
            geometryTable: Table);
        byte[] frame = Frame(40, 7);

        int first = sender.Modulate(frame, 300).Length;
        int second = sender.Modulate(frame, OfdmFmModem.HostLeadInWithinKeyupMs).Length;

        // Contiguous, so the second has no lead-in, but it has everything else.
        second.Should().Be(new OfdmFmBurstCodec(Sender, Table)
            .Modulate(frame, OfdmFmConstellation.Qam16, leadInSymbols: 0).Length);
        first.Should().BeGreaterThan(second);
    }

    [Fact]
    public void A_Change_Of_Geometry_Inside_A_Keyup_Goes_Out_As_A_Full_Burst()
    {
        // A follow-on frame is read against the channel of the frame before, on the same layout;
        // a frame on another layout has no such channel at the far end, so it must carry the
        // acquisition again. The geometry changes when the correspondent asks for one.
        var a = new OfdmFmModem("ofdm-fm:a", Sender with { AdaptiveRate = true }, _ => { },
            geometryTable: Table);
        var b = new OfdmFmModem("ofdm-fm:b", Receiver with { AdaptiveRate = true }, _ => { },
            geometryTable: Table);
        byte[] frame = Frame(40, 9);

        float[] opening = a.Modulate(frame, 300);
        a.Process(b.Modulate(frame, 300));                 // B asks A for the acquisition layout
        a.TransmittingOn.Should().Be(0);

        float[] changed = a.Modulate(frame, OfdmFmModem.HostLeadInWithinKeyupMs);
        float[] followOn = a.Modulate(frame, OfdmFmModem.HostLeadInWithinKeyupMs);

        changed.Length.Should().BeGreaterThan(followOn.Length, "the first frame on a new geometry acquires again");
        opening.Length.Should().BeGreaterThan(followOn.Length);
    }

    /// <summary>The frames of a keyup with a silence of <paramref name="gap"/> samples between
    /// each burst and the next, the way a host that drains its sound card between frames plays
    /// them.</summary>
    private static List<float[]> KeyupWithGaps(OfdmFmModem sender, byte[][] frames, Func<int, int> gap)
    {
        var pieces = new List<float[]>();
        List<float[]> bursts = Keyup(sender, frames);
        for (int i = 0; i < bursts.Count; i++)
        {
            if (i > 0)
            {
                pieces.Add(new float[gap(i)]);
            }

            pieces.Add(bursts[i]);
        }

        return pieces;
    }

    private static float[] WithNoise(IEnumerable<float[]> pieces, double fullBandSnrDb, int seed)
    {
        float[] all = [.. pieces.SelectMany(p => p)];
        double power = all.Sum(s => (double)s * s) / all.Length;
        double sigma = Math.Sqrt(power / Math.Pow(10, fullBandSnrDb / 10));
        var random = new Random(seed);
        for (int i = 0; i < all.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            all[i] += (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return all;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(250)]
    public void A_Keyup_Whose_Bursts_Have_Silence_Between_Them_Is_Still_Read(int gap)
    {
        // Measured on air, 2026-09-19: pdn-soundmodem drains its sound card after every frame and
        // the card pads the last period, so the frames of one keyup arrive with 30 to 35 ms of
        // silence between them, carrier up: two thirds of a symbol on the 8 kHz preset. These
        // gaps run from a sample to near the two-symbol allowance on this profile's 136-sample
        // symbol. The receiver reads the header where it expects it and, when nothing is there,
        // finds the burst by its symbols' cyclic prefixes within the allowance.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] frames = [Frame(60, 61), Frame(31, 62), Frame(90, 63), Frame(45, 64), Frame(120, 65), Frame(20, 66)];

        List<float[]> pieces = KeyupWithGaps(sender, frames, _ => gap);
        pieces.Add(new float[Small.SymbolSamples * 6]);

        Stream(receiver, pieces, block: 733);

        delivered.Should().HaveCount(frames.Length, "a gap of {0} samples between bursts", gap);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i], "frame {0} of the keyup", i);
        }
    }

    [Fact]
    public void Gaps_That_Differ_Between_Every_Burst_Of_A_Noisy_Keyup_Are_Each_Found()
    {
        // The gap a host leaves is not a constant: it is the modulate time of the next frame plus
        // whatever the card pads, so every one is different. Eight frames at QAM-64, a different
        // gap of up to a symbol and a half before each, and noise over the whole band.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender with
        {
            Constellation = OfdmFmConstellation.Qam64,
        }, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] frames = Enumerable.Range(0, 8).Select(i => Frame(80 + (i * 17), 70 + i)).ToArray();
        var random = new Random(77);

        List<float[]> pieces = KeyupWithGaps(sender, frames, _ => random.Next(0, Small.SymbolSamples * 3 / 2));
        pieces.Add(new float[Small.SymbolSamples * 6]);

        Stream(receiver, [WithNoise(pieces, fullBandSnrDb: 22, seed: 5)], block: 997);

        delivered.Should().HaveCount(frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            delivered[i].Should().Equal(frames[i], "frame {0} of the keyup", i);
        }
    }

    [Fact]
    public void A_Gap_Beyond_The_Allowance_Loses_The_Keyup_And_Not_The_Next_One()
    {
        // The allowance is a bound, and what lies beyond it is the trade the option makes: the
        // rest of that keyup, and nothing after it.
        var sender = new OfdmFmModem("ofdm-fm:tx", Sender, _ => { }, geometryTable: Table);
        var delivered = new List<byte[]>();
        var receiver = new OfdmFmModem("ofdm-fm:rx", Receiver, delivered.Add, geometryTable: Table);
        byte[][] first = [Frame(50, 81), Frame(70, 82), Frame(30, 83)];
        byte[][] second = [Frame(40, 91), Frame(80, 92)];
        int beyond = ((OfdmFmBurstCodec.FollowOnGapSymbols + 1) * Small.SymbolSamples) + (2 * Small.CyclicPrefix);

        List<float[]> pieces = KeyupWithGaps(sender, first, i => i == 1 ? beyond : 0);
        pieces.Add(new float[Small.SymbolSamples * 9]);
        pieces.AddRange(Keyup(sender, second));
        pieces.Add(new float[Small.SymbolSamples * 4]);

        Stream(receiver, pieces);

        delivered.Should().HaveCount(3);
        delivered[0].Should().Equal(first[0]);
        delivered[1].Should().Equal(second[0]);
        delivered[2].Should().Equal(second[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(37)]
    [InlineData(150)]
    [InlineData(250)]
    public void The_Search_Offers_Where_A_Follow_On_Burst_Starts_Among_Its_Candidates(int gap)
    {
        // Two bursts of one keyup with a silence between them, as a host that drains its sound
        // card between frames plays them: the second is expected where the first ended, and where
        // it actually starts is among the candidates, to the sample.
        var codec = new OfdmFmBurstCodec(Sender, Table);
        int symbol = Small.SymbolSamples;
        float[] previous = codec.Modulate(Frame(70, 98), OfdmFmConstellation.Qam16, leadInSymbols: 1);
        float[] burst = codec.ModulateFollowOn(Frame(60, 99), OfdmFmConstellation.Qam16);
        float[] audio = [.. previous, .. new float[gap], .. burst, .. new float[symbol * 4]];
        int expected = previous.Length;
        int actual = previous.Length + gap;

        codec.FollowOnStartCandidates(audio, expected).Should().Contain(c => Math.Abs(c - actual) <= 1);

        // Expected a few samples into the burst rather than before it, the other way a host can
        // be off: still found.
        codec.FollowOnStartCandidates(audio, actual + 5).Should().Contain(c => Math.Abs(c - actual) <= 1);
    }

    [Fact]
    public void A_Silence_Longer_Than_The_Allowance_Offers_Nothing()
    {
        var codec = new OfdmFmBurstCodec(Sender, Table);
        int symbol = Small.SymbolSamples;
        int gap = ((OfdmFmBurstCodec.FollowOnGapSymbols + 1) * symbol) + (2 * Small.CyclicPrefix);
        float[] previous = codec.Modulate(Frame(70, 98), OfdmFmConstellation.Qam16, leadInSymbols: 1);
        float[] burst = codec.ModulateFollowOn(Frame(60, 99), OfdmFmConstellation.Qam16);
        float[] audio = [.. previous, .. new float[gap], .. burst, .. new float[symbol * 4]];

        codec.FollowOnStartCandidates(audio, previous.Length).Should().BeEmpty();

        // And after the last burst of a keyup, silence alone.
        codec.FollowOnStartCandidates(audio, previous.Length + gap + burst.Length).Should().BeEmpty();
    }
}
