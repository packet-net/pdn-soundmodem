using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// BPSK demodulator emitting one logical bit per symbol (per the IL2P symbol map: 1 = phase
/// repeat, 0 = reversal) - feed straight into <see cref="M0LTE.Il2p.Il2pDeframer"/>. Covers the
/// NinoTNC 300 (mode 8) and 1200 (mode 10) BPSK symbol rates. Two detection methods (see
/// <see cref="PskDetector"/>):
/// <list type="bullet">
/// <item><b>Differential</b> (the default): complex mix to baseband → root-raised-cosine
/// matched filter → decision-feedback differential detection against a remodulated carrier
/// reference (see <see cref="DecideAgainstReference"/>). Acquires within a couple of symbols,
/// tolerates ±baud/4 of carrier offset on a single branch, and measures within ~0.7 dB of
/// ideal coherent BPSK - the differential detector's classic ~1 dB give-away bought back
/// without a carrier loop that can lose lock.</item>
/// <item><b>Coherent</b>: band-pass → <see cref="CostasLoop"/> carrier recovery → ⅔-baud
/// low-pass → absolute symbols differentially decoded downstream - what the NinoTNC does,
/// kept exactly as measured for issue #5 as the acquisition cross-check variant.</item>
/// <item><b>Mlse</b>: the differential front-end unchanged, with the per-symbol decision
/// handed to a 4-state adaptive <see cref="MlseEqualiser"/> instead of the decision-feedback
/// reference - rx-roadmap workstream 5's answer to multipath ISI. Bits emerge a traceback
/// (16 symbols) late, which the deframer never notices.</item>
/// </list>
/// </summary>
public sealed class BpskDemodulator
{
    /// <summary>Memory of the carrier-offset window, per contributing sample - ~1000 samples,
    /// so the reading describes roughly the last tenth of a second of signal rather than the
    /// whole burst. See <see cref="CarrierOffsetHz"/>.</summary>
    private const double OffsetWindowRate = 0.001;

    /// <summary>Coherence (0..1) below which the offset window is noise rather than a signal.
    /// Matches <see cref="BpskCarrierOffsetEstimator.HasEstimate"/>: comfortably above the
    /// noise floor, well below a clean signal's ~0.9.</summary>
    private const double OffsetCoherenceFloor = 0.5;

    /// <summary>Samples between oscillator renormalisations; see
    /// <see cref="AfskDemodulator"/>'s twin constant.</summary>
    private const int OscillatorRenormInterval = 4096;

    /// <summary>DPLL inertia for the differential path. Dire Wolf's 0.74 (which the coherent
    /// path keeps) costs ~1 dB of timing jitter at this mode's Reed-Solomon threshold;
    /// measured on the −3 dB AWGN BER floor, 0.92 recovers most of it while the all-reversal
    /// IL2P preamble (a transition every symbol) still pulls a cold clock in within a normal
    /// TXDELAY. 0.96+ measured better still in steady state but failed acquisition on the
    /// 24-bit minimum preamble.</summary>
    private const double DifferentialInertia = 0.92;

    /// <summary>DPLL inertia once DCD holds: the clock acquires at
    /// <see cref="DifferentialInertia"/> and then holds nearly rigid - see
    /// <see cref="QpskDemodulator"/>, where it was measured on the off-air fixture. DCD is the
    /// gate here, not the offset window's seed: <see cref="PacketDcd"/> asserting means 30 of
    /// the last 32 transitions landed inside an eighth of a symbol, which is the clock having
    /// converged, whereas BPSK's square-law seed fires within a dozen symbols of a burst, and a
    /// clock frozen that early keeps its residual error (measured: the QtSM bpsk300 fixture
    /// 10 -> 8 frames, a single modem losing a 30 Hz burst, and DCD itself no longer asserting
    /// on the transitions it then scores). Gated on DCD it is neutral on the sim's knee rows
    /// (bpsk300 -5/-4 dB 174/197 and 173/194 of 200 with or without it, the seven-phase
    /// figures) and kept for the reason QpskDemodulator measured on the fixture: a clock that
    /// no longer wanders is a clock the timing phases can reach past.</summary>
    private const double HoldInertia = 0.995;

    /// <summary>Reference memory λ of the decision-feedback detector: the reference averages
    /// ~1/(1−λ) = 20 symbols, taking its effective noise well below the single symbol plain
    /// differential detection divides by. Longer memories measured no better once the
    /// frequency loop was in place, and forget fades more slowly.</summary>
    private const double ReferenceMemory = 0.95;

    /// <summary>Per-symbol gain of the reference frequency loop. Measured at −3 dB AWGN:
    /// 0.02 leaves acquisition lag on mid-branch offsets, 0.3 jitters; 0.05-0.15 are
    /// equivalent and flat across the bank's offset span.</summary>
    private const double ReferenceFrequencyGain = 0.05;

    /// <summary>Clamp on the seeded part of the reference rotation, in radians per symbol:
    /// π/2 is a ±baud/4 offset, the offset window's own unambiguous range and the
    /// single-branch tolerance the plain differential detector always had (±75 Hz at
    /// 300 Bd).</summary>
    private const double MaxSeededRotation = Math.PI / 2;

    /// <summary>Clamp on the tracked part of the reference rotation, in radians per symbol:
    /// π/10 is two diversity-bank branch steps (±15 Hz at 300 Bd, the step being baud/40).
    /// The tracker free-runs so that a burst too weak to seed still captures a mid-branch
    /// residual from its first symbols, and this bound is what keeps its noise wander
    /// between bursts from carrying a large stale rotation into the next acquisition. It
    /// also means every branch tracks every signal within two steps, whose
    /// quasi-independent decisions add selection diversity - measured worth several points
    /// of frame rate at the Reed-Solomon threshold.</summary>
    private const double MaxTrackedRotation = Math.PI / 10;

    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly MlseEqualiser? _mlse;
    private readonly Action<int> _bitSink;
    private readonly Action<int, float, int>? _softBitSink;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly PskDetector _detector;
    private readonly CostasLoop? _costas;
    private readonly float[] _delayI;
    private readonly float[] _delayQ;
    private readonly double _rotateCos;
    private readonly double _rotateSin;
    private readonly int _sampleRate;
    private double _oscillatorCos = 1;
    private double _oscillatorSin;
    private int _renormCountdown = OscillatorRenormInterval;
    private double _averageDiffMagnitude;
    private double _offsetWindowReal;
    private double _offsetWindowImag;
    private int _delayPosition;
    private int _previousLevel;
    private float _previousDecision;
    private float _previousI;
    private float _lastPlotI;
    private float _currentI;
    private float _currentQ;
    private double _seededRotation;
    private bool _rotationSeeded;

    // Per-timing-phase decision state (see TimingDiversity), indexed like its PhaseFractions:
    // the decision-feedback reference, its rotation tracker, the polarity the differential bit
    // is decided against, the running confidence mean, and the previous symbol's decision
    // magnitude for the pair-min per-bit confidence (MaxValue = no previous symbol yet, so a
    // burst's first bit is judged on its own magnitude alone).
    private readonly double[] _referenceI;
    private readonly double[] _referenceQ;
    private readonly double[] _trackedRotation;
    private readonly int[] _previousPolarity;
    private readonly double[] _confidenceMean;
    private readonly float[] _previousDecisionMagnitude;

    // Recent baseband samples, indexed by input sample modulo the ring length, so a decision
    // can be taken a little before or after the clock's instant (the late phase needs samples
    // that arrive after the instant, hence the deferral in DecidePending).
    private readonly float[] _ringI;
    private readonly float[] _ringQ;
    private readonly double _phaseOffsetSamples;
    private readonly int _ringLead;
    private long _pendingInstant = -1;

    private long _sampleIndex = -1;
    private int _scheduleIndex;

    /// <summary>Raised once per recovered symbol with the 1-D decision as (I,0): the recovered
    /// absolute symbol in coherent mode, the differential product in differential mode.
    /// Null-safe; wire from the modem.</summary>
    public Action<float, float>? SymbolPlotted { get; set; }

    /// <summary>Bench seam (rx-roadmap workstream 4): receives the input-sample index of every
    /// symbol instant this demodulator sampled at. Set before the first
    /// <see cref="Process(ReadOnlySpan{float})"/> call; indices count input samples from zero.
    /// </summary>
    internal Action<long>? SymbolInstantObserver { get; set; }

    /// <summary>Bench seam (rx-roadmap workstream 4): when set, symbols are sampled at these
    /// input-sample indices instead of at the DPLL's own wraps - the timing oracle. The DPLL
    /// still runs, still observes transitions and still drives <see cref="PacketDcd"/>, so a
    /// schedule replaces exactly one variable (when the symbol is read) and holds carrier
    /// detection, which gates the deframer, constant. Indices must ascend; instants beyond the
    /// end of the schedule emit nothing.</summary>
    internal IReadOnlyList<long>? SymbolInstantSchedule { get; set; }

    /// <summary>Creates a demodulator delivering logical bits to <paramref name="bitSink"/>
    /// once per symbol.</summary>
    /// <param name="sampleRate">Input sample rate (must be a multiple of
    /// <paramref name="baud"/>).</param>
    /// <param name="bitSink">Receives each decided bit (1 = phase repeat, 0 = reversal).</param>
    /// <param name="carrierFrequency">Carrier centre, 1500 Hz by convention.</param>
    /// <param name="baud">Symbol rate: 300 (mode 8) or 1200 (mode 10).</param>
    /// <param name="detector">Differential (default) or coherent detection.</param>
    /// <param name="loopBandwidthHz">Costas loop bandwidth (coherent only); defaults to 6 %
    /// of the symbol rate, tuned against measurement.</param>
    /// <param name="rollOff">The transmitter's root-raised-cosine roll-off, which the
    /// differential path's matched filter mirrors. Defaults to
    /// <see cref="BpskModulator.DefaultRollOff"/>; <see cref="BpskModem"/> passes the
    /// per-mode value (0.20 for the 300 Bd mode).</param>
    /// <param name="softBitSink">When supplied, receives each decided bit together with a
    /// confidence in (0, 1) - the symbol's decision magnitude against a slow running mean,
    /// so a faded or hit symbol ranks low - and the timing phase that decided it (see
    /// <see cref="TimingDiversity"/>; phase 0 is the clock's instant, the others a little
    /// early and late). Feed each phase to its own <see cref="Il2pReceiver.PushBit(int, float)"/>
    /// and failed Reed-Solomon blocks retry with the weakest bytes erased.
    /// <paramref name="bitSink"/> is called for phase 0 either way.</param>
    public BpskDemodulator(
        int sampleRate, Action<int> bitSink, double carrierFrequency = 1500, int baud = 300,
        PskDetector detector = PskDetector.Differential, double? loopBandwidthHz = null,
        double rollOff = BpskModulator.DefaultRollOff,
        Action<int, float, int>? softBitSink = null)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        _detector = detector;
        _bitSink = bitSink;
        _softBitSink = softBitSink;
        if (detector == PskDetector.Mlse)
        {
            _mlse = new MlseEqualiser((bit, magnitude) => EmitBit(0, bit, magnitude));
        }

        // QtSoundModem's P300 band-pass, scaled by symbol rate: ±baud about the carrier (which
        // lands on Nino's published OBW at both rates - 500 Hz at 300 Bd, 2400 Hz at 1200 Bd).
        // It gates EnergyBusy for both detectors, and fronts the decode chain only on the
        // coherent path: on the differential path it costs a measured ~1.5 dB (its passband is
        // not flat across the signal's ±(1+rollOff)·baud/2, which breaks the matched-filter
        // condition and adds ISI), and the matched filter below provides the selectivity - its
        // stopband measures −54 dB one whole baud out, −77 dB at two.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        if (detector == PskDetector.Coherent)
        {
            // The issue-#5 measured configuration, untouched: ⅔-baud low-pass I/Q.
            _lowPassI = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
            _lowPassQ = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
            _costas = new CostasLoop(sampleRate, carrierFrequency, loopBandwidthHz ?? baud * 0.06);
        }
        else
        {
            // Root-raised-cosine matched to the transmitter's shaping: the cascade is a
            // raised cosine - ISI-free at the symbol instants, minimal noise bandwidth.
            // Replacing the ⅔-baud low-pass measured ~0.5 dB at the RS threshold.
            float[] taps = MatchedFilterTaps(sampleRate, baud, rollOff);
            _lowPassI = new FirFilter(taps);
            _lowPassQ = new FirFilter((float[])taps.Clone());
        }

        double step = 2 * Math.PI * carrierFrequency / sampleRate;
        _rotateCos = Math.Cos(step);
        _rotateSin = Math.Sin(step);
        _sampleRate = sampleRate;

        int samplesPerSymbol = sampleRate / baud;
        _delayI = new float[samplesPerSymbol];
        _delayQ = new float[samplesPerSymbol];

        int phases = TimingDiversity.PhaseCount;
        _referenceI = new double[phases];
        _referenceQ = new double[phases];
        _trackedRotation = new double[phases];
        _previousPolarity = new int[phases];
        Array.Fill(_previousPolarity, 1);
        _confidenceMean = new double[phases];
        _previousDecisionMagnitude = new float[phases];
        Array.Fill(_previousDecisionMagnitude, float.MaxValue);
        _phaseOffsetSamples = TimingDiversity.Reach * samplesPerSymbol;
        // The late phase reads up to ceil(offset) samples past the instant, plus one for the
        // interpolation's upper neighbour; the ring spans both sides of the instant with room.
        _ringLead = (int)Math.Ceiling(_phaseOffsetSamples) + 1;
        int ring = (2 * _ringLead) + 4;
        _ringI = new float[ring];
        _ringQ = new float[ring];
        _dpll = new BitDpll(
            baud, sampleRate,
            level =>
            {
                // Under a timing oracle the DPLL still runs for its transition/DCD duties,
                // but the schedule decides when a symbol is read (see Process).
                if (SymbolInstantSchedule is null)
                {
                    OnSymbolInstant(level);
                }
            },
            inertia: detector == PskDetector.Coherent ? 0.74 : DifferentialInertia,
            transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
        _energyBusy = new EnergyBusyDetector(sampleRate);
    }

    /// <summary>Reads one symbol at the current sampling instant, whether the DPLL chose it or a
    /// timing oracle did.</summary>
    private void OnSymbolInstant(int level)
    {
        SymbolInstantObserver?.Invoke(_sampleIndex);
        SymbolPlotted?.Invoke(_lastPlotI, 0f);
        // Coherent feeds the absolute sign bit; differentially decode against the
        // previous symbol (a repeat is a '1'), resolving the loop's π ambiguity.
        // Differential decides the symbol-instant sample against the reference.
        // MLSE hands the symbol-instant sample to the trellis, whose bits emerge
        // through EmitBit a traceback later.
        if (_detector == PskDetector.Mlse)
        {
            // Only the seed event matters here. The burst-over event must NOT
            // flush the trellis: on a fading channel a deep mid-frame fade
            // collapses the offset window's coherence exactly like a burst end
            // does, and a mid-frame path-memory reset swallows a pipeline's worth
            // of bits - a bit slip that destroys the frame's RS block alignment
            // (measured: flushing here cost ~15 points on CCIR Moderate). The
            // pipeline free-runs instead, one bit per symbol, always; a real
            // burst's final bits still emerge within the traceback (16 symbols),
            // inside the ~24-symbol window before the deframer's DCD reset.
            if (UpdateRotationSeed() == RotationSeedEvent.Seeded)
            {
                _mlse!.OnRotationSeeded();
            }

            _mlse!.Push(_currentI, _currentQ, _seededRotation, _rotationSeeded);
            return;
        }

        if (_detector == PskDetector.Coherent)
        {
            int decided = level == _previousLevel ? 1 : 0;
            _previousLevel = level;
            float magnitude = Math.Abs(_lastPlotI);
            float pairMin = Math.Min(magnitude, _previousDecisionMagnitude[0]);
            _previousDecisionMagnitude[0] = magnitude;
            EmitBit(0, decided, pairMin);
            return;
        }

        // Differential decides later, once the samples either side of this instant are in the
        // ring: the instant itself and the early and late timing phases (see DecidePending).
        if (_pendingInstant >= 0)
        {
            DecidePending();
        }

        _pendingInstant = _sampleIndex;
    }

    /// <summary>Decides the symbol whose clock instant is pending, at each timing phase: the
    /// per-burst seed advances once, then every phase reads its own interpolated baseband
    /// sample from the ring and decides against its own reference.</summary>
    private void DecidePending()
    {
        long instant = _pendingInstant;
        _pendingInstant = -1;
        if (UpdateRotationSeed() == RotationSeedEvent.Seeded)
        {
            Array.Clear(_trackedRotation);
        }

        // The trackers keep free-running between bursts, as #236 measured: their noise walk
        // is bounded by the clamp, BPSK's 90° margin absorbs it, and the quasi-independent
        // decisions it gives the bank's branches are selection diversity worth several
        // points at the threshold. Gating them on signal presence, as QpskDemodulator does,
        // measured 131/186 -> 120/170 of 200 on bpsk1200 at +1/+2 dB here.

        // The clock acquires at the differential inertia and holds, nearly rigid, once DCD
        // says it has converged - see HoldInertia.
        _dpll.Inertia = _packetDcd.Asserted ? HoldInertia : DifferentialInertia;

        int ring = _ringI.Length;
        for (int phase = 0; phase < TimingDiversity.PhaseCount; phase++)
        {
            double position = instant + (TimingDiversity.PhaseFractions[phase] * _delayI.Length);
            long lower = (long)Math.Floor(position);
            float fraction = (float)(position - lower);
            int a = (int)(((lower % ring) + ring) % ring);
            int b = (a + 1) % ring;
            float i = _ringI[a] + (fraction * (_ringI[b] - _ringI[a]));
            float q = _ringQ[a] + (fraction * (_ringQ[b] - _ringQ[a]));
            int decided = DecideAgainstReference(phase, i, q, out double projection);
            float magnitude = (float)Math.Abs(projection);

            // A differentially-decoded bit is a function of TWO symbol decisions - bit k
            // compares symbol k's polarity against symbol k-1's, so an error in EITHER flips
            // it, and a single hit symbol flips two consecutive bits. The projection magnitude
            // above measures only the current symbol (the decision-directed reference is
            // smoothed over many, so one weak predecessor barely dents it), which left the
            // second bit of every such pair looking confident - and a bit-level chase decoder
            // hunting the weakest bits could never find it (measured: 0 chase hits in 50
            // attempts at the AWGN knee before this line). The honest per-bit confidence is
            // the weaker of the two symbols the bit depends on. Coherent detection decides
            // bits the same differential way (level == previous), so it gets the same
            // treatment; MLSE margins come from the trellis, which already spans both.
            float pairMin = Math.Min(magnitude, _previousDecisionMagnitude[phase]);
            _previousDecisionMagnitude[phase] = magnitude;
            EmitBit(phase, decided, pairMin);
        }
    }

    /// <summary>True while DPLL transition timing indicates a coherent packet signal.</summary>
    public bool CarrierDetect => _packetDcd.Asserted;

    /// <summary>
    /// How far the signal sat from <em>this</em> demodulator's own carrier centre, in Hz
    /// (positive = above it), or <c>null</c> when nothing coherent enough to measure is present.
    /// </summary>
    /// <remarks>
    /// <para>This is a measurement, not a label, and it is what makes a
    /// <see cref="BpskMultiModem"/> branch's reading honest however far out the branch that
    /// copied a frame happened to be: branch step + this residual is the station's offset from
    /// the bank's centre (see issue #202).</para>
    /// <para><b>Differential.</b> The chain still forms z·conj(z one symbol ago) per sample for
    /// symbol timing; that product's angle is the per-symbol carrier rotation plus a 0-or-π
    /// data step, so squaring the normalised product removes the data and leaves a phasor at
    /// twice the offset - the estimate rides for free on arithmetic the timing path was doing
    /// anyway. This is <see cref="BpskCarrierOffsetEstimator"/>'s algorithm inlined (that class
    /// stays as the standalone way to measure a channel without decoding it), with one
    /// difference that matters here: it peak-holds since the last reset, which would freeze on
    /// the first strong burst of a 26-hour session, whereas this windows the recent signal so a
    /// frame's reading describes <em>that</em> frame. Unambiguous over ±baud/4 - ±75 Hz at
    /// 300 Bd, far wider than any bank's span. The same window seeds the decision-feedback
    /// detector's frequency loop (see <see cref="DecideAgainstReference"/>).</para>
    /// <para><b>Coherent.</b> The Costas NCO is already tracking the carrier, so its frequency
    /// correction is the residual directly; it is only trustworthy while the loop is on a
    /// signal, hence the DCD gate.</para>
    /// <para>Read it when a frame arrives - between bursts the window decays into the noise and
    /// this goes null, which is the honest answer to "how far off was the station" when there
    /// is no station.</para>
    /// </remarks>
    public double? CarrierOffsetHz
    {
        get
        {
            if (_detector == PskDetector.Coherent)
            {
                return _packetDcd.Asserted ? _costas!.FrequencyOffsetHz(_sampleRate) : null;
            }

            double coherence = Math.Sqrt(
                (_offsetWindowReal * _offsetWindowReal) + (_offsetWindowImag * _offsetWindowImag));
            if (coherence < OffsetCoherenceFloor)
            {
                return null;
            }

            // Halving the squared phasor's angle recovers the per-symbol rotation, which is the
            // offset in cycles per symbol.
            return Math.Atan2(_offsetWindowImag, _offsetWindowReal)
                / (2.0 * _delayI.Length) * _sampleRate / (2 * Math.PI);
        }
    }

    /// <summary>Channel-busy for carrier sense: packet DCD or any significant in-band
    /// energy (a carrier, voice, another mode).</summary>
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <summary>Clears carrier state, e.g. while the channel's own transmitter is keyed.</summary>
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
        // Our own transmission is not a measurement of anybody's offset, and not a carrier
        // reference to decide the next station's symbols against.
        _averageDiffMagnitude = 0;
        _offsetWindowReal = 0;
        _offsetWindowImag = 0;
        Array.Clear(_referenceI);
        Array.Clear(_referenceQ);
        Array.Clear(_trackedRotation);
        Array.Clear(_confidenceMean);
        Array.Fill(_previousDecisionMagnitude, float.MaxValue);
        _seededRotation = 0;
        _rotationSeeded = false;
        _pendingInstant = -1;
        _mlse?.Reset();
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _sampleIndex++;
            float filtered = _bandPass.Next(sample);
            _energyBusy.Process(filtered);
            if (_detector == PskDetector.Coherent)
            {
                ProcessCoherent(filtered);
            }
            else
            {
                ProcessDifferential(sample);
            }

            // A timing oracle reads the symbol here, after this sample has been through the
            // whole front end (so the value read is the same one the DPLL would have read at
            // this instant) and after the DPLL has taken its own turn at the transition.
            if (SymbolInstantSchedule is { } schedule)
            {
                // Step over any instant this stream has already passed - one that fell before the
                // first sample, above all - rather than waiting for a sample index that can never
                // arrive, which would stall the whole schedule at its first entry and emit nothing
                // at all. (Found by a displacement profile whose entire negative half read exactly
                // zero: a plausible-looking result that was the instrument, not the receiver.)
                while (_scheduleIndex < schedule.Count && schedule[_scheduleIndex] < _sampleIndex)
                {
                    _scheduleIndex++;
                }

                if (_scheduleIndex < schedule.Count && schedule[_scheduleIndex] == _sampleIndex)
                {
                    _scheduleIndex++;
                    OnSymbolInstant(_lastPlotI > 0 ? 1 : 0);
                }
            }
        }
    }

    // Coherent: the Costas NCO tracks the carrier phase, so I lands on the real axis (±A) and
    // Q on zero; the sign of I is the absolute symbol, differentially decoded in the DPLL
    // sink. The loop's π ambiguity is harmless - it only flips the decode of the reference
    // symbol, which the sync hunt discards.
    private void ProcessCoherent(float filtered)
    {
        float i = _lowPassI.Next(filtered * _costas!.Cos);
        float q = _lowPassQ.Next(filtered * _costas.Sin);
        _costas.Advance(CostasLoop.BpskError(i, q));

        // Symbol clock from sign changes of the recovered in-phase component.
        double crossing = 0;
        if ((i > 0) != (_previousI > 0) && i != _previousI)
        {
            crossing = Math.Clamp(i / (i - _previousI), 0, 0.999);
        }

        _previousI = i;
        _lastPlotI = i;
        _dpll.Sample(i > 0 ? 1 : 0, crossing);
    }

    // Differential: the per-sample product z·conj(z one symbol ago) - + on a phase repeat,
    // − on a reversal - drives symbol timing (its crossings are the symbol boundaries) and
    // the carrier-offset window; the bit itself is decided at the DPLL instant against the
    // decision-feedback reference.
    private void ProcessDifferential(float sample)
    {
        // The mixer NCO as a rotating phasor rather than per-sample Math.Sin/Cos - the
        // same treatment as AfskDemodulator, and for the same reason: a diversity bank
        // multiplies this loop by its branch count.
        double rotatedCos = (_oscillatorCos * _rotateCos) - (_oscillatorSin * _rotateSin);
        double rotatedSin = (_oscillatorSin * _rotateCos) + (_oscillatorCos * _rotateSin);
        _oscillatorCos = rotatedCos;
        _oscillatorSin = rotatedSin;
        if (--_renormCountdown == 0)
        {
            double scale = 1 / Math.Sqrt((rotatedCos * rotatedCos) + (rotatedSin * rotatedSin));
            _oscillatorCos *= scale;
            _oscillatorSin *= scale;
            _renormCountdown = OscillatorRenormInterval;
        }

        float i = _lowPassI.Next(sample * (float)_oscillatorSin);
        float q = _lowPassQ.Next(sample * (float)_oscillatorCos);

        _currentI = i;
        _currentQ = q;
        float delayedI = _delayI[_delayPosition];
        float delayedQ = _delayQ[_delayPosition];
        _delayI[_delayPosition] = i;
        _delayQ[_delayPosition] = q;
        if (++_delayPosition == _delayI.Length)
        {
            _delayPosition = 0;
        }

        // Re(z·conj(z_delayed)): + on phase repeat ('1'), − on reversal ('0'). The imaginary
        // part the timing path throws away is what carries the carrier offset.
        float decision = i * delayedI + q * delayedQ;
        TrackCarrierOffset(decision, (q * delayedI) - (i * delayedQ));
        double crossing = 0;
        if ((decision > 0) != (_previousDecision > 0) && decision != _previousDecision)
        {
            crossing = Math.Clamp(decision / (double)(decision - _previousDecision), 0, 0.999);
        }

        _previousDecision = decision;
        _lastPlotI = decision;   // held for the constellation tap (see QpskDemodulator)
        int slot = (int)(_sampleIndex % _ringI.Length);
        _ringI[slot] = i;
        _ringQ[slot] = q;
        _dpll.Sample(decision > 0 ? 1 : 0, crossing);
        if (_pendingInstant >= 0 && _sampleIndex >= _pendingInstant + _ringLead)
        {
            DecidePending();
        }
    }

    /// <summary>Decision-feedback differential detection, once per symbol at the DPLL
    /// instant. The reference is an exponentially-averaged, decision-remodulated estimate of
    /// the carrier phasor - a ~20-symbol-clean version of the single one-symbol-ago sample
    /// plain differential detection divides by, which is where the classic ~1 dB
    /// differential-vs-coherent gap comes from. Decides the absolute polarity of the
    /// symbol-instant baseband sample against the reference, emits the differential bit
    /// (repeat = 1), and folds the remodulated sample back in. Convergence is a few symbols
    /// from cold, there is no lock to lose - a fade that wipes the reference costs exactly
    /// the symbols the fade already cost - and a polarity slip costs the same two bits it
    /// costs plain differential detection.</summary>
    /// <remarks>
    /// A carrier offset rotates z symbol over symbol, and an exponential average trails a
    /// rotating phasor by ~λ/(1−λ) times the per-symbol rotation - enough to null the
    /// detector at half a bank step. Two measures close that hole, both measured on the sim
    /// ladder: the loop rotates the reference each symbol by a tracked rotation estimate
    /// (decision-directed, driven by the angle between each aligned sample and the
    /// reference), and that estimate is seeded once per burst from the offset window the
    /// moment it turns coherent - a single injection, because feeding the window's angle in
    /// per-symbol measurably jitters the reference with the estimator's own noise.
    /// </remarks>
    private int DecideAgainstReference(int phase, float sampleI, float sampleQ, out double projection)
    {
        double referenceI = _referenceI[phase];
        double referenceQ = _referenceQ[phase];
        double rotation = _seededRotation + _trackedRotation[phase];
        if (rotation != 0)
        {
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);
            (referenceI, referenceQ) = (
                (referenceI * cos) - (referenceQ * sin),
                (referenceI * sin) + (referenceQ * cos));
        }

        projection = (sampleI * referenceI) + (sampleQ * referenceQ);
        int polarity = projection >= 0 ? 1 : -1;
        int bit = polarity == _previousPolarity[phase] ? 1 : 0;
        _previousPolarity[phase] = polarity;

        // Advance the tracker by the angle by which the aligned sample leads the reference.
        double cross = (polarity * sampleQ * referenceI) - (polarity * sampleI * referenceQ);
        double magnitude = Math.Sqrt(
            ((double)sampleI * sampleI + (double)sampleQ * sampleQ)
            * ((referenceI * referenceI) + (referenceQ * referenceQ)));
        if (magnitude > 1e-12)
        {
            double error = Math.Asin(Math.Clamp(cross / magnitude, -1, 1));
            _trackedRotation[phase] = Math.Clamp(
                _trackedRotation[phase] + (ReferenceFrequencyGain * error),
                -MaxTrackedRotation, MaxTrackedRotation);
        }

        _referenceI[phase] = (ReferenceMemory * referenceI) + ((1 - ReferenceMemory) * polarity * sampleI);
        _referenceQ[phase] = (ReferenceMemory * referenceQ) + ((1 - ReferenceMemory) * polarity * sampleQ);
        return bit;
    }

    /// <summary>What <see cref="UpdateRotationSeed"/> observed this symbol.</summary>
    private enum RotationSeedEvent
    {
        /// <summary>No change of burst state.</summary>
        None,

        /// <summary>The offset window just turned coherent and the burst rotation was
        /// seeded from it; the detector's own rotation tracker starts afresh.</summary>
        Seeded,

        /// <summary>The offset window's coherence collapsed - the burst is over, several
        /// symbol times after the carrier stopped and well before DCD falls.</summary>
        BurstOver,
    }

    /// <summary>Per-symbol bookkeeping of the per-burst rotation seed, shared by the DF-DD
    /// and MLSE decision stages. The seed is a one-shot per burst, placed anywhere within
    /// ±baud/4 the moment the offset window turns coherent - which it only does at SNRs
    /// where the estimate is solid. A detector's own narrow tracker is always running, so a
    /// burst too weak to seed (the window's floor is above the mode's own decode threshold)
    /// still captures a mid-branch residual from its first symbols. The seed does not
    /// outlive its burst - the next station may sit elsewhere, and a burst too weak to
    /// re-seed must start from the tracker's own bounded window, not a stale neighbour's
    /// offset.</summary>
    private RotationSeedEvent UpdateRotationSeed()
    {
        double offsetCoherence = Math.Sqrt(
            (_offsetWindowReal * _offsetWindowReal) + (_offsetWindowImag * _offsetWindowImag));
        if (!_rotationSeeded && offsetCoherence >= OffsetCoherenceFloor)
        {
            _seededRotation = Math.Clamp(
                Math.Atan2(_offsetWindowImag, _offsetWindowReal) / 2.0,
                -MaxSeededRotation, MaxSeededRotation);
            _rotationSeeded = true;
            return RotationSeedEvent.Seeded;
        }

        if (_rotationSeeded && offsetCoherence < OffsetCoherenceFloor / 2)
        {
            _rotationSeeded = false;
            _seededRotation = 0;
            return RotationSeedEvent.BurstOver;
        }

        return RotationSeedEvent.None;
    }

    /// <summary>Delivers one decided bit to the sinks. Confidence: the decision magnitude
    /// (DF-DD projection, coherent amplitude, or MLSE traceback margin) against a slow
    /// running mean of itself (~500 symbols), squashed into (0, 1). Only the ordering
    /// matters downstream - a fade drops whole bytes to the bottom of the ranking, which is
    /// exactly what erasure decoding wants flagged.</summary>
    private void EmitBit(int phase, int decided, float magnitude)
    {
        if (phase == 0)
        {
            _bitSink(decided);
        }

        if (_softBitSink is null)
        {
            return;
        }

        _confidenceMean[phase] = _confidenceMean[phase] == 0
            ? magnitude
            : _confidenceMean[phase] + (0.002 * (magnitude - _confidenceMean[phase]));
        float confidence = _confidenceMean[phase] <= 0
            ? 0.5f
            : Math.Min(0.999f, (float)(magnitude / (4.0 * _confidenceMean[phase])));
        _softBitSink(decided, confidence, phase);
    }

    /// <summary>Root-raised-cosine matched-filter taps at the sample rate, spanning ±6
    /// symbols (the modulator's own truncation), normalised to unity DC gain. Deliberately
    /// unwindowed: a taper widens the transition band and de-matches the cascade, and the
    /// truncated pulse's own stopband (−54 dB one baud out) already exceeds what the old
    /// band-pass provided against a neighbouring modem on the same audio. Shared with
    /// <see cref="QpskDemodulator"/>, whose differential path learned the same lesson in
    /// the QPSK campaign (docs/qpsk/plan.md Q1-2).</summary>
    internal static float[] MatchedFilterTaps(int sampleRate, int baud, double rollOff)
    {
        int samplesPerSymbol = sampleRate / baud;
        int half = 6 * samplesPerSymbol;
        var taps = new float[(2 * half) + 1];
        double sum = 0;
        for (int k = 0; k < taps.Length; k++)
        {
            double t = (k - half) / (double)samplesPerSymbol;
            taps[k] = (float)FilterDesign.RootRaisedCosine(t, rollOff);
            sum += taps[k];
        }

        for (int k = 0; k < taps.Length; k++)
        {
            taps[k] = (float)(taps[k] / sum);
        }

        return taps;
    }

    /// <summary>Folds one differential product into the carrier-offset window (see
    /// <see cref="CarrierOffsetHz"/>). Ported from
    /// <see cref="BpskCarrierOffsetEstimator.Process"/>.</summary>
    /// <remarks>
    /// Working at symbol spacing is what tolerates the all-reversal training preamble: a
    /// per-sample squarer reads those reversals as a tone at ±baud/2 and false-locks to it,
    /// whereas one symbol apart they are a constant π step the squaring removes. Samples whose
    /// magnitude is below its running mean - the amplitude nulls a reversal sweeps through -
    /// are dropped, so only full-amplitude symbol centres contribute.
    /// </remarks>
    private void TrackCarrierOffset(double real, double imaginary)
    {
        double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));
        _averageDiffMagnitude += OffsetWindowRate * (magnitude - _averageDiffMagnitude);
        if (magnitude <= _averageDiffMagnitude || magnitude < 1e-9)
        {
            return;   // a reversal null - no reliable phase here
        }

        double normalisedReal = real / magnitude;
        double normalisedImaginary = imaginary / magnitude;

        // (d/|d|)² strips the ±1 data, leaving a phasor at twice the per-symbol rotation. The
        // exponential window starts from zero, not from the first phasor, so a lone early
        // sample cannot read as full coherence.
        double squaredReal = (normalisedReal * normalisedReal) - (normalisedImaginary * normalisedImaginary);
        double squaredImaginary = 2 * normalisedReal * normalisedImaginary;
        _offsetWindowReal += OffsetWindowRate * (squaredReal - _offsetWindowReal);
        _offsetWindowImag += OffsetWindowRate * (squaredImaginary - _offsetWindowImag);
    }
}
