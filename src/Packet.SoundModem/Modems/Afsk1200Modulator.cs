using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Bell 202 AFSK modulator: NRZI-encodes the logical HDLC bit stream and synthesises
/// phase-continuous mark/space tones (1200/2200 Hz at 1200 baud). Phase continuity across
/// tone changes is what keeps the spectrum tight; there is no per-bit phase reset.
/// </summary>
public sealed class Afsk1200Modulator
{
    private readonly int _sampleRate;
    private readonly double _markStep;
    private readonly double _spaceStep;
    private readonly double _samplesPerBit;

    /// <summary>Creates a modulator for the given sample rate (mark 1200 Hz, space 2200 Hz,
    /// 1200 baud).</summary>
    public Afsk1200Modulator(int sampleRate = 12000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _sampleRate = sampleRate;
        _markStep = 2 * Math.PI * 1200 / sampleRate;
        _spaceStep = 2 * Math.PI * 2200 / sampleRate;
        _samplesPerBit = (double)sampleRate / 1200;
    }

    /// <summary>The configured sample rate.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Modulates logical HDLC bits (from <see cref="HdlcFramer.FrameBits"/>) to
    /// audio samples at unit amplitude.</summary>
    public float[] Modulate(ReadOnlySpan<byte> hdlcBits, float amplitude = 0.8f)
    {
        var encoder = new NrziEncoder();
        var samples = new float[(int)Math.Ceiling(hdlcBits.Length * _samplesPerBit)];
        double phase = 0;
        double bitClock = 0;
        int position = 0;

        foreach (byte bit in hdlcBits)
        {
            int level = encoder.Encode(bit);
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
