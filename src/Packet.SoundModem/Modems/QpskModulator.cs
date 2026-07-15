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
    public float[] Modulate(ReadOnlySpan<byte> bits, float amplitude = 0.8f)
    {
        if (bits.Length % 2 != 0)
        {
            throw new ArgumentException("QPSK needs an even number of bits", nameof(bits));
        }

        int symbolCount = bits.Length / 2;
        var samples = new float[(int)Math.Ceiling(symbolCount * _samplesPerSymbol)];
        double carrierPhase = 0;
        double symbolPhase = 0;
        double clock = 0;
        int position = 0;

        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int dibit = ((bits[symbol * 2] & 1) << 1) | (bits[symbol * 2 + 1] & 1);
            double step = DibitToQuarterTurns[dibit] * Math.PI / 2;
            double previousPhase = symbolPhase;
            symbolPhase += step;

            clock += _samplesPerSymbol;
            int symbolSamples = 0;
            int total = (int)(clock - position);
            int ramp = Math.Max(1, (int)(_samplesPerSymbol / 4));
            while (position < clock && position < samples.Length)
            {
                carrierPhase += _carrierStep;
                double phase = symbolPhase;
                if (step != 0 && symbolSamples < ramp)
                {
                    // Sweep smoothly from the previous symbol phase to the new one over
                    // the first quarter symbol (raised-cosine phase trajectory).
                    double progress = 0.5 - 0.5 * Math.Cos(Math.PI * (symbolSamples + 1) / ramp);
                    phase = previousPhase + step * progress;
                }

                samples[position++] = amplitude * (float)Math.Sin(carrierPhase + phase);
                symbolSamples++;
            }

            _ = total;
        }

        return position == samples.Length ? samples : samples[..position];
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
