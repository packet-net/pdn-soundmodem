using System.Globalization;
using System.Runtime.CompilerServices;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

public static partial class StandardFormats
{
    // The prototype's bisection: the margin is searched between these bounds, to this step.
    private const double MarginLowDb = -40.0;
    private const double MarginHighDb = 40.0;
    private const double MarginStepDb = 0.25;

    private static readonly Lazy<GmiTable> Gmi = new(GmiTable.Load);

    // The format numbers fastest first (by the rate to the thousandth), the lower number first among equals.
    private static readonly Lazy<int[]> ByRate = new(() =>
    {
        var order = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) =>
        {
            int c = RateInThousandths(y).CompareTo(RateInThousandths(x));
            return c != 0 ? c : x.CompareTo(y);
        });
        return order;
    });

    /// <summary>
    /// How many decibels a measured quality could fall before format <paramref name="number"/> no longer holds (the prototype's
    /// <c>margin_db</c>). <paramref name="quality"/> is a power ratio on each of the 333 carriers as heard from a burst sent
    /// <paramref name="heardDriveDb"/> over full drive. Negative: it does not hold now. The result is a point of the bisection's
    /// lattice, between -40 and 40, so the same decisions give the same double.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double MarginDb(int number, ReadOnlySpan<double> quality, double heardDriveDb = 0.0)
    {
        if (quality.Length != FullBandCarriers)
        {
            throw new ArgumentException("quality must hold one value for each of the 333 carriers.", nameof(quality));
        }

        FormatSpec s = Describe(number);
        int used = s.Carriers;
        double lift = (double)FullBandCarriers / used * Math.Pow(10.0, (s.DriveDb - heardDriveDb) / 10.0);
        double need = s.InfoBitsPerCarrier * used;
        double[] row = Gmi.Value.Row(s.ConstellationBits);
        Span<double> carried = stackalloc double[FullBandCarriers];
        double lo = MarginLowDb;
        double hi = MarginHighDb;
        if (CarriedSum(quality, used, lift, lo, row, carried) < need)
        {
            return lo;
        }

        while (hi - lo > MarginStepDb)
        {
            double mid = (lo + hi) / 2;
            if (CarriedSum(quality, used, lift, mid, row, carried) >= need)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// The fastest standard format that a measured quality supports with at least <paramref name="marginDb"/> decibels in hand, or
    /// the most robust one if none does (the prototype's <c>recommend</c>). Formats are walked from 0 to 255; of those that hold, the
    /// one with the greater rate (to the thousandth of a kbit/s) wins, of equal rates the one with more margin, and of equal both
    /// the lower number. <paramref name="noFasterThan"/>: a format whose rate is not to be exceeded, in whatever band.
    /// </summary>
    public static int Recommend(ReadOnlySpan<double> quality, double marginDb, double heardDriveDb = 0.0, int? noFasterThan = null)
    {
        if (quality.Length != FullBandCarriers)
        {
            throw new ArgumentException("quality must hold one value for each of the 333 carriers.", nameof(quality));
        }

        double ceiling = double.PositiveInfinity;
        if (noFasterThan is int ceilingFormat)
        {
            ceiling = Describe(ceilingFormat).KbitPerSecond + 1e-9;
        }

        // Same answer as walking 0 to 255 and keeping the best by (rate, margin) with the lower number winning a tie, but in order of
        // rate, fastest first: the first rate at which any format holds decides, and the slower ones are never worked out.
        int[] order = ByRate.Value;
        int best = -1;
        double bestMargin = 0;
        long groupRate = 0;
        foreach (int n in order)
        {
            long rate = RateInThousandths(n);
            if (best >= 0 && rate < groupRate)
            {
                break;
            }

            if (Table[n].KbitPerSecond > ceiling)
            {
                continue;
            }

            if (!MayHold(n, quality, heardDriveDb, marginDb))
            {
                continue;
            }

            double m = MarginDb(n, quality, heardDriveDb);
            if (m >= marginDb && (best < 0 || m > bestMargin || (m == bestMargin && n < best)))
            {
                best = n;
                bestMargin = m;
                groupRate = rate;
            }
        }

        return best < 0 ? MostRobust : best;
    }

    /// <summary>The rate of a format in thousandths of a kbit/s, rounded as the prototype's <c>round(rate, 3)</c> does.</summary>
    internal static long RateInThousandths(int number) => (long)Math.Round(Describe(number).KbitPerSecond * 1000.0, MidpointRounding.ToEven);

    /// <summary>Bits a carrier gets from a constellation of this many bits at this signal to noise ratio in dB (the prototype's <c>carried</c>, for one carrier).</summary>
    internal static double Carried(int constellationBits, double snrDb) => Gmi.Value.Interpolate(Gmi.Value.Row(constellationBits), snrDb);

    /// <summary>
    /// False only when <see cref="MarginDb"/> is certain to come out under <paramref name="marginDb"/>, from one evaluation instead
    /// of the bisection's ten. The bisection ends on the largest point of a lattice (-40 plus whole steps of 80/512 dB) at which what
    /// the carriers carry still meets what the format asks, so when that falls with the reduction, as it does for every table row
    /// but the one-bit row (which dips by 1e-4 between 9 and 10 dB), the margin reaches <paramref name="marginDb"/> exactly when the
    /// first lattice point at or above it holds. The one-bit formats are always worked out in full.
    /// </summary>
    private static bool MayHold(int number, ReadOnlySpan<double> quality, double heardDriveDb, double marginDb)
    {
        FormatSpec s = Table[number];
        const double Lattice = (MarginHighDb - MarginLowDb) / 512;
        if (s.ConstellationBits == 1 || !(marginDb > MarginLowDb))
        {
            return true;
        }

        double k = Math.Ceiling((marginDb - MarginLowDb) / Lattice);
        if (k > 511)
        {
            return false;
        }

        while (MarginLowDb + (k * Lattice) < marginDb)
        {
            k++;
        }

        while (k > 1 && MarginLowDb + ((k - 1) * Lattice) >= marginDb)
        {
            k--;
        }

        if (k > 511)
        {
            return false;
        }

        int used = s.Carriers;
        double lift = (double)FullBandCarriers / used * Math.Pow(10.0, (s.DriveDb - heardDriveDb) / 10.0);
        Span<double> carried = stackalloc double[FullBandCarriers];
        return CarriedSum(quality, used, lift, MarginLowDb + (k * Lattice), Gmi.Value.Row(s.ConstellationBits), carried) >= s.InfoBitsPerCarrier * used;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double CarriedSum(ReadOnlySpan<double> quality, int used, double lift, double reductionDb, double[] row, Span<double> carried)
    {
        double scale = Math.Pow(10.0, -reductionDb / 10);
        GmiTable gmi = Gmi.Value;
        for (int i = 0; i < used; i++)
        {
            double snr = lift * quality[i] * scale;
            carried[i] = gmi.Interpolate(row, 10 * Math.Log10(Math.Max(snr, 1e-6)));
        }

        return PairwiseSum.Sum(carried[..used]);
    }

    /// <summary>The bits-a-carrier table (the embedded resource link-gmi.txt): a row of signal to noise ratios, 1 dB apart, and a row for each constellation size.</summary>
    private sealed class GmiTable
    {
        private const int Points = 56;
        private const double FirstDb = -10.0;
        private readonly double[][] rows = new double[11][];

        private GmiTable()
        {
        }

        public double[] Row(int constellationBits) =>
            (uint)constellationBits < (uint)rows.Length && rows[constellationBits] is { } row
                ? row
                : throw new ArgumentOutOfRangeException(nameof(constellationBits));

        /// <summary>Linear interpolation clamped at both ends, as the prototype's array library does it for a table with points 1 dB apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public double Interpolate(double[] row, double snrDb)
        {
            if (double.IsNaN(snrDb))
            {
                return double.NaN;
            }

            if (snrDb > FirstDb + Points - 1)
            {
                return row[Points - 1];
            }

            if (snrDb < FirstDb)
            {
                return row[0];
            }

            int j = (int)Math.Floor(snrDb - FirstDb);
            double at = FirstDb + j;
            if (snrDb == at)
            {
                return row[j];
            }

            double slope = (row[j + 1] - row[j]) / 1.0;
            return (slope * (snrDb - at)) + row[j];
        }

        public static GmiTable Load()
        {
            using Stream stream = typeof(StandardFormats).Assembly.GetManifestResourceStream("link-gmi.txt")
                ?? throw new InvalidDataException("embedded table link-gmi.txt is missing");
            using var reader = new StreamReader(stream);
            var table = new GmiTable();
            bool sawRatios = false;
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length != Points + 1)
                {
                    throw new InvalidDataException("link-gmi.txt: every row must hold a name and 56 values.");
                }

                var values = new double[Points];
                for (int i = 0; i < Points; i++)
                {
                    values[i] = double.Parse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                if (words[0] == "snr_db")
                {
                    for (int i = 0; i < Points; i++)
                    {
                        if (values[i] != FirstDb + i)
                        {
                            throw new InvalidDataException("link-gmi.txt: the signal to noise ratios must run from -10 dB in steps of 1 dB.");
                        }
                    }

                    sawRatios = true;
                }
                else
                {
                    int bits = int.Parse(words[0], CultureInfo.InvariantCulture);
                    if ((uint)bits >= (uint)table.rows.Length || table.rows[bits] is not null)
                    {
                        throw new InvalidDataException("link-gmi.txt: a constellation size is out of range or repeated.");
                    }

                    table.rows[bits] = values;
                }
            }

            if (!sawRatios)
            {
                throw new InvalidDataException("link-gmi.txt: the row of signal to noise ratios is missing.");
            }

            foreach (int bits in new[] { 1, 2, 4, 6, 8, 10 })
            {
                if (table.rows[bits] is null)
                {
                    throw new InvalidDataException("link-gmi.txt: a row is missing.");
                }
            }

            return table;
        }
    }
}
