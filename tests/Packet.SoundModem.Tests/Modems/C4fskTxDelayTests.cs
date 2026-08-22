namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Regression cover for issue #336: a longer TXDELAY made both C4FSK modes worse (5 dB on AWGN,
/// 2 dB on the data-port FM path), because the envelope tracker was a max-hold on the noisy
/// waveform and climbed the noise for the whole run-in. These pin the two facts the fix rests
/// on, on the sim ladder's own bursts: the envelope the sync word meets after a 300 ms run-in
/// is the one it meets after a 20 ms run-in, and a long run-in costs no frames.
/// </summary>
public class C4fskTxDelayTests
{
    /// <summary>
    /// Before the fix, at these SNRs, the envelope at the sync after 300 ms sat 1.2 to 1.35
    /// times the clean burst's and the sync's own outer symbols read 0.70 to 0.75 normalised
    /// against 0.89 to 0.94 after a short run-in; after it the two run-ins agree within a few
    /// per cent and the outer symbols sit where the slicer expects them.
    /// </summary>
    [Theory]
    [InlineData("c4fsk9600", 20.0)]
    [InlineData("c4fsk19200", 23.0)]
    public void A_Long_Run_In_Leaves_The_Envelope_Where_A_Short_One_Does(string mode, double snr)
    {
        const int seeds = 8;
        double shortHalf = 0;
        double longHalf = 0;
        double longOuter = 0;
        int scored = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            C4fskTxDelayRig.Burst brief = C4fskTxDelayRig.Run(mode, snr, 20, seed);
            C4fskTxDelayRig.Burst lengthy = C4fskTxDelayRig.Run(mode, snr, 300, seed);
            if (brief.SyncSymbol < 0 || lengthy.SyncSymbol < 0)
            {
                continue;
            }

            scored++;
            shortHalf += brief.HalfAtSync / brief.CleanHalf;
            longHalf += lengthy.HalfAtSync / lengthy.CleanHalf;
            longOuter += lengthy.SyncNormalised;
        }

        scored.Should().BeGreaterThanOrEqualTo(
            seeds - 1, "the clock instant's own stream should carry the sync word at {0} dB", snr);
        (longHalf / shortHalf).Should().BeLessThan(
            1.1,
            "the envelope the sync word meets must not grow with the run-in for {0}: the max-hold "
            + "that climbed the noise for 300 ms is what issue #336 measured",
            mode);
        (longOuter / scored).Should().BeGreaterThan(
            0.8,
            "the sync's own outer symbols must sit well clear of the 2/3 slice after a long "
            + "run-in for {0} (they read 0.70 to 0.75 before the fix)",
            mode);
    }

    /// <summary>Before the fix c4fsk9600 at 22 dB decoded 40 of 40 of these bursts after a
    /// 20 ms run-in and 15 of 40 after 300 ms; c4fsk19200 at 25 dB 40 and 22.</summary>
    [Theory]
    [InlineData("c4fsk9600", 22.0)]
    [InlineData("c4fsk19200", 25.0)]
    public void A_Long_Run_In_Costs_No_Frames(string mode, double snr)
    {
        const int seeds = 20;
        int brief = 0;
        int lengthy = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            brief += C4fskTxDelayRig.Run(mode, snr, 0, seed, cleanReference: false).Decoded ? 1 : 0;
            lengthy += C4fskTxDelayRig.Run(mode, snr, 300, seed, cleanReference: false).Decoded ? 1 : 0;
        }

        brief.Should().BeGreaterThanOrEqualTo(seeds - 1, "{0} decodes these bursts at {1} dB", mode, snr);
        lengthy.Should().BeGreaterThanOrEqualTo(
            brief - 1,
            "a preamble exists to let the receiver settle, so a 300 ms run-in must decode at least "
            + "what a 0 ms one does for {0}",
            mode);
    }
}
