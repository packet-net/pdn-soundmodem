using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// BPSK modulator per the IL2P symbol map (spec draft v0.6): a '1' bit is no change in
/// carrier phase, a '0' bit is a 180° change — differential encoding is inherent in the
/// symbol map, not applied separately. Covers the NinoTNC 300 (mode 8) and 1200 (mode 10)
/// BPSK symbol rates, both on a 1500 Hz carrier.
/// </summary>
/// <remarks>
/// Symbols are root-raised-cosine pulse-shaped rather than keyed with a cosine amplitude
/// ramp through each reversal. The ramp was the UZ7HO approach and it is not enough: it
/// measured 1245 Hz of 99 % occupied bandwidth at 300 sym/s against Nino's published
/// 500 Hz for the mode — 2.5x over, i.e. it would have splattered either side on the HF
/// channels these modes exist for. RRC occupies about symbolRate·(1 + roll-off).
/// </remarks>
public sealed class BpskModulator
{
    /// <summary>Default RRC roll-off; matches <see cref="QpskModulator"/>.</summary>
    public const double DefaultRollOff = 0.35;

    /// <summary>Pulse truncation, in symbols either side of centre.</summary>
    private const int PulseSpan = 6;

    private readonly double _rollOff;
    private readonly int _sampleRate;
    private readonly double _carrierStep;
    private readonly int _samplesPerSymbol;

    /// <summary>Creates a modulator at the given sample rate, symbol rate and carrier
    /// (300 baud on 1500 Hz is the NinoTNC HF convention).</summary>
    public BpskModulator(
        int sampleRate = 12000, int baud = 300, double carrierFrequency = 1500,
        double rollOff = DefaultRollOff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(rollOff);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rollOff, 1);
        _rollOff = rollOff;
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _carrierStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _samplesPerSymbol = sampleRate / baud;
    }

    /// <summary>The configured sample rate.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Modulates logical bits (from <see cref="M0LTE.Il2p.Il2pFramer.FrameBits"/> with
    /// the zeros preamble style) to audio samples.</summary>
    public float[] Modulate(ReadOnlySpan<byte> bits, float amplitude = 0.8f)
    {
        // Differential map: '1' holds phase, '0' reverses it. BPSK rides I alone.
        var symbols = new double[bits.Length];
        double polarity = 1;
        for (int i = 0; i < bits.Length; i++)
        {
            if ((bits[i] & 1) == 0)
            {
                polarity = -polarity;
            }

            symbols[i] = polarity;
        }

        var samples = new float[bits.Length * _samplesPerSymbol];
        double carrierPhase = 0;
        for (int position = 0; position < samples.Length; position++)
        {
            double centre = position / (double)_samplesPerSymbol;
            int first = Math.Max(0, (int)Math.Ceiling(centre - PulseSpan));
            int last = Math.Min(symbols.Length - 1, (int)Math.Floor(centre + PulseSpan));

            double i = 0;
            for (int symbol = first; symbol <= last; symbol++)
            {
                i += symbols[symbol] * FilterDesign.RootRaisedCosine(centre - symbol, _rollOff);
            }

            carrierPhase += _carrierStep;
            samples[position] = (float)(i * Math.Sin(carrierPhase));
        }

        float peak = 0;
        foreach (float v in samples)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        if (peak > 1e-9f)
        {
            float gain = amplitude / peak;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        return samples;
    }
}
