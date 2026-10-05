using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// Several frames in one MS110D burst (<see cref="IFramePackingModem"/>): one preamble, the
/// IL2P+CRC frames back to back, one EOM. The receiver is unchanged, so what these prove is that
/// it already reads a packed burst - every frame, in order, byte for byte.
/// </summary>
public class Ms110dPackingTests(ITestOutputHelper output)
{
    /// <summary>AX.25 UI frames of the given payload sizes, random bodies from a fixed seed.</summary>
    private static List<byte[]> UiFrames(int seed, params int[] payloads)
    {
        byte[] header = [0x9A, 0x86, 0x82, 0xA6, 0xA8, 0x40, 0x60, 0x8E, 0x84, 0x6E, 0xA4, 0x88, 0x8E, 0x61, 0x03, 0xF0];
        var random = new Random(seed);
        var frames = new List<byte[]>();
        foreach (int payload in payloads)
        {
            var frame = new byte[header.Length + payload];
            header.CopyTo(frame, 0);
            random.NextBytes(frame.AsSpan(header.Length));
            frames.Add(frame);
        }

        return frames;
    }

    private static Ms110dModem Modem(int sampleRate, int wn, Action<byte[]> sink, double maxBurstSeconds = 120) =>
        new(sampleRate, sink, new Ms110dTxSettings { WaveformNumber = wn })
        {
            Packing = new FramePacking(TimeSpan.FromSeconds(maxBurstSeconds), TimeSpan.Zero),
        };

    [Fact]
    public void A_Packed_Burst_Through_An_Awgn_Channel_Yields_Every_Frame_In_Order()
    {
        // The mailcast shape: WN4, 960-byte payloads, plus short and odd sizes so the frame
        // boundaries fall in different places against the interleaver blocks.
        List<byte[]> frames = UiFrames(11, 960, 37, 960, 1, 200);
        Ms110dModem tx = Modem(9600, 4, _ => { });
        tx.FramesPerBurst(frames).Should().Be(frames.Count, "a two-minute limit holds all of them");

        float[] burst = tx.ModulateFrames(frames, txDelayMilliseconds: 0);
        float[] onAir = new WattersonChannel(9600, seed: 21).Apply(
            burst, snrDb: 15, leadInSamples: 2000, leadOutSamples: 6000);

        var received = new List<byte[]>();
        var rx = new Ms110dModem(9600, received.Add);
        rx.Process(onAir);

        received.Should().HaveCount(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            received[i].Should().Equal(frames[i], $"frame {i} must arrive whole and in its place");
        }
    }

    [Fact]
    public void A_Packed_Burst_Is_One_Burst_Not_Several_Glued_Together()
    {
        List<byte[]> frames = UiFrames(12, 200, 200, 200, 200);
        Ms110dModem tx = Modem(9600, 6, _ => { });
        float[] packed = tx.ModulateFrames(frames, 0);
        int separate = frames.Sum(f => tx.Modulate(f, 0).Length);

        packed.Length.Should().BeLessThan(separate,
            "four frames behind one preamble must take less air than four preambles");

        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.Process(new float[1000]);
        demod.Process(packed);
        demod.Process(new float[6000]);
        bursts.Should().ContainSingle().Which.Reason.Should().Be(Ms110dBurstEndReason.Eom);
    }

    [Fact]
    public void A_Packed_Burst_Round_Trips_At_The_Daemons_48_Kilohertz_Rate()
    {
        List<byte[]> frames = UiFrames(13, 120, 300, 64);
        Ms110dModem tx = Modem(48000, 6, _ => { });
        var received = new List<byte[]>();
        var rx = new Ms110dModem(48000, received.Add);
        rx.Process(new float[4800]);
        rx.Process(tx.ModulateFrames(frames, 50));
        rx.Process(new float[24000]);

        received.Should().HaveCount(3);
        received.Zip(frames).Should().OnlyContain(pair => pair.First.SequenceEqual(pair.Second));
    }

    [Fact]
    public void The_Count_Respects_The_Burst_Limit()
    {
        // WN4 carries 1200 bps, so 960 bytes is about 6.5 s of air: a 20 s limit fits two
        // with the preamble, never three.
        List<byte[]> frames = UiFrames(14, 960, 960, 960, 960);
        Ms110dModem tx = Modem(9600, 4, _ => { }, maxBurstSeconds: 20);
        int fit = tx.FramesPerBurst(frames);

        fit.Should().BeInRange(1, 3);
        (tx.ModulateFrames(frames.Take(fit).ToList(), 0).Length / 9600.0).Should().BeLessThanOrEqualTo(20.5);
        (tx.ModulateFrames(frames.Take(fit + 1).ToList(), 0).Length / 9600.0).Should().BeGreaterThan(20);
    }

    [Fact]
    public void A_First_Frame_Longer_Than_The_Limit_Still_Counts_As_One()
    {
        List<byte[]> frames = UiFrames(15, 960, 10);
        Modem(9600, 4, _ => { }, maxBurstSeconds: 1).FramesPerBurst(frames).Should().Be(1);
    }

    [Fact]
    public void A_Frame_Il2p_Cannot_Carry_Ends_The_Count_Before_It()
    {
        List<byte[]> frames = UiFrames(16, 100, 100, 1100, 100);
        Ms110dModem tx = Modem(9600, 6, _ => { });
        Assert.Throws<ArgumentException>(() => tx.Modulate(frames[2], 0));

        tx.FramesPerBurst(frames).Should().Be(2);
        tx.FramesPerBurst(frames.Skip(2).ToList()).Should().Be(1,
            "a refusable frame at the head is answered 1 so the channel refuses it on its own");
    }

    [Fact]
    public void Packing_Off_Counts_One_Frame_Per_Burst()
    {
        var modem = new Ms110dModem(9600, _ => { });
        modem.Packing.Should().BeNull();
        modem.FramesPerBurst(UiFrames(17, 10, 10, 10)).Should().Be(1);
    }

    [Fact]
    public void A_Moved_Modem_Forwards_Packing_To_The_One_It_Wraps()
    {
        IModem moved = ModemCatalog.Create("ms110d-wn6", 48000, _ => { }, new ModemOptions(CentreFrequencyHz: 2000));
        moved.Should().BeOfType<FrequencyShiftedModem>();
        var packer = (IFramePackingModem)moved;
        packer.Packing = new FramePacking(TimeSpan.FromSeconds(30), TimeSpan.Zero);

        ((IFramePackingModem)((FrequencyShiftedModem)moved).Inner).Packing.Should().Be(packer.Packing);
        packer.FramesPerBurst(UiFrames(18, 50, 50)).Should().Be(2);

        IModem afsk = FrequencyShiftedModem.Wrap(new Afsk1200Modem(48000, _ => { }), 48000, 1900, 1700);
        Action refused = () => ((IFramePackingModem)afsk).Packing = new FramePacking(TimeSpan.FromSeconds(1), TimeSpan.Zero);
        refused.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sixty_Mailcast_Frames_At_Wn4_Take_Less_Air_Packed()
    {
        // The figure the mailcast design is sized on, from sample counts: 60 frames of 960 bytes
        // at WN4, one burst each against bursts of up to a minute. TXDELAY left out of both.
        List<byte[]> frames = UiFrames(19, Enumerable.Repeat(960, 60).ToArray());
        Ms110dModem tx = Modem(9600, 4, _ => { }, maxBurstSeconds: 60);

        long unpacked = 0;
        foreach (byte[] frame in frames)
        {
            unpacked += tx.Modulate(frame, 0).Length;
        }

        long packed = 0;
        int bursts = 0;
        for (int at = 0; at < frames.Count; bursts++)
        {
            List<byte[]> rest = frames.Skip(at).ToList();
            int fit = tx.FramesPerBurst(rest);
            packed += tx.ModulateFrames(rest.Take(fit).ToList(), 0).Length;
            at += fit;
        }

        output.WriteLine(
            $"60 x 960 B at WN4: unpacked {unpacked / 9600.0:F1} s in 60 bursts, "
            + $"packed {packed / 9600.0:F1} s in {bursts} bursts, saving {(unpacked - packed) / 9600.0:F1} s");
        packed.Should().BeLessThan(unpacked);
        bursts.Should().BeLessThan(60);
    }
}
