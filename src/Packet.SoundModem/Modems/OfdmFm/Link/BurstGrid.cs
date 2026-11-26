namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// What the transmitter and the receiver both build from a header and a payload, in one place so that they cannot differ:
/// the opening's grid (training symbols and the header's symbols, each carrier's value before the inverse transform), the
/// body a grid makes (each symbol an inverse transform behind its last 64 samples), and the level the body is scaled to.
/// </summary>
internal static class BurstGrid
{
    /// <summary>
    /// Writes the opening's grid: the training rows on the band's carriers, then the header's symbols, all times
    /// <paramref name="gain"/>. <paramref name="gridRe"/> and <paramref name="gridIm"/> take 333 values a symbol, the opening's
    /// symbols first; the carriers above the opening's band are zero. <paramref name="loading"/> (333) and
    /// <paramref name="headerCoded"/> (666) are scratch.
    /// </summary>
    public static void BuildOpening(FormatOpening opening, ReadOnlySpan<byte> headerBits, double gain, Span<double> gridRe, Span<double> gridIm, Span<byte> loading, Span<byte> headerCoded)
    {
        int training = HeaderReader.TrainingSymbolCount(opening);
        int headerSymbols = HeaderReader.HeaderSymbolCount(opening);
        int band = HeaderReader.OpeningCarriers(opening);
        for (int r = 0; r < training; r++)
        {
            Span<double> re = gridRe.Slice(r * BurstRenderer.Carriers, BurstRenderer.Carriers);
            Span<double> im = gridIm.Slice(r * BurstRenderer.Carriers, BurstRenderer.Carriers);
            TrainingSymbols.Points(r, re, im);
            for (int c = 0; c < BurstRenderer.Carriers; c++)
            {
                if (c >= band)
                {
                    re[c] = 0;
                    im[c] = 0;
                }
                else
                {
                    re[c] *= gain;
                    im[c] *= gain;
                }
            }
        }

        HeaderCode.Encode(opening, headerBits, headerCoded.Slice(0, headerSymbols * 2 * band));
        loading.Clear();
        loading.Slice(0, band).Fill(2);
        int perSymbol = 2 * band;
        for (int h = 0; h < headerSymbols; h++)
        {
            int at = (training + h) * BurstRenderer.Carriers;
            Span<double> re = gridRe.Slice(at, BurstRenderer.Carriers);
            Span<double> im = gridIm.Slice(at, BurstRenderer.Carriers);
            Constellation.MapSymbol(headerCoded.Slice(h * perSymbol, perSymbol), loading, re, im);
            for (int c = 0; c < band; c++)
            {
                re[c] *= gain;
                im[c] *= gain;
            }
        }
    }

    /// <summary>
    /// The grid's symbols as the body: each an inverse transform, behind its last 64 samples. <paramref name="body"/> must
    /// hold <paramref name="symbols"/> times 2112 samples; the first that many are written and returned.
    /// </summary>
    public static Span<double> Transform(ReadOnlySpan<double> gridRe, ReadOnlySpan<double> gridIm, int symbols, double[] fftRe, double[] fftIm, double[] body)
    {
        for (int s = 0; s < symbols; s++)
        {
            Array.Clear(fftRe);
            Array.Clear(fftIm);
            int at = s * BurstRenderer.Carriers;
            for (int c = 0; c < BurstRenderer.Carriers; c++)
            {
                int k = BurstRenderer.FirstBin + c;
                fftRe[k] = gridRe[at + c];
                fftIm[k] = gridIm[at + c];
                fftRe[BurstRenderer.FftSize - k] = gridRe[at + c];
                fftIm[BurstRenderer.FftSize - k] = -gridIm[at + c];
            }

            RealFft.Inverse(fftRe, fftIm);
            int o = s * BurstRenderer.SymbolSamples;
            fftRe.AsSpan(BurstRenderer.FftSize - BurstRenderer.CyclicPrefix, BurstRenderer.CyclicPrefix).CopyTo(body.AsSpan(o, BurstRenderer.CyclicPrefix));
            fftRe.CopyTo(body.AsSpan(o + BurstRenderer.CyclicPrefix, BurstRenderer.FftSize));
        }

        return body.AsSpan(0, symbols * BurstRenderer.SymbolSamples);
    }

    /// <summary>The factor a body is scaled by to reach the drive's level: the full-drive level times the drive, over the body's root mean square.</summary>
    public static double Level(ReadOnlySpan<double> body, double driveDb)
    {
        double rms = Math.Sqrt(SumOfSquares(body) / body.Length);
        return BurstRenderer.RmsFull * Math.Pow(10, driveDb / 20) / rms;
    }

    public static double SumOfSquares(ReadOnlySpan<double> x)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return sum;
    }
}
