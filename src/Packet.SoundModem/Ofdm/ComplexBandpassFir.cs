namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Streaming complex-coefficient FIR — a port of codec2's <c>quisk_cfTune</c> +
/// <c>quisk_ccfFilter</c> (<c>filter.c:232,263</c>, LGPL-2.1, © Jim Ahlstrom / David Rowe). A
/// real low-pass prototype is heterodyned to a carrier centre (<c>cpxCoefs[i] =
/// cmplx(2π·freq·(i−D))·proto[i]</c>, <c>D=(nTaps−1)/2</c>) to form an analytic band-pass, then
/// applied as an ordinary complex convolution over a zero-initialised circular history. This is
/// the datac TX out-of-band-cleanup filter in the Hilbert-clipper chain
/// (<c>ofdm_hilbert_clipper</c>). The accumulation order (newest sample × <c>cpxCoefs[0]</c>
/// first) mirrors quisk exactly so the float rounding tracks codec2. Its state persists across a
/// burst's preamble → data → postamble segments and is only cleared by <see cref="Reset"/> —
/// see docs/ofdm-design.md §3.9 for why that group-delay carryover is part of the waveform.
/// </summary>
internal sealed class ComplexBandpassFir
{
    private readonly Cf[] _coefficients;
    private readonly Cf[] _history;
    private int _position;

    /// <summary>Builds and tunes the filter.</summary>
    /// <param name="prototype">Real low-pass prototype taps (the <c>filtP*</c> arrays).</param>
    /// <param name="normalisedFreq">Carrier centre as a fraction of the sample rate
    /// (<c>centre/Fs</c>), matching <c>quisk_cfTune</c>'s <c>freq</c> argument.</param>
    public ComplexBandpassFir(float[] prototype, float normalisedFreq)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentOutOfRangeException.ThrowIfZero(prototype.Length);

        int nTaps = prototype.Length;
        _coefficients = new Cf[nTaps];
        _history = new Cf[nTaps];

        // quisk_cfTune (filter.c:232-247): D and tune in double, tval rounded to float per tap.
        double tune = 2.0 * Math.PI * normalisedFreq;
        double d = (nTaps - 1) / 2.0;
        for (int i = 0; i < nTaps; i++)
        {
            float tval = (float)(tune * (i - d));
            _coefficients[i] = new Cf(MathF.Cos(tval), MathF.Sin(tval)) * prototype[i];
        }
    }

    /// <summary>Filters one sample (<c>quisk_ccfFilter</c>'s inner loop): the newest sample
    /// pairs with <c>cpxCoefs[0]</c>, walking backwards through history.</summary>
    public Cf Next(Cf sample)
    {
        _history[_position] = sample;
        Cf acc = Cf.Zero;
        int index = _position;
        foreach (Cf coefficient in _coefficients)
        {
            acc += coefficient * _history[index];
            if (--index < 0)
            {
                index = _history.Length - 1;
            }
        }

        if (++_position == _history.Length)
        {
            _position = 0;
        }

        return acc;
    }

    /// <summary>Filters a block in place-safe fashion (<paramref name="input"/> and
    /// <paramref name="output"/> may alias — each input is captured into history before its
    /// output is written, exactly as <c>quisk_ccfFilter</c> permits <c>in==out</c>).</summary>
    public void Filter(ReadOnlySpan<Cf> input, Span<Cf> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = Next(input[i]);
        }
    }

    /// <summary>Clears the history to zero — matches a freshly allocated <c>quisk_cfFilter</c>
    /// (start of a burst on a fresh codec2 struct).</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }
}
