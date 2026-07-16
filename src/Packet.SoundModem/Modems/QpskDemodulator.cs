using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Differential QPSK demodulator: band-pass → complex mix → I/Q low-pass → product with
/// the conjugate of the one-symbol-delayed baseband → phase-change quadrant → dibit (the
/// inverse of the spec's symbol map). The one-symbol delay is fractional-capable (linear
/// interpolation) because 1800 baud at 12 kHz is 6⅔ samples per symbol. Symbol clock from
/// the shared <see cref="BitDpll"/>, driven by quadrant changes.
/// </summary>
public sealed class QpskDemodulator
{
    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly float[] _delayI;
    private readonly float[] _delayQ;
    private readonly int _delayWhole;
    private readonly float _delayFraction;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;
    private int _delayPosition;
    private float _lastRe;
    private float _lastIm;

    /// <summary>Raised once per recovered symbol with the differential product (I,Q) at
    /// the symbol instant — the constellation point. Null-safe; wire from the modem.</summary>
    public Action<float, float>? SymbolPlotted { get; set; }

    /// <summary>Creates a demodulator delivering dibits (left bit first) to
    /// <paramref name="dibitSink"/> once per symbol.</summary>
    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="baud">Symbol rate (1200 or 1800).</param>
    /// <param name="dibitSink">Receives (firstBit, secondBit) per symbol.</param>
    /// <param name="carrierFrequency">Carrier centre.</param>
    public QpskDemodulator(int sampleRate, int baud, Action<int, int> dibitSink, double carrierFrequency)
    {
        ArgumentNullException.ThrowIfNull(dibitSink);
        // Filter plan follows QtSM's per-mode tables: BPF ≈ 2×baud wide, LPF ≈ 0.75×baud.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - baud, carrierFrequency + baud, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(0.75 * baud, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(0.75 * baud, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _energyBusy = new EnergyBusyDetector(sampleRate);

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
                dibitSink((QuadrantToDibit[quadrant] >> 1) & 1, QuadrantToDibit[quadrant] & 1);
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
}
