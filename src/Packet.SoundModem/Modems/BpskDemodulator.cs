using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// BPSK demodulator: band-pass → complex mix to baseband → I/Q low-pass → per-symbol bit
/// (per the IL2P symbol map: 1 = phase repeat, 0 = reversal). Two detection methods share the
/// chain (see <see cref="PskDetector"/>): the default <b>coherent</b> path recovers the
/// carrier phase with a <see cref="CostasLoop"/> (locking the constellation to the real axis)
/// and differentially decodes consecutive <em>absolute</em> symbols — what the NinoTNC does;
/// the <b>differential</b> path multiplies by the conjugate of the one-symbol-delayed
/// baseband, whose real part is positive on a phase repeat and negative on a reversal,
/// tolerant of small frequency offsets and acquiring instantly. Emits logical bits once per
/// symbol — feed straight into <see cref="M0LTE.Il2p.Il2pDeframer"/>. Covers the NinoTNC 300
/// (mode 8) and 1200 (mode 10) BPSK symbol rates.
/// </summary>
public sealed class BpskDemodulator
{
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
    private double _oscillatorPhase;
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
        // lands on Nino's published OBW at both rates — 500 Hz at 300 Bd, 2400 Hz at
        // 1200 Bd), I/Q low-pass at ⅔·baud.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(baud * 2.0 / 3.0, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * carrierFrequency / sampleRate;
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

    /// <summary>Channel-busy for carrier sense: packet DCD or any significant in-band
    /// energy (a carrier, voice, another mode).</summary>
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <summary>Clears carrier state, e.g. while the channel's own transmitter is keyed.</summary>
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
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
    // sink. The loop's π ambiguity is harmless — it only flips the decode of the reference
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

        // Re(z·conj(z_delayed)): + on phase repeat ('1'), − on reversal ('0').
        float decision = i * delayedI + q * delayedQ;
        double crossing = 0;
        if ((decision > 0) != (_previousDecision > 0) && decision != _previousDecision)
        {
            crossing = Math.Clamp(decision / (double)(decision - _previousDecision), 0, 0.999);
        }

        _previousDecision = decision;
        _lastPlotI = decision;   // held for the constellation tap (see QpskDemodulator)
        _dpll.Sample(decision > 0 ? 1 : 0, crossing);
    }
}
