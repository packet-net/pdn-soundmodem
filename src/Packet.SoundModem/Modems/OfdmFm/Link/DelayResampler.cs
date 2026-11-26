namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// Takes a time-varying delay out of a stretch of a capture: the prototype's <c>lab.resample_delay</c>. Output sample
/// <c>i</c> is the capture read at <c>at + i + d(i)</c>, where <c>d</c> is the delay curve in samples, by a 48-tap
/// Hann-windowed sinc interpolation; the curve is given at knots (seconds from the burst's first sample, the lead-in's
/// <c>lead</c> samples before output sample <c>lead</c>) and is linear between them, the first value before the first knot
/// and the last value after the last. Reads before the capture's first sample and after its last take those samples.
/// </summary>
/// <remarks>
/// The prototype builds each kernel from a sinc and a window evaluated tap by tap. Here the sinc's numerator is
/// <c>sin(pi (k - f)) = -(-1)^k sin(pi f)</c> and the window's cosine is <c>cos(pi k / 24) cos(pi f / 24) + sin(pi k / 24) sin(pi f / 24)</c>,
/// the same numbers (identities, not approximations) with three trigonometric calls a sample instead of ninety-six; the
/// fixed cosines and sines of <c>pi k / 24</c> are computed once. Nothing is stored between calls, so one instance (or the
/// static methods) may be used from anywhere.
/// </remarks>
internal static class DelayResampler
{
    /// <summary>Taps of the interpolation.</summary>
    public const int Taps = 48;

    private const int Half = Taps / 2;
    private const double SampleRate = BurstRenderer.SampleRate;

    private static readonly double[] CosK = BuildK(false);
    private static readonly double[] SinK = BuildK(true);

    /// <summary>Resamples <paramref name="n"/> samples of <paramref name="capture"/> from <paramref name="at"/> (which may lie before the capture) along the delay curve, into <paramref name="output"/>.</summary>
    /// <param name="capture">The capture.</param>
    /// <param name="at">The index in the capture of output sample 0 before any delay.</param>
    /// <param name="n">Samples to produce.</param>
    /// <param name="knots">Times of the curve's knots in seconds from the burst's first sample, increasing; at least one.</param>
    /// <param name="delays">The delay at each knot in seconds (positive reads the capture later).</param>
    /// <param name="lead">Output samples before the burst's first sample.</param>
    /// <param name="output">At least <paramref name="n"/> samples.</param>
    public static void Resample(ReadOnlySpan<float> capture, int at, int n, ReadOnlySpan<double> knots, ReadOnlySpan<double> delays, int lead, Span<double> output)
    {
        Check(capture, n, knots, delays, output.Length);
        int segment = 0;
        for (int i = 0; i < n; i++)
        {
            output[i] = Sample(capture, at, i, knots, delays, lead, ref segment);
        }
    }

    /// <summary>As <see cref="Resample(ReadOnlySpan{float}, int, int, ReadOnlySpan{double}, ReadOnlySpan{double}, int, Span{double})"/>, each output rounded to a float.</summary>
    public static void Resample(ReadOnlySpan<float> capture, int at, int n, ReadOnlySpan<double> knots, ReadOnlySpan<double> delays, int lead, Span<float> output)
    {
        Check(capture, n, knots, delays, output.Length);
        int segment = 0;
        for (int i = 0; i < n; i++)
        {
            output[i] = (float)Sample(capture, at, i, knots, delays, lead, ref segment);
        }
    }

    /// <summary>
    /// <c>numpy.interp</c> of <paramref name="x"/> on <paramref name="xp"/> (increasing) and <paramref name="fp"/>: linear between
    /// the points, the end values outside. <paramref name="segment"/> remembers where the last call ended and must start at 0;
    /// successive calls must not decrease <paramref name="x"/>.
    /// </summary>
    internal static double Interpolate(double x, ReadOnlySpan<double> xp, ReadOnlySpan<double> fp, ref int segment)
    {
        int last = xp.Length - 1;
        if (x < xp[0])
        {
            return fp[0];
        }

        if (x > xp[last])
        {
            return fp[last];
        }

        while (segment < last && x >= xp[segment + 1])
        {
            segment++;
        }

        if (segment == last)
        {
            return fp[last];
        }

        double slope = (fp[segment + 1] - fp[segment]) / (xp[segment + 1] - xp[segment]);
        return (slope * (x - xp[segment])) + fp[segment];
    }

    private static void Check(ReadOnlySpan<float> capture, int n, ReadOnlySpan<double> knots, ReadOnlySpan<double> delays, int room)
    {
        if (capture.Length == 0)
        {
            throw new ArgumentException("The capture is empty.", nameof(capture));
        }

        if (knots.Length == 0 || delays.Length != knots.Length)
        {
            throw new ArgumentException("Need at least one knot, and a delay for each.", nameof(delays));
        }

        if (n < 0 || room < n)
        {
            throw new ArgumentException("The output cannot hold the samples.", nameof(n));
        }
    }

    private static double Sample(ReadOnlySpan<float> capture, int at, int i, ReadOnlySpan<double> knots, ReadOnlySpan<double> delays, int lead, ref int segment)
    {
        double tt = (i - lead) / SampleRate;
        double d = Interpolate(tt, knots, delays, ref segment) * SampleRate;
        double pos = (at + (double)i) + d;
        double floor = Math.Floor(pos);
        double frac = pos - floor;
        int baseIndex = (int)floor;
        double sinF = Math.Sin(Math.PI * frac);
        double cosW = Math.Cos(Math.PI * frac / Half);
        double sinW = Math.Sin(Math.PI * frac / Half);
        int first = baseIndex - Half + 1;
        bool inside = first >= 0 && first + Taps <= capture.Length;
        double sum = 0;
        for (int j = 0; j < Taps; j++)
        {
            int k = j - Half + 1;
            double x = k - frac;
            double sinc;
            if (x == 0)
            {
                sinc = 1.0;
            }
            else
            {
                double numerator = (k & 1) == 0 ? -sinF : sinF;
                sinc = numerator / (Math.PI * x);
            }

            double window = 0.5 + (0.5 * ((CosK[j] * cosW) + (SinK[j] * sinW)));
            double kernel = sinc * window;
            int index = first + j;
            if (!inside)
            {
                index = Math.Clamp(index, 0, capture.Length - 1);
            }

            sum += capture[index] * kernel;
        }

        return sum;
    }

    // cos and sin of pi k / 24 for the taps k = -23 .. 24 (index j = k + 23).
    private static double[] BuildK(bool sine)
    {
        var table = new double[Taps];
        for (int j = 0; j < Taps; j++)
        {
            int k = j - Half + 1;
            double a = Math.PI * k / Half;
            table[j] = sine ? Math.Sin(a) : Math.Cos(a);
        }

        return table;
    }
}
