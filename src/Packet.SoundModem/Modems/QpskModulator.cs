using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Differential QPSK modulator per the IL2P symbol map (spec draft v0.6): bits are taken
/// as dibits (left bit first) and each dibit selects a carrier phase *change* —
/// 11 → 0°, 10 → +90°, 01 → +270°, 00 → +180°. A zeros preamble therefore produces
/// continuous reversals. The symbol map is bench-confirmed against a NinoTNC (QtSM history
/// shows QPSK phase maps were pairwise-negotiated; ours interoperates as-is).
/// </summary>
/// <remarks>
/// Symbols are root-raised-cosine pulse-shaped on I/Q rather than written as a phase
/// trajectory. Direct phase synthesis at constant envelope is the intuitive construction
/// and is what this modulator did first, but its transitions are far too fast: it measured
/// 5344 Hz of 99 % occupied bandwidth at 1200 sym/s where a NinoTNC's own mode-11 signal
/// measures 1887 Hz and Nino's published figure is 2400 Hz — i.e. it would have splattered
/// a channel either side. Pulses are summed in continuous time so that fractional
/// samples-per-symbol rates (1800 Bd at 12 kHz = 6⅔) need no resampling.
/// </remarks>
public sealed class QpskModulator
{
    private static readonly int[] DibitToQuarterTurns = BuildDibitMap();

    /// <summary>RRC roll-off. 0.35 puts 1200 sym/s at ~1620 Hz of occupied bandwidth,
    /// inside Nino's 2400 Hz, while keeping the pulse tail short.</summary>
    private const double RollOff = 0.35;

    /// <summary>Pulse truncation, in symbols either side of centre.</summary>
    private const int PulseSpan = 6;

    private readonly int _sampleRate;
    private readonly double _carrierStep;
    private readonly double _samplesPerSymbol;

    /// <summary>Creates a modulator.</summary>
    /// <param name="sampleRate">Output sample rate.</param>
    /// <param name="baud">Symbol rate (bit rate / 2): 1200 for QPSK-2400, 1800 for
    /// QPSK-3600.</param>
    /// <param name="carrierFrequency">Carrier centre (1500 Hz for 2400; the 3600 mode
    /// conventionally sits at 1650 Hz).</param>
    public QpskModulator(int sampleRate, int baud, double carrierFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        _sampleRate = sampleRate;
        _carrierStep = 2 * Math.PI * carrierFrequency / sampleRate;
        _samplesPerSymbol = (double)sampleRate / baud;
    }

    /// <summary>Modulates logical bits (even count; byte streams always are) to audio.</summary>
    /// <param name="bits">Logical bits, one per byte LSB.</param>
    /// <param name="amplitude">Peak amplitude of the shaped envelope.</param>
    /// <param name="rampFraction">Ignored. Retained so callers that tuned the old
    /// phase-ramp knob keep compiling; RRC shaping replaces what it was approximating.</param>
    public float[] Modulate(ReadOnlySpan<byte> bits, float amplitude = 0.8f, double rampFraction = 0.25)
    {
        _ = rampFraction;
        if (bits.Length % 2 != 0)
        {
            throw new ArgumentException("QPSK needs an even number of bits", nameof(bits));
        }

        int symbolCount = bits.Length / 2;

        // Differential map: cumulative phase, one entry per symbol.
        var inPhase = new double[symbolCount];
        var quadrature = new double[symbolCount];
        double phase = 0;
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int dibit = ((bits[symbol * 2] & 1) << 1) | (bits[symbol * 2 + 1] & 1);
            phase += DibitToQuarterTurns[dibit] * (Math.PI / 2);
            inPhase[symbol] = Math.Cos(phase);
            quadrature[symbol] = Math.Sin(phase);
        }

        var samples = new float[(int)Math.Ceiling(symbolCount * _samplesPerSymbol)];
        double carrierPhase = 0;

        // Sum the RRC pulses contributing to each sample. Only the symbols within
        // PulseSpan of this instant matter, so the inner loop is bounded.
        for (int position = 0; position < samples.Length; position++)
        {
            double centre = position / _samplesPerSymbol;
            int first = Math.Max(0, (int)Math.Ceiling(centre - PulseSpan));
            int last = Math.Min(symbolCount - 1, (int)Math.Floor(centre + PulseSpan));

            double i = 0, q = 0;
            for (int symbol = first; symbol <= last; symbol++)
            {
                double pulse = FilterDesign.RootRaisedCosine(centre - symbol, RollOff);
                i += inPhase[symbol] * pulse;
                q += quadrature[symbol] * pulse;
            }

            carrierPhase += _carrierStep;

            // Same convention as an unshaped sin(carrier + phase): with i = cos(phase)
            // and q = sin(phase) at the symbol centre this is exactly that, so the
            // demodulator sees the constellation it always did.
            samples[position] = (float)((i * Math.Sin(carrierPhase)) + (q * Math.Cos(carrierPhase)));
        }

        Normalise(samples, amplitude);
        return samples;
    }

    /// <summary>Scales to the requested peak. RRC shaping is not constant-envelope — the
    /// peak depends on the symbol sequence — so normalise rather than let the level wander
    /// with the payload.</summary>
    private static void Normalise(float[] samples, float amplitude)
    {
        float peak = 0;
        foreach (float s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        if (peak <= 1e-9f)
        {
            return;
        }

        float gain = amplitude / peak;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    private static int[] BuildDibitMap()
    {
        // Index = dibit value (left bit << 1 | right bit); value = quarter turns of
        // carrier phase change, from the spec's QPSK symbol map.
        var map = new int[4];
        map[0b11] = 0;
        map[0b10] = 1; // +90°
        map[0b01] = 3; // +270°
        map[0b00] = 2; // +180°
        return map;
    }
}
