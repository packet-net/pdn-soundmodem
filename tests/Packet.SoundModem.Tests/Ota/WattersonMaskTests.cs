using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Watterson performance masks for the non-FM modems - rx-roadmap workstream 0, the MS110D
/// programme's mask idea extended across the catalogue. Every point runs through
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
/// <para><b>Coverage rule.</b> Every non-FM mode the sim rig can drive gets a mask: the
/// NinoTNC SSB lineage (afsk300-il2pc, bpsk300/1200, qpsk600/2400), the FreeDV datac OFDM
/// family, and ARDOP's wild-relevant frame ladder via <see cref="ArdopFrameProbe"/>
/// (<c>ardop:&lt;FrameName&gt;</c> - a session TNC is not a catalogue <c>IModem</c>, so it
/// takes the dedicated-probe path; the campaign's A3 leg closed what rx-roadmap workstream 0
/// recorded as this suite's one open gap). Deliberately absent: <b>ms110d-*</b>, which
/// carries its own, richer mask suite (this file is that suite's idea generalised); and the
/// FM modes (afsk1200, fsk*, c4fsk*, qpsk3600), outside this suite's channel model, except
/// the single fsk9600 AWGN anchor kept because it is nearly free.</para>
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
    [InlineData("bpsk300", "moderate", 2.0, 0.0, 10)]  // measured 21/40 - the CCIR middle,
                                                       // where receiver work actually shows
    [InlineData("bpsk300", "poor", 9.0, 0.0, 2)]       // measured 7/40 - the equaliser-bound
                                                       // floor must not collapse further
    [InlineData("bpsk1200", "awgn", 2.0, 0.0, 28)]     // measured 37/40
    [InlineData("afsk300-il2pc", "awgn", 0.0, 0.0, 30)] // measured 39/40
    [InlineData("qpsk600", "awgn", 4.0, 0.0, 36)]      // measured 100/100 2026-08-07 (QPSK
                                                       // campaign fix-set 1: matched filter
                                                       // + DPLL timing; was 39/40)
    [InlineData("qpsk2400", "awgn", 11.0, 0.0, 36)]    // measured 99/100 2026-08-07 (same
                                                       // campaign; was 40/40 at a knee that
                                                       // has since moved ~2 dB down)
    [InlineData("freedv-datac0", "awgn", 0.0, 0.0, 20, 25)]  // measured 25/25
    [InlineData("freedv-datac1", "awgn", 3.0, 0.0, 20, 25)]  // measured 25/25
    [InlineData("freedv-datac3", "awgn", 0.0, 0.0, 20, 25)]  // measured 25/25
    [InlineData("freedv-datac3", "good", 3.0, 0.0, 13, 25)]  // measured 21/25 - the OFDM
                                                             // family's fading capability
                                                             // is exactly what it is for
    [InlineData("freedv-datac1", "good", 9.0, 0.0, 14, 25)]  // measured 22/25
    [InlineData("freedv-datac3", "moderate", 3.0, 0.0, 15, 25)] // measured 23/25
    // ARDOP wild-relevant frame ladder (campaign A3; measured 2026-08-08, N=100, seed 1).
    // The single-shot ceiling is ~98 %, not 100: a small fraction of noise realisations
    // capture the leader detector during the lead-in (ArdopFrameProbe remarks) - the
    // floors pin that measured reality.
    [InlineData("ardop:4FSK.200.50S", "awgn", -2.0, 0.0, 22, 25)]   // measured 98/100
    [InlineData("ardop:4FSK.200.50S", "moderate", 6.0, 0.0, 15, 25)] // measured 82/100
    [InlineData("ardop:4PSK.200.100S", "awgn", 2.0, 0.0, 23, 25)]   // measured 99/100
    [InlineData("ardop:4FSK.500.100", "awgn", 2.0, 0.0, 23, 25)]    // measured 99/100
    [InlineData("ardop:4FSK.500.100", "moderate", 9.0, 0.0, 13, 25)] // measured 74/100 -
                                                                    // the wild workhorse on
                                                                    // the representative
                                                                    // channel
    [InlineData("ardop:4PSK.500.100", "awgn", 4.0, 0.0, 23, 25)]    // measured 99/100
    [InlineData("ardop:8PSK.500.100", "awgn", 12.0, 0.0, 22, 25)]   // measured 97/100
    [InlineData("ardop:16QAM.500.100", "awgn", 16.0, 0.0, 22, 25)]  // measured 98/100 -
                                                                    // the top rung's sim
                                                                    // floor on record; its
                                                                    // wild zero was SNR,
                                                                    // not the decoder
    public void Smoke_Mask_Holds(
        string mode, string channel, double snrDb, double cfoHz, int floor,
        int bursts = SmokeBursts)
    {
        SimPointResult result = Point(mode, SimChannel.Parse(channel), snrDb, cfoHz, bursts);

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

        // bpsk300 fading - measured Good 35/49/60/70/76 %, Moderate (N=40) 23/42/53/60 %
        // across -2..+4, Poor 23/26/28/28 %.
        yield return ["bpsk300", "moderate", 0.0, 0.0, 22];
        yield return ["bpsk300", "moderate", 4.0, 0.0, 40];
        yield return ["bpsk300", "good", -4.0, 0.0, 15];
        yield return ["bpsk300", "good", 0.0, 0.0, 38];
        yield return ["bpsk300", "good", 4.0, 0.0, 55];
        yield return ["bpsk300", "poor", 3.0, 0.0, 8];
        yield return ["bpsk300", "poor", 9.0, 0.0, 10];

        // bpsk1200 AWGN - measured 86 % @ +2, 99 % @ +4.
        yield return ["bpsk1200", "awgn", 2.0, 0.0, 62];
        yield return ["bpsk1200", "awgn", 4.0, 0.0, 82];

        // ARDOP flagship: 4FSK.500.100, the wild workhorse (campaign A3, measured
        // 2026-08-08, N=100). AWGN knee at ~-1 dB; CFO-flat to 50 Hz; the fading rows are
        // the wild corpus's own weather.
        yield return ["ardop:4FSK.500.100", "awgn", -2.0, 0.0, 20];   // measured 38/100
        yield return ["ardop:4FSK.500.100", "awgn", 0.0, 0.0, 75];    // measured 93/100
        yield return ["ardop:4FSK.500.100", "awgn", 2.0, 0.0, 85];    // measured 99/100
        yield return ["ardop:4FSK.500.100", "awgn", 0.0, 20.0, 75];   // measured 93/100
        yield return ["ardop:4FSK.500.100", "awgn", 0.0, 50.0, 75];   // measured 94/100
        yield return ["ardop:4FSK.500.100", "good", 3.0, 0.0, 48];    // measured 66/100
        yield return ["ardop:4FSK.500.100", "good", 9.0, 0.0, 68];    // measured 85/100
        yield return ["ardop:4FSK.500.100", "moderate", 3.0, 0.0, 15]; // measured 30/100
        yield return ["ardop:4FSK.500.100", "moderate", 9.0, 0.0, 56]; // measured 74/100
        yield return ["ardop:4FSK.500.100", "moderate", 12.0, 0.0, 70]; // measured 88/100
        yield return ["ardop:4FSK.500.100", "poor", 9.0, 0.0, 26];    // measured 43/100
        yield return ["ardop:4FSK.500.100", "poor", 16.0, 0.0, 47];   // measured 65/100

        // ARDOP top rungs - the sim floors behind the wild campaign's confirmed null:
        // on Moderate/Poor these frames are effectively undecodable at any plausible
        // NVIS SNR (0/25 through +12 and +16 coarse), so the full-tier rows pin AWGN and
        // Good, where they exist at all.
        yield return ["ardop:8PSK.500.100", "awgn", 12.0, 0.0, 80];   // measured 97/100
        yield return ["ardop:8PSK.500.100", "good", 16.0, 0.0, 40];   // measured 59/100
        yield return ["ardop:16QAM.500.100", "awgn", 12.0, 0.0, 65];  // measured 85/100
        yield return ["ardop:16QAM.500.100", "awgn", 16.0, 0.0, 80];  // measured 98/100
        yield return ["ardop:16QAM.500.100", "good", 16.0, 0.0, 36];  // measured 55/100

        // ARDOP remaining wild-ladder types - one full-tier anchor each.
        yield return ["ardop:4FSK.200.50S", "awgn", -2.0, 0.0, 88];   // measured 98/100
        yield return ["ardop:4FSK.200.50S", "moderate", 6.0, 0.0, 64]; // measured 82/100
        yield return ["ardop:4PSK.200.100S", "awgn", 0.0, 0.0, 82];   // measured 97/100
        yield return ["ardop:4PSK.200.100S", "good", 3.0, 0.0, 56];   // measured 74/100
        yield return ["ardop:4PSK.500.100", "awgn", 4.0, 0.0, 85];    // measured 99/100
        yield return ["ardop:4PSK.500.100", "moderate", 9.0, 0.0, 52]; // measured 70/100
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
