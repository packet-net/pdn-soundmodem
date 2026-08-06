namespace Packet.SoundModem.Modems;

/// <summary>
/// Per-branch adaptive MLSE equaliser for the BPSK differential path - rx-roadmap workstream 5.
/// A 4-state Viterbi detector over the absolute BPSK polarities, with a 3-tap complex composite
/// channel estimate adapted by decision-directed LMS. Consumes the symbol-instant baseband
/// sample the DPLL picks (the same sample <see cref="BpskDemodulator.DecideAgainstReference"/>
/// decides today) and emits differentially-decoded bits <see cref="TracebackDepth"/> symbols
/// late, each with a confidence for the erasure ladder.
/// </summary>
/// <remarks>
/// <para><b>Why 3 taps / 4 states.</b> CCIR Poor's 2 ms echo spans 0.6 symbol at 300 Bd, and
/// the DPLL centres its timing somewhere between the two Watterson paths as their relative
/// strength fades, so the composite (TX pulse x channel x matched filter) response sampled at
/// the DPLL instant puts significant energy in up to two neighbours of the dominant tap - on
/// either side. Three taps cover that envelope; the trellis state is the two previous symbol
/// polarities, 4 states, 8 transitions per symbol - negligible beside the per-sample FIR work.
/// The preamble training rule (see AdaptTaps) parks the main energy in the middle tap, so the
/// model spans one pre-cursor and one post-cursor lag - a pre-cursor-heavy timing phase fits
/// as well as a post-cursor one.</para>
/// <para><b>What this buys, measured honestly (2026-08-06 A/B, docs/rx-roadmap.md).</b>
/// Parity with the DF-DD baseline on every rung measured - AWGN, CFO, Good, Moderate, Poor -
/// and no more. The frames CCIR Poor takes from this receiver die of timing wander, composite
/// spectral nulls and flat outage, none of which a symbol-rate trellis can address, and the
/// periodic preamble physically hides the tap split (only h0-h1+h2 is observable), so the
/// outer taps cannot converge before the sync word and header-limited frames cannot benefit.
/// The equaliser is kept as an opt-in detector: the machinery - and those findings - are what
/// workstream 4's two-pass receiver builds on, where converged taps can be applied from the
/// burst's first symbol.</para>
/// <para><b>Degeneracy is the safety property.</b> With the outer taps at zero the branch
/// metric splits per symbol and the trellis decision reduces exactly to the sign of the
/// projection against the main tap - the decision-feedback differential detector's own rule.
/// On a flat channel the equaliser can therefore only differ from the DF-DD baseline by its
/// adaptation noise, which the Watterson masks measure.</para>
/// <para><b>Confidence.</b> The add-compare-select margin - the metric difference between the
/// two paths merging into a state - is the first-order SOVA reliability of the symbol the
/// merging paths disagree on (they share the state's two newest symbols, so that is always the
/// symbol two back). Margins ride the register-exchange alongside the decisions; a
/// differential bit's confidence is the smaller of its two symbols' margins. Only the ordering
/// matters downstream, exactly as for the DF-DD magnitude this replaces.</para>
/// </remarks>
internal sealed class MlseEqualiser
{
    /// <summary>Composite channel taps: model prediction is h[0]*a(k) + h[1]*a(k-1) +
    /// h[2]*a(k-2) for polarity hypotheses a in {-1,+1}.</summary>
    private const int TapCount = 3;

    /// <summary>Trellis states: (a(k-1), a(k-2)) as two bits, +1 encoded as 1.</summary>
    private const int StateCount = 4;

    /// <summary>Traceback delay, in symbols. The 5(K-1) folklore says ~10 for this trellis;
    /// 16 adds margin and stays well inside the ~24 symbol times DCD survives past a burst's
    /// end (see <see cref="BpskModem"/>'s falling-edge reset), so every delayed bit of a
    /// burst - the trailer above all - is out before the deframer is reset.</summary>
    private const int TracebackDepth = 16;

    /// <summary>Register length: a differential bit at the traceback point needs the symbol
    /// one older still.</summary>
    private const int HistoryLength = TracebackDepth + 2;

    /// <summary>LMS step. The DF-DD reference this generalises averages ~20 symbols
    /// (lambda 0.95, an effective rate of 0.05); the same rate here tracks CCIR Poor's 1 Hz
    /// fading (channel coherence ~100 symbols at 300 Bd) with an order of magnitude to
    /// spare, and at this rate the -5..-3 dB AWGN ladder matches the DF-DD baseline
    /// point-for-point (the 2026-08-06 A/B).</summary>
    private const double AdaptationRate = 0.05;

    /// <summary>Per-symbol decay of the outer shadow taps toward zero while they are
    /// adapting - mild, because the engage gate below is what protects the metrics; the leak
    /// only stops a disengaged shadow wandering on old evidence.</summary>
    private const double OuterTapLeak = 0.995;

    /// <summary>Engage threshold for an outer tap, as smoothed |outer|^2 over smoothed
    /// |main|^2: the shadow estimate enters the branch metrics only above this. The ratio
    /// is taken between <see cref="PowerSmoothing"/>-averaged powers because the decision
    /// must ride the evidence, not its noise: with instantaneous powers the shadows' LMS
    /// noise floor crossed the gate in brief excursions, and each transient engagement
    /// poisoned the metrics for a burst of symbols - measured at -4 dB AWGN as 90 %
    /// (engagement disabled) against 44 % (instantaneous gating). 0.05 is an amplitude
    /// ratio of ~0.22: above the delayed-decision shadows' flat-channel noise floor, and
    /// low enough that CCIR Moderate's shallow composite echo (~0.25-0.3 of the main tap)
    /// engages, not only Poor's 0.5-1.0.</summary>
    private const double EngageThreshold = 0.05;

    /// <summary>Disengage threshold (hysteresis partner of <see cref="EngageThreshold"/>):
    /// an engaged outer tap drops out of the metrics below a smoothed power ratio of
    /// this.</summary>
    private const double DisengageThreshold = 0.02;

    /// <summary>Smoothing rate for the tap powers the engage gate compares - ~20 symbols,
    /// the LMS's own timescale.</summary>
    private const double PowerSmoothing = 0.05;

    /// <summary>Symbols of traceback maturity behind the newest hypothesis that the OUTER
    /// taps' LMS adapts on (the main tap adapts undelayed - see AdaptTaps). Tentative
    /// decisions at the Reed-Solomon threshold carry a 10-15 % error rate that correlates
    /// straight into the tap estimates - and the newest decision on a flat channel is pure
    /// tie-break noise, feeding the pre-cursor shadow nothing but noise; six symbols in,
    /// most merges have resolved and the error rate is near the final one, so the shadows'
    /// noise floor drops enough for <see cref="EngageThreshold"/> to sit at 0.05. The cost
    /// is six symbols of tracking lag against fading two orders of magnitude slower, plus
    /// a phase re-alignment by six symbols of the current rotation.</summary>
    private const int AdaptDelay = 6;

    /// <summary>Per-symbol step an outer tap's metric contribution ramps IN with once
    /// engagement is warranted (~8 symbols to full). Engagement lands mid-burst - the
    /// preamble is periodic, so the shadows can only converge from the sync word on - and
    /// a step change in the branch metrics right across the header's 2-parity Reed-Solomon
    /// block was costing the frames it was meant to save.</summary>
    private const double EngageRampStep = 0.125;

    /// <summary>Per-symbol step the contribution ramps OUT with - faster than in, because
    /// a tap that lost its evidence is doing damage now.</summary>
    private const double DisengageRampStep = 0.25;

    /// <summary>Outer-tap decay on the symbols their adaptation skips (the alternating
    /// triples, below): the DF-DD reference's own forgetting rate, so stale ISI evidence
    /// fades over a new burst's preamble just as a stale reference does.</summary>
    private const double FrozenTapLeak = 0.95;

    /// <summary>Per-symbol gain of the decision-directed rotation tracker - twin of
    /// <see cref="BpskDemodulator"/>.ReferenceFrequencyGain, for the same measured
    /// reasons.</summary>
    private const double RotationGain = 0.05;

    /// <summary>Clamp on the tracked rotation, radians per symbol - twin of
    /// <see cref="BpskDemodulator"/>.MaxTrackedRotation: two diversity-bank branch steps,
    /// bounding the free-running tracker's between-burst wander.</summary>
    private const double MaxTrackedRotation = Math.PI / 10;

    private readonly Action<int, float> _sink;
    private readonly double[] _tapReal = new double[TapCount];
    private readonly double[] _tapImag = new double[TapCount];
    private readonly double[] _metric = new double[StateCount];
    private readonly double[] _metricNext = new double[StateCount];
    private readonly uint[] _history = new uint[StateCount];
    private readonly uint[] _historyNext = new uint[StateCount];
    private readonly float[][] _margins;
    private readonly float[][] _marginsNext;
    private readonly double[] _delayedI = new double[AdaptDelay];
    private readonly double[] _delayedQ = new double[AdaptDelay];
    private double _trackedRotation;
    private long _symbolCount;
    private double _mainPower;
    private double _preShadowPower;
    private double _postShadowPower;
    private double _preRamp;
    private double _postRamp;
    private bool _preEngaged;
    private bool _postEngaged;
    private bool _burstActive;

    /// <summary>Creates the equaliser.</summary>
    /// <param name="sink">Receives each decided bit (1 = phase repeat) with its raw
    /// reliability margin, <see cref="TracebackDepth"/> symbols after the symbol arrived
    /// (immediately on <see cref="Flush"/>). The caller squashes the margin into a
    /// confidence; only its ordering is meaningful.</param>
    public MlseEqualiser(Action<int, float> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _margins = new float[StateCount][];
        _marginsNext = new float[StateCount][];
        for (int s = 0; s < StateCount; s++)
        {
            _margins[s] = new float[HistoryLength];
            _marginsNext[s] = new float[HistoryLength];
            Array.Fill(_margins[s], float.PositiveInfinity);
        }
    }

    /// <summary>Zeroes the tracked rotation - called when the demodulator seeds the burst
    /// rotation from its offset window, mirroring the DF-DD path's tracker reset on
    /// seeding.</summary>
    public void OnRotationSeeded() => _trackedRotation = 0;

    /// <summary>Processes one symbol-instant baseband sample.</summary>
    /// <param name="zI">In-phase symbol-instant sample.</param>
    /// <param name="zQ">Quadrature symbol-instant sample.</param>
    /// <param name="seededRotation">The demodulator's per-burst seeded rotation, radians per
    /// symbol; added to the internal tracked rotation and applied to the taps.</param>
    /// <param name="burstActive">Whether the demodulator's offset window currently holds a
    /// seeded burst. Outer-tap engagement is allowed only while true: between bursts the
    /// noise walks the main tap and the shadows to similar magnitudes, so a power RATIO is
    /// meaningless there - measured as every burst starting its preamble with garbage outer
    /// taps engaged, and 0/50 at -5 dB AWGN. A burst too weak to seed simply never engages
    /// the outers and gets the DF-DD-degenerate detector, which is the receiver that works
    /// at those SNRs anyway.</param>
    public void Push(float zI, float zQ, double seededRotation, bool burstActive)
    {
        _burstActive = burstActive;
        double rotation = seededRotation + _trackedRotation;
        RotateTaps(rotation);

        // The metrics see an outer tap scaled by its engagement ramp - zero while
        // disengaged (the DF-DD-degenerate detector), full once its evidence has been in
        // for a while. The shadow estimates keep adapting either way (see AdaptTaps).
        double preR = _preRamp * _tapReal[0];
        double preQ = _preRamp * _tapImag[0];
        double postR = _postRamp * _tapReal[2];
        double postQ = _postRamp * _tapImag[2];

        // Add-compare-select over the 8 transitions. Predictions are formed from the
        // pre-rotated taps; ties break toward the first candidate so a cold start (all
        // metrics equal, taps zero) is deterministic.
        for (int next = 0; next < StateCount; next++)
        {
            double aK = (next & 2) != 0 ? 1.0 : -1.0;      // newest hypothesis a(k)
            double aK1 = (next & 1) != 0 ? 1.0 : -1.0;     // a(k-1), shared with predecessors
            double bestMetric = double.PositiveInfinity;
            double otherMetric = double.PositiveInfinity;
            int bestPredecessor = 0;
            for (int older = 1; older >= 0; older--)
            {
                double aK2 = older != 0 ? 1.0 : -1.0;      // a(k-2), where the predecessors differ
                int predecessor = ((aK1 > 0 ? 1 : 0) << 1) | older;
                double predictedR = (aK * preR) + (aK1 * _tapReal[1]) + (aK2 * postR);
                double predictedQ = (aK * preQ) + (aK1 * _tapImag[1]) + (aK2 * postQ);
                double errorR = zI - predictedR;
                double errorQ = zQ - predictedQ;
                double metric = _metric[predecessor] + (errorR * errorR) + (errorQ * errorQ);
                if (metric < bestMetric)
                {
                    otherMetric = bestMetric;
                    bestMetric = metric;
                    bestPredecessor = predecessor;
                }
                else if (metric < otherMetric)
                {
                    otherMetric = metric;
                }
            }

            _metricNext[next] = bestMetric;
            _historyNext[next] = (_history[bestPredecessor] << 1) | (uint)((next & 2) >> 1);

            // Register-exchange the margins one slot older, open a fresh slot for a(k), and
            // record the merge margin against the symbol the losing path disagreed on: a(k-2),
            // slot 2. Later merges that would also constrain older symbols are ignored - the
            // truncated SOVA that ranking (not an LLR consumer) needs.
            float[] from = _margins[bestPredecessor];
            float[] into = _marginsNext[next];
            for (int i = HistoryLength - 1; i >= 1; i--)
            {
                into[i] = from[i - 1];
            }

            into[0] = float.PositiveInfinity;
            float delta = (float)(otherMetric - bestMetric);
            if (delta < into[2])
            {
                into[2] = delta;
            }
        }

        // Swap the double buffers and renormalise so metrics stay small forever.
        for (int s = 0; s < StateCount; s++)
        {
            _metric[s] = _metricNext[s];
            _history[s] = _historyNext[s];
            (_margins[s], _marginsNext[s]) = (_marginsNext[s], _margins[s]);
        }

        int best = BestState();
        double floor = _metric[best];
        for (int s = 0; s < StateCount; s++)
        {
            _metric[s] -= floor;
        }

        AdaptTaps(zI, zQ, best, rotation);
        _symbolCount++;
        if (_symbolCount >= HistoryLength)
        {
            EmitAt(best, TracebackDepth);
        }
    }

    /// <summary>Emits every bit still inside the traceback register from the current best
    /// survivor and clears the path memory. The streaming demodulator never calls this - a
    /// mid-stream flush would swallow a pipeline of bits while the register refills, and a
    /// fading channel hands the demodulator no edge that cannot also fire mid-frame - it
    /// exists for finite-input consumers (tests above all) that need the bit count to
    /// balance without trailing padding. The taps and tracked rotation survive, exactly as
    /// the DF-DD reference and tracker persist between bursts.</summary>
    public void Flush()
    {
        int best = BestState();
        int newest = (int)Math.Min(_symbolCount - 2, TracebackDepth - 1);
        for (int position = newest; position >= 0; position--)
        {
            EmitAt(best, position);
        }

        ClearPathMemory();
    }

    /// <summary>Full reset - taps, rotation tracker and path memory - for
    /// <see cref="BpskDemodulator.ResetCarrierState"/>.</summary>
    public void Reset()
    {
        Array.Clear(_tapReal);
        Array.Clear(_tapImag);
        _trackedRotation = 0;
        _mainPower = 0;
        _preShadowPower = 0;
        _postShadowPower = 0;
        _preRamp = 0;
        _postRamp = 0;
        _preEngaged = false;
        _postEngaged = false;
        ClearPathMemory();
    }

    private void ClearPathMemory()
    {
        Array.Clear(_metric);
        Array.Clear(_history);
        Array.Clear(_delayedI);
        Array.Clear(_delayedQ);
        for (int s = 0; s < StateCount; s++)
        {
            Array.Fill(_margins[s], float.PositiveInfinity);
        }

        _symbolCount = 0;
    }

    private int BestState()
    {
        int best = 0;
        for (int s = 1; s < StateCount; s++)
        {
            if (_metric[s] < _metric[best])
            {
                best = s;
            }
        }

        return best;
    }

    /// <summary>Emits the differential bit between the survivor's symbols at
    /// <paramref name="position"/> and one older (1 = same polarity, the IL2P repeat), with
    /// the smaller of the two symbols' margins as its reliability.</summary>
    private void EmitAt(int state, int position)
    {
        uint bits = _history[state];
        int a = (int)((bits >> position) & 1);
        int aOlder = (int)((bits >> (position + 1)) & 1);
        float margin = Math.Min(_margins[state][position], _margins[state][position + 1]);
        _sink(a == aOlder ? 1 : 0, margin);
    }

    /// <summary>One decision-directed LMS step plus the rotation-tracker update - the same
    /// two jobs <see cref="BpskDemodulator.DecideAgainstReference"/> does for its single
    /// reference. The tracker runs on the newest hypotheses (phase is time-critical); the
    /// taps adapt on the survivor's <see cref="AdaptDelay"/>-old decisions against the
    /// matching buffered sample, where the decision error rate is near-final rather than
    /// tentative.</summary>
    private void AdaptTaps(float zI, float zQ, int best, double rotation)
    {
        uint bits = _history[best];

        // Rotation tracker and the MAIN tap, on the current symbol against the metric
        // view's prediction. Both stay undelayed deliberately: the main tap's phase and the
        // tracker form a per-symbol feedback loop exactly like the DF-DD reference's, and
        // putting the tap adaptation AdaptDelay symbols behind wrapped that delay into the
        // loop - measured as a broad ~1-2 dB AWGN loss from the resulting phase jitter.
        // The depth-1 decision the main update uses is the DF-DD-equivalent one.
        double n0 = (bits & 1) != 0 ? 1.0 : -1.0;
        double n1 = (bits & 2) != 0 ? 1.0 : -1.0;
        double n2 = (bits & 4) != 0 ? 1.0 : -1.0;
        double predictedR = (n0 * _preRamp * _tapReal[0]) + (n1 * _tapReal[1])
            + (n2 * _postRamp * _tapReal[2]);
        double predictedQ = (n0 * _preRamp * _tapImag[0]) + (n1 * _tapImag[1])
            + (n2 * _postRamp * _tapImag[2]);
        double cross = (zQ * predictedR) - (zI * predictedQ);
        double magnitude = Math.Sqrt(
            ((double)zI * zI + (double)zQ * zQ)
            * ((predictedR * predictedR) + (predictedQ * predictedQ)));
        if (magnitude > 1e-12)
        {
            double angle = Math.Asin(Math.Clamp(cross / magnitude, -1, 1));
            _trackedRotation = Math.Clamp(
                _trackedRotation + (RotationGain * angle),
                -MaxTrackedRotation, MaxTrackedRotation);
        }

        _tapReal[1] += AdaptationRate * (zI - predictedR) * n1;
        _tapImag[1] += AdaptationRate * (zQ - predictedQ) * n1;

        // The sample ring: read the one AdaptDelay symbols old, then overwrite it.
        int slot = (int)(_symbolCount % AdaptDelay);
        double delayedI = _delayedI[slot];
        double delayedQ = _delayedQ[slot];
        _delayedI[slot] = zI;
        _delayedQ[slot] = zQ;
        if (_symbolCount < AdaptDelay + 2)
        {
            return;   // the ring or the delayed triple is not yet filled since the last reset
        }

        // The delayed sample was received before the taps' last AdaptDelay rotation steps;
        // rotate it forward to the taps' present phase (the rate is taken as constant over
        // the lag, which the clamps bound to a couple of degrees of error).
        double alignAngle = AdaptDelay * rotation;
        double alignCos = Math.Cos(alignAngle);
        double alignSin = Math.Sin(alignAngle);
        (delayedI, delayedQ) = (
            (delayedI * alignCos) - (delayedQ * alignSin),
            (delayedI * alignSin) + (delayedQ * alignCos));

        double a0 = (bits & (1u << AdaptDelay)) != 0 ? 1.0 : -1.0;
        double a1 = (bits & (1u << (AdaptDelay + 1))) != 0 ? 1.0 : -1.0;
        double a2 = (bits & (1u << (AdaptDelay + 2))) != 0 ? 1.0 : -1.0;

        // The IL2P preamble is all reversals - a perfectly periodic input on which three
        // taps are unidentifiable (the observation only constrains h0-h1+h2), and
        // unconstrained LMS splits the main energy equally across all three, walking into
        // the sync word with large spurious ISI taps on a flat channel. The degenerate
        // update direction is exactly the alternating triple, so the rule is per-symbol and
        // stateless: when the three decisions alternate, only the main tap adapts. The
        // preamble - at any SNR, whatever the decision error rate does to run lengths -
        // therefore trains the main tap alone, exactly the DF-DD bootstrap, and parks the
        // main energy in the middle tap so the model covers one pre-cursor and one
        // post-cursor lag. Random payload alternates one triple in four, leaving the outer
        // taps three-quarters of their nominal adaptation rate.
        bool frozen = a0 != a1 && a1 != a2;

        // The error is formed against the ramped view - the same shape the metrics used.
        // While an outer tap is disengaged, the ISI it is not yet modelling stays in this
        // error, which is exactly what drives its shadow toward the true echo. Only the
        // outer taps adapt from here - the main tap already took its undelayed step above.
        double delayedPredictedR = (a0 * _preRamp * _tapReal[0]) + (a1 * _tapReal[1])
            + (a2 * _postRamp * _tapReal[2]);
        double delayedPredictedQ = (a0 * _preRamp * _tapImag[0]) + (a1 * _tapImag[1])
            + (a2 * _postRamp * _tapImag[2]);
        double errorR = delayedI - delayedPredictedR;
        double errorQ = delayedQ - delayedPredictedQ;

        if (frozen)
        {
            _tapReal[0] *= FrozenTapLeak;
            _tapImag[0] *= FrozenTapLeak;
            _tapReal[2] *= FrozenTapLeak;
            _tapImag[2] *= FrozenTapLeak;
        }
        else
        {
            _tapReal[0] = (OuterTapLeak * _tapReal[0]) + (AdaptationRate * errorR * a0);
            _tapImag[0] = (OuterTapLeak * _tapImag[0]) + (AdaptationRate * errorQ * a0);
            _tapReal[2] = (OuterTapLeak * _tapReal[2]) + (AdaptationRate * errorR * a2);
            _tapImag[2] = (OuterTapLeak * _tapImag[2]) + (AdaptationRate * errorQ * a2);
        }

        // An outer tap engages while its shadow carries real, SUSTAINED evidence of ISI -
        // smoothed power measured against the main tap's, with hysteresis so the boundary
        // does not chatter, and only while a seeded burst is present (see Push). The gate
        // is what preserves the flat-channel degeneracy at low SNR: unconditional exposure
        // measured a ~5 dB AWGN loss from metric self-noise alone, and instantaneous
        // gating still lost half of it to transient crossings.
        _mainPower += PowerSmoothing
            * ((_tapReal[1] * _tapReal[1]) + (_tapImag[1] * _tapImag[1]) - _mainPower);
        _preShadowPower += PowerSmoothing
            * ((_tapReal[0] * _tapReal[0]) + (_tapImag[0] * _tapImag[0]) - _preShadowPower);
        _postShadowPower += PowerSmoothing
            * ((_tapReal[2] * _tapReal[2]) + (_tapImag[2] * _tapImag[2]) - _postShadowPower);
        if (_burstActive)
        {
            _preEngaged = _preShadowPower
                > (_preEngaged ? DisengageThreshold : EngageThreshold) * _mainPower;
            _postEngaged = _postShadowPower
                > (_postEngaged ? DisengageThreshold : EngageThreshold) * _mainPower;
        }
        else
        {
            _preEngaged = false;
            _postEngaged = false;
        }

        _preRamp = _preEngaged
            ? Math.Min(1, _preRamp + EngageRampStep)
            : Math.Max(0, _preRamp - DisengageRampStep);
        _postRamp = _postEngaged
            ? Math.Min(1, _postRamp + EngageRampStep)
            : Math.Max(0, _postRamp - DisengageRampStep);
    }

    private void RotateTaps(double rotation)
    {
        if (rotation == 0)
        {
            return;
        }

        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);
        for (int l = 0; l < TapCount; l++)
        {
            (_tapReal[l], _tapImag[l]) = (
                (_tapReal[l] * cos) - (_tapImag[l] * sin),
                (_tapReal[l] * sin) + (_tapImag[l] * cos));
        }
    }
}
