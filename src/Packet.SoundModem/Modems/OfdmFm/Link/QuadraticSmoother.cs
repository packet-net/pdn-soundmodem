namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The prototype's <c>_smooth</c>: a local quadratic least-squares fit across neighbouring carriers (complex values, real
/// fit), window 2*half+1 carriers, the fit evaluated at the carrier itself. Near either end of the band the window is
/// cut short (carriers 0 to half+c, or c-half to the last), the quadratic is still fitted to what is there and still
/// evaluated at the carrier, not at the middle of the shorter window: at carrier 0 it is the fit's value at the start of
/// a half+1 point window, which is an extrapolation of the nearest points, not their mean.
/// </summary>
/// <remarks>
/// A fitted value is a fixed weighted sum of the window's values, so the weights are worked out once, in the constructor,
/// from the 3 by 3 normal equations of the window's carrier offsets, and a call is a multiply-add per window point. Not
/// thread-safe only in that callers must not share one output span; the object itself is read-only after construction.
/// </remarks>
internal sealed class QuadraticSmoother
{
    private readonly int _count;
    private readonly int _half;
    private readonly int _width;
    private readonly double[] _weights;

    /// <summary>Builds the weights for <paramref name="count"/> carriers and a window of 2*half+1.</summary>
    /// <param name="count">Carriers; at least half+1 (so that every window holds at least three points when half is 2 or more).</param>
    /// <param name="half">Carriers either side in a full window; at least 2.</param>
    public QuadraticSmoother(int count, int half)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(half, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, half + 1);
        _count = count;
        _half = half;
        _width = (2 * half) + 1;
        _weights = new double[count * _width];
        for (int c = 0; c < count; c++)
        {
            int lo = Math.Max(0, c - half);
            int hi = Math.Min(count, c + half + 1);
            double m0 = 0, m1 = 0, m2 = 0, m3 = 0, m4 = 0;
            for (int j = lo; j < hi; j++)
            {
                double x = j - c;
                double x2 = x * x;
                m0 += 1;
                m1 += x;
                m2 += x2;
                m3 += x2 * x;
                m4 += x2 * x2;
            }

            // Normal equations for the basis (x^2, x, 1): solve N v = (0, 0, 1); the weight of point x is v . (x^2, x, 1).
            double[] n = [m4, m3, m2, m3, m2, m1, m2, m1, m0];
            double[] rhs = [0, 0, 1];
            Solve3(n, rhs);
            for (int j = lo; j < hi; j++)
            {
                double x = j - c;
                _weights[(c * _width) + (j - c + half)] = (rhs[0] * x * x) + (rhs[1] * x) + rhs[2];
            }
        }
    }

    /// <summary>Carriers this smoother was built for.</summary>
    public int Count => _count;

    /// <summary>Smooths <paramref name="re"/> and <paramref name="im"/> (each <see cref="Count"/> long) into the outputs, which must not overlap the inputs.</summary>
    public void Smooth(ReadOnlySpan<double> re, ReadOnlySpan<double> im, Span<double> outRe, Span<double> outIm)
    {
        if (re.Length < _count || im.Length < _count || outRe.Length < _count || outIm.Length < _count)
        {
            throw new ArgumentException("Spans must hold one value per carrier.");
        }

        for (int c = 0; c < _count; c++)
        {
            int lo = Math.Max(0, c - _half);
            int hi = Math.Min(_count, c + _half + 1);
            int w = (c * _width) + (lo - c + _half);
            double sr = 0;
            double si = 0;
            for (int j = lo; j < hi; j++, w++)
            {
                sr += _weights[w] * re[j];
                si += _weights[w] * im[j];
            }

            outRe[c] = sr;
            outIm[c] = si;
        }
    }

    // Gaussian elimination with partial pivoting on a row-major 3 by 3; the right-hand side becomes the solution.
    private static void Solve3(double[] a, double[] b)
    {
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
            {
                if (Math.Abs(a[(r * 3) + col]) > Math.Abs(a[(pivot * 3) + col]))
                {
                    pivot = r;
                }
            }

            if (pivot != col)
            {
                for (int k = 0; k < 3; k++)
                {
                    (a[(col * 3) + k], a[(pivot * 3) + k]) = (a[(pivot * 3) + k], a[(col * 3) + k]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r = col + 1; r < 3; r++)
            {
                double f = a[(r * 3) + col] / a[(col * 3) + col];
                for (int k = col; k < 3; k++)
                {
                    a[(r * 3) + k] -= f * a[(col * 3) + k];
                }

                b[r] -= f * b[col];
            }
        }

        for (int r = 2; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < 3; k++)
            {
                s -= a[(r * 3) + k] * b[k];
            }

            b[r] = s / a[(r * 3) + r];
        }
    }
}
