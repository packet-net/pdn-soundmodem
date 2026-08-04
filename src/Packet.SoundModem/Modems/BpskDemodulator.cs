using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// BPSK demodulator: band-pass → complex mix to baseband → I/Q low-pass → per-symbol bit
/// (per the IL2P symbol map: 1 = phase repeat, 0 = reversal). Two detection methods share the
/// chain (see <see cref="PskDetector"/>): the default <b>coherent</b> path recovers the
/// carrier phase with a <see cref="CostasLoop"/> (locking the constellation to the real axis)
/// and differentially decodes consecutive <em>absolute</em> symbols - what the NinoTNC does;
/// the <b>differential</b> path multiplies by the conjugate of the one-symbol-delayed
/// baseband, whose real part is positive on a phase repeat and negative on a reversal,
/// tolerant of small frequency offsets and acquiring instantly. Emits logical bits once per
/// symbol - feed straight into <see cref="M0LTE.Il2p.Il2pDeframer"/>. Covers the NinoTNC 300
/// (mode 8) and 1200 (mode 10) BPSK symbol rates.
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

    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly PskDetector _detector;
    private readonly CostasLoop? _costas;
    private readonly float[] _delayI;
    private readonly float[] _delayQ;
    private readonly double _oscillatorStep;
    private readonly int _sampleRate;
    private double _oscillatorPhase;
    private double _averageDiffMagnitude;
    private double _offsetWindowReal;
    private double _offsetWindowImag;
    private int _delayPosition;
    private int _previousLevel;
    private float _previousDecision;
    private float _previousI;
    private float _lastPlotI;

    /// <summary>Raised once per recovered symbol with the 1-D decision as (I,0): the recovered
    /// absolute symbol in coherent mode, the differential product in differential mode.
    /// Null-safe; wire from the modem.</summary>
    public Action<float, float>? SymbolPlotted { get; set; }

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
    public BpskDemodulator(
        int sampleRate, Action<int> bitSink, double carrierFrequency = 1500, int baud = 300,
        PskDetector detector = PskDetector.Differential, double? loopBandwidthHz = null)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        _detector = detector;
        // QtSoundModem's P300 filter set, scaled by symbol rate: band-pass ±baud (which
        // lands on Nino's published OBW at both rates - 500 Hz at 300 Bd, 2400 Hz at
        // 1200 Bd), I/Q low-pass at ⅔·baud.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _sampleRate = sampleRate;
        if (detector == PskDetector.Coherent)
        {
            _costas = new CostasLoop(sampleRate, carrierFrequency, loopBandwidthHz ?? baud * 0.06);
        }

        int samplesPerSymbol = sampleRate / baud;
        _delayI = new float[samplesPerSymbol];
        _delayQ = new float[samplesPerSymbol];
        _dpll = new BitDpll(
            baud, sampleRate,
            level =>
            {
                SymbolPlotted?.Invoke(_lastPlotI, 0f);
                // Coherent feeds the absolute sign bit; differentially decode against the
                // previous symbol (a repeat is a '1'), resolving the loop's π ambiguity.
                // Differential already feeds the decided logical bit, so pass it through.
                if (_detector == PskDetector.Coherent)
                {
                    bitSink(level == _previousLevel ? 1 : 0);
                    _previousLevel = level;
                }
                else
                {
                    bitSink(level);
                }
            },
            transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
        _energyBusy = new EnergyBusyDetector(sampleRate);
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
    /// <para><b>Differential.</b> The detector has already formed z·conj(z one symbol ago) to
    /// decide the bit - its real part <em>is</em> the decision. That product's angle is the
    /// per-symbol carrier rotation plus a 0-or-π data step, so squaring the normalised product
    /// removes the data and leaves a phasor at twice the offset; the estimate rides for free on
    /// arithmetic the detector was doing anyway. This is
    /// <see cref="BpskCarrierOffsetEstimator"/>'s algorithm inlined (that class stays as the
    /// standalone way to measure a channel without decoding it), with one difference that
    /// matters here: it peak-holds since the last reset, which would freeze on the first strong
    /// burst of a 26-hour session, whereas this windows the recent signal so a frame's reading
    /// describes <em>that</em> frame. Unambiguous over ±baud/4 - ±75 Hz at 300 Bd, far wider
    /// than any bank's span.</para>
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
        // Our own transmission is not a measurement of anybody's offset.
        _averageDiffMagnitude = 0;
        _offsetWindowReal = 0;
        _offsetWindowImag = 0;
    }

    /// <summary>Processes a block of audio samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _bandPass.Next(sample);
            _energyBusy.Process(filtered);
            if (_detector == PskDetector.Coherent)
            {
                ProcessCoherent(filtered);
            }
            else
            {
                ProcessDifferential(filtered);
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

    // Differential: multiply by the conjugate of the one-symbol-delayed baseband; the real
    // part is + on a phase repeat ('1') and − on a reversal ('0').
    private void ProcessDifferential(float filtered)
    {
        _oscillatorPhase += _oscillatorStep;
        if (_oscillatorPhase > 2 * Math.PI)
        {
            _oscillatorPhase -= 2 * Math.PI;
        }

        float i = _lowPassI.Next(filtered * (float)Math.Sin(_oscillatorPhase));
        float q = _lowPassQ.Next(filtered * (float)Math.Cos(_oscillatorPhase));

        float delayedI = _delayI[_delayPosition];
        float delayedQ = _delayQ[_delayPosition];
        _delayI[_delayPosition] = i;
        _delayQ[_delayPosition] = q;
        if (++_delayPosition == _delayI.Length)
        {
            _delayPosition = 0;
        }

        // Re(z·conj(z_delayed)): + on phase repeat ('1'), − on reversal ('0'). The imaginary
        // part the bit decision throws away is what carries the carrier offset.
        float decision = i * delayedI + q * delayedQ;
        TrackCarrierOffset(decision, (q * delayedI) - (i * delayedQ));
        double crossing = 0;
        if ((decision > 0) != (_previousDecision > 0) && decision != _previousDecision)
        {
            crossing = Math.Clamp(decision / (double)(decision - _previousDecision), 0, 0.999);
        }

        _previousDecision = decision;
        _lastPlotI = decision;   // held for the constellation tap (see QpskDemodulator)
        _dpll.Sample(decision > 0 ? 1 : 0, crossing);
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
