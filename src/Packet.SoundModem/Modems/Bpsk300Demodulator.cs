using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// 300-baud BPSK demodulator (differential detection, UZ7HO lineage): band-pass →
/// complex mix to baseband → I/Q low-pass → multiply by the conjugate of the
/// one-symbol-delayed baseband. The real part of that product is positive when the
/// carrier phase repeated (a '1' per the IL2P symbol map) and negative on a reversal
/// (a '0'), independent of absolute phase and tolerant of small frequency offsets.
/// Emits logical bits once per symbol — feed straight into
/// <see cref="Il2p.Il2pDeframer"/>.
/// </summary>
public sealed class Bpsk300Demodulator
{
    private readonly FirFilter _bandPass;
    private readonly FirFilter _lowPassI;
    private readonly FirFilter _lowPassQ;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly float[] _delayI;
    private readonly float[] _delayQ;
    private readonly double _oscillatorStep;
    private double _oscillatorPhase;
    private int _delayPosition;

    /// <summary>Creates a demodulator delivering logical bits to <paramref name="bitSink"/>
    /// once per symbol.</summary>
    /// <param name="sampleRate">Input sample rate (must be a multiple of 300).</param>
    /// <param name="bitSink">Receives each decided bit (1 = phase repeat, 0 = reversal).</param>
    /// <param name="carrierFrequency">Carrier centre, 1500 Hz by convention.</param>
    public Bpsk300Demodulator(int sampleRate, Action<int> bitSink, double carrierFrequency = 1500)
    {
        ArgumentNullException.ThrowIfNull(bitSink);
        if (sampleRate % 300 != 0)
        {
            throw new ArgumentException("sample rate must be a multiple of 300", nameof(sampleRate));
        }

        // QtSoundModem's P300 filter set: 600 Hz-wide band-pass, 200 Hz I/Q low-pass.
        _bandPass = new FirFilter(FilterDesign.BandPass(
            carrierFrequency - 300, carrierFrequency + 300, sampleRate, 256 * sampleRate / 12000));
        _lowPassI = new FirFilter(FilterDesign.LowPass(200, sampleRate, 128 * sampleRate / 12000));
        _lowPassQ = new FirFilter(FilterDesign.LowPass(200, sampleRate, 128 * sampleRate / 12000));
        _oscillatorStep = 2 * Math.PI * carrierFrequency / sampleRate;

        int samplesPerSymbol = sampleRate / 300;
        _delayI = new float[samplesPerSymbol];
        _delayQ = new float[samplesPerSymbol];
        _dpll = new BitDpll(300, sampleRate, bitSink, transitionObserver: _packetDcd.OnTransition);
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
            _dpll.Sample(decision > 0 ? 1 : 0);
        }
    }
}
