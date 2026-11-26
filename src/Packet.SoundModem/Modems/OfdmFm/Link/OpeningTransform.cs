namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The prototype's <c>first_two</c>: the carriers of a burst's symbols, taken from the capture one symbol at a time, each
/// as the 2048-point forward transform of the symbol after its 64-sample prefix, bins 9 to 341 (333 carriers). Used for
/// the opening's training and header symbols; the payload receiver takes its symbols the same way. Owns its buffers.
/// </summary>
internal sealed class OpeningTransform
{
    private readonly double[] _re = new double[BurstRenderer.FftSize];
    private readonly double[] _im = new double[BurstRenderer.FftSize];
    private readonly double[] _outRe;
    private readonly double[] _outIm;
    private int _symbols;

    /// <summary>Makes a transform with room for <paramref name="maxSymbols"/> symbols.</summary>
    public OpeningTransform(int maxSymbols)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSymbols, 1);
        _outRe = new double[maxSymbols * BurstRenderer.Carriers];
        _outIm = new double[maxSymbols * BurstRenderer.Carriers];
    }

    /// <summary>Symbols the last <see cref="Transform"/> took.</summary>
    public int Symbols => _symbols;

    /// <summary>Real parts of the last symbols' carriers, symbol by symbol, 333 each.</summary>
    public ReadOnlySpan<double> Real => _outRe.AsSpan(0, _symbols * BurstRenderer.Carriers);

    /// <summary>Imaginary parts, as <see cref="Real"/>.</summary>
    public ReadOnlySpan<double> Imaginary => _outIm.AsSpan(0, _symbols * BurstRenderer.Carriers);

    /// <summary>
    /// Takes <paramref name="symbols"/> symbols whose first sample (the prefix's) is <paramref name="start"/>. The capture
    /// must hold all of them.
    /// </summary>
    public void Transform(ReadOnlySpan<float> capture, int start, int symbols)
    {
        if (symbols < 1 || symbols * BurstRenderer.Carriers > _outRe.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(symbols));
        }

        if (start < 0 || capture.Length - start < symbols * BurstRenderer.SymbolSamples)
        {
            throw new ArgumentException("The capture does not hold the symbols.", nameof(capture));
        }

        for (int k = 0; k < symbols; k++)
        {
            ReadOnlySpan<float> body = capture.Slice(start + (k * BurstRenderer.SymbolSamples) + BurstRenderer.CyclicPrefix, BurstRenderer.FftSize);
            for (int j = 0; j < BurstRenderer.FftSize; j++)
            {
                _re[j] = body[j];
                _im[j] = 0;
            }

            RealFft.Forward(_re, _im);
            Array.Copy(_re, BurstRenderer.FirstBin, _outRe, k * BurstRenderer.Carriers, BurstRenderer.Carriers);
            Array.Copy(_im, BurstRenderer.FirstBin, _outIm, k * BurstRenderer.Carriers, BurstRenderer.Carriers);
        }

        _symbols = symbols;
    }
}
