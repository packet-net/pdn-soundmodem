using System.Reflection;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The known QPSK training symbols of <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>: six rows of 333 quadrant numbers
/// (0 to 3) from the prototype's frozen table. Quadrant q is the point exp(j(pi/4 + q pi/2)). An opening uses the first
/// 1, 3 or 6 rows, cut to its band; that cut is not made here.
/// </summary>
/// <remarks>
/// The table is the embedded resource <c>link-training.txt</c>, the prototype's file unchanged, parsed once on first
/// use. The four points are the doubles the prototype's numpy code produces; they differ from (+-a, +-a) with
/// a = sqrt(0.5) in the last place or two, so they are written out here rather than computed.
/// </remarks>
public static class TrainingSymbols
{
    /// <summary>Rows in the table.</summary>
    public const int Rows = 6;

    /// <summary>Carriers in a row.</summary>
    public const int Carriers = 333;

    private static readonly double[] PointRe = [0.7071067811865476, -0.7071067811865475, -0.7071067811865477, 0.7071067811865474];
    private static readonly double[] PointIm = [0.7071067811865475, 0.7071067811865476, -0.7071067811865475, -0.7071067811865477];

    private static readonly Lazy<byte[]> Table = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Row <paramref name="row"/> (0 to 5) as 333 quadrant numbers, 0 to 3.</summary>
    public static ReadOnlySpan<byte> Quadrants(int row)
    {
        if ((uint)row >= Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, "The table has six rows, 0 to 5.");
        }

        return Table.Value.AsSpan(row * Carriers, Carriers);
    }

    /// <summary>The point of a quadrant number (0 to 3).</summary>
    public static void Point(int quadrant, out double re, out double im)
    {
        re = PointRe[quadrant];
        im = PointIm[quadrant];
    }

    /// <summary>Row <paramref name="row"/> as 333 points.</summary>
    public static void Points(int row, Span<double> re, Span<double> im)
    {
        if (re.Length != Carriers || im.Length != Carriers)
        {
            throw new ArgumentException("re and im must hold 333 values.");
        }

        ReadOnlySpan<byte> q = Quadrants(row);
        for (int c = 0; c < Carriers; c++)
        {
            re[c] = PointRe[q[c]];
            im[c] = PointIm[q[c]];
        }
    }

    private static byte[] Load()
    {
        using Stream stream = typeof(TrainingSymbols).Assembly.GetManifestResourceStream("link-training.txt")
            ?? throw new InvalidDataException("embedded table link-training.txt is missing");
        using var reader = new StreamReader(stream);
        var table = new byte[Rows * Carriers];
        int row = 0;
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (row >= Rows || line.Length != Carriers)
            {
                throw new InvalidDataException("link-training.txt must hold 6 rows of 333 digits.");
            }

            for (int c = 0; c < Carriers; c++)
            {
                int d = line[c] - '0';
                if ((uint)d > 3)
                {
                    throw new InvalidDataException("link-training.txt holds a digit outside 0 to 3.");
                }

                table[row * Carriers + c] = (byte)d;
            }

            row++;
        }

        if (row != Rows)
        {
            throw new InvalidDataException("link-training.txt must hold 6 rows of 333 digits.");
        }

        return table;
    }
}
