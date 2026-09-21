namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// One rate on the ladder: what to transmit, and what it was measured to cost and buy.
/// </summary>
/// <param name="Constellation">Bits per subcarrier.</param>
/// <param name="Coding">The payload code.</param>
/// <param name="CliffCarrierToNoiseDb">The carrier-to-noise ratio at which half the frames land,
/// measured rather than derived. A reference point for reasoning about a link, NOT a threshold
/// anything switches on: a station cannot measure its own carrier-to-noise ratio, and if it could,
/// running at the level where half the frames fail would be a poor place to sit.</param>
/// <param name="GoodputBitsPerSecond">Payload bits delivered per second of transmission with every
/// frame landing. What a channel shared with other stations actually pays.</param>
/// <param name="ErrorRateFactorPerDb">How far this rate's pre-FEC error rate falls for each extra
/// decibel of signal, fitted over the first 4 dB above its cliff, which is where rate decisions get
/// taken.
/// <para><b>Per rate, because one number for all of them does not work.</b> That was tried: with a
/// single fitted slope of 0.7 the closed loop settled a rung below the best fixed rate at almost
/// every signal level, and the reason is that the real slope runs from 0.36 on BPSK to 0.81 on
/// QAM-256. At 4 dB of genuine margin, one slope of 0.7 reads anything from 2.4 dB to 11.3
/// depending on the rate - too shy to climb on the dense rates and reckless on the sparse ones.</para>
/// <para>Physically it is the constellation's own noise sensitivity: a dense constellation's
/// decision regions shrink slowly against extra signal, so its error rate falls slowly, while BPSK
/// falls off a cliff. BPSK's 0.36 is the steepest and the least trustworthy - a burst that long is
/// fade-limited rather than noise-limited, so the number describes a dropout statistic more than a
/// signal-to-noise one.</para>
/// <para>NaN for a rate this station has no measurement for, which a margin estimate must treat as
/// "unknown" rather than substituting a default.</para></param>
public sealed record OfdmFmRate(
    OfdmFmConstellation Constellation,
    OfdmFmCoding Coding,
    double CliffCarrierToNoiseDb,
    double GoodputBitsPerSecond,
    double ErrorRateFactorPerDb = double.NaN);

/// <summary>
/// The rates worth transmitting, slowest and most robust first.
/// </summary>
/// <remarks>
/// <para>A header can name eight constellations against three code rates, and <b>eleven of those 24
/// are never the right answer</b>: for each there is another rate that is at least as fast AND needs
/// less signal. A scheme able to select a dominated rate eventually will, and will then be slower
/// and less robust at the same time, so they are simply not on this list. The full grid and the
/// dominated list are in docs/dev/ofdm-fm/receiver-findings.md.</para>
/// <para>Seven K=9 rungs rather than the thirteen that survive domination, chosen because they sat
/// roughly 1 to 4 dB apart on the narrow profile's grid that ran the domination analysis. The six
/// left out are real rates that are simply too close to a neighbour to be worth a step: an adaptation
/// scheme with rungs half a decibel apart spends its time moving between them and gains nothing
/// for it. On the synthetic profile this ladder is now measured on, the two QPSK rungs land only
/// 0.4 dB apart, tighter than that bar; they stay, because removing either would leave a real gap
/// rather than because the spacing reasoning stopped applying. Then four K=7 rungs at QAM-64, at
/// 2/3, 3/4, 5/6 and 7/8, for two reasons the
/// grid could not see: the deployed 8 kHz preset runs K=7 2/3 at QAM-64, and a profile that is not
/// on the ladder opens a link at the bottom rung and crawls up, which was seen on air; and 5/6 and
/// 7/8 have a wire name for K=7 only (coding ids 8 and 9). A ladder may hold both codes as long as
/// every rung is calibrated; the controller orders them by what it measures, and K=7 is four times
/// cheaper to decode.</para>
/// <para><b>This ordering is measured, not derived, and now on one geometry throughout.</b> Every
/// rung, K=9 and K=7 alike, comes from one run of <c>PuncturedRungProbe</c> on 2026-09-20: R1/T13
/// on a TM8100 at narrow bandwidth, 2500 Hz peak deviation,
/// <see cref="OfdmFmParameters.Synthetic"/>, a 256-byte payload, 64 seeds. That is the toy layout
/// rather than one of <see cref="OfdmFmPresets"/>, so the ABSOLUTE levels here were not taken on
/// anything a station ships; what the controller reads is the ORDER of the rungs and the gaps
/// between them, which travels better (see <see cref="OfdmFmParameters.AdaptiveRate"/>). The seven
/// K=9 rungs replace figures measured earlier on a narrow TM8100 profile that is not among the
/// presets, so nothing here is a mix of two instruments any more; the four K=7 rungs replace the
/// figures the 2026-09-19 run that first added them tied to the QAM-64 K=9 2/3 rung by an offset,
/// because that run read this ladder's old K=9 figures 1.2 dB above what the same instrument read
/// for the same rung. Two things would still move it. Payload size:
/// a burst is a whole number of symbols, so at 64 bytes whole groups of rungs round to the same
/// length and the ordering does not hold. And the audio path: on an emphasised microphone path
/// everything is about 2 dB worse and the code rates spread further apart. Re-measure with
/// <c>PuncturedRungProbe</c> before trusting this on a different profile or geometry.</para>
/// <para><b>The top of the ladder is a near-tie, measured rather than smoothed away.</b> QAM-256
/// K=9 2/3 reads +22.1 dB for 6231 bit/s; QAM-64 K=7 7/8 reads +22.2 dB for the same 6231 bit/s,
/// because at 256 bytes the two round to the same 329 ms burst on this instrument. A tenth of a
/// decibel is inside the resolution 64 seeds gives a cliff, so the order is kept as measured rather
/// than collapsed or swapped; a run at a different payload size would likely move it, and
/// docs/dev/ofdm-fm/receiver-findings.md says more.</para>
/// <para>The two surprises in it, both worth knowing before editing: <b>rate 2/3 owns the top of
/// the K=9 ladder</b> (every K=9 rung above QAM-16 3/4 is a 2/3 code, so the obvious design of one
/// code rate with the constellation varying would miss most of the range; the K=7 rungs at QAM-64
/// are the punctured rates measured against that, not a reversal of it), and <b>QAM-128 is dominated
/// at all three code rates</b> because seven bits per carrier usually rounds up to the same symbol
/// count as eight, paying QAM-256's noise penalty for QAM-64's air time.</para>
/// </remarks>
public static class OfdmFmRateLadder
{
    private static readonly OfdmFmCoding Half = new(OfdmFmFec.Convolutional, 9, 1, 2, true);
    private static readonly OfdmFmCoding TwoThirds = new(OfdmFmFec.Convolutional, 9, 2, 3, true);
    private static readonly OfdmFmCoding ThreeQuarters = new(OfdmFmFec.Convolutional, 9, 3, 4, true);

    // The K=7 code: what the deployed 8 kHz preset runs at QAM-64, and the only code the header
    // can name at 5/6 and 7/8.
    private static readonly OfdmFmCoding K7TwoThirds = new(OfdmFmFec.Convolutional, 7, 2, 3, true);
    private static readonly OfdmFmCoding K7ThreeQuarters = new(OfdmFmFec.Convolutional, 7, 3, 4, true);
    private static readonly OfdmFmCoding K7FiveSixths = new(OfdmFmFec.Convolutional, 7, 5, 6, true);
    private static readonly OfdmFmCoding K7SevenEighths = new(OfdmFmFec.Convolutional, 7, 7, 8, true);

    // The whole ladder, one instrument, one run: PuncturedRungProbe on the synthetic profile,
    // 2026-09-20, R1/T13 narrow at 2500 Hz, a 256-byte payload, 64 seeds. Every cliff, goodput
    // and slope below is that run's raw figure - nothing here is derived from a different
    // geometry or tied to a neighbour through an offset. The seven K=9 rungs replace figures
    // measured earlier on a narrow TM8100 profile that is not among the presets; the
    // four K=7 rungs replace the offset-tied figures from the 2026-09-19 run that first added
    // them. The run, the near-tie it surfaced at the top of the ladder, and the caveats are in
    // docs/dev/ofdm-fm/receiver-findings.md.
    /// <summary>The ladder, most robust first.</summary>
    public static IReadOnlyList<OfdmFmRate> Rungs { get; } =
    [
        new(OfdmFmConstellation.Bpsk, ThreeQuarters, 6.3, 1229, 0.390),
        new(OfdmFmConstellation.Qpsk, TwoThirds, 6.7, 2077, 0.519),
        new(OfdmFmConstellation.Qpsk, ThreeQuarters, 7.1, 2317, 0.563),
        new(OfdmFmConstellation.Qam16, Half, 9.7, 2962, 0.803),
        new(OfdmFmConstellation.Qam16, ThreeQuarters, 13.0, 4107, 0.651),
        new(OfdmFmConstellation.Qam64, TwoThirds, 17.4, 5163, 0.768),
        new(OfdmFmConstellation.Qam64, K7TwoThirds, 18.3, 5163, 0.744),
        new(OfdmFmConstellation.Qam64, K7ThreeQuarters, 19.7, 5647, 0.712),
        new(OfdmFmConstellation.Qam64, K7FiveSixths, 21.6, 6024, 0.643),
        new(OfdmFmConstellation.Qam256, TwoThirds, 22.1, 6231, 0.800),
        new(OfdmFmConstellation.Qam64, K7SevenEighths, 22.2, 6231, 0.545),
    ];

    /// <summary>The most robust rung, which is where a link with no history starts.</summary>
    public const int Slowest = 0;

    /// <summary>The fastest rung.</summary>
    public static int Fastest => Rungs.Count - 1;

    /// <summary>Where a rate sits on the ladder, or -1 if it is not on it.</summary>
    /// <remarks>
    /// A correspondent may well transmit something that is not on this ladder - a dominated rate, or
    /// one this build does not know - and that is not an error. It means only that this station
    /// cannot place what it heard, so it has nothing to recommend from.
    /// </remarks>
    public static int IndexOf(OfdmFmConstellation constellation, OfdmFmCoding coding)
    {
        for (int i = 0; i < Rungs.Count; i++)
        {
            if (Rungs[i].Constellation == constellation && Rungs[i].Coding == coding)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The pre-FEC bit error rate at which a rate stops copying half its frames.
    /// </summary>
    /// <remarks>
    /// <para><b>A property of the code rate, not of the constellation</b>, which is the measurement
    /// that makes an adaptation policy writeable at all: three numbers rather than one per rung.
    /// Physically it has to be - how many raw errors a burst can absorb is set by the code's
    /// correcting power, and that belongs to the code.</para>
    /// <para>Measured to about +/-15 % across the constellations sharing each rate. The worst
    /// outlier is BPSK at rate 3/4, which fails at 0.022 against 0.036 for the others, and that
    /// fits rather than contradicts: a BPSK burst is over a second long and fade-limited, so its
    /// errors arrive bunched in a dropout, and bunched errors are harder for a convolutional code
    /// than the same number scattered.</para>
    /// <para>Zero for an uncoded burst, which has no correcting power to spend and therefore no
    /// margin to read.</para>
    /// </remarks>
    public static double CliffPreFecErrorRate(OfdmFmCoding coding) => coding.Scheme switch
    {
        OfdmFmFec.None => 0,

        // Measured on the 256-byte grid with LdpcLadderProbe: 0.081 at QPSK, 0.102 at QAM-16,
        // 0.112 at QAM-64, 0.121 at QAM-256 - a spread of about +/-20 % around this figure where
        // the convolutional rates held +/-15 %, and unlike them it drifts upward with density.
        // Good enough for a margin readout; no LDPC rate is on the ladder (see the findings:
        // at 256 bytes the segmentation into independent codewords puts the whole family 0.3 to
        // 0.5 dB behind convolutional 1/2), so nothing steps on the strength of this number.
        OfdmFmFec.Ldpc => 0.10,
        _ => (coding.RateNumerator, coding.RateDenominator) switch
        {
            (1, 2) => 0.091,
            (2, 3) => 0.053,
            (3, 4) => 0.032,

            // PuncturedRungProbe on the synthetic profile, 2026-09-19: 5/6 read 0.019 at QAM-16
            // and 0.017 at QAM-64, 7/8 0.014 at QAM-64, all on the K=7 code. The same run put
            // K=7 3/4 at 0.033 and K=7 2/3 at 0.050 beside the K=9 figures above, so the number
            // is the rate's and not the constraint length's, as it is the rate's and not the
            // constellation's.
            (5, 6) => 0.018,
            (7, 8) => 0.014,
            _ => throw new InvalidOperationException(
                $"rate {coding.RateNumerator}/{coding.RateDenominator} has no measured cliff"),
        },
    };

    /// <summary>
    /// How much of the way to failure a burst got: its pre-FEC error rate against the rate at which
    /// its code gives out. Zero is a clean path, one is the cliff.
    /// </summary>
    /// <remarks>
    /// Null where there is nothing to read: an uncoded burst, or one whose payload did not decode.
    /// A failed burst does report an error rate and it will be large, but it is computed by
    /// re-encoding what the decoder produced, so on a failed burst the reference is wrong too. It is
    /// honest as a direction and useless as a number, and a policy must not average it in.
    /// </remarks>
    public static double? SpentFraction(OfdmFmBurst burst)
    {
        if (burst.Payload is null || burst.PreFecBitErrorRate is not double ber)
        {
            return null;
        }

        double cliff = CliffPreFecErrorRate(burst.Coding);
        return cliff > 0 ? ber / cliff : null;
    }
}
