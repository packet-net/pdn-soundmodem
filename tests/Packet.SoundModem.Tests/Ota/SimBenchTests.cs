using Packet.SoundModem.Ota;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The generalized simulation harness (<c>sm-ota sim</c>): the mode-generic generate → channel →
/// score loop that drives the FreeDV datac OFDM waterfalls, plus the raw-datac packet probe that
/// backs the published-FreeDV cross-check. These guard the harness itself - that a burst round-trips
/// through calibrated AWGN, that a very low SNR fails it, that the loop is mode-generic (not
/// FreeDV-only), and that the Good channel profile stays the CCIR 0.5&#160;ms/0.1&#160;Hz pairing.
/// </summary>
public class SimBenchTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------------------------------
    // Channel profiles and parsing.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Good_Profile_Is_The_Ccir_Half_Ms_Tenth_Hz_Pairing()
    {
        WattersonPath[] good = SimChannel.Good;
        good.Should().HaveCount(2, "CCIR Good is two equal-power Rayleigh paths");
        good.Should().OnlyContain(p => p.Fading, "both paths fade");
        good.Should().OnlyContain(p => p.DopplerSpreadHz == 0.1, "Good is a 0.1 Hz two-sigma fade");
        good.Select(p => p.DelayMs).Should().BeEquivalentTo([0.0, 0.5], "0.5 ms differential delay");
    }

    [Fact]
    public void Poor_Is_The_Mask_Suites_Own_Rig_Two_Ms_One_Hz()
    {
        // The sim Poor must be the exact WattersonChannel.Poor geometry the MS110D masks gate
        // against - an OFDM Poor waterfall and an MS110D Poor waterfall must be one rig.
        WattersonPath[] poor = SimChannel.Paths(SimChannelKind.Poor);
        poor.Should().BeEquivalentTo(WattersonChannel.Poor, "sim Poor is the mask suite's own Poor");
        poor.Should().HaveCount(2).And.OnlyContain(p => p.Fading && p.DopplerSpreadHz == 1);
        poor.Select(p => p.DelayMs).Should().BeEquivalentTo([0.0, 2.0], "2 ms differential delay");
        SimChannel.Paths(SimChannelKind.Awgn).Should().BeEmpty("AWGN is the ideal direct path");
    }

    [Fact]
    public void Parses_Channel_Names()
    {
        SimChannel.Parse("awgn").Should().Be(SimChannelKind.Awgn);
        SimChannel.Parse("Good").Should().Be(SimChannelKind.Good);
        SimChannel.Parse("poor").Should().Be(SimChannelKind.Poor);
        SimChannel.Parse("p").Should().Be(SimChannelKind.Poor);
    }

    // ---------------------------------------------------------------------------------------
    // The frame layer - the mode-generic IModem path.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Frame_Layer_Round_Trips_Datac0_Through_Clean_Awgn()
    {
        var modem = new SimModem("freedv-datac0", rate: 8000);
        byte[] frame = SimModem.Frame(60, seed: 7);
        float[] burst = modem.RenderBurst(frame);
        float[] rx = SimChannel.Apply(burst, modem.Rate, SimChannelKind.Awgn, snrDb: 20, seed: 7);

        SimDecode decode = modem.Decode(rx, frame);
        decode.Matched.Should().BeTrue("a 20 dB AWGN datac0 frame must decode bit-exact");
        decode.FramesDecoded.Should().Be(1);
    }

    [Fact]
    public void Frame_Layer_Fails_At_Deep_Negative_Snr()
    {
        var modem = new SimModem("freedv-datac0", rate: 8000);
        byte[] frame = SimModem.Frame(60, seed: 3);
        float[] burst = modem.RenderBurst(frame);
        float[] rx = SimChannel.Apply(burst, modem.Rate, SimChannelKind.Awgn, snrDb: -20, seed: 3);

        modem.Decode(rx, frame).Matched.Should().BeFalse("−20 dB is far below any datac threshold");
    }

    [Fact]
    public void Frame_Layer_Is_Mode_Generic_Not_Freedv_Only()
    {
        // The generalization claim: the same loop drives a non-OFDM catalogue mode. A clean-AWGN
        // FSK9600 IL2P frame must round-trip through exactly the same generate → channel → score.
        var modem = new SimModem("fsk9600-il2p", rate: 48000);
        byte[] frame = SimModem.Frame(32, seed: 11);
        float[] burst = modem.RenderBurst(frame);
        float[] rx = SimChannel.Apply(burst, modem.Rate, SimChannelKind.Awgn, snrDb: 25, seed: 11);

        modem.Decode(rx, frame).Matched.Should().BeTrue("a 25 dB AWGN FSK9600 frame must decode");
    }

    [Fact]
    public void Frame_Payload_Is_Deterministic_Per_Seed()
    {
        SimModem.Frame(40, 5).Should().Equal(SimModem.Frame(40, 5));
        SimModem.Frame(40, 5).Should().NotEqual(SimModem.Frame(40, 6));
    }

    // ---------------------------------------------------------------------------------------
    // The packet layer - the FreeDV cross-check probe.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Packet_Probe_Decodes_Clean_And_Fails_In_Noise()
    {
        var probe = new DatacPacketProbe("freedv-datac3");
        probe.Rate.Should().Be(8000, "the probe runs at codec2's native rate");

        PacketResult clean = probe.Run(seed: 2, SimChannelKind.Awgn, snrDb: 10);
        clean.CrcOk.Should().BeTrue("a 10 dB AWGN datac3 packet must pass CRC");
        clean.BitErrors.Should().Be(0, "and be bit-exact");

        PacketResult noise = probe.Run(seed: 2, SimChannelKind.Awgn, snrDb: -20);
        noise.CrcOk.Should().BeFalse("−20 dB is far below datac3's threshold");
        noise.BitErrors.Should().Be(noise.PayloadBits, "a lost packet counts every payload bit wrong");
    }

    // ---------------------------------------------------------------------------------------
    // The runner and its statistics.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Run_Point_Tallies_Full_Success_On_A_Clean_Channel()
    {
        SimPointResult r = SimBench.RunPoint(
            "freedv-datac3", rate: null, SimLayer.Packet, SimChannelKind.Awgn, snrDb: 10,
            bursts: 8, frameBytes: 60, firstSeed: 1, workers: 4);

        output.WriteLine($"datac3 @ 10 dB: {r.Successes}/{r.Trials}, FER {r.Fer}, coded {r.CodedBer}");
        r.Successes.Should().Be(r.Trials, "every clean-channel packet decodes");
        r.Fer.Should().Be(0);
        r.CodedBer.Should().Be(0);
    }

    [Fact]
    public void Absolute_Level_Scan_Is_Invariant_At_A_Healthy_Snr()
    {
        // The WN2-analogue probe: at a fixed, healthy SNR, scaling the absolute input level down
        // must not change the decode - OFDM's pilot-based normalisation should make it level-blind
        // (unlike the un-normalised MS110D WN2 front end). A −20 dB quieter signal at the same SNR
        // must still decode every packet.
        SimPointResult nominal = SimBench.RunPoint(
            "freedv-datac3", null, SimLayer.Packet, SimChannelKind.Awgn, snrDb: 8,
            bursts: 8, frameBytes: 60, firstSeed: 1, workers: 4, levelDb: 0);
        SimPointResult quiet = SimBench.RunPoint(
            "freedv-datac3", null, SimLayer.Packet, SimChannelKind.Awgn, snrDb: 8,
            bursts: 8, frameBytes: 60, firstSeed: 1, workers: 4, levelDb: -20);

        output.WriteLine($"nominal {nominal.Successes}/{nominal.Trials}, −20 dB {quiet.Successes}/{quiet.Trials}");
        nominal.Successes.Should().Be(nominal.Trials, "a healthy-SNR packet decodes at nominal level");
        quiet.Successes.Should().Be(nominal.Successes, "and identically 20 dB quieter - level-invariant");
        quiet.LevelDb.Should().Be(-20);
    }

    [Fact]
    public void Threshold_Interpolates_The_Half_Success_Crossing()
    {
        var curve = new List<SimPointResult>
        {
            Point(-6, 0, 10), Point(-4, 2, 10), Point(-2, 8, 10), Point(0, 10, 10),
        };
        // crosses 0.5 between −4 (0.2) and −2 (0.8): −4 + (0.5−0.2)/(0.8−0.2)·2 = −3.0
        SimBench.Threshold(curve, 0.5).Should().BeApproximately(-3.0, 1e-9);
        // 0.99 crosses between −2 (0.8) and 0 (1.0): −2 + (0.99−0.8)/(1.0−0.8)·2 = −0.1
        SimBench.Threshold(curve, 0.99).Should().BeApproximately(-0.1, 1e-9);

        static SimPointResult Point(double snr, int ok, int n)
            => new("m", SimChannelKind.Awgn, SimLayer.Packet, snr, n, ok, 0, n, 0);
    }

    [Fact]
    public void Wilson_Interval_Brackets_The_Rate_And_Handles_The_Tails()
    {
        (double lo, double hi) = SimStats.Wilson(50, 100);
        lo.Should().BeLessThan(0.5);
        hi.Should().BeGreaterThan(0.5);

        SimStats.Wilson(100, 100).Lo.Should().BeGreaterThan(0.95, "100/100 is a tight lower bound, not 1.0");
        SimStats.Wilson(0, 100).Hi.Should().BeLessThan(0.05, "0/100 caps the upper bound below 5%");
        SimStats.Wilson(0, 0).Should().Be((0.0, 1.0));
    }
}
