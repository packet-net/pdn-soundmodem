namespace Packet.SoundModem.Fec;

/// <summary>
/// Golden-prime interleaver — a port of codec2's <c>gp_interleaver.c</c> (David Rowe, after
/// Xie et al, "On the Analysis and Design of Good Algebraic Interleavers"). A pure algebraic
/// permutation <c>interleaved[(b·i) mod N] = frame[i]</c> where <c>b</c> is the prime nearest
/// (from above) the golden section of N, so it is fully determined by N. The FreeDV datac
/// modes interleave the OFDM QPSK symbols with this (and the RX deinterleaves both the complex
/// symbols and their amplitudes), spreading a burst error across the LDPC codeword. LGPL-2.1
/// lineage — see PROVENANCE.md.
/// </summary>
internal static class GpInterleaver
{
    private static bool IsPrime(int x)
    {
        for (int i = 2; i < x; i++)
        {
            if (x % i == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int NextPrime(int x)
    {
        x++;
        while (!IsPrime(x))
        {
            x++;
        }

        return x;
    }

    /// <summary>The interleave stride: the prime nearest above N's golden section.</summary>
    public static int ChooseB(int n) => NextPrime((int)Math.Floor(n / 1.62));

    /// <summary>Interleaves N elements: <c>interleaved[(b·i) mod N] = frame[i]</c>.</summary>
    public static void Interleave<T>(ReadOnlySpan<T> frame, Span<T> interleaved, int n)
    {
        int b = ChooseB(n);
        for (int i = 0; i < n; i++)
        {
            interleaved[(int)((long)b * i % n)] = frame[i];
        }
    }

    /// <summary>Inverse of <see cref="Interleave{T}"/>: <c>frame[i] = interleaved[(b·i) mod N]</c>.</summary>
    public static void Deinterleave<T>(ReadOnlySpan<T> interleaved, Span<T> frame, int n)
    {
        int b = ChooseB(n);
        for (int i = 0; i < n; i++)
        {
            frame[i] = interleaved[(int)((long)b * i % n)];
        }
    }
}
