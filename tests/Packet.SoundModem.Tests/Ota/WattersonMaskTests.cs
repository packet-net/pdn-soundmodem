using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Watterson performance masks for the audio modems - rx-roadmap workstream 0, the MS110D
/// programme's mask idea extended to the packet modes. Every point runs through
/// <see cref="SimBench.RunPoint"/>, the same rig <c>sm-ota sim</c> drives, so a mask number
/// and a CLI number are the same measurement; every point is deterministic in
/// (mode, channel, SNR, CFO, first seed, burst count), so a mask failure reproduces exactly
/// or it is not a mask failure.
/// </summary>
/// <remarks>
/// <para><b>Two tiers.</b> The smoke tier blocks CI: one or a few anchor points per mode,
/// with floors set two to three binomial standard deviations below the measured value - far
/// enough that statistical wobble cannot trip them, close enough that losing a decibel does.
/// The full tier (<c>SM_MASK_GATE=1</c>) is the A/B instrument: the flagship's whole
/// SNR x channel x CFO grid at higher trial counts, floors slacker still.</para>
/// <para><b>Masks are measured reality, never aspiration.</b> Every floor below derives from
/// a measurement taken 2026-08-06 on the post-#236 + erasure-decoding receiver, quoted in
/// the comment beside it. The QPSK rows sit at their current, uncampaigned levels on
/// purpose: documenting the truth is what will make their eventual campaign measurable. A
/// mask moves only with a mode-validation.md entry saying what moved it - in either
/// direction.</para>
/// <para><b>What a green mask means.</b> No regression against the <em>model</em>. The
/// Watterson sim carries no static crashes, no SSB filter tilt, no AGC, and our own transmit
/// shaping rather than a NinoTNC's; the 40 m capture campaign (docs/rx-roadmap.md) stays the
/// truth about the band. Two instruments, two jobs.</para>
/// </remarks>
public class WattersonMaskTests(ITestOutputHelper output)
{
    /// <summary>Smoke-tier trials per point. Enough that the floors below sit 2-3 binomial
    /// standard deviations under their measured values; small enough that the whole blocking
    /// tier stays tens of seconds.</summary>
    private const int SmokeBursts = 40;

    private SimPointResult Point(
        string mode, SimChannelKind channel, double snrDb, double cfoHz = 0,
        int bursts = SmokeBursts, int txDelayMs = 0)
    {
        SimPointResult result = SimBench.RunPoint(
            mode, rate: null, SimLayer.Frame, channel, snrDb, bursts, frameBytes: 60,
            firstSeed: 1, workers: 4, txDelayMs: txDelayMs, cfoHz: cfoHz);
        output.WriteLine(
            $"{mode} {channel} {snrDb:+0.0;-0.0} dB cfo {cfoHz:+0.0;-0.0}: "
            + $"{result.Successes}/{result.Trials}");
        return result;
    }

    // ------------------------------------------------------------------------------------
    // Smoke tier - blocking. Measured values quoted per row (2026-08-06, N=40, seed 1).
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bpsk300", "awgn", -4.0, 0.0, 28)]     // measured 37/40
    [InlineData("bpsk300", "awgn", -3.0, 0.0, 34)]     // measured 40/40
    [InlineData("bpsk300", "awgn", -3.0, 3.75, 32)]    // measured 39/40 - the mid-branch
                                                       // CFO point a lagging reference nulls
    [InlineData("bpsk300", "good", 0.0, 0.0, 12)]      // measured 22/40
    [InlineData("bpsk300", "poor", 9.0, 0.0, 2)]       // measured 7/40 - the equaliser-bound
                                                       // floor must not collapse further
    [InlineData("bpsk1200", "awgn", 2.0, 0.0, 28)]     // measured 37/40
    [InlineData("afsk300-il2pc", "awgn", 0.0, 0.0, 30)] // measured 39/40
    [InlineData("qpsk600", "awgn", 4.0, 0.0, 30)]      // measured 39/40
    [InlineData("qpsk2400", "awgn", 11.0, 0.0, 32)]    // measured 40/40
    public void Smoke_Mask_Holds(
        string mode, string channel, double snrDb, double cfoHz, int floor)
    {
        SimPointResult result = Point(mode, SimChannel.Parse(channel), snrDb, cfoHz);

        result.Successes.Should().BeGreaterThanOrEqualTo(
            floor,
            "the {0} mask at {1} {2} dB (cfo {3} Hz) is measured reality - a miss this deep "
            + "is a receiver regression, not wobble; if a deliberate change moved it, the "
            + "mask moves with a mode-validation.md entry",
            mode, channel, snrDb, cfoHz);
    }

    [Fact]
    public void Fsk9600_Smoke_Mask_Holds()
    {
        // The fast classic-HDLC detector needs a real TXDELAY run-in to lock (see
        // SimCommand's --txdelay note), and 25 bursts keep the 48 kHz rate affordable.
        SimPointResult result = Point(
            "fsk9600-il2p", SimChannel.Parse("awgn"), 16.0, bursts: 25, txDelayMs: 150);

        result.Successes.Should().BeGreaterThanOrEqualTo(19, "measured 25/25 on 2026-08-06");
    }

    // ------------------------------------------------------------------------------------
    // Full tier - the A/B instrument, env-gated. The flagship's whole grid; floors slacker
    // (measured minus ~20 points at N=100), because this tier's job is the printed table.
    // ------------------------------------------------------------------------------------

    public static IEnumerable<object[]> FullGrid()
    {
        // bpsk300 AWGN ladder - measured 2026-08-06 (erasure receiver, N>=100): 7/55/88/96/98 %.
        yield return ["bpsk300", "awgn", -5.0, 0.0, 35];
        yield return ["bpsk300", "awgn", -4.0, 0.0, 68];
        yield return ["bpsk300", "awgn", -3.0, 0.0, 80];
        yield return ["bpsk300", "awgn", -2.0, 0.0, 85];

        // bpsk300 CFO flatness at -3 dB - measured 92-97 % across the span.
        yield return ["bpsk300", "awgn", -3.0, 3.75, 75];
        yield return ["bpsk300", "awgn", -3.0, 7.5, 75];
        yield return ["bpsk300", "awgn", -3.0, 15.0, 75];
        yield return ["bpsk300", "awgn", -3.0, 30.0, 75];

        // bpsk300 fading - measured Good 35/49/60/70/76 %, Poor 23/26/28/28 %.
        yield return ["bpsk300", "good", -4.0, 0.0, 15];
        yield return ["bpsk300", "good", 0.0, 0.0, 38];
        yield return ["bpsk300", "good", 4.0, 0.0, 55];
        yield return ["bpsk300", "poor", 3.0, 0.0, 8];
        yield return ["bpsk300", "poor", 9.0, 0.0, 10];

        // bpsk1200 AWGN - measured 86 % @ +2, 99 % @ +4.
        yield return ["bpsk1200", "awgn", 2.0, 0.0, 62];
        yield return ["bpsk1200", "awgn", 4.0, 0.0, 82];
    }

    [Theory]
    [MemberData(nameof(FullGrid))]
    public void Full_Mask_Holds(
        string mode, string channel, double snrDb, double cfoHz, int floorPerHundred)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1",
            "set SM_MASK_GATE=1 for the full mask ladder - the receive-path A/B instrument");

        SimPointResult result = Point(mode, SimChannel.Parse(channel), snrDb, cfoHz, bursts: 100);

        result.Successes.Should().BeGreaterThanOrEqualTo(
            floorPerHundred,
            "the full-ladder {0} mask at {1} {2} dB (cfo {3} Hz); masks move only with a "
            + "mode-validation.md entry",
            mode, channel, snrDb, cfoHz);
    }
}
