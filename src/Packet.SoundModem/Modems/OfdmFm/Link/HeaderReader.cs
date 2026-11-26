namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// Reads a burst's header from its opening, knowing nothing about the burst but where it starts and which opening it has:
/// the receive side of the header, pinned to the prototype's <c>modem.first_two</c> and <c>modem.read_header</c> (called
/// with the agreed header, <c>hdr=3</c>).
/// </summary>
/// <remarks>
/// <para>The opening's training symbols give the channel on the opening's carriers (the carriers up to the opening's top
/// frequency), smoothed across 11 carriers; their residual gives the noise on each carrier, a running median across 25,
/// floored, and scaled by 1.5 for one training symbol or n/(n-0.5) for n. Each header symbol is then read against that
/// channel: a ladder of 97 delays from -12 to +12 microseconds in steps of a quarter of a microsecond, on top of the delay
/// carried from the previous header symbol, is searched for the one that leaves the carriers nearest to QPSK points
/// (weighted by signal to noise), two refinement steps follow from the phase slope across the carriers, and the
/// accumulated delay is carried on. The soft values go to the header code, and the 51 bits it gives to the field reader
/// and its check.</para>
/// <para>One instance per receiver; not thread-safe. Every buffer is allocated by the constructor, so a read allocates
/// nothing once the header code's plans for the opening are cached (the first read of an opening builds them). The state of
/// the last read is kept for later stages and tests: the accessors say what they hold and when.</para>
/// </remarks>
public sealed class HeaderReader
{
    /// <summary>Delays tried for each header symbol: -12 to +12 microseconds in quarter-microsecond steps.</summary>
    public const int LadderSteps = 97;

    private const int SmoothHalf = 5;
    private const int MedianHalf = 12;
    private const double VarianceFloor = 1e-30;
    private const int MaxOpeningSymbols = 9;
    private const int MaxHeaderSymbols = 3;
    private const int MaxCodedBits = 666;
    private const double Sqrt2 = 1.4142135623730951;

    private static readonly double[] Ladder = BuildLadder();
    private static readonly OpeningShape[] Shapes = BuildShapes();

    private readonly OpeningTransform _transform = new(MaxOpeningSymbols);
    private readonly QuadraticSmoother[] _smoothers = new QuadraticSmoother[3];
    private readonly RunningMedian _median = new(MedianHalf);
    private readonly double[] _rawRe = new double[BurstRenderer.Carriers];
    private readonly double[] _rawIm = new double[BurstRenderer.Carriers];
    private readonly double[] _hRe = new double[BurstRenderer.Carriers];
    private readonly double[] _hIm = new double[BurstRenderer.Carriers];
    private readonly double[] _h2 = new double[BurstRenderer.Carriers];
    private readonly double[] _resid = new double[BurstRenderer.Carriers];
    private readonly double[] _var = new double[BurstRenderer.Carriers];
    private readonly double[] _weight = new double[BurstRenderer.Carriers];
    private readonly double[] _twoPiF = new double[BurstRenderer.Carriers];
    private readonly double[] _freq = new double[BurstRenderer.Carriers];
    private readonly double[] _yRe = new double[BurstRenderer.Carriers];
    private readonly double[] _yIm = new double[BurstRenderer.Carriers];
    private readonly double[] _kRe = new double[BurstRenderer.Carriers];
    private readonly double[] _kIm = new double[BurstRenderer.Carriers];
    private readonly float[] _soft = new float[MaxCodedBits];
    private readonly byte[] _bits = new byte[BurstHeaderCodec.HeaderBits];
    private readonly double[] _delays = new double[MaxHeaderSymbols];
    private readonly int[] _ladderIndex = new int[MaxHeaderSymbols];

    private FormatOpening _opening;
    private bool _valid;
    private bool _converged;

    /// <summary>Makes a reader with every buffer it needs.</summary>
    public HeaderReader()
    {
        for (int i = 0; i < Shapes.Length; i++)
        {
            _smoothers[i] = new QuadraticSmoother(Shapes[i].Carriers, SmoothHalf);
        }
    }

    /// <summary>
    /// The samples an opening takes, counted from the burst's first training sample (the one the start index of
    /// <see cref="TryRead"/> names): training symbols and header symbols, 2112 samples each.
    /// </summary>
    public static int OpeningSamples(FormatOpening opening)
    {
        OpeningShape s = ShapeOf(opening);
        return (s.Training + s.HeaderSymbols) * BurstRenderer.SymbolSamples;
    }

    /// <summary>The carriers an opening uses, from the lowest: 333, 120 or 56.</summary>
    public static int OpeningCarriers(FormatOpening opening) => ShapeOf(opening).Carriers;

    /// <summary>Training symbols an opening starts with: 1, 3 or 6.</summary>
    public static int TrainingSymbolCount(FormatOpening opening) => ShapeOf(opening).Training;

    /// <summary>Header symbols an opening has: 1, 2 or 3.</summary>
    public static int HeaderSymbolCount(FormatOpening opening) => ShapeOf(opening).HeaderSymbols;

    /// <summary>
    /// Reads the header of the burst whose first training sample is <paramref name="start"/> in the capture. False when the
    /// capture does not hold the opening from there (nothing is computed then), when the header code does not converge, or
    /// when the header's check fails; true with the fields otherwise, including a header of a version this code does not
    /// know (as <see cref="BurstHeaderCodec.TryRead"/> reports it). The intermediates stay readable either way once the
    /// opening was long enough.
    /// </summary>
    public bool TryRead(ReadOnlySpan<float> capture, int start, FormatOpening opening, out BurstHeader header)
    {
        header = default;
        _valid = false;
        _converged = false;
        OpeningShape s = ShapeOf(opening);
        int symbols = s.Training + s.HeaderSymbols;
        if (start < 0 || capture.Length - start < symbols * BurstRenderer.SymbolSamples)
        {
            return false;
        }

        _opening = opening;
        _transform.Transform(capture, start, symbols);
        EstimateChannel(s);
        int coded = 0;
        double tau = 0;
        for (int k = 0; k < s.HeaderSymbols; k++)
        {
            tau = ReadSymbol(s, s.Training + k, tau, coded, out int pick);
            _delays[k] = tau;
            _ladderIndex[k] = pick;
            coded += 2 * s.Carriers;
        }

        _valid = true;
        _converged = HeaderCode.TryDecode(opening, _soft.AsSpan(0, coded), _bits);
        return _converged && BurstHeaderCodec.TryRead(_bits, out header);
    }

    /// <summary>The opening of the last read; meaningful when <see cref="HasResult"/>.</summary>
    public FormatOpening Opening => _opening;

    /// <summary>Whether the last call had enough samples to compute the intermediates below.</summary>
    public bool HasResult => _valid;

    /// <summary>Whether the header code converged in the last read.</summary>
    public bool Converged => _converged;

    /// <summary>The 51 bits the header code gave in the last read (written whether or not it converged); empty when there is no result.</summary>
    public ReadOnlySpan<byte> Bits => _valid ? _bits : default;

    /// <summary>The opening's symbols' carriers, real parts: training symbols then header symbols, 333 carriers each (bins 9 to 341).</summary>
    public ReadOnlySpan<double> CarriersReal => _valid ? _transform.Real : default;

    /// <summary>The opening's symbols' carriers, imaginary parts, laid out as <see cref="CarriersReal"/>.</summary>
    public ReadOnlySpan<double> CarriersImaginary => _valid ? _transform.Imaginary : default;

    /// <summary>The channel estimate on the opening's carriers, real parts.</summary>
    public ReadOnlySpan<double> ChannelReal => _valid ? _hRe.AsSpan(0, ShapeOf(_opening).Carriers) : default;

    /// <summary>The channel estimate on the opening's carriers, imaginary parts.</summary>
    public ReadOnlySpan<double> ChannelImaginary => _valid ? _hIm.AsSpan(0, ShapeOf(_opening).Carriers) : default;

    /// <summary>The noise estimate (variance) on the opening's carriers, in the units of the capture's carriers squared.</summary>
    public ReadOnlySpan<double> Noise => _valid ? _var.AsSpan(0, ShapeOf(_opening).Carriers) : default;

    /// <summary>
    /// The delay found for each header symbol, in seconds: the accumulated delay after that symbol's ladder search and
    /// refinement, which is also what the next symbol starts from. A positive value means the symbol's carriers were turned
    /// forward by that much to meet the channel estimate.
    /// </summary>
    public ReadOnlySpan<double> Delays => _valid ? _delays.AsSpan(0, ShapeOf(_opening).HeaderSymbols) : default;

    /// <summary>The ladder step each header symbol's search chose, 0 to 96 (step 48 is no change).</summary>
    public ReadOnlySpan<int> LadderChoices => _valid ? _ladderIndex.AsSpan(0, ShapeOf(_opening).HeaderSymbols) : default;

    /// <summary>The soft values handed to the header code (positive meaning 0), all header symbols, two per carrier, in-phase first.</summary>
    public ReadOnlySpan<float> SoftValues => _valid ? _soft.AsSpan(0, ShapeOf(_opening).HeaderSymbols * 2 * ShapeOf(_opening).Carriers) : default;

    private static OpeningShape ShapeOf(FormatOpening opening)
    {
        if ((uint)opening >= (uint)Shapes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(opening));
        }

        return Shapes[(int)opening];
    }

    // The channel from the training symbols alone, and the noise from what they leave over.
    private void EstimateChannel(OpeningShape s)
    {
        int nb = s.Carriers;
        int t = s.Training;
        ReadOnlySpan<double> yRe = _transform.Real;
        ReadOnlySpan<double> yIm = _transform.Imaginary;
        for (int c = 0; c < nb; c++)
        {
            double sr = 0;
            double si = 0;
            for (int k = 0; k < t; k++)
            {
                TrainingPoint(k, c, out double pr, out double pi);
                double ar = yRe[(k * BurstRenderer.Carriers) + c];
                double ai = yIm[(k * BurstRenderer.Carriers) + c];

                // y * conj(point)
                sr += (ar * pr) + (ai * pi);
                si += (ai * pr) - (ar * pi);
            }

            _rawRe[c] = sr / t;
            _rawIm[c] = si / t;
        }

        _smoothers[(int)_opening].Smooth(_rawRe, _rawIm, _hRe, _hIm);
        for (int c = 0; c < nb; c++)
        {
            double e = 0;
            for (int k = 0; k < t; k++)
            {
                TrainingPoint(k, c, out double pr, out double pi);
                double dr = yRe[(k * BurstRenderer.Carriers) + c] - ((_hRe[c] * pr) - (_hIm[c] * pi));
                double di = yIm[(k * BurstRenderer.Carriers) + c] - ((_hRe[c] * pi) + (_hIm[c] * pr));
                e += (dr * dr) + (di * di);
            }

            _resid[c] = e / t;
        }

        _median.Apply(_resid.AsSpan(0, nb), _var.AsSpan(0, nb));
        double factor = t == 1 ? 1.5 : t / (t - 0.5);
        for (int c = 0; c < nb; c++)
        {
            _var[c] = Math.Max(_var[c], VarianceFloor) * factor;
            _h2[c] = (_hRe[c] * _hRe[c]) + (_hIm[c] * _hIm[c]);
            _weight[c] = _h2[c] / _var[c];
            _freq[c] = (BurstRenderer.FirstBin + c) * 48000.0 / BurstRenderer.FftSize;
            _twoPiF[c] = 2 * Math.PI * _freq[c];
        }
    }

    private static void TrainingPoint(int row, int carrier, out double re, out double im) =>
        TrainingSymbols.Point(TrainingSymbols.Quadrants(row)[carrier], out re, out im);

    // One header symbol: the delay search, the refinement and the soft values. Returns the accumulated delay.
    private double ReadSymbol(OpeningShape s, int symbol, double tau, int softOffset, out int pick)
    {
        int nb = s.Carriers;
        ReadOnlySpan<double> sr = _transform.Real.Slice(symbol * BurstRenderer.Carriers, nb);
        ReadOnlySpan<double> si = _transform.Imaginary.Slice(symbol * BurstRenderer.Carriers, nb);
        for (int c = 0; c < nb; c++)
        {
            double th = _twoPiF[c] * tau;
            double cs = Math.Cos(th);
            double sn = Math.Sin(th);
            _kRe[c] = (sr[c] * cs) - (si[c] * sn);
            _kIm[c] = (sr[c] * sn) + (si[c] * cs);
        }

        double best = double.PositiveInfinity;
        pick = 0;
        for (int i = 0; i < LadderSteps; i++)
        {
            double miss = 0;
            for (int c = 0; c < nb; c++)
            {
                Rotate(c, Ladder[i], out double zr, out double zi);
                double dr = zr - Sign(zr) / Sqrt2;
                double di = zi - Sign(zi) / Sqrt2;
                miss += ((dr * dr) + (di * di)) * _h2[c] / _var[c];
            }

            if (miss < best)
            {
                best = miss;
                pick = i;
            }
        }

        tau += Ladder[pick];
        for (int c = 0; c < nb; c++)
        {
            Rotate(c, Ladder[pick], out _yRe[c], out _yIm[c]);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            double num = 0;
            double den = 0;
            for (int c = 0; c < nb; c++)
            {
                double nr = Sign(_yRe[c]) / Sqrt2;
                double ni = Sign(_yIm[c]) / Sqrt2;

                // y * conj(nearest)
                double pr = (_yRe[c] * nr) + (_yIm[c] * ni);
                double pi = (_yIm[c] * nr) - (_yRe[c] * ni);
                double ang = Math.Atan2(pi, pr);
                num += _weight[c] * ang * _freq[c];
                den += _weight[c] * _freq[c] * _freq[c];
            }

            double step = -num / den / (2 * Math.PI);
            for (int c = 0; c < nb; c++)
            {
                double th = _twoPiF[c] * step;
                double cs = Math.Cos(th);
                double sn = Math.Sin(th);
                double r = (_yRe[c] * cs) - (_yIm[c] * sn);
                _yIm[c] = (_yRe[c] * sn) + (_yIm[c] * cs);
                _yRe[c] = r;
            }

            tau += step;
        }

        for (int c = 0; c < nb; c++)
        {
            // The prototype's _qpsk_llr with variance var / |h|^2.
            double scale = 2 * Sqrt2 / (_var[c] / _h2[c]);
            _soft[softOffset + (2 * c)] = Finite(-_yRe[c] * scale);
            _soft[softOffset + (2 * c) + 1] = Finite(-_yIm[c] * scale);
        }

        return tau;
    }

    // Carrier c of the current symbol, turned by the ladder delay and divided by the channel.
    private void Rotate(int c, double delay, out double zr, out double zi)
    {
        double th = _twoPiF[c] * delay;
        double cs = Math.Cos(th);
        double sn = Math.Sin(th);
        double ar = (_kRe[c] * cs) - (_kIm[c] * sn);
        double ai = (_kRe[c] * sn) + (_kIm[c] * cs);

        // divided by h: times conj(h) over |h|^2
        zr = ((ar * _hRe[c]) + (ai * _hIm[c])) / _h2[c];
        zi = ((ai * _hRe[c]) - (ar * _hIm[c])) / _h2[c];
    }

    // A carrier with no channel at all (digital silence) has nothing to say: its soft value is 0, not not-a-number.
    private static float Finite(double v) => double.IsFinite(v) ? (float)v : 0f;

    // numpy's sign: 0 for zero.
    private static double Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    // numpy.arange(-12e-6, 12e-6 + 1e-9, 0.25e-6): start + i * delta, with delta the difference of the first two values.
    private static double[] BuildLadder()
    {
        double start = -12e-6;
        double delta = (start + 0.25e-6) - start;
        var ladder = new double[LadderSteps];
        for (int i = 0; i < ladder.Length; i++)
        {
            ladder[i] = start + (i * delta);
        }

        return ladder;
    }

    private static OpeningShape[] BuildShapes()
    {
        var shapes = new OpeningShape[3];
        double[] tops = [double.PositiveInfinity, 3000.0, 1500.0];
        int[] training = [1, 3, 6];
        int[] headerSymbols = [1, 2, 3];
        for (int o = 0; o < shapes.Length; o++)
        {
            int carriers = 0;
            for (int k = 0; k < BurstRenderer.Carriers; k++)
            {
                if ((BurstRenderer.FirstBin + k) * 48000.0 / BurstRenderer.FftSize <= tops[o])
                {
                    carriers++;
                }
            }

            shapes[o] = new OpeningShape(training[o], headerSymbols[o], carriers);
        }

        return shapes;
    }

    private readonly record struct OpeningShape(int Training, int HeaderSymbols, int Carriers);
}
