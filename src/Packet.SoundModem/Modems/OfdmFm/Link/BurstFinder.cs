namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>A place a burst may start: the capture sample where its first training sample sits, and how well the capture matches the training there.</summary>
/// <param name="Index">Sample index of the burst's first training sample (the lead-in is not part of it).</param>
/// <param name="Peak">The normalised correlation there, 0 to 1.</param>
public readonly record struct BurstCandidate(int Index, double Peak);

/// <summary>
/// Finds the bursts of one opening in a whole capture, offline: normalised correlation of the capture with the opening's
/// training waveform, then every peak over a threshold that is the largest within a symbol either side. Pinned to the
/// prototype (<c>modem.find_bursts</c> and <c>modem.training_wave</c>).
/// </summary>
/// <remarks>
/// <para>The correlation of a weak burst is small in absolute terms, so unless a threshold is given a peak counts when it
/// stands <see cref="DefaultFactor"/> times above the correlation's own median over the capture (what noise alone makes),
/// and at least <see cref="DefaultLeast"/>. The caller's header check sorts the real bursts from the rest.</para>
/// <para>The reference is the opening's training symbols (1, 3 or 6, as the station uses them: the opening's own count,
/// not the prototype function's bare default of 2) on the opening's carriers, at unit scale, each with its cyclic prefix.</para>
/// <para>This is the offline form and allocates freely. The correlation, the energy under the reference window and the
/// peak rule are separate internal steps so that a streaming form can use them.</para>
/// </remarks>
public static class BurstFinder
{
    /// <summary>How many times the median correlation a peak must reach when no threshold is given.</summary>
    public const double DefaultFactor = 7.0;

    /// <summary>The lowest threshold used when none is given.</summary>
    public const double DefaultLeast = 0.1;

    private const int Fft = BurstRenderer.FftSize;
    private const int Cp = BurstRenderer.CyclicPrefix;
    private const int Sym = BurstRenderer.SymbolSamples;

    /// <summary>Training symbols the opening starts with: the count the station searches with.</summary>
    public static int TrainingSymbolCount(FormatOpening opening) => opening switch
    {
        FormatOpening.Full => 1,
        FormatOpening.UpTo3000Hz => 3,
        FormatOpening.UpTo1500Hz => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(opening)),
    };

    /// <summary>Samples of the opening's reference (training symbols with their prefixes).</summary>
    public static int ReferenceLength(FormatOpening opening) => TrainingSymbolCount(opening) * Sym;

    /// <summary>
    /// The burst starts in a capture, in order of index, for one opening.
    /// </summary>
    /// <param name="capture">The whole capture, 48 kHz.</param>
    /// <param name="opening">Which opening to search for.</param>
    /// <param name="threshold">Set to the threshold used.</param>
    /// <param name="fixedThreshold">A fixed threshold, or null for the median rule.</param>
    /// <param name="factor">For the median rule: multiples of the median correlation.</param>
    /// <param name="least">For the median rule: the lowest threshold.</param>
    public static List<BurstCandidate> Find(
        ReadOnlySpan<float> capture,
        FormatOpening opening,
        out double threshold,
        double? fixedThreshold = null,
        double factor = DefaultFactor,
        double least = DefaultLeast)
    {
        double[] reference = Reference(opening);
        var result = new List<BurstCandidate>();
        threshold = fixedThreshold ?? 0;
        if (capture.Length < reference.Length)
        {
            return result;
        }

        var x = new double[capture.Length];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = capture[i];
        }

        double[] correlation = Correlate(x, reference);
        double[] energy = Energy(x, reference.Length, correlation.Length);
        double referenceEnergy = SumOfSquares(reference);
        double[] rho = new double[correlation.Length];
        for (int j = 0; j < rho.Length; j++)
        {
            rho[j] = Math.Abs(correlation[j]) / Math.Sqrt(Math.Max(energy[j], 1e-12) * referenceEnergy);
        }

        threshold = fixedThreshold ?? Math.Max(least, factor * MedianEvery16th(rho));
        foreach (int j in Peaks(rho, threshold, Sym))
        {
            result.Add(new BurstCandidate(j, rho[j]));
        }

        return result;
    }

    /// <summary>The burst starts in a capture, with the prototype's defaults.</summary>
    public static List<BurstCandidate> Find(ReadOnlySpan<float> capture, FormatOpening opening) => Find(capture, opening, out _);

    /// <summary>
    /// The opening's training waveform as the transmitter sends it, at unit scale: the training rows on the opening's band,
    /// each symbol an inverse transform behind its last 64 samples.
    /// </summary>
    internal static double[] Reference(FormatOpening opening)
    {
        int symbols = TrainingSymbolCount(opening);
        double top = opening switch
        {
            FormatOpening.Full => double.PositiveInfinity,
            FormatOpening.UpTo3000Hz => 3000.0,
            _ => 1500.0,
        };

        var wave = new double[symbols * Sym];
        var re = new double[Fft];
        var im = new double[Fft];
        var rowRe = new double[TrainingSymbols.Carriers];
        var rowIm = new double[TrainingSymbols.Carriers];
        for (int s = 0; s < symbols; s++)
        {
            Array.Clear(re);
            Array.Clear(im);
            TrainingSymbols.Points(s, rowRe, rowIm);
            for (int c = 0; c < TrainingSymbols.Carriers; c++)
            {
                int k = BurstRenderer.FirstBin + c;
                if (((double)k * BurstRenderer.SampleRate / Fft) > top)
                {
                    continue;
                }

                re[k] = rowRe[c];
                im[k] = rowIm[c];
                re[Fft - k] = rowRe[c];
                im[Fft - k] = -rowIm[c];
            }

            RealFft.Inverse(re, im);
            re.AsSpan(Fft - Cp, Cp).CopyTo(wave.AsSpan(s * Sym, Cp));
            re.CopyTo(wave.AsSpan((s * Sym) + Cp, Fft));
        }

        return wave;
    }

    /// <summary>
    /// The raw correlation of a capture with a reference, by FFT: element j is the sum over i of
    /// <c>capture[j + i] * reference[i]</c>, for every j where the reference fits.
    /// </summary>
    internal static double[] Correlate(double[] capture, double[] reference)
    {
        int n = 1;
        while (n < capture.Length + reference.Length)
        {
            n <<= 1;
        }

        // One complex transform for both real signals: the capture in the real part, the reference in the imaginary part.
        var re = new double[n];
        var im = new double[n];
        capture.CopyTo(re, 0);
        reference.CopyTo(im, 0);
        RealFft.Forward(re, im);

        // Capture spectrum C[k] = (Z[k] + conj Z[n-k]) / 2, reference spectrum R[k] = (Z[k] - conj Z[n-k]) / 2i; product C conj(R).
        var pr = new double[n];
        var pi = new double[n];
        for (int k = 0; k < n; k++)
        {
            int m = (n - k) & (n - 1);
            double cr = 0.5 * (re[k] + re[m]);
            double ci = 0.5 * (im[k] - im[m]);
            double rr = 0.5 * (im[k] + im[m]);
            double ri = -0.5 * (re[k] - re[m]);
            pr[k] = (cr * rr) + (ci * ri);
            pi[k] = (ci * rr) - (cr * ri);
        }

        RealFft.Inverse(pr, pi);
        int length = capture.Length - reference.Length + 1;
        var c = new double[length];
        Array.Copy(pr, c, length);
        return c;
    }

    /// <summary>The energy of the capture under the reference window at each start: running sum of squares.</summary>
    internal static double[] Energy(double[] capture, int referenceLength, int count)
    {
        var cumulative = new double[capture.Length + 1];
        double sum = 0;
        for (int i = 0; i < capture.Length; i++)
        {
            sum += capture[i] * capture[i];
            cumulative[i + 1] = sum;
        }

        var energy = new double[count];
        for (int j = 0; j < count; j++)
        {
            energy[j] = cumulative[referenceLength + j] - cumulative[j];
        }

        return energy;
    }

    /// <summary>
    /// The peaks of a series over a height that are the largest within <paramref name="distance"/> samples either side.
    /// A peak is a sample strictly higher than both neighbours (the ends are never peaks); a flat top counts once, at its
    /// middle (rounded down), if it is strictly lower on both sides. Peaks lower than <paramref name="height"/> are dropped;
    /// then, working from the highest peak down, any other peak closer than <paramref name="distance"/> is removed. Equal
    /// heights are taken in index order.
    /// </summary>
    internal static List<int> Peaks(ReadOnlySpan<double> x, double height, int distance)
    {
        var peaks = new List<int>();
        int i = 1;
        int last = x.Length - 1;
        while (i < last)
        {
            if (x[i - 1] < x[i])
            {
                int ahead = i + 1;
                while (ahead < last && x[ahead] == x[i])
                {
                    ahead++;
                }

                if (x[ahead] < x[i])
                {
                    int left = i;
                    int right = ahead - 1;
                    int mid = (left + right) / 2;
                    if (x[mid] >= height)
                    {
                        peaks.Add(mid);
                    }

                    i = ahead;
                }
            }

            i++;
        }

        int count = peaks.Count;
        if (count < 2 || distance <= 1)
        {
            return peaks;
        }

        var order = new int[count];
        for (int p = 0; p < count; p++)
        {
            order[p] = p;
        }

        // Highest first; equal heights in index order.
        var heights = new double[count];
        for (int p = 0; p < count; p++)
        {
            heights[p] = x[peaks[p]];
        }

        Array.Sort(order, (a, b) =>
        {
            int c = heights[b].CompareTo(heights[a]);
            return c != 0 ? c : a.CompareTo(b);
        });

        var keep = new bool[count];
        Array.Fill(keep, true);
        foreach (int j in order)
        {
            if (!keep[j])
            {
                continue;
            }

            for (int k = j - 1; k >= 0 && peaks[j] - peaks[k] < distance; k--)
            {
                keep[k] = false;
            }

            for (int k = j + 1; k < count && peaks[k] - peaks[j] < distance; k++)
            {
                keep[k] = false;
            }
        }

        var kept = new List<int>(count);
        for (int p = 0; p < count; p++)
        {
            if (keep[p])
            {
                kept.Add(peaks[p]);
            }
        }

        return kept;
    }

    private static double MedianEvery16th(double[] x)
    {
        int n = (x.Length + 15) / 16;
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = x[i * 16];
        }

        Array.Sort(s);
        return (n & 1) == 1 ? s[n / 2] : (s[(n / 2) - 1] + s[n / 2]) / 2;
    }

    private static double SumOfSquares(double[] x)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return sum;
    }
}
