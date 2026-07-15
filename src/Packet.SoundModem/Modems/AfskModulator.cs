using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>
/// AFSK modulator: synthesises phase-continuous mark/space tones. Phase continuity across
/// tone changes is what keeps the spectrum tight; there is no per-bit phase reset.
/// Defaults are Bell 202 (1200 baud, 1200/2200 Hz — NinoTNC modes 6/7); Nino's HF modes
/// (12/13/14) are 300 baud on 1600/1800 Hz.
/// </summary>
public sealed class AfskModulator
{
    private readonly int _sampleRate;
    private readonly double _markStep;
    private readonly double _spaceStep;
    private readonly double _samplesPerBit;

    /// <summary>Creates a modulator.</summary>
    /// <param name="sampleRate">Output sample rate.</param>
    /// <param name="baud">Bit rate: 1200 (Bell 202) or 300 (HF).</param>
    /// <param name="markFrequency">Tone for a mark (line level 1).</param>
    /// <param name="spaceFrequency">Tone for a space (line level 0).</param>
    public AfskModulator(
        int sampleRate = 12000, int baud = 1200,
        double markFrequency = 1200, double spaceFrequency = 2200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        _sampleRate = sampleRate;
        _markStep = 2 * Math.PI * markFrequency / sampleRate;
        _spaceStep = 2 * Math.PI * spaceFrequency / sampleRate;
        _samplesPerBit = (double)sampleRate / baud;
    }

    /// <summary>The configured sample rate.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Modulates logical HDLC bits (from <see cref="HdlcFramer.FrameBits"/>) to
    /// audio samples at unit amplitude.</summary>
    public float[] Modulate(ReadOnlySpan<byte> hdlcBits, float amplitude = 0.8f)
    {
        var encoder = new NrziEncoder();
        var levels = new byte[hdlcBits.Length];
        for (int i = 0; i < hdlcBits.Length; i++)
        {
            levels[i] = (byte)encoder.Encode(hdlcBits[i]);
        }

        return ModulateLevels(levels, amplitude);
    }

    /// <summary>Modulates line levels directly — 1 = mark, 0 = space, no NRZI. The IL2P
    /// bit layer uses this: its transparency comes from packet-synchronous scrambling and
    /// its bits go on the wire raw (Dire Wolf's IL2P receiver likewise taps the
    /// pre-NRZI-decode bit).</summary>
    public float[] ModulateLevels(ReadOnlySpan<byte> levels, float amplitude = 0.8f)
    {
        var samples = new float[(int)Math.Ceiling(levels.Length * _samplesPerBit)];
        double phase = 0;
        double bitClock = 0;
        int position = 0;

        foreach (byte level in levels)
        {
            double step = level == 1 ? _markStep : _spaceStep;
            bitClock += _samplesPerBit;
            while (position < bitClock && position < samples.Length)
            {
                phase += step;
                samples[position++] = amplitude * (float)Math.Sin(phase);
            }
        }

        return position == samples.Length ? samples : samples[..position];
    }
}
