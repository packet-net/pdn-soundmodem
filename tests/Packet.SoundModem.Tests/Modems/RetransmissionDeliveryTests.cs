using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Issue #342: a stop-and-wait ARQ retransmission is byte-identical to the frame it retries,
/// and identical repeats are AX.25's ordinary repair grammar (SABM trains, timer-recovery
/// polls, RR responses to repeated polls). The diversity banks' content dedupe exists to merge
/// the copies their branches decode of ONE transmission, and must never reach far enough in
/// time to merge two transmissions - which the previous flat 3 s window did, silently
/// swallowing any retry inside it. These tests pin both properties for every mode that runs a
/// content dedupe wider than a frame: a retransmission at a realistic ARQ interval reaches the
/// host both times, and one transmission still reaches it exactly once however many branches
/// copy it.
/// </summary>
public class RetransmissionDeliveryTests
{
    private static readonly byte[] Frame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    /// <summary>Every catalogue mode that runs a cross-branch or cross-route content dedupe:
    /// the four diversity bank families under each of their framings, and the FX.25 variants
    /// whose two decode routes share a window.</summary>
    public static TheoryData<string> DedupingModes() =>
    [
        "afsk1200-multi",
        "afsk1200-fx25",
        "afsk1200-fx25rx",
        "afsk300",
        "afsk300-il2p",
        "afsk300-il2pc",
        "bpsk300",
        "bpsk300-multi",
        "bpsk300-nocrc",
        "bpsk1200",
        "bpsk1200-multi",
        "qpsk600",
        "qpsk2400",
        "qpsk3600",
    ];

    /// <summary>The PSK banks that can run the ensemble second detector - the widest branch
    /// configuration the catalogue can build (18 branches).</summary>
    public static TheoryData<string> EnsembleModes() =>
    [
        "bpsk300",
        "bpsk1200",
        "qpsk600",
        "qpsk2400",
    ];

    private static float[] TwoBursts(IModem modem, int rate, int gapSamples, int txDelayMs)
    {
        float[] burst = modem.Modulate(Frame, txDelayMs);
        int pad = rate / 2;
        var audio = new float[pad + burst.Length + gapSamples + burst.Length + pad];
        burst.CopyTo(audio, pad);
        burst.CopyTo(audio, pad + burst.Length + gapSamples);
        return audio;
    }

    private static int TxDelayFor(string mode) => mode.StartsWith("afsk") ? 240 : 150;

    [Theory]
    [MemberData(nameof(DedupingModes))]
    public void A_Retransmission_At_A_Realistic_Arq_Interval_Is_Delivered_Both_Times(string mode)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create(mode, rate, frames.Add);

        // 600 ms is the shortest retry interval in service (pdn-qso chat); packet.net's
        // T1V = RC * 250 ms + 2 * SRT lands in the same region on a fast channel.
        modem.Process(TwoBursts(modem, rate, gapSamples: rate * 6 / 10, TxDelayFor(mode)));

        frames.Should().HaveCount(2,
            "a byte-identical retransmission is a new transmission, not a duplicate decode");
        frames.Should().AllSatisfy(frame => frame.Should().Equal(Frame));
    }

    [Theory]
    [MemberData(nameof(DedupingModes))]
    public void One_Transmission_Is_Delivered_Exactly_Once(string mode)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create(mode, rate, frames.Add);

        float[] burst = modem.Modulate(Frame, TxDelayFor(mode));
        int pad = rate / 2;
        var audio = new float[burst.Length + (2 * pad)];
        burst.CopyTo(audio, pad);
        modem.Process(audio);

        frames.Should().ContainSingle(
            "however many branches or routes copy a transmission, the host hears it once")
            .Which.Should().Equal(Frame);
    }

    [Theory]
    [MemberData(nameof(EnsembleModes))]
    public void The_Ensemble_Bank_Delivers_A_Retransmission_Both_Times(string mode)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create(mode, rate, frames.Add,
            new ModemOptions(SecondDetector: PskDetector.Coherent));

        modem.Process(TwoBursts(modem, rate, gapSamples: rate * 6 / 10, TxDelayFor(mode)));

        frames.Should().HaveCount(2);
        frames.Should().AllSatisfy(frame => frame.Should().Equal(Frame));
    }

    [Theory]
    [MemberData(nameof(EnsembleModes))]
    public void The_Ensemble_Bank_Delivers_One_Transmission_Exactly_Once(string mode)
    {
        // The widest configuration the catalogue can build, and the widest measured branch
        // skew: the coherent twins deliver about 11 ms behind the differential branches
        // (issue #342's probe), which the dedupe window must still merge to one frame.
        int rate = ModemCatalog.DspRateFor(mode);
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create(mode, rate, frames.Add,
            new ModemOptions(SecondDetector: PskDetector.Coherent));

        float[] burst = modem.Modulate(Frame, TxDelayFor(mode));
        int pad = rate / 2;
        var audio = new float[burst.Length + (2 * pad)];
        burst.CopyTo(audio, pad);
        modem.Process(audio);

        frames.Should().ContainSingle().Which.Should().Equal(Frame);
    }

    [Fact]
    public void A_Retry_Faster_Than_The_Residual_Window_Is_Delivered_By_The_Acquisition_Boundary()
    {
        // The dedupe's second layer: even a repeat whose delivery lands INSIDE the residual
        // time window is a new transmission when the carrier dropped and re-synced between the
        // copies. qpsk3600 with a short TXDELAY is the one catalogue mode fast enough to put
        // two whole bursts inside the 200 ms window; the precondition assert keeps the test
        // honest if frame or preamble sizes ever drift.
        int rate = ModemCatalog.DspRateFor("qpsk3600");
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create("qpsk3600", rate, frames.Add);

        float[] burst = modem.Modulate(Frame, txDelayMilliseconds: 30);
        int gap = rate / 20;   // 50 ms of silence: well past the measured ~20 ms DCD hang
        (burst.Length + gap).Should().BeLessThan(rate / 5,
            "the deliveries must land within one residual window of each other, "
            + "so that only the acquisition boundary can separate the transmissions");

        modem.Process(TwoBursts(modem, rate, gap, txDelayMs: 30));

        frames.Should().HaveCount(2,
            "a carrier drop and re-acquisition between identical frames is positive evidence "
            + "of two transmissions, however close together they fall");
    }

    [Fact]
    public void A_Retransmission_Survives_Production_Sized_Feed_Slices()
    {
        // The banks' dedupe clock is stamped at feed-slice ends, so the daemon's small
        // real-time blocks are a different quantisation regime from the tests' single big
        // buffer. Same property, fed 20 ms at a time.
        int rate = ModemCatalog.DspRateFor("qpsk2400");
        var frames = new List<byte[]>();
        IModem modem = ModemCatalog.Create("qpsk2400", rate, frames.Add);

        float[] audio = TwoBursts(modem, rate, gapSamples: rate * 6 / 10, txDelayMs: 150);
        int block = rate / 50;
        for (int i = 0; i < audio.Length; i += block)
        {
            modem.Process(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
        }

        frames.Should().HaveCount(2);
    }
}
