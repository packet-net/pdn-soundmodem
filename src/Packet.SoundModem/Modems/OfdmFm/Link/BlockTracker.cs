namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// Follows the delay of a capture of a burst against the burst's samples, a few milliseconds at a time: the prototype's
/// <c>lab.track</c> as the station calls it (<c>at</c> 0, blocks of 1024 samples every 256, 200 to 8050 hertz, three
/// iterations), returning the centre of each block and its delay relative to the burst's mean delay.
/// </summary>
/// <remarks>
/// <para>The blocks are those whose reference energy is more than half the median block's (the lead-in's silence goes). Each
/// block of the reference and of the capture is windowed (Hann) and transformed; a static channel is fitted from the blocks
/// around the middle of the burst, each block's delay is found by a grid search of 750 delays four microseconds apart
/// (minus 1500 to plus 1496), and then three rounds of: the channel over all blocks, each block's residual phase fitted
/// against frequency through the origin, and each block's gain. The delays are returned less their mean.</para>
/// <para>One instance per receiver; not thread-safe. The buffers are for the longest burst; a call allocates nothing.</para>
/// </remarks>
internal sealed class BlockTracker
{
    /// <summary>Samples in a block.</summary>
    public const int Block = 1024;

    /// <summary>Samples between the starts of blocks.</summary>
    public const int Hop = 256;

    /// <summary>Delays tried by the grid search.</summary>
    public const int GridSteps = 750;

    private const double SampleRate = BurstRenderer.SampleRate;
    private const double LowHz = 200.0;
    private const double HighHz = 8050.0;
    private const int Iterations = 3;

    private static readonly double[] Window = BuildWindow();
    private static readonly int FirstBin;
    private static readonly int Bins;
    private static readonly double[] Hz;
    private static readonly double[] SteerRe;
    private static readonly double[] SteerIm;
    private static readonly double[] Grid;

    private readonly int _maxBlocks;
    private readonly int[] _starts;
    private readonly double[] _energy;
    private readonly double[] _sorted;
    private readonly double[] _xRe;
    private readonly double[] _xIm;
    private readonly double[] _yRe;
    private readonly double[] _yIm;
    private readonly double[] _tau;
    private readonly double[] _gain;
    private readonly double[] _fftRe = new double[Block];
    private readonly double[] _fftIm = new double[Block];
    private readonly double[] _px;
    private readonly double[] _hRe;
    private readonly double[] _hIm;
    private readonly double[] _cRe;
    private readonly double[] _cIm;
    private readonly double[] _sumRe;
    private readonly double[] _sumIm;

    static BlockTracker()
    {
        double spacing = 1.0 / (Block * (1.0 / SampleRate));
        int first = -1;
        int count = 0;
        for (int k = 0; k <= Block / 2; k++)
        {
            double f = k * spacing;
            if (f >= LowHz && f <= HighHz)
            {
                if (first < 0)
                {
                    first = k;
                }

                count++;
            }
        }

        FirstBin = first;
        Bins = count;
        Hz = new double[Bins];
        for (int b = 0; b < Bins; b++)
        {
            Hz[b] = (FirstBin + b) * spacing;
        }

        // numpy.arange(-1500e-6, 1500e-6, 4e-6): start plus i times the difference of the first two values.
        double start = -1500e-6;
        double delta = (start + 4e-6) - start;
        Grid = new double[GridSteps];
        SteerRe = new double[GridSteps * Bins];
        SteerIm = new double[GridSteps * Bins];
        for (int g = 0; g < GridSteps; g++)
        {
            Grid[g] = start + (g * delta);
            for (int b = 0; b < Bins; b++)
            {
                double angle = 2 * Math.PI * Hz[b] * Grid[g];
                SteerRe[(g * Bins) + b] = Math.Cos(angle);
                SteerIm[(g * Bins) + b] = Math.Sin(angle);
            }
        }
    }

    /// <summary>Makes a tracker with room for a burst of <paramref name="maxSamples"/> samples (lead-in included).</summary>
    public BlockTracker(int maxSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, Block);
        _maxBlocks = ((maxSamples - Block) / Hop) + 1;
        _starts = new int[_maxBlocks];
        _energy = new double[_maxBlocks];
        _sorted = new double[_maxBlocks];
        _xRe = new double[_maxBlocks * Bins];
        _xIm = new double[_maxBlocks * Bins];
        _yRe = new double[_maxBlocks * Bins];
        _yIm = new double[_maxBlocks * Bins];
        _tau = new double[_maxBlocks];
        _gain = new double[_maxBlocks];
        _px = new double[Bins];
        _hRe = new double[Bins];
        _hIm = new double[Bins];
        _cRe = new double[Bins];
        _cIm = new double[Bins];
        _sumRe = new double[Bins];
        _sumIm = new double[Bins];
    }

    /// <summary>Most blocks a call can return.</summary>
    public int MaxBlocks => _maxBlocks;

    /// <summary>
    /// Measures <paramref name="capture"/> against <paramref name="reference"/>, both from the same sample (the reference's
    /// leading zeros are the lead-in). Writes each kept block's centre time (seconds from the reference's first non-zero sample)
    /// and delay (seconds, positive later) and returns how many. The capture must be at least as long as the reference.
    /// </summary>
    public int Track(ReadOnlySpan<float> capture, ReadOnlySpan<double> reference, Span<double> times, Span<double> delays)
    {
        int end = reference.Length;
        if (capture.Length < end)
        {
            throw new ArgumentException("The capture is shorter than the reference.", nameof(capture));
        }

        if (end >= Block && ((end - Block) / Hop) + 1 > _maxBlocks)
        {
            throw new ArgumentException("The reference is longer than the longest burst.", nameof(reference));
        }

        int lead = 0;
        while (lead < end && reference[lead] == 0)
        {
            lead++;
        }

        if (lead == end)
        {
            lead = 0;
        }

        int total = 0;
        for (int s = lead; s <= end - Block; s += Hop)
        {
            double e = 0;
            for (int j = 0; j < Block; j++)
            {
                double v = reference[s + j];
                e += v * v;
            }

            _starts[total] = s;
            _energy[total] = e;
            _sorted[total] = e;
            total++;
        }

        if (total == 0)
        {
            return 0;
        }

        _sorted.AsSpan(0, total).Sort();
        double median = (total & 1) == 1 ? _sorted[total / 2] : (_sorted[(total / 2) - 1] + _sorted[total / 2]) / 2;
        double floor = 0.5 * median;
        int m = 0;
        for (int i = 0; i < total; i++)
        {
            if (_energy[i] > floor)
            {
                _starts[m++] = _starts[i];
            }
        }

        if (m == 0)
        {
            return 0;
        }

        if (times.Length < m || delays.Length < m)
        {
            throw new ArgumentException("Need room for every block.", nameof(times));
        }

        for (int blk = 0; blk < m; blk++)
        {
            int s = _starts[blk];
            for (int j = 0; j < Block; j++)
            {
                _fftRe[j] = Window[j] * reference[s + j];
                _fftIm[j] = 0;
            }

            RealFft.Forward(_fftRe, _fftIm);
            Array.Copy(_fftRe, FirstBin, _xRe, blk * Bins, Bins);
            Array.Copy(_fftIm, FirstBin, _xIm, blk * Bins, Bins);
            for (int j = 0; j < Block; j++)
            {
                _fftRe[j] = Window[j] * capture[s + j];
                _fftIm[j] = 0;
            }

            RealFft.Forward(_fftRe, _fftIm);
            Array.Copy(_fftRe, FirstBin, _yRe, blk * Bins, Bins);
            Array.Copy(_fftIm, FirstBin, _yIm, blk * Bins, Bins);
        }

        Array.Clear(_px);
        for (int blk = 0; blk < m; blk++)
        {
            int o = blk * Bins;
            for (int b = 0; b < Bins; b++)
            {
                _px[b] += (_xRe[o + b] * _xRe[o + b]) + (_xIm[o + b] * _xIm[o + b]);
            }
        }

        _gain.AsSpan(0, m).Fill(1.0);

        // The bootstrap channel, from the blocks around the middle of the burst (its first symbols occupy only the bottom of the band).
        int mid = m / 2;
        int span = Math.Max(4, (int)(0.04 * SampleRate / Hop));
        int from = Math.Max(0, mid - (span / 2));
        int to = Math.Min(m, mid + (span / 2));
        Array.Clear(_sumRe);
        Array.Clear(_sumIm);
        Array.Clear(_cRe);
        for (int blk = from; blk < to; blk++)
        {
            int o = blk * Bins;
            for (int b = 0; b < Bins; b++)
            {
                // Y * conj(X)
                _sumRe[b] += (_yRe[o + b] * _xRe[o + b]) + (_yIm[o + b] * _xIm[o + b]);
                _sumIm[b] += (_yIm[o + b] * _xRe[o + b]) - (_yRe[o + b] * _xIm[o + b]);
                _cRe[b] += (_xRe[o + b] * _xRe[o + b]) + (_xIm[o + b] * _xIm[o + b]);
            }
        }

        for (int b = 0; b < Bins; b++)
        {
            double d = Math.Max(_cRe[b], 1e-30);
            _hRe[b] = _sumRe[b] / d;
            _hIm[b] = _sumIm[b] / d;
        }

        // Each block's delay by a grid search of the correlation of what is left after the channel with a delay's steering.
        for (int blk = 0; blk < m; blk++)
        {
            int o = blk * Bins;
            for (int b = 0; b < Bins; b++)
            {
                // C = Y * conj(H * X)
                double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                _cRe[b] = (_yRe[o + b] * hxRe) + (_yIm[o + b] * hxIm);
                _cIm[b] = (_yIm[o + b] * hxRe) - (_yRe[o + b] * hxIm);
            }

            double best = -1;
            int bestG = 0;
            for (int g = 0; g < GridSteps; g++)
            {
                int so = g * Bins;
                double re = 0;
                double im = 0;
                for (int b = 0; b < Bins; b++)
                {
                    re += (_cRe[b] * SteerRe[so + b]) - (_cIm[b] * SteerIm[so + b]);
                    im += (_cRe[b] * SteerIm[so + b]) + (_cIm[b] * SteerRe[so + b]);
                }

                double a = (re * re) + (im * im);
                if (a > best)
                {
                    best = a;
                    bestG = g;
                }
            }

            _tau[blk] = Grid[bestG];
        }

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            // H over all blocks, with each block's delay taken out and its gain divided out.
            Array.Clear(_sumRe);
            Array.Clear(_sumIm);
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double tau = _tau[blk];
                double gain = _gain[blk];
                for (int b = 0; b < Bins; b++)
                {
                    double angle = 2 * Math.PI * Hz[b] * tau;
                    double rr = Math.Cos(angle);
                    double ri = Math.Sin(angle);
                    double pr = (_yRe[o + b] * rr) - (_yIm[o + b] * ri);
                    double pi = (_yRe[o + b] * ri) + (_yIm[o + b] * rr);
                    _sumRe[b] += ((pr * _xRe[o + b]) + (pi * _xIm[o + b])) / gain;
                    _sumIm[b] += ((pi * _xRe[o + b]) - (pr * _xIm[o + b])) / gain;
                }
            }

            for (int b = 0; b < Bins; b++)
            {
                double d = Math.Max(_px[b], 1e-30);
                _hRe[b] = _sumRe[b] / d;
                _hIm[b] = _sumIm[b] / d;
            }

            // Each block's residual phase fitted against frequency through the origin.
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double tau = _tau[blk];
                double num = 0;
                double den = 0;
                for (int b = 0; b < Bins; b++)
                {
                    double angle = 2 * Math.PI * Hz[b] * tau;
                    double rr = Math.Cos(angle);
                    double ri = Math.Sin(angle);
                    double pr = (_yRe[o + b] * rr) - (_yIm[o + b] * ri);
                    double pi = (_yRe[o + b] * ri) + (_yIm[o + b] * rr);
                    double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                    double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                    double cr = (pr * hxRe) + (pi * hxIm);
                    double ci = (pi * hxRe) - (pr * hxIm);
                    double w = Math.Sqrt((cr * cr) + (ci * ci));
                    double ang = Math.Atan2(ci, cr);
                    num += w * ang * Hz[b];
                    den += w * Hz[b] * Hz[b];
                }

                _tau[blk] = tau - (num / den / (2 * Math.PI));
            }

            // Each block's gain against the channel and its new delay.
            for (int blk = 0; blk < m; blk++)
            {
                int o = blk * Bins;
                double tau = _tau[blk];
                double cross = 0;
                double power = 0;
                for (int b = 0; b < Bins; b++)
                {
                    double angle = -2 * Math.PI * Hz[b] * tau;
                    double rr = Math.Cos(angle);
                    double ri = Math.Sin(angle);
                    double hxRe = (_hRe[b] * _xRe[o + b]) - (_hIm[b] * _xIm[o + b]);
                    double hxIm = (_hRe[b] * _xIm[o + b]) + (_hIm[b] * _xRe[o + b]);
                    double refRe = (hxRe * rr) - (hxIm * ri);
                    double refIm = (hxRe * ri) + (hxIm * rr);
                    cross += (_yRe[o + b] * refRe) + (_yIm[o + b] * refIm);
                    power += (refRe * refRe) + (refIm * refIm);
                }

                _gain[blk] = cross / Math.Max(power, 1e-30);
            }
        }

        double sum = 0;
        for (int blk = 0; blk < m; blk++)
        {
            sum += _tau[blk];
        }

        double mean = sum / m;
        for (int blk = 0; blk < m; blk++)
        {
            times[blk] = (_starts[blk] + (Block / 2.0) - lead) / SampleRate;
            delays[blk] = _tau[blk] - mean;
        }

        return m;
    }

    // numpy.hanning(1024).
    private static double[] BuildWindow()
    {
        var w = new double[Block];
        for (int i = 0; i < Block; i++)
        {
            double n = (1 - Block) + (2 * i);
            w[i] = 0.5 + (0.5 * Math.Cos(Math.PI * n / (Block - 1)));
        }

        return w;
    }
}
