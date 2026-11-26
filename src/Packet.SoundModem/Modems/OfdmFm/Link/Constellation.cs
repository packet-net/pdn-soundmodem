namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The Gray-mapped square or rectangular constellations of the standard formats, at unit mean power, as drawn by the
/// prototype: 1, 2, 4, 6, 8 or 10 bits a carrier. The first bits of a carrier's group pick the in-phase level and the
/// rest the quadrature level (an odd count, which the standard formats do not use apart from 1, gives the extra bit to
/// the in-phase axis).
/// </summary>
/// <remarks>
/// Carrier values are held as two parallel <see cref="double"/> spans (real, imaginary), as the rest of the OFDM-FM code
/// does for its twiddles and delays, not as <see cref="System.Numerics.Complex"/>. Bits are held one to a byte (0 or 1).
/// The point tables are built once per size, on first use. The arithmetic is the prototype's, in the same order (levels
/// are exact integers, the scale is 1 / sqrt of a sum of two exact fractions), so the points agree with it to the bit.
/// </remarks>
public sealed class Constellation
{
    private static readonly int[] Sizes = [1, 2, 4, 6, 8, 10];
    private static readonly Constellation?[] Cache = new Constellation?[11];
    private static readonly object Gate = new();

    private readonly double[] _re;
    private readonly double[] _im;

    private Constellation(int bits)
    {
        Bits = bits;
        int mi = (bits + 1) / 2, mq = bits / 2;
        double energy = ((double)((1L << (2 * mi)) - 1) / 3) + ((double)((1L << (2 * mq)) - 1) / 3);
        double scale = 1 / Math.Sqrt(energy);
        HalfDistance = scale;
        _re = new double[1 << bits];
        _im = new double[1 << bits];
        int[] gi = GrayToIndex(mi);
        int[] gq = GrayToIndex(mq);
        for (int p = 0; p < _re.Length; p++)
        {
            int wi = p >> mq, wq = p & ((1 << mq) - 1);
            _re[p] = (2.0 * gi[wi] - ((1 << mi) - 1)) * scale;
            _im[p] = mq == 0 ? 0.0 : (2.0 * gq[wq] - ((1 << mq) - 1)) * scale;
        }
    }

    /// <summary>Bits a carrier.</summary>
    public int Bits { get; }

    /// <summary>Half the distance between nearest points at unit mean power.</summary>
    public double HalfDistance { get; }

    /// <summary>Whether <paramref name="bitsPerCarrier"/> is one of the sizes the standard formats use (1, 2, 4, 6, 8, 10).</summary>
    public static bool IsSupported(int bitsPerCarrier) => Array.IndexOf(Sizes, bitsPerCarrier) >= 0;

    /// <summary>The constellation for a size, built on first use.</summary>
    public static Constellation For(int bitsPerCarrier)
    {
        if (!IsSupported(bitsPerCarrier))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerCarrier), bitsPerCarrier, "Carriers carry 1, 2, 4, 6, 8 or 10 bits.");
        }

        lock (Gate)
        {
            return Cache[bitsPerCarrier] ??= new Constellation(bitsPerCarrier);
        }
    }

    /// <summary>The point for a bit pattern read as an integer, first bit most significant.</summary>
    public void Point(int pattern, out double re, out double im)
    {
        re = _re[pattern];
        im = _im[pattern];
    }

    /// <summary>
    /// Maps a run of carriers of this size: <c>re.Length</c> carriers take <c>Bits</c> bits each, in order, from
    /// <paramref name="bits"/> (one to a byte).
    /// </summary>
    public void Map(ReadOnlySpan<byte> bits, Span<double> re, Span<double> im)
    {
        if (im.Length != re.Length || bits.Length != re.Length * Bits)
        {
            throw new ArgumentException("bits must hold Bits per carrier, and re and im must be the same length.");
        }

        for (int c = 0, k = 0; c < re.Length; c++)
        {
            int p = 0;
            for (int i = 0; i < Bits; i++, k++)
            {
                p = (p << 1) | (bits[k] & 1);
            }

            re[c] = _re[p];
            im[c] = _im[p];
        }
    }

    /// <summary>
    /// Maps one symbol: carrier c takes <c>loading[c]</c> bits (0, or a supported size) from <paramref name="bits"/>,
    /// carriers in order, and carriers loaded with 0 are set to zero. This is how a symbol's bits go to its carriers.
    /// </summary>
    /// <returns>The number of bits consumed.</returns>
    public static int MapSymbol(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> loading, Span<double> re, Span<double> im)
    {
        if (re.Length != loading.Length || im.Length != loading.Length)
        {
            throw new ArgumentException("loading, re and im must be the same length.");
        }

        int k = 0;
        for (int c = 0; c < loading.Length; c++)
        {
            int b = loading[c];
            if (b == 0)
            {
                re[c] = 0;
                im[c] = 0;
                continue;
            }

            Constellation t = For(b);
            if (k + b > bits.Length)
            {
                throw new ArgumentException("not enough bits for the loading.", nameof(bits));
            }

            int p = 0;
            for (int i = 0; i < b; i++, k++)
            {
                p = (p << 1) | (bits[k] & 1);
            }

            re[c] = t._re[p];
            im[c] = t._im[p];
        }

        return k;
    }

    private static int[] GrayToIndex(int m)
    {
        var idx = new int[1 << m];
        for (int g = 0; g < idx.Length; g++)
        {
            int v = g;
            for (int s = g >> 1; s != 0; s >>= 1)
            {
                v ^= s;
            }

            idx[g] = v;
        }

        return idx;
    }
}
