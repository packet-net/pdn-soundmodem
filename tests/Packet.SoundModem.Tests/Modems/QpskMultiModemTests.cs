using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Frequency-diversity QPSK (issue #326): <see cref="QpskMultiModem"/> runs a bank of stock
/// <see cref="QpskModem"/> branches at stepped centres, the arrangement the BPSK modes and
/// afsk300 already had. These tests show the bank decoding off-frequency signals a single
/// modem misses, deduping to one frame per transmission, and reporting where the station
/// actually was rather than which branch happened to copy it - the #202 discipline.
/// </summary>
public class QpskMultiModemTests
{
    private const int SampleRate = 12000;

    private static readonly byte[] Frame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] OffTune(double offsetHz, int preambleBits = 90, int baud = 300)
    {
        // ~150 ms preamble (45 symbols at 300 baud) - a realistic NinoTNC TXDELAY - at a carrier
        // offset from the 1500 Hz channel centre, then padded with lead-in/out silence.
        byte[] wire = Il2pCodec.Encode(Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        double rollOff = baud == 300 ? 0.20 : QpskModulator.DefaultRollOff;
        float[] audio = new QpskModulator(SampleRate, baud, 1500 + offsetHz, rollOff).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void An_Off_Frequency_Signal_A_Single_Coherent_Modem_Misses_Decodes_On_The_Bank(int offsetHz)
    {
        float[] audio = OffTune(offsetHz);

        var single = new List<byte[]>();
        QpskModem.Qpsk600(SampleRate, single.Add, crc: true, detector: PskDetector.Coherent).Process(audio);
        single.Should().BeEmpty("a single centred coherent modem cannot acquire {0} Hz in a short preamble", offsetHz);

        var banked = new List<byte[]>();
        QpskMultiModem.Qpsk600(SampleRate, banked.Add, crc: true, PskDetector.Coherent).Process(audio);
        banked.Should().ContainSingle("the bank has a branch within tolerance of {0} Hz", offsetHz)
            .Which.Should().Equal(Frame);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-15)]
    [InlineData(15)]
    [InlineData(30)]
    public void The_Differential_Bank_Decodes_Across_Its_Whole_Comb(int offsetHz)
    {
        // The single-modem CFO wall this mode carried (issues #11 and #116: measured +-5 Hz, and
        // 3 % at 30 Hz on the campaign ladder) is what the bank exists to remove.
        var banked = new List<byte[]>();
        QpskMultiModem.Qpsk600(SampleRate, banked.Add).Process(OffTune(offsetHz));
        banked.Should().ContainSingle().Which.Should().Equal(Frame);
    }

    [Fact]
    public void An_On_Frequency_Signal_Is_Emitted_Exactly_Once()
    {
        var frames = new List<byte[]>();
        QpskMultiModem.Qpsk600(SampleRate, frames.Add).Process(OffTune(0));

        frames.Should().ContainSingle("branches decode the same transmission but dedupe to one")
            .Which.Should().Equal(Frame);
    }

    /// <summary>
    /// The #202 acceptance shape: on a swept-CFO grid the reported offset tracks the injected
    /// offset instead of snapping to a comb position. Every offset sits exactly half a step
    /// (3.75 Hz) from the nearest branch of the default 4 pairs x 7.5 Hz comb, so with a 2 Hz
    /// tolerance no branch label can pass any of them.
    /// </summary>
    [Theory]
    [InlineData(-26.25)]
    [InlineData(-11.25)]
    [InlineData(-3.75)]
    [InlineData(3.75)]
    [InlineData(11.25)]
    [InlineData(26.25)]
    public void The_Reported_Offset_Is_A_Measurement_Of_The_Signal(double offsetHz)
    {
        var qualities = new List<FrameQuality>();
        var modem = QpskMultiModem.Qpsk600(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(offsetHz));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(
                offsetHz, 2, "the bank reports where the station actually was, not which branch won");
    }

    [Fact]
    public void A_Centred_Signal_Does_Not_Read_As_The_Most_Negative_Branch()
    {
        var qualities = new List<FrameQuality>();
        var modem = QpskMultiModem.Qpsk600(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(0));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(0, 2, "the station was on frequency");
        qualities[0].Mode.Should().Be("qpsk600-il2pc");
    }

    [Fact]
    public void The_Quality_Names_The_Mode_Not_The_Receivers_Construction()
    {
        // FrameQuality.Mode is an identity: consumers correlate it against their configured
        // mode, so the same transmission must report the same string on every receiver that
        // hears it, however many branches each happens to be built with. The bank's width is
        // static receiver configuration and stays on IModem.Mode, where the daemon's own logs
        // and waterfall read it (issue #343).
        var qualities = new List<FrameQuality>();
        var modem = QpskMultiModem.Qpsk600(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(0));

        qualities.Should().ContainSingle().Which.Mode.Should().Be("qpsk600-il2pc");
        modem.Mode.Should().Be(
            "qpsk600-il2pc-multi9", "the receiver describing itself keeps its construction");
    }

    [Fact]
    public void Identical_Content_Later_Is_Not_Deduplicated()
    {
        float[] one = OffTune(0);
        var audio = new float[one.Length + 4 * SampleRate + one.Length];
        one.CopyTo(audio, 0);
        one.CopyTo(audio, one.Length + 4 * SampleRate);

        var frames = new List<byte[]>();
        QpskMultiModem.Qpsk600(SampleRate, frames.Add, offsetPairs: 2).Process(audio);

        frames.Should().HaveCount(2);
    }

    /// <summary>A burst whose sync survives and whose payload is beyond Reed-Solomon - a
    /// genuine transmission the receiver cannot bring back.</summary>
    private static float[] DamagedBurst(double offsetHz = 0)
    {
        byte[] wire = Il2pCodec.Encode(Frame, appendCrc: true);
        for (int i = Il2pCodec.HeaderWireLength; i < wire.Length; i += 2)
        {
            wire[i] ^= 0xA5;
        }

        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits: 90, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new QpskModulator(SampleRate, 300, 1500 + offsetHz, 0.20).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    /// <summary>
    /// The BPSK twin's calibration case on the QPSK bank: a genuine transmission too damaged
    /// to decode still asserts DCD, and the only trace of the lost frame is the counter. This
    /// test was written without the DCD assertion while the QPSK path scored DCD on the
    /// product's quadrant transitions, which flicker at every phase-change null and never
    /// asserted on a noise-free burst (issue #329); <see cref="QpskDecisionDcd"/> scores the
    /// decisions instead, and a clean burst asserts whether or not its payload survives.
    /// </summary>
    [Fact]
    public void A_Damaged_Burst_Asserts_Dcd_And_Ticks_The_Sync_Failure_Counter()
    {
        float[] audio = DamagedBurst();
        var frames = new List<byte[]>();
        var monitored = new List<byte[]>();
        var bank = QpskMultiModem.Qpsk600(SampleRate, frames.Add);
        bank.FrameDecoded += (frame, _) => monitored.Add(frame);
        bank.RsFailures.Should().Be(0, "a fresh bank has failed nothing");

        bool dcdSeen = false;
        int block = SampleRate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            bank.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            dcdSeen |= bank.CarrierDetect;
        }

        dcdSeen.Should().BeTrue("QPSK decision quality asserts DCD whether or not the frame decodes");
        frames.Should().BeEmpty();
        monitored.Should().BeEmpty("the payload is beyond Reed-Solomon on every branch");
        bank.RsFailures.Should().BeGreaterThan(0,
            "sync was found and the frame refused - the damaged-burst verdict");
    }

    [Fact]
    public void The_Bank_Measures_The_Carrier_Offset_Of_A_Burst_It_Cannot_Decode()
    {
        // Decoded frames carry their offset in FrameQuality; the bursts that never decode are
        // the ones the live CarrierOffsetHz property exists for. Half a branch step off centre,
        // like the swept-grid test, so a comb label cannot pass it. Polled while DCD holds, as
        // the BPSK twin polls it (the QPSK DCD could not be trusted for this until #329).
        float[] audio = DamagedBurst(offsetHz: 11.25);
        var bank = QpskMultiModem.Qpsk600(SampleRate, _ => { });
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
        var modem = QpskMultiModem.Qpsk600(SampleRate, _ => { });
        float[] audio = modem.Modulate(Frame, txDelayMilliseconds: 300);
        var padded = new float[audio.Length + 2 * (SampleRate / 5)];
        audio.CopyTo(padded, SampleRate / 5);

        var received = new List<byte[]>();
        QpskModem.Qpsk600(SampleRate, received.Add).Process(padded);
        received.Should().ContainSingle().Which.Should().Equal(Frame);
    }

    [Fact]
    public void Each_Mode_Factory_Builds_Its_Own_Branches()
    {
        QpskMultiModem.Qpsk600(SampleRate, _ => { }).Mode.Should().Be("qpsk600-il2pc-multi9");
        QpskMultiModem.Qpsk2400(SampleRate, _ => { }).Mode.Should().Be("qpsk2400-il2pc-multi9");
        // FM: the audio tones arrive on frequency whatever the RF offset, and the threefold
        // upsampled chain makes every branch dear - a one-branch bank by default.
        QpskMultiModem.Qpsk3600(SampleRate, _ => { }).Mode.Should().Be("qpsk3600-il2pc-multi1");
        QpskMultiModem.Qpsk3600(SampleRate, _ => { }, offsetPairs: 1).Mode.Should().Be("qpsk3600-il2pc-multi3");
    }

    [Fact]
    public void An_Unknown_Symbol_Rate_Is_Refused()
    {
        Action act = () => new QpskMultiModem(SampleRate, _ => { }, baud: 600);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_Ensemble_Doubles_The_Branches_And_Still_Delivers_Once()
    {
        var ensemble = new QpskMultiModem(SampleRate, _ => { }, secondDetector: PskDetector.Coherent);
        ensemble.Mode.Should().Be("qpsk600-il2pc-multi18");

        var frames = new List<byte[]>();
        var rx = new QpskMultiModem(SampleRate, frames.Add, secondDetector: PskDetector.Coherent);
        rx.Process(OffTune(0));
        frames.Should().ContainSingle().Which.Should().Equal(Frame);
    }

    [Fact]
    public void The_2400_Bank_Decodes_A_Station_Well_Off_Frequency()
    {
        // 4 pairs x 30 Hz at 1200 Bd: +-120 Hz of comb, the figure bpsk1200 rides the undisciplined
        // RSP1 with (issue #116).
        var frames = new List<byte[]>();
        QpskMultiModem.Qpsk2400(SampleRate, frames.Add).Process(OffTune(100, preambleBits: 360, baud: 1200));
        frames.Should().ContainSingle().Which.Should().Equal(Frame);
    }
}
