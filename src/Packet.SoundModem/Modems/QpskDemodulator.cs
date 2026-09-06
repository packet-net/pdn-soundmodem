using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// QPSK demodulator emitting one dibit per symbol (the inverse of the spec's symbol map) for
/// the NinoTNC 600 / 2400 / 3600 family. Two detection methods (see <see cref="PskDetector"/>):
/// <list type="bullet">
/// <item><b>Differential</b> (the catalogue default): complex mix to baseband → root-raised-cosine
/// matched filter → decision-feedback differential detection against a remodulated carrier
/// reference (see <see cref="DecideAgainstReference"/>), with a rotation tracker that runs whenever a signal is present,
/// a per-burst offset seed, and the one-symbol conjugate product - de-rotated by that estimate -
/// driving symbol timing. The <see cref="BpskDemodulator"/> chain of PR #236 on the four-phase
/// grid, which issue #326 ported.</item>
/// <item><b>Coherent</b>: band-pass → <see cref="CostasLoop"/> carrier recovery → 0.75-baud
/// low-pass → absolute quadrants differentially decoded downstream - what the NinoTNC does, kept
/// as measured as the acquisition cross-check variant.</item>
/// </list>
/// The one-symbol delay is fractional-capable because 1800 baud at 12 kHz is 6⅔ samples per
/// symbol, and the differential chain runs upsampled to a whole ratio there. Symbol clock from
/// the shared <see cref="BitDpll"/>, driven by quadrant changes; either way the same
/// <see cref="QuadrantToDibit"/> map turns a phase-change quadrant into a dibit, so the wire
/// format is identical.
/// </summary>
public sealed class QpskDemodulator
{
    /// <summary>Memory of the carrier-offset window, per contributing sample - ~1000
    /// samples, so the reading describes roughly the last tenth of a second of signal
    /// rather than the whole burst. Mirrors <see cref="BpskDemodulator"/>.</summary>
    private const double OffsetWindowRate = 0.001;

    /// <summary>Coherence (0..1) below which the offset window is noise rather than a
    /// signal. As in <see cref="BpskDemodulator"/>.</summary>
    private const double OffsetCoherenceFloor = 0.5;

    /// <summary>Samples between oscillator renormalisations; see
    /// <see cref="AfskDemodulator"/>'s twin constant.</summary>
    private const int OscillatorRenormInterval = 4096;

    /// <summary>Reference memory λ of the decision-feedback detector - the value
    /// <see cref="BpskDemodulator"/> measured (~20 symbols of averaging), inherited with
    /// its rationale: the reference's effective noise sits well below the single
    /// one-symbol-ago sample the plain differential product divides by, which is where the
    /// classic differential-vs-coherent give-away lives, and QPSK's halved phase margins
    /// make that give-away larger, not smaller.</summary>
    private const double ReferenceMemory = 0.95;

    /// <summary>Per-symbol gain of the reference frequency loop while acquiring - the
    /// <see cref="BpskDemodulator"/> measured value, which pulls a bank half-step residual
    /// (4.5° per symbol at the default comb) in before the reference's lag reaches the 45°
    /// margin. 0.1 measured clearly worse (27 % against 39 % at -1 dB).</summary>
    private const double ReferenceAcquisitionGain = 0.05;

    /// <summary>Per-symbol gain of the reference frequency loop once packet DCD holds. The
    /// tracker's error is a noisy angle and every step lands on the reference, so at BPSK's
    /// 0.05 the reference's own jitter at the 0 dB knee measured as wide as the plain
    /// product's (18° rms either way - the "AWGN null" the 2026-08-07 campaign recorded
    /// for the reference); 0.01 measured 14° against the 14° a tracker-free reference
    /// reaches, and on the 2026-08-21 off-air qpsk600 burst 12° against the product's
    /// 16°. 0.005-0.02 are equivalent. The acquisition gain above stays until DCD asserts
    /// because a low gain cannot pull a residual in from cold.</summary>
    private const double ReferenceTrackingGain = 0.01;

    /// <summary>DPLL inertia for the differential path. Dire Wolf's 0.74 (which the
    /// coherent path keeps) costs ~1 dB of timing jitter at the Reed-Solomon threshold
    /// (docs/qpsk/plan.md Q1-3, PR #236's measurement reproduced); 0.92 recovered it, and
    /// 0.94 measured better still once the crossing interpolation below was in place (N=200,
    /// -1/0 dB, zero and 150 ms TXDELAY: qpsk600 100/156 and 115/179 against 97/157 and
    /// 95/178), while 0.96 loses the zero-TXDELAY acquisition the 16-symbol minimum
    /// preamble allows (70/143) - BPSK's finding at its own 24-bit minimum. The all-reversal
    /// IL2P preamble, a transition every symbol, still pulls a cold clock in within a normal
    /// TXDELAY.</summary>
    private const double DifferentialInertia = 0.94;

    /// <summary>DPLL inertia once packet DCD holds (the decisions are good, see
    /// <see cref="QpskDecisionDcd"/>): the clock acquires at <see cref="DifferentialInertia"/> and then holds
    /// nearly rigid, correcting half a per cent of its error per transition, which still
    /// follows any plausible symbol-rate error (a 500 ppm clock costs a tenth of a symbol of
    /// lag) while no longer wandering on noise. On the 2026-08-21 off-air qpsk600 fixture the
    /// clock that acquired at 6-7 samples and then wandered 2-10 through the payload left 10
    /// wrong bytes against a Reed-Solomon limit of 8, while a rigid clock two samples later
    /// copies the frame; held, the timing phases reach that clock and the frame copies with
    /// 6 corrected. Neutral on the sim's knee rows, where nothing establishes a burst.</summary>
    private const double HoldInertia = 0.995;

    /// <summary>Clamp on the seeded part of the reference rotation, radians per symbol:
    /// π/4 is ±baud/8, the fourth-power offset window's own unambiguous range - ±37.5 Hz
    /// at 300 Bd, ±150 Hz at 1200 Bd, far beyond any real station error.</summary>
    private const double MaxSeededRotation = Math.PI / 4;

    /// <summary>Clamp on the tracked part of the reference rotation, radians per symbol:
    /// π/20 is one <see cref="QpskMultiModem"/> branch step (baud/40 - ±7.5 Hz at 300 Bd,
    /// ±30 Hz at 1200 Bd). The tracker free-runs, as <see cref="BpskDemodulator"/>'s does,
    /// so a burst too weak to seed still captures a mid-branch residual from its first
    /// symbols; this bound keeps its between-burst noise wander from carrying a large stale
    /// rotation into the next acquisition. One step rather than BPSK's two because QPSK
    /// decides on a 45° margin where BPSK has 90°: a stale rotation of one step against 45°
    /// is the same ratio BPSK tolerates with two steps against 90°, and every branch still
    /// tracks every signal within a step either side, which is the selection diversity the
    /// bank's best-residual choice feeds on.</summary>
    private const double MaxTrackedRotation = Math.PI / 20;

    /// <summary>Per-symbol decay of the tracked rotation while no packet carrier is
    /// detected (~50-symbol memory). Between bursts the tracker is driven by noise alone and
    /// would otherwise park anywhere inside its clamp; draining it toward zero means a burst
    /// from a station near the branch centre starts with no stale rotation to unlearn, while
    /// a rate this gentle cannot fight the acquisition of a real residual during the dozen or
    /// so preamble symbols before DCD asserts.</summary>
    private const double IdleTrackerLeak = 0.98;

    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;

    /// <summary>Whether to interpolate the decision back to the clock's true instant. On only
    /// where the samples-per-symbol ratio is not a whole number, which in this catalogue means
    /// qpsk3600 alone (12 kHz / 1800 Bd = 6.667): the integer-ratio modes are measured to be flat
    /// at high signal without it, and leaving their sample path bit-identical is worth more than
    /// the consistency.</summary>
    private readonly bool _interpolateSymbolInstant;
    /// <summary>Transition-timing DCD, fed by the DPLL on the coherent path only.</summary>
    private readonly PacketDcd _packetDcd = new();

    /// <summary>Decision-quality DCD for the differential path (see
    /// <see cref="QpskDecisionDcd"/>); fed once per symbol at the clock's own instant.</summary>
    private readonly QpskDecisionDcd _decisionDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly PskDetector _detector;
    private readonly CostasLoop? _costas;
    private readonly float[] _delayI;
    private readonly float[] _delayQ;
    private readonly int _delayWhole;
    private readonly float _delayFraction;
    private readonly double _rotateCos;
    private readonly double _rotateSin;
    private readonly double _delaySamples;
    private readonly int _sampleRate;

    /// <summary>How many internal samples the decode chain runs per input sample. 1 everywhere
    /// except where the input rate gives a fractional samples-per-symbol ratio - see the
    /// constructor.</summary>
    private readonly int _upsample;

    /// <summary>Anti-image filter for the upsampled decode chain; null when not upsampling.</summary>
    private readonly FirFilter? _interpolator;
    private double _oscillatorCos = 1;
    private double _oscillatorSin;
    private int _renormCountdown = OscillatorRenormInterval;
    private double _averageDiffMagnitude;
    private double _offsetWindowReal;
    private double _offsetWindowImag;
    private int _delayPosition;
    private int _previousQuadrant;
    private float _lastRe;
    private float _lastIm;
    private float _currentI;
    private float _currentQ;
    private double _seededRotation;
    private bool _windowCoherent;
    private bool _dcdWasAsserted;
    private bool _energyWasBusy;
    private bool _trackerActive;
    private double _derotateCos = 1;
    private double _derotateSin;

    /// <summary>Per-symbol decay of a seeded rotation while neither packet DCD nor the offset
    /// window says a burst is present: a three-second time constant at the mode's symbol rate,
    /// the backstop that stops one station's seed outliving the gap to the next when DCD never
    /// marked the burst's end (see <see cref="DecideAgainstReference"/>).</summary>
    private readonly double _seedIdleDecay;
    private readonly bool _decisionFeedback;
    private readonly Action<int, int> _dibitSink;
    private readonly Action<int, int, float, int>? _softDibitSink;

    /// <summary>How many timing phases the soft sink is fed (see <see cref="TimingDiversity"/>):
    /// the index it carries runs 0 to this minus one, phase 0 being the clock's own instant.</summary>
    internal static int TimingPhaseCount => TimingDiversity.PhaseCount;

    // Per-timing-phase decision state, indexed like TimingDiversity.PhaseFractions.
    private readonly double[] _referenceI;
    private readonly double[] _referenceQ;
    private readonly double[] _trackedRotation;
    private readonly int[] _previousAbsoluteQuadrant;
    private readonly double[] _confidenceMean;

    // Previous symbol's decision margin per phase, for the pair-min per-dibit confidence (see
    // EmitDibit). MaxValue = no previous symbol yet, so a burst's first dibit is judged on
    // its own margin alone.
    private readonly float[] _previousDecisionMargin;

    // Recent baseband samples and de-rotated products, indexed by chain sample modulo the
    // ring length, so a decision can be taken a little before or after the clock's instant
    // (the late phase needs samples that arrive after the instant, hence the deferral).
    private readonly float[] _ringI;
    private readonly float[] _ringQ;
    private readonly float[] _ringRe;
    private readonly float[] _ringIm;
    private readonly double _phaseOffsetSamples;
    private readonly int _ringLead;
    private long _pendingInstant = -1;
    private float _pendingOvershoot;

    /// <summary>Raised once per recovered symbol with the symbol-instant constellation point
    /// (I,Q): the recovered absolute constellation in coherent mode, the differential product
    /// in differential mode (de-rotated by the burst's estimated carrier rotation, so an
    /// off-frequency station's four clusters stay on the axes). Null-safe; wire from the
    /// modem.</summary>
    public Action<float, float>? SymbolPlotted { get; set; }

    /// <summary>Bench seam: receives, once per symbol on the decision-feedback path, the
    /// symbol-instant sample as seen from the reference (in-phase, quadrature, both in the
    /// sample's own amplitude) together with the quadrant it was decided to - the detector's
    /// actual decision variable, which <see cref="SymbolPlotted"/>'s product is not. Not part
    /// of the deployment surface.</summary>
    internal Action<double, double, int>? DecisionObserver { get; set; }

    /// <summary>Bench seam: receives the input-sample index of every symbol instant this
    /// demodulator sampled at (the chain's own grid, so threefold-upsampled modes count chain
    /// samples). Indices count from zero at the first <see cref="Process"/> call. Not part of
    /// the deployment surface.</summary>
    internal Action<long>? SymbolInstantObserver { get; set; }

    /// <summary>Bench seam: receives the DPLL phase (-0.5..0.5 of a symbol) of every slicer
    /// transition, the quantity <see cref="PacketDcd"/> scores. Not part of the deployment
    /// surface.</summary>
    internal Action<double>? TransitionObserver { get; set; }

    /// <summary>Bench seam: the current per-burst seed and the clock instant's tracked
    /// rotation, in Hz. Not part of the deployment surface.</summary>
    internal (double SeededHz, double TrackedHz) RotationState =>
        (_seededRotation / _delaySamples * _sampleRate / (2 * Math.PI),
         _trackedRotation[0] / _delaySamples * _sampleRate / (2 * Math.PI));

    private long _chainSampleIndex = -1;
    private long _inputSampleIndex = -1;

    /// <summary>
    /// How much audio this demodulator has been given, as the zero-based index of the input
    /// sample it is working on - the same clock the channel counts its receive audio on, so a
    /// mark taken here places a frame in the input stream exactly. Deliberately not
    /// <c>_chainSampleIndex</c>, which counts the threefold-upsampled decode grid on the
    /// differential path. See <see cref="Modems.FrameSpan"/>.
    /// </summary>
    public long InputSamplePosition => _inputSampleIndex;

    /// <summary>Creates a demodulator delivering dibits (left bit first) to
    /// <paramref name="dibitSink"/> once per symbol.</summary>
    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="baud">Symbol rate (1200 or 1800).</param>
    /// <param name="dibitSink">Receives (firstBit, secondBit) per symbol.</param>
    /// <param name="carrierFrequency">Carrier centre.</param>
    /// <param name="detector">Coherent (default) or differential detection.</param>
    /// <param name="loopBandwidthHz">Costas loop bandwidth (coherent only); defaults to 6 %
    /// of the symbol rate, tuned against measurement.</param>
    /// <param name="rollOff">The transmitter's root-raised-cosine roll-off, which the
    /// differential path's matched filter mirrors. <see cref="QpskModem"/> passes the
    /// per-mode value (the 0.35 default for qpsk600 and qpsk2400, 0.25 for qpsk3600).</param>
    /// <param name="decisionFeedback">Whether the differential path decides against the
    /// decision-feedback reference (see <see cref="DecideAgainstReference"/>) or against the
    /// plain one-symbol conjugate product. On for the SSB modes; OFF for qpsk3600, where the
    /// product still measures better at the FM knee (re-measured for #326 with the
    /// reference deciding on the interpolated instant: fm-mic +8 dB CNR 78 % product against
    /// 68 % reference, fm-data 80 % against 72 %, N=50, while the 2026-08-07 campaign's
    /// clean-signal regression no longer shows). See <see cref="QpskModem.Qpsk3600"/>.</param>
    /// <param name="softDibitSink">When supplied, receives each decided dibit together with
    /// a confidence in (0, 1) - the symbol's decision margin against a slow running mean, so
    /// a faded or hit symbol ranks low; both bits of a dibit share it, the erasure decoder
    /// working in bytes. Feed it to <see cref="Il2pReceiver.PushBit(int, float)"/> and
    /// failed Reed-Solomon blocks retry with the weakest bytes erased, exactly as
    /// <see cref="BpskDemodulator"/>'s soft sink does. <paramref name="dibitSink"/> is
    /// called either way.</param>
    public QpskDemodulator(
        int sampleRate, int baud, Action<int, int> dibitSink, double carrierFrequency,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null,
        double rollOff = QpskModulator.DefaultRollOff, bool decisionFeedback = true,
        Action<int, int, float, int>? softDibitSink = null)
    {
        ArgumentNullException.ThrowIfNull(dibitSink);
        if (detector == PskDetector.Mlse)
        {
            // The MLSE decision stage is built for the BPSK differential chain (rx-roadmap
            // workstream 5); this demodulator has no equaliser to hand the symbols to.
            throw new ArgumentException("MLSE detection is BPSK-only", nameof(detector));
        }

        _detector = detector;
        _decisionFeedback = decisionFeedback;
        _dibitSink = dibitSink;
        _softDibitSink = softDibitSink;

        // Run the decode chain on a grid where a symbol is a whole number of samples.
        //
        // qpsk3600 is the only catalogue mode whose ratio is fractional - 12000/1800 = 6.667, and
        // 26.667 at the 48 kHz capture rate - and it was measured to decode WORSE as the signal
        // got stronger, uniquely among the modes (docs/mode-modulation-reference.md). Every part
        // of the chain that resolves time is quantised to the input grid: the clock's wrap, the
        // transition crossings that steer it, the matched filter's own sampling. At 6.667 samples
        // per symbol that quantisation is six times coarser than any other mode's, and noise was
        // dithering it - which is why a cleaner signal decoded worse.
        //
        // QtSoundModem solves it the same way from the other end: make_core_INTR interpolates
        // every PSK mode by n_INTR = baud/300 (6 for SPEED_Q3600) and decode_stream_QPSK then
        // hardcodes 300 baud, so its sampler always sees exactly 40 samples per symbol
        // (ax25_demod.c, GPLv3 - the cross-check named in CLAUDE.md). We pick the smallest whole
        // factor that makes the ratio integer, which is 3 for both of qpsk3600's rates.
        //
        // Coherent stays on the input grid: it is the cross-check variant, its measured
        // configuration is deliberately untouched, and the anomaly was measured on the
        // differential path the mode actually deploys with.
        int upsample = 1;
        if (detector != PskDetector.Coherent)
        {
            while (upsample < 8 && (sampleRate * upsample) % baud != 0)
            {
                upsample++;
            }

            if ((sampleRate * upsample) % baud != 0)
            {
                upsample = 1; // no small whole factor works; stay on the input grid
            }
        }

        _upsample = upsample;
        int chainRate = sampleRate * upsample;
        if (upsample > 1)
        {
            // Anti-image low-pass at the original Nyquist, with the gain the zero-stuffing loses.
            float[] taps = FilterDesign.LowPass(
                sampleRate / 2.0 * 0.9, chainRate, 64 * upsample | 1);
            for (int t = 0; t < taps.Length; t++)
            {
                taps[t] *= upsample;
            }

            _interpolator = new FirFilter(taps);
        }
        // Filter plan follows QtSM's per-mode tables: BPF ≈ 2×baud wide, LPF ≈ 0.75×baud.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        if (detector == PskDetector.Coherent)
        {
            // The coherent path keeps its measured QtSM-style configuration untouched, as
            // the acquisition cross-check variant - exactly the BPSK arrangement.
            _lowPassI = new FirFilter(FilterDesign.LowPass(0.75 * baud, chainRate, 128 * chainRate / 12000));
            _lowPassQ = new FirFilter(FilterDesign.LowPass(0.75 * baud, chainRate, 128 * chainRate / 12000));
        }
        else
        {
            // Root-raised-cosine matched to the transmitter's per-mode shaping: the cascade
            // is a raised cosine - ISI-free at the symbol instants, minimal noise bandwidth.
            // Replacing the 0.75-baud low-pass measured ~1-1.5 dB at the Reed-Solomon
            // threshold on both SSB QPSK modes (docs/qpsk/plan.md Q1-2), the same lesson
            // the bpsk300 campaign measured in PR #236.
            float[] taps = BpskDemodulator.MatchedFilterTaps(chainRate, baud, rollOff);
            _lowPassI = new FirFilter(taps);
            _lowPassQ = new FirFilter((float[])taps.Clone());
        }
        double step = 2 * Math.PI * carrierFrequency / chainRate;
        _rotateCos = Math.Cos(step);
        _rotateSin = Math.Sin(step);
        _energyBusy = new EnergyBusyDetector(sampleRate);
        if (detector == PskDetector.Coherent)
        {
            _costas = new CostasLoop(sampleRate, carrierFrequency, loopBandwidthHz ?? baud * 0.06);
        }

        double delay = (double)chainRate / baud;
        _delaySamples = delay;
        _seedIdleDecay = Math.Exp(-1.0 / (3.0 * baud));
        _interpolateSymbolInstant = Math.Abs(delay - Math.Round(delay)) > 1e-9;
        _sampleRate = chainRate;
        _delayWhole = (int)Math.Floor(delay);
        _delayFraction = (float)(delay - _delayWhole);
        // Ring of whole+1: the slot about to be overwritten holds z[n-(whole+1)] (older),
        // the next slot holds z[n-whole] (newer); lerp between them by the fraction.
        _delayI = new float[_delayWhole + 1];
        _delayQ = new float[_delayWhole + 1];

        int phases = TimingDiversity.PhaseFractions.Length;
        _referenceI = new double[phases];
        _referenceQ = new double[phases];
        _trackedRotation = new double[phases];
        _previousAbsoluteQuadrant = new int[phases];
        _confidenceMean = new double[phases];
        _previousDecisionMargin = new float[phases];
        Array.Fill(_previousDecisionMargin, float.MaxValue);
        _phaseOffsetSamples = TimingDiversity.Reach * delay;
        // The late phase reads up to ceil(offset) samples past the instant, plus one for the
        // interpolation's upper neighbour; the ring spans both sides of the instant with room.
        _ringLead = (int)Math.Ceiling(_phaseOffsetSamples) + 1;
        int ring = (2 * _ringLead) + 4;
        _ringI = new float[ring];
        _ringQ = new float[ring];
        _ringRe = new float[ring];
        _ringIm = new float[ring];

        _dpll = new BitDpll(
            baud, chainRate,
            quadrant =>
            {
                SymbolPlotted?.Invoke(_lastRe, _lastIm);
                // The clock can only wrap on a sample, so the emitting sample sits up to one
                // sample late - which at qpsk3600's 6.667 samples per symbol is up to 0.15 of a
                // symbol of timing error on the decision, six times coarser than any other mode
                // in the catalogue. Interpolating the differential product back to the instant
                // the clock actually asked for costs one lerp and removes that quantisation.
                // (QtSoundModem reaches the same place from the other end: it interpolates every
                // PSK mode up to exactly 40 samples per symbol before its sampler.)
                // Differential only: this interpolates the DIFFERENTIAL PRODUCT, which is what
                // the coherent path's _lastRe/_lastIm do not hold (they carry the Costas-tracked
                // I/Q there, and its previous-sample twin is not maintained). Coherent is the
                // cross-check variant, not the deployed one, and it stays byte-identical.
                SymbolInstantObserver?.Invoke(_chainSampleIndex);
                if (_detector == PskDetector.Coherent)
                {
                    // Coherent feeds the absolute quadrant; differentially decode against the
                    // previous symbol so the wire mapping (a phase change) is unchanged.
                    EmitDibit(0, (quadrant - _previousQuadrant) & 3, 1f);
                    _previousQuadrant = quadrant;
                    return;
                }

                // Differential decides later, once the samples either side of this instant are
                // in the ring (see DecidePending): the instant itself, and the early and late
                // timing phases. At qpsk3600's 6.667 samples per symbol the wrap sample sits up
                // to 0.15 of a symbol late, so the decision is read back to the instant the
                // clock actually asked for (QtSoundModem reaches the same place from the other
                // end: it interpolates every PSK mode up to exactly 40 samples per symbol
                // before its sampler); the integer-ratio modes are measured flat without it.
                // The field is still being assigned as this lambda is constructed; nothing
                // reads it until audio flows.
                if (_pendingInstant >= 0)
                {
                    DecidePending();
                }

                _pendingInstant = _chainSampleIndex;
                _pendingOvershoot = _interpolateSymbolInstant ? (float)_dpll!.WrapOvershootSamples : 0f;
            },
            // Dire Wolf's 0.74 costs ~1 dB of timing jitter at the Reed-Solomon threshold
            // on the differential path here exactly as it did on bpsk300 (docs/qpsk/plan.md
            // Q1-3, PR #236's measurement); 0.92 recovers it, and the all-reversal IL2P
            // preamble - a transition every symbol - still pulls a cold clock in within a
            // normal TXDELAY. Coherent keeps its issue-#5 measured configuration.
            inertia: detector == PskDetector.Coherent ? 0.74 : DifferentialInertia,
            // Transition timing scores DCD on the coherent path only. The differential
            // path's slicer is the product's quadrant, whose transitions flicker at every
            // phase-change null (see QpskDecisionDcd); its DCD is scored on the decisions.
            transitionObserver: detector == PskDetector.Coherent
                ? phase =>
                {
                    _packetDcd.OnTransition(phase);
                    TransitionObserver?.Invoke(phase);
                }
                : phase => TransitionObserver?.Invoke(phase),
            // The coherent baseband power at the symbol's sample: a run of one dibit value
            // holds constellation-strength power while producing no quadrant transitions,
            // which must not read as silence (see PacketDcd, issue #339).
            symbolObserver: detector == PskDetector.Coherent
                ? () => _packetDcd.OnSymbol(((double)_lastRe * _lastRe) + ((double)_lastIm * _lastIm))
                : null);
    }

    private static readonly int[] QuadrantToDibit = [0b11, 0b10, 0b00, 0b01]; // 0°,90°,180°,270°

    /// <summary>True while a coherent packet signal is present: DPLL transition timing on the
    /// coherent path (<see cref="PacketDcd"/>), symbol decision quality on the differential
    /// one (<see cref="QpskDecisionDcd"/>).</summary>
    public bool CarrierDetect => _detector == PskDetector.Coherent ? _packetDcd.Asserted : _decisionDcd.Asserted;

    /// <summary>
    /// How far the signal sat from <em>this</em> demodulator's own carrier centre, in Hz
    /// (positive = above it), or <c>null</c> when nothing coherent enough to measure is
    /// present. The QPSK twin of <see cref="BpskDemodulator.CarrierOffsetHz"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Coherent</b> (the mode family's default): the Costas NCO is already tracking
    /// the carrier, so its frequency correction is the residual directly; trustworthy only
    /// while the loop is on a signal, hence the DCD gate.</para>
    /// <para><b>Differential:</b> the detector has already formed z·conj(z one symbol ago)
    /// to decide the dibit; that product's angle is the per-symbol carrier rotation plus a
    /// multiple-of-90° data step, so raising the normalised product to the <em>fourth</em>
    /// power (BPSK squares - one more doubling removes the extra data axis) strips the data
    /// and leaves a phasor at four times the rotation. Unambiguous over ±baud/8, far wider
    /// than any real station error on these modes.</para>
    /// <para>Read it when a frame arrives - between bursts the window decays into the noise
    /// and this goes null, which is the honest answer to "how far off was the station"
    /// when there is no station.</para>
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

            // Quartering the fourth-power phasor's angle recovers the per-symbol rotation,
            // which is the offset in cycles per symbol.
            return Math.Atan2(_offsetWindowImag, _offsetWindowReal)
                / (4.0 * _delaySamples) * _sampleRate / (2 * Math.PI);
        }
    }

    /// <summary>Channel-busy for carrier sense (packet or energy).</summary>
    public bool ChannelBusy => CarrierDetect || _energyBusy.Busy;

    /// <summary>Clears carrier state.</summary>
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _decisionDcd.Reset();
        _energyBusy.Reset();
        // Our own transmission is not a measurement of anybody's offset, and not a
        // carrier reference to decide the next station's symbols against.
        _averageDiffMagnitude = 0;
        _offsetWindowReal = 0;
        _offsetWindowImag = 0;
        Array.Clear(_referenceI);
        Array.Clear(_referenceQ);
        Array.Clear(_trackedRotation);
        Array.Clear(_confidenceMean);
        Array.Fill(_previousDecisionMargin, float.MaxValue);
        _seededRotation = 0;
        _windowCoherent = false;
        _dcdWasAsserted = false;
        _energyWasBusy = false;
        _derotateCos = 1;
        _derotateSin = 0;
        _pendingInstant = -1;
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _inputSampleIndex++;

            // Band-pass and energy detection stay on the input grid whatever the decode chain
            // does: they answer "is something there", which needs no sub-sample resolution, and
            // keeping them here means upsampling costs the decode chain only.
            float filtered = _bandPass.Next(sample);
            _energyBusy.Process(filtered);
            if (_detector == PskDetector.Coherent)
            {
                ProcessCoherent(filtered);
            }
            else if (_upsample > 1)
            {
                // Zero-stuff and filter: the decode chain sees a whole number of samples per
                // symbol, so its clock, its crossings and its matched filter all resolve time on
                // a grid the symbol rate divides exactly.
                for (int k = 0; k < _upsample; k++)
                {
                    ProcessDifferential(_interpolator!.Next(k == 0 ? sample : 0f));
                }
            }
            else
            {
                // The band-pass gates EnergyBusy only on this path: fronting the decode
                // chain with it costs where the matched filter above is doing the
                // selectivity - measured on qpsk600's deepest AWGN rung (40 % raw vs 33 %
                // band-passed at 0 dB, docs/qpsk/plan.md Q1-2), its +-300 Hz passband
                // rippling across the 300 Bd signal; qpsk2400's wider band-pass measured
                // benign, and one arrangement serves both. The same split PR #236 measured
                // for bpsk300.
                ProcessDifferential(sample);
            }
        }
    }

    // Coherent: the Costas NCO mixes to baseband and tracks the carrier phase, so I/Q land on
    // the absolute constellation; the nearest quadrant is the absolute symbol, differentially
    // decoded against the previous symbol in the DPLL sink.
    private void ProcessCoherent(float filtered)
    {
        float i = _lowPassI.Next(filtered * _costas!.Cos);
        float q = _lowPassQ.Next(filtered * _costas.Sin);
        _costas.Advance(CostasLoop.QpskError(i, q));

        // The QPSK Costas detector nulls at the diagonals, so the loop locks the recovered
        // constellation to 45/135/225/315°. Index by 90° sector (floor), not nearest
        // multiple, so those points sit mid-sector rather than on a decision boundary; the
        // differential decode of consecutive sectors then carries the data, and the constant
        // 45° lock offset washes out of the difference.
        double angle = Math.Atan2(q, i);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        int quadrant = (int)(angle / (Math.PI / 2)) & 3;

        _lastRe = i;
        _lastIm = q;
        _dpll.Sample(quadrant);
    }

    // Differential: multiply by the conjugate of the one-symbol-delayed baseband; the nearest
    // quadrant of that product is the phase change, which the sink maps straight to a dibit.
    private void ProcessDifferential(float filtered)
    {
        _chainSampleIndex++;
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

        float i = _lowPassI.Next(filtered * (float)_oscillatorSin);
        float q = _lowPassQ.Next(filtered * (float)_oscillatorCos);

        // Fractional one-symbol delay via linear interpolation in the ring.
        int older = _delayPosition; // about to be overwritten = oldest (whole+2 back)
        int newer = older + 1 == _delayI.Length ? 0 : older + 1;
        float delayedI = _delayI[newer] + (_delayI[older] - _delayI[newer]) * _delayFraction;
        float delayedQ = _delayQ[newer] + (_delayQ[older] - _delayQ[newer]) * _delayFraction;

        _delayI[_delayPosition] = i;
        _delayQ[_delayPosition] = q;
        if (++_delayPosition == _delayI.Length)
        {
            _delayPosition = 0;
        }

        // Phase change over one symbol; quadrant = nearest multiple of 90°. The angle the
        // quadrant decision rounds away is what carries the carrier offset. The product
        // drives symbol timing and the offset window; the dibit itself is decided at the
        // DPLL instant against the decision-feedback reference.
        float rawRe = i * delayedI + q * delayedQ;
        float rawIm = q * delayedI - i * delayedQ;
        TrackCarrierOffset(rawRe, rawIm);

        // De-rotate the product by the burst's known per-symbol rotation (seed plus tracker)
        // before anything decides on it. A carrier offset adds exactly that rotation to every
        // one-symbol phase change, which moves the 45° decision boundaries to a different
        // point of each transition: the DPLL's transitions then land off the expected
        // instant and the clock is biased (and the transition-timing DCD this path used to
        // carry never asserted under any offset at all). Centred again, timing behaves as it
        // does on frequency, and the plain-product decision and DCD (QpskDecisionDcd) see
        // the four clusters on the axes. The raw product above is what the offset window
        // measures; only the decisions see this.
        float re = (float)((rawRe * _derotateCos) + (rawIm * _derotateSin));
        float im = (float)((rawIm * _derotateCos) - (rawRe * _derotateSin));
        double angle = Math.Atan2(im, re);
        int quadrant = ((int)Math.Round(angle / (Math.PI / 2)) + 4) & 3;

        // Where, between the previous sample and this one, the product crossed a decision
        // boundary. The nearest-quadrant decision flips where re - im or re + im changes
        // sign (the 45° lines), so whichever of those two crossed zero gives the sub-sample
        // crossing by linear interpolation - the clock-jitter removal BpskDemodulator has
        // always fed its DPLL, which this path had been passing as zero. At 40 samples per
        // symbol that quantisation is small; at qpsk2400's 10 it is not, and supplying it
        // measured 97/163 -> 156/187 of 200 at +6/+7 dB (zero TXDELAY; 111/171 -> 182/193
        // at 150 ms), with qpsk600 and qpsk3600 a few points better each.
        double crossing = 0;
        float u = re - im, uPrev = _lastRe - _lastIm;
        float v = re + im, vPrev = _lastRe + _lastIm;
        if ((u > 0) != (uPrev > 0) && u != uPrev)
        {
            crossing = Math.Clamp(u / (double)(u - uPrev), 0, 0.999);
        }
        else if ((v > 0) != (vPrev > 0) && v != vPrev)
        {
            crossing = Math.Clamp(v / (double)(v - vPrev), 0, 0.999);
        }

        // Held for the constellation tap: the DPLL fires its symbol sink synchronously
        // inside Sample() on wrap samples, so these are the wrap-instant values.
        _lastRe = re;
        _lastIm = im;
        _currentI = i;
        _currentQ = q;
        int slot = (int)(_chainSampleIndex % _ringI.Length);
        _ringI[slot] = i;
        _ringQ[slot] = q;
        _ringRe[slot] = re;
        _ringIm[slot] = im;
        _dpll.Sample(quadrant, crossing);
        if (_pendingInstant >= 0 && _chainSampleIndex >= _pendingInstant + _ringLead)
        {
            DecidePending();
        }
    }

    /// <summary>Decides the symbol whose clock instant is pending, at each timing phase:
    /// the burst state (seed, DCD edges, leaks) advances once, then every phase reads its
    /// own interpolated baseband sample and product from the ring and decides against its
    /// own reference. Phase 0 is the clock's instant and owns the de-rotation the DPLL
    /// sees.</summary>
    private void DecidePending()
    {
        long instant = _pendingInstant;
        _pendingInstant = -1;
        UpdateBurstState();
        int ring = _ringI.Length;
        for (int phase = 0; phase < TimingDiversity.PhaseFractions.Length; phase++)
        {
            double position = instant - _pendingOvershoot + (TimingDiversity.PhaseFractions[phase] * _delaySamples);
            long lower = (long)Math.Floor(position);
            float fraction = (float)(position - lower);
            int a = (int)(((lower % ring) + ring) % ring);
            int b = (a + 1) % ring;
            float i = _ringI[a] + (fraction * (_ringI[b] - _ringI[a]));
            float q = _ringQ[a] + (fraction * (_ringQ[b] - _ringQ[a]));
            float re = _ringRe[a] + (fraction * (_ringRe[b] - _ringRe[a]));
            float im = _ringIm[a] + (fraction * (_ringIm[b] - _ringIm[a]));
            int productQuadrant = ((int)Math.Round(Math.Atan2(im, re) / (Math.PI / 2)) + 4) & 3;

            int change;
            float margin;
            if (_decisionFeedback)
            {
                change = DecideAgainstReference(phase, i, q, productQuadrant, out margin);
            }
            else
            {
                change = productQuadrant;
                margin = MarginOf(re, im, productQuadrant);
                if (phase == 0)
                {
                    // The plain product is the decision variable here, already de-rotated.
                    _decisionDcd.OnDecision(re, im);
                }
            }

            EmitDibit(phase, change, margin);
        }
    }

    /// <summary>Decision-feedback differential detection, once per symbol at the DPLL
    /// instant - <see cref="BpskDemodulator.DecideAgainstReference"/>'s rule with the
    /// polarity pair widened to the four-phase grid. The reference is an exponentially
    /// averaged, decision-remodulated estimate of the carrier phasor; the symbol-instant
    /// sample's absolute quadrant is decided against it, the emitted value is the quadrant
    /// CHANGE (the wire format), and the sample - rotated back by its decided quadrant, an
    /// exact axis swap - folds into the reference. There is no lock to lose, and a
    /// quadrant slip costs the same two symbols it costs the plain product detector.</summary>
    /// <remarks>
    /// <para><b>The reference decides from cold, as BPSK's does.</b> A cold reference is
    /// the previous symbol scaled, so the first decision against it IS the one-symbol
    /// conjugate product, and every symbol after that averages one more: the plain product
    /// detector is this detector's first symbol, not a separate mode to hand over from.
    /// The 2026-08-07 campaign gated the handover on the fourth-power offset window turning
    /// coherent, and measured an AWGN null for the reference as a result: on the real
    /// off-air <c>qpsk600</c> fixture of 2026-08-21 (+1.7 dB) that window is coherent
    /// through the all-reversal preamble and collapses for the whole payload - the fourth
    /// power of a noisy phasor is far noisier than the square BPSK gets away with - so the
    /// gated detector read the payload, the only part that matters, on the plain product,
    /// and the frame failed Reed-Solomon by a few bytes. Deciding against the reference
    /// throughout is what copies it (issue #326).</para>
    /// <para><b>The tracker always runs, bounded, and the seed is a one-shot per burst.</b>
    /// The campaign's finding that an unseeded reference "must drag its phase through the
    /// offset by decision-remodulation alone" (60 % at 8 Hz) was measured with the rotation
    /// tracker dormant until the seed, so a single modem met 9.6° per symbol of rotation
    /// with nothing correcting it. With the tracker running from the first symbol and
    /// clamped to one bank step (<see cref="MaxTrackedRotation"/>) the lag it must absorb
    /// before catching up stays inside the 45° margin for any residual a
    /// <see cref="QpskMultiModem"/> branch can see, and the seed from the offset window -
    /// placed anywhere within ±baud/8 the moment the window turns coherent, which on a
    /// real TXDELAY it does during the preamble - still takes care of the far offsets a
    /// lone modem meets. The seed now outlives the window: it is released when DCD has
    /// dropped and the window has emptied, both, so a payload whose fourth power is noise
    /// cannot un-seed its own burst.</para>
    /// <para>The decision margin returned is the remodulated sample's distance from the
    /// nearer 45° decision boundary, in sample-amplitude units - the ordering
    /// <see cref="EmitDibit"/> turns into a per-dibit confidence for erasure decoding.</para>
    /// </remarks>
    private void UpdateBurstState()
    {
        // The seed fires on the offset window's rising edge into coherence - the start of
        // every burst strong enough to measure, and again should a burst's window recover
        // after a mid-payload collapse (it then re-measures the whole rotation, so the
        // trackers restart from zero). It is never released on window collapse alone: on a
        // real QPSK payload the fourth power is incoherent at the SNRs that matter, and a
        // seed released there hands an 18°-per-symbol burst to a 9°-per-symbol tracker.
        // Release is the DCD falling edge, the burst's end; the decay below is the backstop
        // for a burst DCD never marked, so one station's offset cannot outlive the gap to a
        // weaker next station that must start from the tracker's own window.
        double offsetCoherence = Math.Sqrt(
            (_offsetWindowReal * _offsetWindowReal) + (_offsetWindowImag * _offsetWindowImag));
        bool windowCoherent = _windowCoherent
            ? offsetCoherence >= OffsetCoherenceFloor / 2
            : offsetCoherence >= OffsetCoherenceFloor;
        if (windowCoherent && !_windowCoherent)
        {
            _seededRotation = Math.Clamp(
                Math.Atan2(_offsetWindowImag, _offsetWindowReal) / 4.0,
                -MaxSeededRotation, MaxSeededRotation);
            Array.Clear(_trackedRotation);
        }

        _windowCoherent = windowCoherent;
        bool dcd = CarrierDetect;
        if (_dcdWasAsserted && !dcd)
        {
            _seededRotation = 0;
        }

        _dcdWasAsserted = dcd;
        if (!dcd)
        {
            for (int phase = 0; phase < _trackedRotation.Length; phase++)
            {
                _trackedRotation[phase] *= IdleTrackerLeak;
            }

            if (!windowCoherent)
            {
                _seededRotation *= _seedIdleDecay;
            }
        }

        // A burst starts from a zero tracker. Between bursts the free-running tracker walks on
        // noise and, at the acquisition gain, parks anywhere inside its clamp (measured on the
        // parity test's idle channel: the clamp itself, 30 Hz at 1200 Bd, more often than not),
        // which a 24-symbol preamble cannot unlearn. In-band energy rising while nothing says
        // a burst is already under way is the earliest sign of one - within a 20 ms block -
        // so the trackers restart from zero there and acquire the real residual from the first
        // symbols as designed. A burst too weak for the energy detector keeps today's walk.
        bool energy = _energyBusy.Busy;
        if (energy && !_energyWasBusy && !dcd && !windowCoherent)
        {
            Array.Clear(_trackedRotation);
        }

        _energyWasBusy = energy;

        // And the trackers integrate only while something says a signal is there: the
        // decision-directed error on noise alone is a random angle, and at the acquisition
        // gain it walks the tracker to its clamp between bursts (measured: 30 Hz at 1200 Bd,
        // parked there at the start of a burst its 24-symbol preamble then could not save).
        // Frozen on noise, the idle leak above drains it instead.
        _trackerActive = dcd || windowCoherent || energy;

        // The clock acquires at the differential inertia and holds, nearly rigid, once DCD
        // says the decisions are good: on the 2026-08-21 off-air fixture the clock that
        // acquired at 6-7 samples then wandered 2-10 through the payload while a rigid clock
        // at 8 copies the frame, and the timing phases can only reach a phase the clock keeps
        // still for. PR #330 gated the hold on the offset window's seed as well, because the
        // transition-timing DCD of the day never asserted on a clean or off-frequency burst;
        // that froze a clock the pre-seed rotation residual had biased (a lone modem at
        // 11.25 Hz and +40 dB stopped copying). The decision-quality DCD asserts only once
        // the symbol decisions are good, which is exactly when the clock is worth holding.
        _dpll.Inertia = dcd ? HoldInertia : DifferentialInertia;

        // The DPLL and DCD see the product de-rotated by the clock instant's own estimate.
        double rotation = _seededRotation + _trackedRotation[0];
        _derotateCos = Math.Cos(rotation);
        _derotateSin = Math.Sin(rotation);
    }

    private int DecideAgainstReference(int phase, float sampleI, float sampleQ, int productQuadrant, out float margin)
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

        // The sample's angle relative to the reference, decided to the nearest quadrant. An
        // empty reference (the very first symbol after a reset) lets the product decide and
        // the absolute chain follow it; the blend below then builds the reference along the
        // signal's own phase - absolute phase is arbitrary and the wire format differential,
        // exactly as on BPSK.
        double relativeRe = (sampleI * referenceI) + (sampleQ * referenceQ);
        double relativeIm = (sampleQ * referenceI) - (sampleI * referenceQ);
        double relativeMagnitude = Math.Sqrt((relativeRe * relativeRe) + (relativeIm * relativeIm));
        if (phase == 0)
        {
            // DCD scores the decision variable itself: the sample seen from the reference,
            // before the quadrant decision rounds it (which the fourth power ignores anyway).
            _decisionDcd.OnDecision(relativeRe, relativeIm);
        }

        int previousAbsolute = _previousAbsoluteQuadrant[phase];
        int absolute = relativeMagnitude < 1e-12
            ? (previousAbsolute + productQuadrant) & 3
            : ((int)Math.Round(Math.Atan2(relativeIm, relativeRe) / (Math.PI / 2)) + 4) & 3;
        int change = (absolute - previousAbsolute) & 3;
        _previousAbsoluteQuadrant[phase] = absolute;

        // Remodulate: rotate the sample back by its decided quadrant - exact axis swaps,
        // no trigonometry - so every symbol stacks on the reference phasor.
        (double backRe, double backIm) = absolute switch
        {
            1 => ((double)sampleQ, (double)-sampleI),
            2 => ((double)-sampleI, (double)-sampleQ),
            3 => ((double)-sampleQ, (double)sampleI),
            _ => ((double)sampleI, (double)sampleQ),
        };

        double referenceMagnitude = Math.Sqrt((referenceI * referenceI) + (referenceQ * referenceQ));
        if (phase == 0)
        {
            DecisionObserver?.Invoke(
                referenceMagnitude > 1e-12 ? relativeRe / referenceMagnitude : 0,
                referenceMagnitude > 1e-12 ? relativeIm / referenceMagnitude : 0,
                absolute);
        }

        if (referenceMagnitude > 1e-12)
        {
            // The remodulated sample seen from the reference: its in-phase part less its
            // quadrature part is the distance to the nearer of the two 45° boundaries (times
            // root two), in the sample's own amplitude once the reference's is divided out.
            double alignedRe = ((backRe * referenceI) + (backIm * referenceQ)) / referenceMagnitude;
            double alignedIm = ((backIm * referenceI) - (backRe * referenceQ)) / referenceMagnitude;
            margin = (float)Math.Max(0, alignedRe - Math.Abs(alignedIm));

            // Advance the tracker by the angle by which the remodulated sample leads the
            // reference - the BPSK tracker's rule on the four-phase grid.
            double sampleMagnitude = Math.Sqrt((backRe * backRe) + (backIm * backIm));
            if (_trackerActive && sampleMagnitude > 1e-12)
            {
                double error = Math.Asin(Math.Clamp(alignedIm / sampleMagnitude, -1, 1));
                _trackedRotation[phase] = Math.Clamp(
                    _trackedRotation[phase] + ((_decisionDcd.Asserted ? ReferenceTrackingGain : ReferenceAcquisitionGain) * error),
                    -MaxTrackedRotation, MaxTrackedRotation);
            }
        }
        else
        {
            margin = 0;
        }

        _referenceI[phase] = (ReferenceMemory * referenceI) + ((1 - ReferenceMemory) * backRe);
        _referenceQ[phase] = (ReferenceMemory * referenceQ) + ((1 - ReferenceMemory) * backIm);
        return change;
    }

    /// <summary>Decision margin of a plain conjugate-product decision: the product rotated
    /// back by its decided quadrant, in-phase less quadrature - the distance to the nearer
    /// 45° boundary, in the product's own units (only the ordering matters downstream).</summary>
    private static float MarginOf(float re, float im, int quadrant)
    {
        (float backRe, float backIm) = quadrant switch
        {
            1 => (im, -re),
            2 => (-re, -im),
            3 => (-im, re),
            _ => (re, im),
        };
        return Math.Max(0, backRe - Math.Abs(backIm));
    }

    /// <summary>Delivers one decided dibit to the sinks. Confidence: the weaker of the two
    /// symbol margins the differential decision depends on (a dibit compares symbol k's
    /// quadrant with symbol k-1's, so a hit on either flips it - the pair-min
    /// <see cref="BpskDemodulator"/> arrived at when a bit-level chase decoder could not
    /// find the second bit of any such pair), against a slow running mean of itself
    /// (~500 symbols), squashed into (0, 1). Only the ordering matters downstream - a fade
    /// drops whole bytes to the bottom of the ranking, which is exactly what erasure
    /// decoding wants flagged. The coherent path passes a constant, which is the
    /// confidence-free behaviour it was validated with.</summary>
    private void EmitDibit(int phase, int change, float margin)
    {
        int first = (QuadrantToDibit[change] >> 1) & 1;
        int second = QuadrantToDibit[change] & 1;
        if (phase == 0)
        {
            _dibitSink(first, second);
        }

        if (_softDibitSink is null)
        {
            return;
        }

        if (_detector == PskDetector.Coherent)
        {
            _softDibitSink(first, second, 1f, phase);
            return;
        }

        float pairMin = Math.Min(margin, _previousDecisionMargin[phase]);
        _previousDecisionMargin[phase] = margin;
        _confidenceMean[phase] = _confidenceMean[phase] == 0
            ? pairMin
            : _confidenceMean[phase] + (0.002 * (pairMin - _confidenceMean[phase]));
        float confidence = _confidenceMean[phase] <= 0
            ? 0.5f
            : Math.Min(0.999f, (float)(pairMin / (4.0 * _confidenceMean[phase])));
        _softDibitSink(first, second, confidence, phase);
    }

    /// <summary>Folds one differential product into the carrier-offset window (see
    /// <see cref="CarrierOffsetHz"/>) - <see cref="BpskDemodulator"/>'s tracker with the
    /// squaring doubled to strip QPSK's four-way data steps.</summary>
    /// <remarks>
    /// Samples whose magnitude is below its running mean - the amplitude nulls a phase
    /// transition sweeps through - are dropped, so only full-amplitude symbol centres
    /// contribute; that is also what tolerates the all-reversal training preamble, whose
    /// π steps the fourth power removes entirely.
    /// </remarks>
    private void TrackCarrierOffset(double real, double imaginary)
    {
        double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));
        _averageDiffMagnitude += OffsetWindowRate * (magnitude - _averageDiffMagnitude);
        if (magnitude <= _averageDiffMagnitude || magnitude < 1e-9)
        {
            return;   // a transition null - no reliable phase here
        }

        double normalisedReal = real / magnitude;
        double normalisedImaginary = imaginary / magnitude;

        // (d/|d|)⁴ strips the multiple-of-90° data steps, leaving a phasor at four times
        // the per-symbol rotation. The window starts from zero, not from the first phasor,
        // so a lone early sample cannot read as full coherence.
        double squaredReal = (normalisedReal * normalisedReal) - (normalisedImaginary * normalisedImaginary);
        double squaredImaginary = 2 * normalisedReal * normalisedImaginary;
        double fourthReal = (squaredReal * squaredReal) - (squaredImaginary * squaredImaginary);
        double fourthImaginary = 2 * squaredReal * squaredImaginary;
        _offsetWindowReal += OffsetWindowRate * (fourthReal - _offsetWindowReal);
        _offsetWindowImag += OffsetWindowRate * (fourthImaginary - _offsetWindowImag);
    }
}
