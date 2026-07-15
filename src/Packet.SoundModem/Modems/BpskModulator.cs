namespace Packet.SoundModem.Modems;

/// <summary>
/// BPSK modulator per the IL2P symbol map (spec draft v0.6): a '1' bit is no change in
/// carrier phase, a '0' bit is a 180° change — differential encoding is inherent in the
/// symbol map, not applied separately. Phase transitions are shaped with a cosine
/// amplitude ramp over a quarter symbol to limit keying sidebands (the UZ7HO approach;
/// full spectral shaping for on-air use is a Phase 3 concern). Covers the NinoTNC
/// 300 (mode 8) and 1200 (mode 10) BPSK symbol rates, both on a 1500 Hz carrier.
/// </summary>
public sealed class BpskModulator
{
    private readonly int _sampleRate;
    private readonly double _carrierStep;
    private readonly int _samplesPerSymbol;

    /// <summary>Creates a modulator at the given sample rate, symbol rate and carrier
    /// (300 baud on 1500 Hz is the NinoTNC HF convention).</summary>
    public BpskModulator(int sampleRate = 12000, int baud = 300, double carrierFrequency = 1500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
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

    /// <summary>Modulates logical bits (from <see cref="Il2p.Il2pFramer.FrameBits"/> with
    /// the zeros preamble style) to audio samples.</summary>
    public float[] Modulate(ReadOnlySpan<byte> bits, float amplitude = 0.8f)
    {
        var samples = new float[bits.Length * _samplesPerSymbol];
        double carrierPhase = 0;
        float polarity = 1f;
        int position = 0;
        int ramp = _samplesPerSymbol / 4;

        foreach (byte bit in bits)
        {
            bool reversal = (bit & 1) == 0;
            for (int i = 0; i < _samplesPerSymbol; i++)
            {
                carrierPhase += _carrierStep;
                float envelope = 1f;
                if (reversal && i < ramp)
                {
                    // Cosine ramp through the reversal: amplitude dips to zero exactly at
                    // the polarity flip instead of a hard 180° step.
                    envelope = (float)Math.Cos(Math.PI * (ramp - i) / (2.0 * ramp));
                    if (i == 0)
                    {
                        polarity = -polarity;
                    }
                }

                samples[position++] = amplitude * polarity * envelope * (float)Math.Sin(carrierPhase);
            }
        }

        return samples;
    }
}
