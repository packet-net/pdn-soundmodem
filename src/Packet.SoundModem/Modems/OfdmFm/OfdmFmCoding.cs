using M0LTE.Fec;
using M0LTE.FecLdpc;

namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>Forward error correction over the subcarriers.</summary>
public enum OfdmFmFec
{
    /// <summary>None: a burst is all-or-nothing on its CRC. Only as good as its worst subcarrier,
    /// which on an FM audio path is always one of the top ones.</summary>
    None,

    /// <summary>Rate-1/2 tail-biting convolutional, soft-decision Viterbi, optionally punctured.
    /// The 802.11a arrangement, on the same shape of problem.</summary>
    Convolutional,

    /// <summary>Rate-1/2 repeat-accumulate LDPC, soft sum-product decoding: the FreeDV datac
    /// family's codes, via M0LTE.FecLdpc. Here so that the two code families can be measured
    /// against each other on this path rather than one of them simply being the one that arrived
    /// first; a payload is segmented into full frames of the largest mother code it fills, with
    /// one shortened tail frame, so the on-air rate sits at 1/2 for payloads that fill their
    /// frames and below it for ones that do not.
    /// The rate fields of <see cref="OfdmFmCoding"/> are not consulted - one family, one rate,
    /// today.</summary>
    Ldpc,
}

/// <summary>
/// How a burst is coded. Config, not code, because the scheme is exactly what is unsettled:
/// which family, which constraint length and which punctured rate a burst should carry are all
/// still being measured, and a burst signals its own coding, so nothing has to be agreed in
/// advance or recompiled to change it.
/// </summary>
/// <param name="Scheme">Which code, if any.</param>
/// <param name="ConstraintLength">7 or 9 - the two convolutional mother codes the decoder knows.
/// K=7 is the classic 0o133/0o171 pair, the same code 802.11a carries. Meaningless for the other
/// schemes, and canonicalised away before a header names them.</param>
/// <param name="RateNumerator">Punctured rate numerator: 1, 2, 3, 5 or 7.</param>
/// <param name="RateDenominator">Punctured rate denominator: 2, 3, 4, 6 or 8, giving 1/2, 2/3,
/// 3/4, 5/6 or 7/8. The two above 3/4 have a wire name for K=7 only (coding ids 8 and 9 in the
/// burst codec's table): a K=9 profile at them builds and decodes against itself, but no header
/// can carry it, so validation refuses it the way it refuses an un-interleaved code.</param>
/// <param name="Interleave">Spread the coded bits across the whole burst before they meet the
/// subcarriers. Not optional in practice: an FM audio path's noise rises with frequency, so the
/// SAME high subcarriers are bad on every burst and an un-interleaved code sees one solid block
/// of errors rather than a scatter. The switch exists to measure that, not to turn it off.</param>
public sealed record OfdmFmCoding(
    OfdmFmFec Scheme = OfdmFmFec.None,
    int ConstraintLength = 7,
    int RateNumerator = 1,
    int RateDenominator = 2,
    bool Interleave = true)
{
    /// <summary>Coded bits per payload bit.</summary>
    public double Expansion => Scheme == OfdmFmFec.None
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
public sealed record OfdmFmBitLoadingTier(int Carriers, int Bits);

/// <summary>
/// The coding chain: payload bits in, subcarrier-ready bits out, and soft metrics back again.
/// </summary>
internal sealed class OfdmFmCodec
{
    /// <summary>
    /// The mean absolute log-likelihood ratio the LDPC decoder falls back to when a caller has no
    /// noise measurement to offer.
    /// </summary>
    /// <remarks>
    /// <para>The sum-product decoder is the one consumer in this chain that cares about the LLRs'
    /// ABSOLUTE scale - a max-log Viterbi is scale invariant and the demapper leans on that,
    /// producing distances on the constellation's own scale rather than true likelihoods. The
    /// right scale comes from the caller: distance over noise, per burst (see
    /// <see cref="Decode"/>). This constant is only the fallback for a caller without a noise
    /// measurement, normalising the block's mean magnitude to roughly what a cliff burst truly
    /// carries, ln((1-p)/p) at the 5-10 % such a burst spends.</para>
    /// <para>A fixed target was the shipped mechanism first, swept to its optimum (2.5) at QPSK
    /// and QAM-16 - and at QAM-64 that same number was FOUR DECIBELS of underconfidence, because
    /// a dense constellation's mean max-log distance shrinks while its true likelihoods do not
    /// scale with it. The sweep and the collapse are in docs/dev/ofdm-fm/receiver-findings.md; the lesson is
    /// that the scale belongs to the burst's own noise, not to a constant.</para>
    /// </remarks>
    private const double LdpcLlrFallbackMean = 2.5;

    /// <summary>The LDPC mother codes, smallest first. A payload is cut into full frames of the
    /// largest code it fills, then one shortened tail frame on the smallest code that swallows
    /// the remainder - full frames run at the mother rate 1/2, and the tail trades rate for
    /// protection on the fewest bits possible.</summary>
    private static readonly LdpcCode[] _ldpcBySize =
    [
        LdpcCodes.HRA_56_56,
        LdpcCodes.H_128_256_5,
        LdpcCodes.H_256_512_4,
        LdpcCodes.H_1024_2048_4f,
        LdpcCodes.H_4096_8192_3d,
    ];

    private readonly OfdmFmCoding _coding;
    private readonly ConvolutionalCode? _code;
    private readonly int[] _keepA;
    private readonly int[] _keepB;
    private readonly Dictionary<(string Code, int DataBits), LdpcFrameCodec> _ldpcFrames = [];

    public OfdmFmCodec(OfdmFmCoding coding)
    {
        _coding = coding;
        (_keepA, _keepB) = Puncture(coding.RateNumerator, coding.RateDenominator);
        if (coding.Scheme == OfdmFmFec.Convolutional)
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

    /// <summary>How a payload of this many bits lands on LDPC frames.</summary>
    private static List<(LdpcCode Code, int DataBits)> LdpcFrames(int payloadBits)
    {
        var frames = new List<(LdpcCode, int)>();
        int remaining = payloadBits;
        while (remaining > 0)
        {
            LdpcCode? full = null;
            for (int i = _ldpcBySize.Length - 1; i >= 0; i--)
            {
                if (_ldpcBySize[i].NumberRowsHcols <= remaining)
                {
                    full = _ldpcBySize[i];
                    break;
                }
            }

            if (full is LdpcCode code)
            {
                frames.Add((code, code.NumberRowsHcols));
                remaining -= code.NumberRowsHcols;
                continue;
            }

            frames.Add((_ldpcBySize[0], remaining));
            remaining = 0;
        }

        return frames;
    }

    private LdpcFrameCodec LdpcFrameFor(LdpcCode code, int dataBits)
    {
        if (!_ldpcFrames.TryGetValue((code.Name, dataBits), out LdpcFrameCodec? codec))
        {
            codec = new LdpcFrameCodec(code, dataBits);
            _ldpcFrames[(code.Name, dataBits)] = codec;
        }

        return codec;
    }

    /// <summary>Coded bits a payload of this many bits becomes.</summary>
    public int CodedBits(int payloadBits)
    {
        if (_coding.Scheme == OfdmFmFec.Ldpc)
        {
            int total = 0;
            foreach ((LdpcCode code, int dataBits) in LdpcFrames(payloadBits))
            {
                total += dataBits + code.NumberParityBits;
            }

            return total;
        }

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
        if (_coding.Scheme == OfdmFmFec.Ldpc)
        {
            coded = new byte[CodedBits(payloadBits.Length)];
            int read = 0;
            int written = 0;
            foreach ((LdpcCode code, int dataBits) in LdpcFrames(payloadBits.Length))
            {
                int frameCoded = dataBits + code.NumberParityBits;
                LdpcFrameFor(code, dataBits).Encode(
                    payloadBits.Slice(read, dataBits), coded.AsSpan(written, frameCoded));
                read += dataBits;
                written += frameCoded;
            }
        }
        else if (_code is null)
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
        GpInterleaver.Interleave<byte>(coded, spread, coded.Length);
        return spread;
    }

    /// <summary>Recovers payload bits from the soft metrics of the coded bits.</summary>
    /// <param name="llrs">One log-likelihood ratio per coded bit, positive meaning zero.</param>
    /// <param name="payloadBits">How many payload bits were coded.</param>
    /// <param name="llrTrueScale">What multiplies the incoming metrics into TRUE log-likelihood
    /// ratios, or 0 when the caller does not know. Only the LDPC path reads it - sum-product is
    /// not scale invariant - and the burst codec derives it per burst as mean channel power over
    /// measured noise, folding out its own demapper scaling, so a carrier's metric arrives at the
    /// decoder as distance over noise, which is what a likelihood is. Without it the block is
    /// normalised to <see cref="LdpcLlrFallbackMean"/>, which is right for the sparse
    /// constellations and measured 4 dB wrong for QAM-64.</param>
    public byte[] Decode(ReadOnlySpan<float> llrs, int payloadBits, double llrTrueScale = 0)
    {
        float[] ordered;
        if (_coding.Interleave && llrs.Length >= 3)
        {
            ordered = new float[llrs.Length];
            GpInterleaver.Deinterleave<float>(llrs, ordered, llrs.Length);
        }
        else
        {
            ordered = llrs.ToArray();
        }

        if (_coding.Scheme == OfdmFmFec.Ldpc)
        {
            // Rescaled to true likelihoods, because sum-product is the one decoder here that
            // reads the scale as meaning something - see the llrTrueScale parameter.
            if (llrTrueScale > 0)
            {
                float scale = (float)llrTrueScale;
                for (int i = 0; i < ordered.Length; i++)
                {
                    ordered[i] *= scale;
                }
            }
            else
            {
                double mean = 0;
                for (int i = 0; i < ordered.Length; i++)
                {
                    mean += Math.Abs(ordered[i]);
                }

                mean /= Math.Max(1, ordered.Length);
                if (mean > 1e-30)
                {
                    float scale = (float)(LdpcLlrFallbackMean / mean);
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        ordered[i] *= scale;
                    }
                }
            }

            var payload = new byte[payloadBits];
            int consumed = 0;
            int produced = 0;
            foreach ((LdpcCode code, int dataBits) in LdpcFrames(payloadBits))
            {
                int frameCoded = dataBits + code.NumberParityBits;
                LdpcFrameFor(code, dataBits).Decode(
                    ordered.AsSpan(consumed, frameCoded),
                    payload.AsSpan(produced, dataBits),
                    out _);
                consumed += frameCoded;
                produced += dataBits;
            }

            return payload;
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

    // The standard puncturing patterns for this mother code, labelled the way 802.11 labels
    // them: A is the 0o133 output and B the 0o171 output (M0LTE.Fec's T1 and T2). 2/3 and 3/4
    // are 802.11a's and 5/6 is 802.11n's. 7/8 is DVB-S's (EN 300 421 table 3), which lists the
    // 0o171 output first, so its two rows are exchanged here to keep the pattern on the
    // polynomial it was published for. Exchanging the rows of any of these gives another valid
    // code, and for 7/8 the two were measured against each other before this one was kept: see
    // the punctured-rates subsection of docs/dev/ofdm-fm/receiver-findings.md.
    private static (int[] A, int[] B) Puncture(int numerator, int denominator) =>
        (numerator, denominator) switch
        {
            (1, 2) => ([1], [1]),
            (2, 3) => ([1, 1], [1, 0]),
            (3, 4) => ([1, 0, 1], [1, 1, 0]),
            (5, 6) => ([1, 0, 1, 0, 1], [1, 1, 0, 1, 0]),
            (7, 8) => ([1, 1, 1, 1, 0, 1, 0], [1, 0, 0, 0, 1, 0, 1]),
            _ => throw new InvalidOperationException(
                $"rate {numerator}/{denominator} is not punctured here; use 1/2, 2/3, 3/4, 5/6 or 7/8"),
        };
}
