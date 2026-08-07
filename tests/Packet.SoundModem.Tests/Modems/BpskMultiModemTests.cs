using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Frequency-diversity BPSK: the coherent detector's narrow tracking loop only pulls in a few Hz
/// of carrier offset within a short preamble, so <see cref="BpskMultiModem"/> runs a bank of
/// stock branches at stepped centres. These tests show the bank decoding off-frequency signals a
/// single centred coherent modem misses, deduping to one frame per transmission, and reporting
/// where the station actually was rather than which branch happened to copy it.
/// </summary>
public class BpskMultiModemTests
{
    private const int SampleRate = 12000;

    private static readonly byte[] Frame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] OffTune(double offsetHz, int preambleBits = 45)
    {
        // ~150 ms preamble (45 symbols at 300 baud) - realistic NinoTNC TXDELAY - at a carrier
        // offset from the 1500 Hz channel centre, then padded with lead-in/out silence.
        byte[] wire = Il2pCodec.Encode(Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, carrierFrequency: 1500 + offsetHz).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(-12)]
    [InlineData(12)]
    [InlineData(24)]
    public void An_Off_Frequency_Signal_A_Single_Coherent_Modem_Misses_Decodes_On_The_Bank(int offsetHz)
    {
        float[] audio = OffTune(offsetHz);

        var single = new List<byte[]>();
        BpskModem.Bpsk300(SampleRate, single.Add, crc: true, PskDetector.Coherent).Process(audio);
        single.Should().BeEmpty("a single centred coherent modem cannot acquire {0} Hz in a short preamble", offsetHz);

        var banked = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, banked.Add, crc: true, PskDetector.Coherent).Process(audio);
        banked.Should().ContainSingle("the bank has a branch within tolerance of {0} Hz", offsetHz)
            .Which.Should().Equal(Frame);
    }

    [Fact]
    public void An_On_Frequency_Signal_Is_Emitted_Exactly_Once()
    {
        var frames = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, frames.Add).Process(OffTune(0));

        frames.Should().ContainSingle("branches decode the same transmission but dedupe to one")
            .Which.Should().Equal(Frame);
    }

    [Fact]
    public void The_Coherent_Path_Reports_Its_Loop_Frequency_As_The_Residual()
    {
        // Coherent has no differential product to measure; the Costas NCO's own frequency
        // correction is the residual, and branch + that is the station's offset.
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { }, crc: true, PskDetector.Coherent);
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(24));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(24, 3, "the loop settles on the carrier");
    }

    /// <summary>
    /// The acceptance test for issue #202: on a swept-CFO grid the reported offset tracks the
    /// injected offset instead of snapping to a comb position. Every offset sits exactly half a
    /// step (3.75 Hz) from the nearest branch of the default 4 pairs × 7.5 Hz comb, so with a
    /// 2 Hz tolerance no branch label - the old behaviour - can pass any of them.
    /// </summary>
    [Theory]
    [InlineData(-26.25)]
    [InlineData(-18.75)]
    [InlineData(-11.25)]
    [InlineData(-3.75)]
    [InlineData(3.75)]
    [InlineData(11.25)]
    [InlineData(18.75)]
    [InlineData(26.25)]
    public void The_Reported_Offset_Is_A_Measurement_Of_The_Signal(double offsetHz)
    {
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(offsetHz));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(
                offsetHz, 2, "the bank reports where the station actually was, not which branch won");
    }

    [Fact]
    public void A_Centred_Signal_Does_Not_Read_As_The_Most_Negative_Branch()
    {
        // The bug: branches run in array order and several copy the same frame, so index 0 -
        // −30 Hz on the default 4 pairs × 7.5 Hz comb - won by iteration order and 82 % of a
        // live station's frames read exactly −30 Hz. A signal on frequency must read ~0.
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(0));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(0, 2, "the station was on frequency");
    }

    [Fact]
    public void A_Single_Branch_Measures_The_Offset_Too()
    {
        // Not a bank property: one BpskModem measures its own residual, which is what makes
        // branch + residual meaningful however far out the winning branch sits.
        var qualities = new List<FrameQuality>();
        var modem = BpskModem.Bpsk300(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(22));

        // ±3 rather than the bank's sub-Hz: the matched filter is centred on the channel, so
        // a signal this far off has its spectrum asymmetrically truncated, which makes the
        // baseband pulse complex and biases the reading away from centre by ~10 % of the
        // offset. A bank never reads through that skew - its winning branch sits within half
        // a step (±3.75 Hz), where the bias is under 0.1 Hz, which is what
        // The_Reported_Offset_Is_A_Measurement_Of_The_Signal pins. What this guards is the
        // lone modem reporting a measurement at all, not a comb position.
        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(22, 3);
    }

    [Fact]
    public void The_Step_And_Pairs_Tuneables_Set_The_Coherent_Coverage()
    {
        // The step/pairs tuneables exist for the COHERENT path, whose narrow loop only acquires a
        // carrier near a branch. A +26 Hz signal is outside a narrow bank (1 pair × 8 Hz = ±8 Hz)
        // but inside a wide one (4 pairs × 10 Hz = ±40 Hz). (Differential tolerates ±baud/4 on a
        // single branch, so coverage is a coherent-only concern - hence the explicit detector.)
        float[] audio = OffTune(26);

        var narrow = new List<byte[]>();
        new BpskMultiModem(SampleRate, narrow.Add, offsetPairs: 1, offsetHz: 8,
            detector: PskDetector.Coherent).Process(audio);
        narrow.Should().BeEmpty("±8 Hz coherent coverage cannot reach +26 Hz");

        var wide = new List<byte[]>();
        new BpskMultiModem(SampleRate, wide.Add, offsetPairs: 4, offsetHz: 10,
            detector: PskDetector.Coherent).Process(audio);
        wide.Should().ContainSingle("±40 Hz coherent coverage reaches +26 Hz").Which.Should().Equal(Frame);
    }

    [Fact]
    public void Identical_Content_Later_Is_Not_Deduplicated()
    {
        float[] one = OffTune(0);
        // Same frame content twice, 4 s apart - both must be delivered (the dedupe window is short).
        var audio = new float[one.Length + 4 * SampleRate + one.Length];
        one.CopyTo(audio, 0);
        one.CopyTo(audio, one.Length + 4 * SampleRate);

        var frames = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, frames.Add, offsetPairs: 2).Process(audio);

        frames.Should().HaveCount(2);
    }

    /// <summary>A burst whose sync survives and whose payload is beyond Reed-Solomon - a
    /// genuine BPSK transmission the receiver cannot bring back, which is the burst-verdict
    /// instrument's DAMAGED-BPSK class made synthetic.</summary>
    private static float[] DamagedBurst(double offsetHz = 0)
    {
        byte[] wire = Il2pCodec.Encode(Frame, appendCrc: true);
        for (int i = Il2pCodec.HeaderWireLength; i < wire.Length; i += 2)
        {
            wire[i] ^= 0xA5;
        }

        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits: 45, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, carrierFrequency: 1500 + offsetHz).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Fact]
    public void A_Damaged_Burst_Asserts_Dcd_And_Ticks_The_Sync_Failure_Counter()
    {
        // The burst-verdict instrument's calibration case (DcdBurstTracker): a genuine BPSK
        // transmission too damaged to decode still asserts DCD - transition-timing coherence is
        // a property of the modulation, not of the payload surviving - and the only trace of
        // the lost frame is the bank's sync-found-but-RS-failed counter.
        float[] audio = DamagedBurst();
        var frames = new List<byte[]>();
        var monitored = new List<byte[]>();
        var bank = BpskMultiModem.Bpsk300(SampleRate, frames.Add);
        bank.FrameDecoded += (frame, _) => monitored.Add(frame);
        bank.RsFailures.Should().Be(0, "a fresh bank has failed nothing");

        bool dcdSeen = false;
        int block = SampleRate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            bank.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            dcdSeen |= bank.CarrierDetect;
        }

        dcdSeen.Should().BeTrue("BPSK symbol timing asserts DCD whether or not the frame decodes");
        frames.Should().BeEmpty();
        monitored.Should().BeEmpty("the payload is beyond Reed-Solomon on every branch");
        bank.RsFailures.Should().BeGreaterThan(0,
            "sync was found and the frame refused - the damaged-bpsk verdict");
    }

    [Fact]
    public void The_Bank_Measures_The_Carrier_Offset_Of_A_Burst_It_Cannot_Decode()
    {
        // Decoded frames carry their offset in FrameQuality; the bursts that never decode are
        // the ones the live CarrierOffsetHz property exists for. Half a branch step off centre,
        // like the swept-grid test, so a comb label cannot pass it.
        float[] audio = DamagedBurst(offsetHz: 11.25);
        var bank = BpskMultiModem.Bpsk300(SampleRate, _ => { });
        bank.CarrierOffsetHz.Should().BeNull("silence measures nothing");

        double? during = null;
        int block = SampleRate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            bank.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            if (bank.CarrierDetect)
            {
                during = bank.CarrierOffsetHz ?? during;
            }
        }

        during.Should().NotBeNull("the burst was there to measure while DCD held");
        during!.Value.Should().BeApproximately(11.25, 3,
            "the bank reports where the station actually was, decodable or not");
    }

    [Fact]
    public void Transmit_Uses_The_Centre_And_Round_Trips_Through_A_Single_Modem()
    {
        // A bank TX is the centre branch only, so a plain centred modem must decode it.
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { });
        float[] audio = modem.Modulate(Frame, txDelayMilliseconds: 300);
        var padded = new float[audio.Length + 2 * (SampleRate / 5)]; // lead-in + flush tail
        audio.CopyTo(padded, SampleRate / 5);

        var received = new List<byte[]>();
        BpskModem.Bpsk300(SampleRate, received.Add).Process(padded);
        received.Should().ContainSingle().Which.Should().Equal(Frame);
    }
}
