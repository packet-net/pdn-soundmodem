namespace Packet.SoundModem.Modems.OfdmAb;

/// <summary>
/// Radix-2 complex FFT, plus the two real-signal wrappers an OFDM chain needs: a spectrum with
/// Hermitian symmetry becomes a real waveform, and a real waveform becomes half a spectrum.
/// </summary>
/// <remarks>
/// Self-contained on purpose. This is the one piece of DSP where a subtle convention mismatch
/// (scaling, bin ordering, which half is conjugated) produces a signal that looks plausible and
/// decodes to nothing, so it is worth having the whole thing in view rather than split across a
/// dependency's assumptions.
/// </remarks>
internal static class RealFft
{
    /// <summary>In-place forward transform.</summary>
    public static void Forward(double[] re, double[] im) => Transform(re, im, inverse: false);

    /// <summary>In-place inverse transform, 1/N scaled.</summary>
    public static void Inverse(double[] re, double[] im) => Transform(re, im, inverse: true);

    /// <summary>
    /// Builds a real time-domain symbol from the positive-frequency bins, by mirroring them
    /// conjugate-symmetrically into the negative half. DC and Nyquist are left empty: neither can
    /// carry a phase, so neither is ever an OFDM subcarrier.
    /// </summary>
    public static double[] ToTimeDomain(double[] binRe, double[] binIm, int fftSize)
    {
        var re = new double[fftSize];
        var im = new double[fftSize];
        int half = fftSize / 2;
        for (int k = 1; k < half; k++)
        {
            re[k] = binRe[k];
            im[k] = binIm[k];
            re[fftSize - k] = binRe[k];
            im[fftSize - k] = -binIm[k];
        }

        Inverse(re, im);
        return re;
    }

    /// <summary>Transforms a real symbol and returns its positive-frequency bins.</summary>
    public static (double[] Re, double[] Im) ToBins(ReadOnlySpan<double> symbol, int fftSize)
    {
        var re = new double[fftSize];
        var im = new double[fftSize];
        symbol[..fftSize].CopyTo(re);
        Forward(re, im);
        return (re, im);
    }

    private static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wRe = Math.Cos(angle);
            double wIm = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1;
                double curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k;
                    int b = a + (len / 2);
                    double vRe = (re[b] * curRe) - (im[b] * curIm);
                    double vIm = (re[b] * curIm) + (im[b] * curRe);
                    re[b] = re[a] - vRe;
                    im[b] = im[a] - vIm;
                    re[a] += vRe;
                    im[a] += vIm;
                    double nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        for (int i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] /= n;
        }
    }
}
