namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The prototype's <c>_med</c>: the median of each value and the <c>half</c> either side of it, the window cut short at
/// the ends (so the first value is the median of half+1 points). An even number of points gives the mean of the two
/// middle ones, as numpy's median does. Owns its scratch; allocates nothing per call.
/// </summary>
internal sealed class RunningMedian
{
    private readonly int _half;
    private readonly double[] _window;

    /// <summary>Makes a running median with a window of 2*half+1.</summary>
    public RunningMedian(int half)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(half);
        _half = half;
        _window = new double[(2 * half) + 1];
    }

    /// <summary>Writes the running median of <paramref name="x"/> into <paramref name="y"/> (the same length; not overlapping).</summary>
    public void Apply(ReadOnlySpan<double> x, Span<double> y)
    {
        if (y.Length < x.Length)
        {
            throw new ArgumentException("The output must be as long as the input.", nameof(y));
        }

        int n = x.Length;
        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - _half);
            int hi = Math.Min(n, i + _half + 1);
            int m = hi - lo;
            for (int j = 0; j < m; j++)
            {
                double v = x[lo + j];
                int k = j;
                while (k > 0 && _window[k - 1] > v)
                {
                    _window[k] = _window[k - 1];
                    k--;
                }

                _window[k] = v;
            }

            y[i] = (m & 1) == 1 ? _window[m / 2] : (_window[(m / 2) - 1] + _window[m / 2]) / 2;
        }
    }
}
