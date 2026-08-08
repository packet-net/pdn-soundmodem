using M0LTE.Fec;

namespace Packet.SoundModem.Modems.OfdmAb;

/// <summary>Forward error correction over the subcarriers.</summary>
public enum OfdmAbFec
{
    /// <summary>None: a burst is all-or-nothing on its CRC. Only as good as its worst subcarrier,
    /// which on an FM audio path is always one of the top ones.</summary>
    None,

    /// <summary>Rate-1/2 tail-biting convolutional, soft-decision Viterbi, optionally punctured.
    /// The 802.11a arrangement, on the same shape of problem.</summary>
    Convolutional,
}

/// <summary>
/// How a burst is coded. Config, not code, because the scheme is exactly what is unsettled: the
/// mode this experiment shadows has "FEC, final scheme to be determined".
/// </summary>
/// <param name="Scheme">Which code, if any.</param>
/// <param name="ConstraintLength">7 or 9 - the two mother codes the decoder knows. K=7 is the
/// classic 0o133/0o171 pair, the same code 802.11a carries.</param>
/// <param name="RateNumerator">Punctured rate numerator: 1, 2 or 3.</param>
/// <param name="RateDenominator">Punctured rate denominator: 2, 3 or 4, giving 1/2, 2/3 or 3/4.</param>
/// <param name="Interleave">Spread the coded bits across the whole burst before they meet the
/// subcarriers. Not optional in practice: an FM audio path's noise rises with frequency, so the
/// SAME high subcarriers are bad on every burst and an un-interleaved code sees one solid block
/// of errors rather than a scatter. The switch exists to measure that, not to turn it off.</param>
public sealed record OfdmAbCoding(
    OfdmAbFec Scheme = OfdmAbFec.None,
    int ConstraintLength = 7,
    int RateNumerator = 1,
    int RateDenominator = 2,
    bool Interleave = true)
{
    /// <summary>Coded bits per payload bit.</summary>
    public double Expansion => Scheme == OfdmAbFec.None
        ? 1.0
        : (double)RateDenominator / RateNumerator;
}

/// <summary>A run of subcarriers carrying the same number of bits.</summary>
/// <param name="Carriers">How many consecutive data carriers.</param>
/// <param name="Bits">Bits each of them carries, 1 to 8.</param>
/// <remarks>
/// Bit loading is the DSL answer to a channel whose signal-to-noise ratio varies across its own
/// band, and an FM audio path is exactly that: discriminator noise power rises with the square of
/// frequency, so the top subcarriers are measurably worse than the bottom ones, systematically and
/// on every burst. Spending eight bits on a carrier that cannot hold four, while a clean low
/// carrier carries the same four, is the waste this removes.
/// </remarks>
public sealed record OfdmAbBitLoadingTier(int Carriers, int Bits);

/// <summary>
/// The coding chain: payload bits in, subcarrier-ready bits out, and soft metrics back again.
/// </summary>
internal sealed class OfdmAbCodec
{
    private readonly OfdmAbCoding _coding;
    private readonly ConvolutionalCode? _code;
    private readonly int[] _keepA;
    private readonly int[] _keepB;

    public OfdmAbCodec(OfdmAbCoding coding)
    {
        _coding = coding;
        (_keepA, _keepB) = Puncture(coding.RateNumerator, coding.RateDenominator);
        if (coding.Scheme == OfdmAbFec.Convolutional)
        {
            _code = coding.ConstraintLength switch
            {
                7 => ConvolutionalCode.K7,
                9 => ConvolutionalCode.K9,
                _ => throw new InvalidOperationException(
                    $"constraint length {coding.ConstraintLength} has no mother code; use 7 or 9"),
            };
        }
    }

    /// <summary>Coded bits a payload of this many bits becomes.</summary>
    public int CodedBits(int payloadBits)
    {
        if (_code is null)
        {
            return payloadBits;
        }

        int period = _keepA.Length;
        int kept = 0;
        for (int p = 0; p < period; p++)
        {
            kept += _keepA[p] + _keepB[p];
        }

        int whole = payloadBits / period;
        int remainder = payloadBits % period;
        int extra = 0;
        for (int p = 0; p < remainder; p++)
        {
            extra += _keepA[p] + _keepB[p];
        }

        return (whole * kept) + extra;
    }

    /// <summary>Encodes payload bits (0/1 per byte) into subcarrier-ready bits.</summary>
    public byte[] Encode(ReadOnlySpan<byte> payloadBits)
    {
        byte[] coded;
        if (_code is null)
        {
            coded = payloadBits.ToArray();
        }
        else
        {
            var mother = new byte[payloadBits.Length * 2];
            TailBitingEncoder.Encode(_code, payloadBits, mother);
            coded = new byte[CodedBits(payloadBits.Length)];
            int output = 0;
            for (int i = 0; i < payloadBits.Length; i++)
            {
                int p = i % _keepA.Length;
                if (_keepA[p] == 1)
                {
                    coded[output++] = mother[i * 2];
                }

                if (_keepB[p] == 1)
                {
                    coded[output++] = mother[(i * 2) + 1];
                }
            }
        }

        if (!_coding.Interleave || coded.Length < 3)
        {
            return coded;
        }

        var spread = new byte[coded.Length];
        Permute(coded, spread, forward: true);
        return spread;
    }

    /// <summary>Recovers payload bits from the soft metrics of the coded bits.</summary>
    /// <param name="llrs">One log-likelihood ratio per coded bit, positive meaning zero.</param>
    /// <param name="payloadBits">How many payload bits were coded.</param>
    public byte[] Decode(ReadOnlySpan<float> llrs, int payloadBits)
    {
        float[] ordered;
        if (_coding.Interleave && llrs.Length >= 3)
        {
            ordered = new float[llrs.Length];
            Permute(llrs, ordered, forward: false);
        }
        else
        {
            ordered = llrs.ToArray();
        }

        if (_code is null)
        {
            var bits = new byte[payloadBits];
            for (int i = 0; i < payloadBits && i < ordered.Length; i++)
            {
                bits[i] = (byte)(ordered[i] >= 0 ? 0 : 1);
            }

            return bits;
        }

        // Rebuild the full rate-1/2 lattice, with zero where puncturing removed a bit. Zero is
        // exactly "no information" to a max-log Viterbi, so depuncturing needs nothing else.
        var mother = new float[payloadBits * 2];
        int read = 0;
        for (int i = 0; i < payloadBits; i++)
        {
            int p = i % _keepA.Length;
            mother[i * 2] = _keepA[p] == 1 && read < ordered.Length ? ordered[read++] : 0f;
            mother[(i * 2) + 1] = _keepB[p] == 1 && read < ordered.Length ? ordered[read++] : 0f;
        }

        var decoded = new byte[payloadBits];
        new TailBitingViterbiDecoder(_code).Decode(mother, decoded);
        return decoded;
    }

    /// <summary>
    /// The frequency interleave: <c>spread[(stride*i) mod n] = coded[i]</c>, and its exact inverse.
    /// </summary>
    /// <remarks>
    /// Written here rather than taken from <c>M0LTE.Fec.GpInterleaver</c> because that type's
    /// Interleave and Deinterleave are not inverses of each other in the pinned version - a
    /// round trip through the pair returns roughly a third of a block wrong, in either order. The
    /// permutation wanted is trivial and provable: a stride coprime with the block length visits
    /// every position exactly once, so the map is a bijection and reading it backwards undoes it.
    /// </remarks>
    private static void Permute<T>(ReadOnlySpan<T> source, Span<T> destination, bool forward)
    {
        int n = source.Length;
        int stride = Stride(n);
        for (int i = 0; i < n; i++)
        {
            int j = (int)(((long)stride * i) % n);
            if (forward)
            {
                destination[j] = source[i];
            }
            else
            {
                destination[i] = source[j];
            }
        }
    }

    /// <summary>A stride near the golden section of the block, coprime with it so the walk is a
    /// permutation. Golden-section spacing is what spreads adjacent coded bits furthest apart,
    /// which is the whole point: neighbouring subcarriers fail together.</summary>
    private static int Stride(int n)
    {
        int start = Math.Max(2, (int)(n * 0.6180339887));
        for (int candidate = start; candidate < start + n; candidate++)
        {
            int value = candidate % n;
            if (value > 1 && Gcd(value, n) == 1)
            {
                return value;
            }
        }

        return 1;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    // 802.11a's puncturing patterns, which are the standard ones for this mother code.
    private static (int[] A, int[] B) Puncture(int numerator, int denominator) =>
        (numerator, denominator) switch
        {
            (1, 2) => ([1], [1]),
            (2, 3) => ([1, 1], [1, 0]),
            (3, 4) => ([1, 0, 1], [1, 1, 0]),
            _ => throw new InvalidOperationException(
                $"rate {numerator}/{denominator} is not punctured here; use 1/2, 2/3 or 3/4"),
        };
}
