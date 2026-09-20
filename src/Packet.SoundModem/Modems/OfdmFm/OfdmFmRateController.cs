namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// Decides what rate to ask a correspondent for, from what has actually been heard from them.
/// </summary>
/// <remarks>
/// <para><b>One of these per direction, and it lives on the receiving end.</b> A transmitter cannot
/// measure the path its own signal takes; only the far end knows what it can hear. So this watches
/// what arrives, forms a recommendation, and that recommendation rides out on the next burst going
/// the other way. What this station transmits at is not its own decision at all - it is whatever
/// the far end last asked for, which is <see cref="Transmit"/>.</para>
/// <para>The two directions never share state. A link is not reciprocal: different noise floor,
/// different antenna, different site at each end, and a repeater path can be strong one way and
/// marginal the other.</para>
/// <para><b>Asymmetric on purpose, but only about failures.</b> One burst that did not decode is
/// enough to step down; nothing else is decided on a single burst, in either direction. That
/// distinction was measured and it is the difference between this policy working and not: retreating
/// whenever one SUCCESSFUL burst looked expensive gave up a working rung 14 times in 200 bursts and
/// cost most of what adaptation was supposed to gain. A burst that decoded is proof the rate works,
/// whatever it spent getting there.</para>
/// <para>The measurement agrees for an independent reason. A pre-FEC error rate is a count over a
/// few thousand coded bits, so near the cliff it is good to about 8 % and at 3 dB of margin it is
/// four errors and good to about 45 %: sharp where a station is in trouble, vague where it has room.
/// Only an outright failure carries no measurement error at all, and that is exactly the one thing
/// a single burst decides.</para>
/// <para><b>Probe and retreat, rather than trusting the margin model.</b> The estimate of how much
/// link is left (see <see cref="EstimatedMarginDb"/>) is calibrated per rate but it is still an
/// estimate and it will sometimes be wrong. When a step up is followed by a failure, this backs off
/// and demands twice as many bursts before trying that step again, so the policy degrades into "try
/// occasionally" when the model is wrong rather than oscillating on it.</para>
/// <para><b>Descent is one rung per failed burst, and that is also its top speed.</b> A path that
/// collapses by several rungs pays one failed burst per rung on the way down. Everything measured
/// here was a static simulated link (see the negotiation caveats in
/// docs/dev/ofdm-fm/receiver-findings.md); a real path that dives may make a multi-rung retreat
/// worth measuring, and nobody has.</para>
/// </remarks>
public sealed class OfdmFmRateController
{
    /// <summary>Bursts before a rate is judged on what it is delivering.</summary>
    /// <remarks>
    /// <b>Two, and only bursts that DECODED.</b> Four was tried and is worse - 89 % of the oracle
    /// becomes 78 % at the bottom of the ladder - because a rate that has genuinely stopped paying
    /// costs throughput for every burst it is not left. Stepping down late is not free just because
    /// stepping down wrongly is expensive. Stepping down whenever a single successful burst
    /// looked dear was measured and was the largest single fault in an earlier version of this: at
    /// the right operating point it gave up a working rung 14 times over 200 bursts, every one of
    /// them on a burst that had decoded perfectly and none on an actual failure. A burst that
    /// decoded is proof the rate works, whatever it spent getting there.
    /// </remarks>
    public const int BurstsBeforeJudgingARate = 2;

    /// <summary>Bursts to judge a step up on.</summary>
    /// <remarks>
    /// Judged on their median rather than on a run of consecutive good ones: a run rule needs every
    /// burst in it to clear the bar, so on a link whose typical burst sits at the bar it almost
    /// never fires. Requiring the TYPICAL burst to clear it is what "several bursts" means.
    /// </remarks>
    public const int BurstsToJudgeAStepOn = 4;

    /// <summary>The most bursts a repeatedly failing step will ever demand.</summary>
    public const int MaxBurstsToJudgeAStepOn = 32;

    /// <summary>
    /// How much better a faster rate has to look before it is worth moving to.
    /// </summary>
    /// <remarks>
    /// Pure hysteresis. Two rungs whose expected throughput is within a few per cent of each other
    /// are, for practical purposes, the same choice, and a controller that switched between them on
    /// the difference would spend its time switching. Downward has no such margin: a rate that has
    /// stopped earning its keep should be left at once.
    /// </remarks>
    public const double StepUpMustBeatCurrentBy = 1.08;

    /// <summary>Follow-on bursts whose payload failed, among the last
    /// <see cref="FollowOnBurstsJudged"/> heard, before this station asks for full bursts.</summary>
    /// <remarks>
    /// Two, because one is what a wobbling clock costs a follow-on burst now and then on a link
    /// that is otherwise fine, and two in eight is a link where the inherited estimate is worth
    /// less than a fresh one. Measured on air, 2026-09-19: on the direction whose clock was
    /// wobbling, long follow-on keyups lost three to ten frames in twenty where full bursts on
    /// the same link at the same time lost at most three, and the losses came in runs of bad
    /// bursts, not singly.
    /// </remarks>
    public const int FollowOnFailuresToAskForFullBursts = 2;

    /// <summary>The window of follow-on bursts the failures are counted in.</summary>
    public const int FollowOnBurstsJudged = 8;

    /// <summary>Full bursts that must decode, once full bursts have been asked for, before this
    /// station lets follow-on bursts be tried again.</summary>
    public const int FullBurstsBeforeFollowOnAgain = 8;

    /// <summary>The most full bursts a repeatedly relapsing link will ever be held at.</summary>
    public const int MaxFullBurstsBeforeFollowOnAgain = 64;

    private readonly int[] _wanted;
    private readonly double[] _window = new double[MaxBurstsToJudgeAStepOn];
    private int _seen;
    private int _rung;
    private bool _justSteppedUp;

    private readonly bool[] _followOnOutcomes = new bool[FollowOnBurstsJudged];
    private int _followOnSeen;
    private int _fullBurstsSeen;
    private int _fullBurstsWanted = FullBurstsBeforeFollowOnAgain;
    private int _followOnSinceAllowed;
    private bool _watchingForRelapse;

    private readonly int _start;
    private readonly OfdmFmConstellation _topConstellation;

    /// <summary>Starts where the station is configured to start.</summary>
    /// <param name="startRung">Where to begin, before anything has been heard. Defaults to the most
    /// robust rate, which is the only safe assumption about a link with no history.
    /// <para>A station whose configuration names a rate on the ladder should pass that instead. A
    /// beacon, or the first transmission of any link, goes out at this rate and nothing corrects it
    /// until something is heard back - so a station that crawled from the bottom every time would
    /// transmit its opening burst four times slower than the operator asked for.</para></param>
    /// <param name="topConstellation">The densest constellation this station's own
    /// <see cref="Recommendation"/> will ever name, from <see
    /// cref="OfdmFmParameters.AdaptiveTopConstellation"/>. Defaults to <see
    /// cref="OfdmFmConstellation.Qam256"/>, the densest this ladder has, which bounds nothing -
    /// every caller that does not pass this is asking for the old, unbounded behaviour.
    /// <para>Bounds only what THIS station asks its correspondent for; what the far end asks of
    /// this station's transmitter is <see cref="Transmit"/>, and this never touches it. A station
    /// cannot measure its own correspondent's receive thread, only its own, so it can only bound
    /// its own ask.</para></param>
    public OfdmFmRateController(
        int startRung = OfdmFmRateLadder.Slowest,
        OfdmFmConstellation topConstellation = OfdmFmConstellation.Qam256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startRung);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startRung, OfdmFmRateLadder.Fastest);

        _start = startRung;
        _topConstellation = topConstellation;
        _wanted = new int[OfdmFmRateLadder.Rungs.Count];
        Array.Fill(_wanted, BurstsToJudgeAStepOn);
        _rung = startRung;
        Transmit = OfdmFmRateLadder.Rungs[startRung];
    }

    /// <summary>What this station wants to hear from its correspondent. Goes in our next header.
    /// </summary>
    public OfdmFmRate Recommendation => OfdmFmRateLadder.Rungs[_rung];

    /// <summary>
    /// What this station should transmit at: the last thing the far end asked for, or the most
    /// robust rate if it has not asked for anything.
    /// </summary>
    /// <remarks>
    /// <b>A recommendation goes stale.</b> Nothing here ages it, because a burst count is a poor
    /// clock and a real one does not belong in a decision this small. A host that has not heard
    /// from a correspondent for longer than the path stays the same should call
    /// <see cref="Reset"/>: an hour-old recommendation from a station that has since driven into a
    /// valley is worse than no recommendation at all.
    /// </remarks>
    public OfdmFmRate Transmit { get; private set; }

    /// <summary>How many bursts the next step up will be judged on.</summary>
    public int BurstsWanted => _wanted[_rung];

    /// <summary>Forgets everything and returns to the starting rate in both directions.</summary>
    public void Reset()
    {
        Array.Fill(_wanted, BurstsToJudgeAStepOn);
        _rung = _start;
        _seen = 0;
        _justSteppedUp = false;
        Transmit = OfdmFmRateLadder.Rungs[_start];
        WantFullBursts = false;
        SendFullBursts = false;
        Array.Clear(_followOnOutcomes);
        _followOnSeen = 0;
        _fullBurstsSeen = 0;
        _fullBurstsWanted = FullBurstsBeforeFollowOnAgain;
        _followOnSinceAllowed = 0;
        _watchingForRelapse = false;
    }

    /// <summary>
    /// Whether this station is asking its correspondent for full bursts rather than follow-on
    /// ones. Goes in our next header beside the rate recommendation.
    /// </summary>
    /// <remarks>
    /// <para>A follow-on burst is read against the timing and channel inherited from the burst
    /// before it; a full burst acquires its own. On a steady link the inheritance is free and
    /// the follow-on burst is three symbols shorter. On a link whose clock wobbles within a
    /// burst, the inheritance is worth less than a fresh estimate symbol and follow-on bursts
    /// fail where full ones decode. Which kind of link this is can only be seen at the receiving
    /// end, and only the transmitting end can change the form, so it has to be asked for.</para>
    /// <para>Asked for after <see cref="FollowOnFailuresToAskForFullBursts"/> failures among the
    /// last <see cref="FollowOnBurstsJudged"/> follow-on bursts; withdrawn after
    /// <see cref="FullBurstsBeforeFollowOnAgain"/> full bursts have decoded, doubled each time
    /// follow-on bursts fail again soon after being allowed, up to
    /// <see cref="MaxFullBurstsBeforeFollowOnAgain"/>: the same backoff the rate steps use.</para>
    /// </remarks>
    public bool WantFullBursts { get; private set; }

    /// <summary>Whether the far end has asked this station for full bursts: what our transmit
    /// path obeys, beside <see cref="Transmit"/>.</summary>
    public bool SendFullBursts { get; private set; }

    /// <summary>How many full bursts have to decode before follow-on bursts are allowed again.
    /// </summary>
    public int FullBurstsWanted => _fullBurstsWanted;

    /// <summary>
    /// Roughly what fraction of frames land, from how much of the code a typical burst spends.
    /// </summary>
    /// <remarks>
    /// <para>Fitted across the whole ladder, where spending the code and losing frames track each
    /// other closely enough for one curve: below about 0.6 of the way to the cliff nearly everything
    /// lands, at the cliff itself half does by definition, and past about 1.4 almost nothing does.
    /// Piecewise linear between those, which is as much shape as the measurement supports.</para>
    /// <para>The point of having it at all is that it turns a margin into a THROUGHPUT, and
    /// throughput is the thing a rate decision is actually about. Deciding on margin alone always
    /// picks the safest rate; a rate losing one frame in ten can still be well ahead of the one
    /// below it, and only this says so.</para>
    /// </remarks>
    public static double EstimatedFrameSuccess(double spentFraction) => spentFraction switch
    {
        <= 0.6 => 0.97,
        <= 1.0 => 0.97 - ((spentFraction - 0.6) / 0.4 * 0.47),
        <= 1.4 => 0.5 - ((spentFraction - 1.0) / 0.4 * 0.45),
        _ => 0.05,
    };

    /// <summary>
    /// Roughly how many decibels of signal a burst had in hand, from how much of its code it spent.
    /// </summary>
    /// <remarks>
    /// <para><b>Approximate, and it needs the rate's own slope.</b> How much of the code a burst
    /// spent is comparable across rates, because the spending limit is a property of the code rate.
    /// How fast that spending falls as the signal improves is not: it belongs to the constellation
    /// and runs from 0.36 per dB on BPSK to 0.81 on QAM-256. One slope for all of them was tried
    /// and the closed loop rejected it, settling a rung low nearly everywhere.</para>
    /// <para>Returns infinity for a burst that spent nothing. That is not margin, it is the
    /// measurement floor: zero errors over a few thousand bits means "more than about 4 dB" and
    /// nothing finer, and a policy should treat it as "as good as this instrument can see". NaN
    /// where the rate has no measured slope, which a caller must treat as unknown rather than as
    /// zero.</para>
    /// </remarks>
    public static double EstimatedMarginDb(double spentFraction, OfdmFmRate rate)
    {
        if (double.IsNaN(rate.ErrorRateFactorPerDb) || rate.ErrorRateFactorPerDb >= 1)
        {
            return double.NaN;
        }

        return spentFraction switch
        {
            <= 0 => double.PositiveInfinity,
            >= 1 => 0,
            _ => Math.Log(spentFraction) / Math.Log(rate.ErrorRateFactorPerDb),
        };
    }

    /// <summary>
    /// Takes a burst that arrived, and updates what this station wants to hear next.
    /// </summary>
    /// <remarks>
    /// A burst that carried a recommendation for us updates <see cref="Transmit"/> whether or not
    /// its payload decoded, because the header has its own CRC and is coded harder than the payload
    /// is. A burst whose header read and whose payload did not is exactly the case where the far
    /// end most needs to be told to slow down, so throwing its advice away would be perverse.
    /// </remarks>
    public void Heard(OfdmFmBurst burst)
    {
        if (burst.Recommendation is OfdmFmRate asked)
        {
            Transmit = asked;
            SendFullBursts = burst.AskedFullBursts;
        }

        // A follow-on burst that failed is a fact about the burst form, not about the rate: on
        // the same link at the same time full bursts at that rate were decoding (measured, see
        // FollowOnFailuresToAskForFullBursts). It is judged there and goes no further, because
        // treating it as a rate failure would step the rate down for a fault the rate did not
        // cause and then step it back up once full bursts were asked for. A follow-on burst that
        // decoded is evidence about the rate like any other.
        if (burst.FollowOn)
        {
            JudgeFollowOn(decoded: burst.Payload is not null);
            if (burst.Payload is null)
            {
                return;
            }
        }
        else if (WantFullBursts && burst.Payload is not null
            && ++_fullBurstsSeen >= _fullBurstsWanted)
        {
            // Enough full bursts have come through cleanly: let follow-on bursts be tried again.
            // If they fail again soon, the next hold is longer.
            WantFullBursts = false;
            _fullBurstsSeen = 0;
            _followOnSinceAllowed = 0;
            _watchingForRelapse = true;
            Array.Clear(_followOnOutcomes);
            _followOnSeen = 0;
        }

        // Anything not on the ladder is watched but not acted on. A correspondent is entitled to
        // transmit a rate this station does not carry, and stepping "down" from a rung we cannot
        // locate would be a guess dressed up as a decision.
        if (OfdmFmRateLadder.IndexOf(burst.Constellation, burst.Coding) < 0)
        {
            return;
        }

        double? spent = OfdmFmRateLadder.SpentFraction(burst);
        if (spent is not double margin)
        {
            // The payload did not decode, or there was no code to spend. Either way the far end is
            // sending something this station cannot copy, which is the one signal that needs no
            // interpretation.
            Retreat();
            return;
        }

        // A window of what this rung has been costing, which every decision reads. A single burst
        // is a count over a few thousand coded bits, so neither direction turns on one of them;
        // only an outright failure does that, and that one carries no measurement error at all.
        _window[_seen % _window.Length] = margin;
        if (++_seen < BurstsBeforeJudgingARate)
        {
            return;
        }

        // Where every rung on the ladder would put us, from where this one has put us. Rung i's
        // margin differs from ours by the difference in their cliffs, and each rate turns margin
        // into spending at its own rate, so this is not a shortcut anyone could take with one
        // number for the whole ladder.
        double here = EstimatedMarginDb(MedianOfWindow(), OfdmFmRateLadder.Rungs[_rung]);
        if (double.IsNaN(here))
        {
            return;
        }

        int best = _rung;
        double bestExpected = ExpectedGoodput(_rung, here);
        for (int r = 0; r < OfdmFmRateLadder.Rungs.Count; r++)
        {
            if (r == _rung)
            {
                continue;
            }

            // Never a candidate to recommend a rung this station's own receive thread was
            // measured not to keep up with, whatever it would otherwise deliver. The far end's
            // own recommendation to us is not filtered here - see Transmit.
            if (OfdmFmRateLadder.Rungs[r].Constellation > _topConstellation)
            {
                continue;
            }

            double expected = ExpectedGoodput(r, here);
            double bar = r > _rung ? bestExpected * StepUpMustBeatCurrentBy : bestExpected;
            if (expected > bar)
            {
                best = r;
                bestExpected = expected;
            }
        }

        if (best < _rung)
        {
            // This rate has stopped earning its keep against a slower one. Not a failure, so no
            // backoff is owed - the link simply changed under us.
            _seen = 0;
            _rung--;
            _justSteppedUp = false;
            return;
        }

        _justSteppedUp = false;
        if (best <= _rung || _seen < _wanted[_rung])
        {
            return;
        }

        _seen = 0;
        _rung++;
        _justSteppedUp = true;
    }

    // What a rung would be expected to deliver, given how much margin we appear to have on the one
    // we are standing on. A rung with a higher cliff has that much less margin at the same signal,
    // and turns whatever margin it has into spending at its own rate.
    private double ExpectedGoodput(int rung, double marginHereDb)
    {
        OfdmFmRate rate = OfdmFmRateLadder.Rungs[rung];
        double marginThere = marginHereDb
            + OfdmFmRateLadder.Rungs[_rung].CliffCarrierToNoiseDb
            - rate.CliffCarrierToNoiseDb;
        return marginThere <= 0
            ? 0
            : rate.GoodputBitsPerSecond * EstimatedFrameSuccess(SpentAt(rate, marginThere));
    }

    // The spending a rate would show at a given margin, which is the inverse of EstimatedMarginDb.
    private static double SpentAt(OfdmFmRate rate, double marginDb) =>
        double.IsInfinity(marginDb) ? 0 : Math.Pow(rate.ErrorRateFactorPerDb, marginDb);

    private double MedianOfWindow()
    {
        int count = Math.Min(_seen, _window.Length);
        Span<double> sorted = stackalloc double[count];
        _window.AsSpan(0, count).CopyTo(sorted);
        sorted.Sort();
        return sorted[count / 2];
    }

    // What a follow-on burst's outcome does to the ask for full bursts. Failures are counted in a
    // window of the last few follow-on bursts, so a lone failure on a link that is otherwise fine
    // costs nothing, and a run of them asks for full bursts at once. A relapse, failures soon
    // after follow-on bursts were allowed again, doubles the number of full bursts the next hold
    // waits for, up to a cap.
    private void JudgeFollowOn(bool decoded)
    {
        _followOnOutcomes[_followOnSeen % _followOnOutcomes.Length] = decoded;
        _followOnSeen++;
        _followOnSinceAllowed++;
        if (WantFullBursts)
        {
            return;
        }

        int failures = 0;
        int counted = Math.Min(_followOnSeen, _followOnOutcomes.Length);
        for (int i = 0; i < counted; i++)
        {
            if (!_followOnOutcomes[i])
            {
                failures++;
            }
        }

        if (failures < FollowOnFailuresToAskForFullBursts)
        {
            return;
        }

        WantFullBursts = true;
        _fullBurstsSeen = 0;
        if (_watchingForRelapse && _followOnSinceAllowed <= FollowOnBurstsJudged)
        {
            _fullBurstsWanted = Math.Min(_fullBurstsWanted * 2, MaxFullBurstsBeforeFollowOnAgain);
        }
        else
        {
            _fullBurstsWanted = FullBurstsBeforeFollowOnAgain;
        }

        _watchingForRelapse = false;
    }

    // Down one rung, because a burst did not decode. If we had only just stepped up, the step is
    // what broke it, so the rung we came from gets harder to leave next time - the backoff that
    // stops this oscillating when the margin estimate is wrong about a particular link.
    private void Retreat()
    {
        _seen = 0;
        if (_rung <= OfdmFmRateLadder.Slowest)
        {
            return;
        }

        _rung--;
        if (_justSteppedUp)
        {
            _wanted[_rung] = Math.Min(_wanted[_rung] * 2, MaxBurstsToJudgeAStepOn);
            _justSteppedUp = false;
        }
    }
}
