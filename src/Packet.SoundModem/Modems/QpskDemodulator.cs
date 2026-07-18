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
    private readonly int _delayWhole;
    private readonly float _delayFraction;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;
    private int _delayPosition;
    private int _previousQuadrant;
    private float _lastRe;
    private float _lastIm;

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
    public QpskDemodulator(
        int sampleRate, int baud, Action<int, int> dibitSink, double carrierFrequency,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null)
    {
        ArgumentNullException.ThrowIfNull(dibitSink);
        _detector = detector;
        // Filter plan follows QtSM's per-mode tables: BPF ≈ 2×baud wide, LPF ≈ 0.75×baud.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(0.75 * baud, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(0.75 * baud, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _energyBusy = new EnergyBusyDetector(sampleRate);
        if (detector == PskDetector.Coherent)
        {
            _costas = new CostasLoop(sampleRate, carrierFrequency, loopBandwidthHz ?? baud * 0.06);
        }

        double delay = (double)sampleRate / baud;
        _delayWhole = (int)Math.Floor(delay);
        _delayFraction = (float)(delay - _delayWhole);
        // Ring of whole+1: the slot about to be overwritten holds z[n-(whole+1)] (older),
        // the next slot holds z[n-whole] (newer); lerp between them by the fraction.
        _delayI = new float[_delayWhole + 1];
        _delayQ = new float[_delayWhole + 1];

        _dpll = new BitDpll(
            baud, sampleRate,
            quadrant =>
            {
                SymbolPlotted?.Invoke(_lastRe, _lastIm);
                // Coherent feeds the absolute quadrant; differentially decode against the
                // previous symbol so the wire mapping (a phase change) is unchanged.
                // Differential already feeds the phase-change quadrant, so its "previous"
                // is a no-op reference of 0.
                int change = _detector == PskDetector.Coherent
                    ? (quadrant - _previousQuadrant) & 3
                    : quadrant;
                _previousQuadrant = quadrant;
                dibitSink((QuadrantToDibit[change] >> 1) & 1, QuadrantToDibit[change] & 1);
            },
            transitionObserver: _packetDcd.OnTransition, symbolObserver: _packetDcd.OnSymbol);
    }

    private static readonly int[] QuadrantToDibit = [0b11, 0b10, 0b00, 0b01]; // 0°,90°,180°,270°

    /// <summary>True while DPLL transition timing indicates a coherent packet signal.</summary>
    public bool CarrierDetect => _packetDcd.Asserted;

    /// <summary>Channel-busy for carrier sense (packet or energy).</summary>
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <summary>Clears carrier state.</summary>
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
        _oscillatorPhase += _oscillatorStep;
        if (_oscillatorPhase > 2 * Math.PI)
        {
            _oscillatorPhase -= 2 * Math.PI;
        }

        float i = _lowPassI.Next(filtered * (float)Math.Sin(_oscillatorPhase));
        float q = _lowPassQ.Next(filtered * (float)Math.Cos(_oscillatorPhase));

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

        // Phase change over one symbol; quadrant = nearest multiple of 90°.
        float re = i * delayedI + q * delayedQ;
        float im = q * delayedI - i * delayedQ;
        double angle = Math.Atan2(im, re);
        int quadrant = ((int)Math.Round(angle / (Math.PI / 2)) + 4) & 3;

        // Held for the constellation tap: the DPLL fires its symbol sink synchronously
        // inside Sample() on wrap samples, so these are the wrap-instant values.
        _lastRe = re;
        _lastIm = im;
        _dpll.Sample(quadrant);
    }
}
