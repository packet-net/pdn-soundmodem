using System.Numerics;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The burst interleaver of <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>. For E coded bits the permutation p is a
/// Fisher-Yates shuffle of 0..E-1 driven by the 31-stage generator of <see cref="PayloadScrambler"/> seeded with
/// <c>(1 &lt;&lt; 30) | E</c>, the seed's own bits unused. For i from E-1 down to 1: take n = bit length of i bits, first
/// bit most significant, as j; while j &gt; i, take the next n bits instead; swap p[i] and p[j]. The burst's coded bit k
/// goes to position p[k]: <c>tx[p[k]] = coded[k]</c>.
/// </summary>
/// <remarks>
/// Building takes millions of generator steps, so the four most recently used permutations are kept; a burst of a size
/// seen lately allocates nothing. The cache is guarded by a lock and is bounded by <see cref="CacheSize"/>.
/// </remarks>
public static class Interleaver
{
    /// <summary>Most permutations kept.</summary>
    public const int CacheSize = 4;

    private static readonly object Gate = new();
    private static readonly List<(int E, int[] Permutation)> Cache = new(CacheSize);

    /// <summary>How many permutations are held now (never more than <see cref="CacheSize"/>).</summary>
    public static int CachedCount
    {
        get
        {
            lock (Gate)
            {
                return Cache.Count;
            }
        }
    }

    /// <summary>Forgets every kept permutation.</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    /// <summary>The permutation for <paramref name="count"/> coded bits. Shared: do not modify.</summary>
    public static ReadOnlySpan<int> Permutation(int count) => Get(count);

    /// <summary>Places coded[k] at tx[p[k]]. Both spans hold exactly <paramref name="coded"/>.Length = E elements.</summary>
    public static void Interleave<T>(ReadOnlySpan<T> coded, Span<T> tx)
    {
        if (tx.Length != coded.Length)
        {
            throw new ArgumentException("coded and tx must be the same length.");
        }

        int[] p = Get(coded.Length);
        for (int k = 0; k < p.Length; k++)
        {
            tx[p[k]] = coded[k];
        }
    }

    /// <summary>The inverse: coded[k] = tx[p[k]]. Works on bits and on soft values alike.</summary>
    public static void Deinterleave<T>(ReadOnlySpan<T> tx, Span<T> coded)
    {
        if (tx.Length != coded.Length)
        {
            throw new ArgumentException("tx and coded must be the same length.");
        }

        int[] p = Get(tx.Length);
        for (int k = 0; k < p.Length; k++)
        {
            coded[k] = tx[p[k]];
        }
    }

    private static int[] Get(int count)
    {
        if (count < 1 || count >= 1 << 30)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The interleaver is defined for 1 to 2^30 - 1 bits.");
        }

        lock (Gate)
        {
            for (int i = 0; i < Cache.Count; i++)
            {
                if (Cache[i].E == count)
                {
                    var hit = Cache[i];
                    Cache.RemoveAt(i);
                    Cache.Add(hit);
                    return hit.Permutation;
                }
            }

            int[] p = Build(count);
            if (Cache.Count >= CacheSize)
            {
                Cache.RemoveAt(0);
            }

            Cache.Add((count, p));
            return p;
        }
    }

    private static int[] Build(int e)
    {
        var p = new int[e];
        for (int i = 0; i < e; i++)
        {
            p[i] = i;
        }

        uint state = (1u << 30) | (uint)e;
        for (int i = e - 1; i >= 1; i--)
        {
            int n = BitOperations.Log2((uint)i) + 1;
            int j;
            do
            {
                j = 0;
                for (int b = 0; b < n; b++)
                {
                    j = (j << 1) | (int)PayloadScrambler.Next(ref state);
                }
            }
            while (j > i);

            (p[i], p[j]) = (p[j], p[i]);
        }

        return p;
    }
}
