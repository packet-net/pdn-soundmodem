namespace Packet.SoundModem.Modems;

/// <summary>
/// Differential QPSK modulator per the IL2P symbol map (spec draft v0.6): bits are taken
/// as dibits (left bit first) and each dibit selects a carrier phase *change* —
/// 11 → 0°, 10 → +90°, 01 → +270°, 00 → +180°. A zeros preamble therefore produces
/// continuous reversals. Phase transitions are shaped with a quarter-symbol cosine ramp
/// scaled to the step size. NinoTNC over-air compatibility is bench-gated (QtSM history
/// shows QPSK phase maps were pairwise-negotiated); this implements the spec exactly.
/// </summary>
public sealed class QpskModulator
{
    private static readonly int[] DibitToQuarterTurns = BuildDibitMap();

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
    /// <param name="amplitude">Peak amplitude.</param>
    /// <param name="rampFraction">Fraction of a symbol over which each phase transition
    /// is swept (raised-cosine trajectory). Evaluated in continuous time, so fractional
    /// samples-per-symbol rates (1800 baud at 12 kHz = 6⅔) neither jitter the symbol
    /// boundaries nor collapse the ramp to a hard step — both mattered on the NinoTNC
    /// bench loop, whose 3600 QPSK demodulator misses hard-stepped bursts.</param>
    public float[] Modulate(ReadOnlySpan<byte> bits, float amplitude = 0.8f, double rampFraction = 0.5)
    {
        if (bits.Length % 2 != 0)
        {
            throw new ArgumentException("QPSK needs an even number of bits", nameof(bits));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rampFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rampFraction, 1);

        int symbolCount = bits.Length / 2;
        var samples = new float[(int)Math.Ceiling(symbolCount * _samplesPerSymbol)];

        // Cumulative phase before each symbol boundary; phases[k] -> phases[k + 1] is
        // the transition at boundary time k * samplesPerSymbol.
        var phases = new double[symbolCount + 1];
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int dibit = ((bits[symbol * 2] & 1) << 1) | (bits[symbol * 2 + 1] & 1);
            phases[symbol + 1] = phases[symbol] + DibitToQuarterTurns[dibit] * (Math.PI / 2);
        }

        double rampSamples = _samplesPerSymbol * rampFraction;
        double carrierPhase = 0;
        for (int position = 0; position < samples.Length; position++)
        {
            carrierPhase += _carrierStep;

            // The most recent boundary at or before this sample instant.
            int boundary = Math.Min((int)(position / _samplesPerSymbol), symbolCount - 1);
            double sinceBoundary = position - boundary * _samplesPerSymbol;
            double phase = phases[boundary + 1];
            if (sinceBoundary < rampSamples)
            {
                double progress = 0.5 - 0.5 * Math.Cos(Math.PI * sinceBoundary / rampSamples);
                phase = phases[boundary] + (phases[boundary + 1] - phases[boundary]) * progress;
            }

            samples[position] = amplitude * (float)Math.Sin(carrierPhase + phase);
        }

        return samples;
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
