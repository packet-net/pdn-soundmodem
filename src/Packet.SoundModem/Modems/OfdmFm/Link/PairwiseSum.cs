using System.Runtime.CompilerServices;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The sum of a run of doubles in the order the prototype's array library adds them: eight running totals over blocks of up to
/// 128 values, halves above that. A plain left-to-right sum differs from it in the last bits; a decision that compares a sum
/// with a threshold would then differ at the margin. A mean is this over the count.
/// </summary>
internal static class PairwiseSum
{
    private const int BlockSize = 128;

    public static double Sum(ReadOnlySpan<double> values) => Sum(values, 0, values.Length);

    public static double Mean(ReadOnlySpan<double> values) => Sum(values, 0, values.Length) / values.Length;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Sum(ReadOnlySpan<double> a, int start, int n)
    {
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++)
            {
                res += a[start + i];
            }

            return res;
        }

        if (n <= BlockSize)
        {
            double r0 = a[start];
            double r1 = a[start + 1];
            double r2 = a[start + 2];
            double r3 = a[start + 3];
            double r4 = a[start + 4];
            double r5 = a[start + 5];
            double r6 = a[start + 6];
            double r7 = a[start + 7];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
            {
                r0 += a[start + i];
                r1 += a[start + i + 1];
                r2 += a[start + i + 2];
                r3 += a[start + i + 3];
                r4 += a[start + i + 4];
                r5 += a[start + i + 5];
                r6 += a[start + i + 6];
                r7 += a[start + i + 7];
            }

            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++)
            {
                res += a[start + i];
            }

            return res;
        }

        int n2 = n / 2;
        n2 -= n2 % 8;
        return Sum(a, start, n2) + Sum(a, start + n2, n - n2);
    }
}
