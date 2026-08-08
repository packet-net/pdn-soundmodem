using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// QPSK demodulator: band-pass → complex mix → I/Q low-pass → per-symbol dibit (the inverse
/// of the spec's symbol map). Two detection methods share this chain (see
/// <see cref="PskDetector"/>): the default <b>coherent</b> path recovers the carrier phase
/// with a <see cref="CostasLoop"/> and differentially decodes consecutive <em>absolute</em>
/// quadrants (what the NinoTNC does); the <b>differential</b> path multiplies by the
/// conjugate of the one-symbol-delayed baseband to read the phase change directly. The
/// one-symbol delay is fractional-capable (linear interpolation) because 1800 baud at 12 kHz
/// is 6⅔ samples per symbol. Symbol clock from the shared <see cref="BitDpll"/>, driven by
/// quadrant changes; either way the same <see cref="QuadrantToDibit"/> map turns a
/// phase-change quadrant into a dibit, so the wire format is identical.
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

    /// <summary>Per-symbol gain of the reference frequency loop - the
    /// <see cref="BpskDemodulator"/> measured value.</summary>
    private const double ReferenceFrequencyGain = 0.05;

    /// <summary>Clamp on the seeded part of the reference rotation, radians per symbol:
    /// π/4 is ±baud/8, the fourth-power offset window's own unambiguous range - ±37.5 Hz
    /// at 300 Bd, ±150 Hz at 1200 Bd, far beyond any real station error.</summary>
    private const double MaxSeededRotation = Math.PI / 4;

    /// <summary>Clamp on the tracked part of the reference rotation, radians per symbol.
    /// Unlike BPSK there is no diversity bank whose step this needs to match; π/16
    /// (±baud/64 - a few Hz at 300 Bd) bounds the free-running tracker's between-burst
    /// noise wander to a fraction of the 45° decision margin while still absorbing
    /// residual drift the per-burst seed missed.</summary>
    private const double MaxTrackedRotation = Math.PI / 16;

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
    private readonly PacketDcd _packetDcd = new();
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
    private int _previousAbsoluteQuadrant;
    private float _lastRe;
    private float _lastIm;
    private float _previousRe;
    private float _previousIm;
    private float _currentI;
    private float _currentQ;
    private double _referenceI;
    private double _referenceQ;
    private double _seededRotation;
    private double _trackedRotation;
    private bool _rotationSeeded;
    private readonly bool _decisionFeedback;

    /// <summary>Raised once per recovered symbol with the symbol-instant constellation point
    /// (I,Q): the recovered absolute constellation in coherent mode, the differential product
    /// in differential mode. Null-safe; wire from the modem.</summary>
    public Action<float, float>? SymbolPlotted { get; set; }

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
    /// per-mode value (0.20 for qpsk600, 0.35 for qpsk2400, 0.25 for qpsk3600).</param>
    /// <param name="decisionFeedback">Whether the differential path decides against the
    /// decision-feedback reference once a burst's rotation is seeded (see
    /// <see cref="DecideAgainstReference"/>). On for the SSB modes the 2026-08-07 QPSK
    /// campaign measured; OFF for qpsk3600, where the same handover measured a clean-signal
    /// regression (22/30 against the plain product's 30/30 at σ=0.12) - the FM chain at
    /// 6⅔ samples per symbol was outside the campaign's scope and keeps the detector it
    /// was validated with until its own campaign says otherwise.</param>
    public QpskDemodulator(
        int sampleRate, int baud, Action<int, int> dibitSink, double carrierFrequency,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null,
        double rollOff = QpskModulator.DefaultRollOff, bool decisionFeedback = true)
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
        _interpolateSymbolInstant = Math.Abs(delay - Math.Round(delay)) > 1e-9;
        _sampleRate = chainRate;
        _delayWhole = (int)Math.Floor(delay);
        _delayFraction = (float)(delay - _delayWhole);
        // Ring of whole+1: the slot about to be overwritten holds z[n-(whole+1)] (older),
        // the next slot holds z[n-whole] (newer); lerp between them by the fraction.
        _delayI = new float[_delayWhole + 1];
        _delayQ = new float[_delayWhole + 1];

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
                if (_interpolateSymbolInstant && _detector != PskDetector.Coherent)
                {
                    // The field is still being assigned as this lambda is constructed; nothing
                    // reads it until audio flows, which is the same pattern BpskModem uses for
                    // its deframer/demodulator knot.
                    float f = (float)_dpll!.WrapOvershootSamples;
                    float re = _lastRe - (f * (_lastRe - _previousRe));
                    float im = _lastIm - (f * (_lastIm - _previousIm));
                    quadrant = ((int)Math.Round(Math.Atan2(im, re) / (Math.PI / 2)) + 4) & 3;
                }

                // Coherent feeds the absolute quadrant; differentially decode against the
                // previous symbol so the wire mapping (a phase change) is unchanged.
                // Differential decides the symbol-instant sample against the
                // decision-feedback reference (the passed product quadrant only drove
                // symbol timing).
                int change = _detector == PskDetector.Coherent
                    ? (quadrant - _previousQuadrant) & 3
                    : _decisionFeedback ? DecideAgainstReference(quadrant) : quadrant;
                _previousQuadrant = quadrant;
                dibitSink((QuadrantToDibit[change] >> 1) & 1, QuadrantToDibit[change] & 1);
            },
            // Dire Wolf's 0.74 costs ~1 dB of timing jitter at the Reed-Solomon threshold
            // on the differential path here exactly as it did on bpsk300 (docs/qpsk/plan.md
            // Q1-3, PR #236's measurement); 0.92 recovers it, and the all-reversal IL2P
            // preamble - a transition every symbol - still pulls a cold clock in within a
            // normal TXDELAY. Coherent keeps its issue-#5 measured configuration.
            inertia: detector == PskDetector.Coherent ? 0.74 : 0.92,
            transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
    }

    private static readonly int[] QuadrantToDibit = [0b11, 0b10, 0b00, 0b01]; // 0°,90°,180°,270°

    /// <summary>True while DPLL transition timing indicates a coherent packet signal.</summary>
    public bool CarrierDetect => _packetDcd.Asserted;

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
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <summary>Clears carrier state.</summary>
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
        // Our own transmission is not a measurement of anybody's offset, and not a
        // carrier reference to decide the next station's symbols against.
        _averageDiffMagnitude = 0;
        _offsetWindowReal = 0;
        _offsetWindowImag = 0;
        _referenceI = 0;
        _referenceQ = 0;
        _seededRotation = 0;
        _trackedRotation = 0;
        _rotationSeeded = false;
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
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
        float re = i * delayedI + q * delayedQ;
        float im = q * delayedI - i * delayedQ;
        TrackCarrierOffset(re, im);
        double angle = Math.Atan2(im, re);
        int quadrant = ((int)Math.Round(angle / (Math.PI / 2)) + 4) & 3;

        // Held for the constellation tap: the DPLL fires its symbol sink synchronously
        // inside Sample() on wrap samples, so these are the wrap-instant values.
        _previousRe = _lastRe;
        _previousIm = _lastIm;
        _lastRe = re;
        _lastIm = im;
        _currentI = i;
        _currentQ = q;
        _dpll.Sample(quadrant);
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
    /// <para><b>The seed gates the handover, not just the rotation - QPSK's own lesson.</b>
    /// Until the burst's rotation is seeded from the fourth-power offset window (quartering
    /// its angle recovers the per-symbol rotation, unambiguous over ±baud/8), the DIBIT
    /// comes from the plain conjugate product, which is offset-immune; the reference warms
    /// up alongside on those product decisions but decides nothing. A first cut that let
    /// the reference decide from cold measured 60 % at a mere 8 Hz offset at +8 dB (and
    /// 0 % at 15 Hz) against the plain product's ~99 %: an unseeded reference must drag
    /// its phase through the offset by decision-remodulation alone, and QPSK gives it half
    /// the preamble (two bits per symbol) and half the phase margin BPSK had. BPSK can
    /// afford to decide from cold; QPSK cannot, and a burst too weak to ever seed simply
    /// keeps the product detector it always had.</para>
    /// <para><b>The tracker exists only after the seed.</b> It starts at zero on seeding
    /// and is updated decision-directed from then on; while unseeded its input would read
    /// the reference's systematic warm-up lag, not a residual - and letting it free-run on
    /// between-burst noise walks a rotation error into exactly the acquisition symbols a
    /// 12-symbol preamble cannot spare. The seed does not outlive its burst.</para>
    /// </remarks>
    private int DecideAgainstReference(int productQuadrant)
    {
        double offsetCoherence = Math.Sqrt(
            (_offsetWindowReal * _offsetWindowReal) + (_offsetWindowImag * _offsetWindowImag));
        if (!_rotationSeeded && offsetCoherence >= OffsetCoherenceFloor)
        {
            _seededRotation = Math.Clamp(
                Math.Atan2(_offsetWindowImag, _offsetWindowReal) / 4.0,
                -MaxSeededRotation, MaxSeededRotation);
            _trackedRotation = 0;
            _rotationSeeded = true;
        }
        else if (_rotationSeeded && offsetCoherence < OffsetCoherenceFloor / 2)
        {
            _rotationSeeded = false;
            _seededRotation = 0;
        }

        double rotation = _seededRotation + _trackedRotation;
        if (rotation != 0)
        {
            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);
            (_referenceI, _referenceQ) = (
                (_referenceI * cos) - (_referenceQ * sin),
                (_referenceI * sin) + (_referenceQ * cos));
        }

        int change;
        int absolute;
        if (_rotationSeeded)
        {
            // The sample's angle relative to the reference, decided to the nearest
            // quadrant. A cold reference pins the decision to quadrant 0; the blend below
            // then builds it along the signal's own phase - absolute phase is arbitrary
            // and the wire format differential, exactly as on BPSK.
            double relativeRe = (_currentI * _referenceI) + (_currentQ * _referenceQ);
            double relativeIm = (_currentQ * _referenceI) - (_currentI * _referenceQ);
            absolute = (relativeRe * relativeRe) + (relativeIm * relativeIm) < 1e-24
                ? 0
                : ((int)Math.Round(Math.Atan2(relativeIm, relativeRe) / (Math.PI / 2)) + 4) & 3;
            change = (absolute - _previousAbsoluteQuadrant) & 3;
        }
        else
        {
            // Unseeded: the plain product decides (the value the DPLL was fed), and the
            // absolute chain follows it so the reference warms up on the same decisions
            // and the handover at the seed is seamless.
            change = productQuadrant;
            absolute = (_previousAbsoluteQuadrant + change) & 3;
        }

        _previousAbsoluteQuadrant = absolute;

        // Remodulate: rotate the sample back by its decided quadrant - exact axis swaps,
        // no trigonometry - so every symbol stacks on the reference phasor.
        (double backRe, double backIm) = absolute switch
        {
            1 => ((double)_currentQ, (double)-_currentI),
            2 => ((double)-_currentI, (double)-_currentQ),
            3 => ((double)-_currentQ, (double)_currentI),
            _ => ((double)_currentI, (double)_currentQ),
        };

        if (_rotationSeeded)
        {
            // Advance the tracker by the angle by which the remodulated sample leads the
            // reference - the BPSK tracker's rule on the four-phase grid.
            double cross = (backIm * _referenceI) - (backRe * _referenceQ);
            double magnitude = Math.Sqrt(
                ((backRe * backRe) + (backIm * backIm))
                * ((_referenceI * _referenceI) + (_referenceQ * _referenceQ)));
            if (magnitude > 1e-12)
            {
                double error = Math.Asin(Math.Clamp(cross / magnitude, -1, 1));
                _trackedRotation = Math.Clamp(
                    _trackedRotation + (ReferenceFrequencyGain * error),
                    -MaxTrackedRotation, MaxTrackedRotation);
            }
        }

        _referenceI = (ReferenceMemory * _referenceI) + ((1 - ReferenceMemory) * backRe);
        _referenceQ = (ReferenceMemory * _referenceQ) + ((1 - ReferenceMemory) * backIm);
        return change;
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
