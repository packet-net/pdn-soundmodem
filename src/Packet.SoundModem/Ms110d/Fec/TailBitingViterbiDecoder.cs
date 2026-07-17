namespace Packet.SoundModem.Ms110d.Fec;

/// <summary>
/// Soft-decision Viterbi decoder for the Appendix D tail-biting mother codes
/// (design §3.3). LLR convention: <b>positive ⇒ bit 0</b>, matching
/// <see cref="Packet.SoundModem.Fec.Ldpc.LdpcDecoder"/>. Branch metrics are max-log
/// (±LLR sums), so punctured positions carry LLR 0 and contribute nothing — depuncturing
/// needs no special-casing here.
/// </summary>
/// <remarks>
/// Tail-biting is handled by circular wrap-extension: the trellis runs over
/// [last W steps ⊕ full block ⊕ first W steps] with equal initial state metrics, traceback
/// starts from the best end state, and only the middle N decisions are kept.
/// W = min(6·K, N). Decision memory is bit-packed per step (1 ulong for K=7's 64 states,
/// 4 for K=9's 256).
/// </remarks>
public sealed class TailBitingViterbiDecoder
{
    private readonly ConvolutionalCode _code;
    private readonly byte[] _outputs; // per K-bit register value v: (T1 << 1) | T2
    private float[] _metricA = [];
    private float[] _metricB = [];
    private ulong[] _decisions = [];

    /// <summary>Creates a decoder for <paramref name="code"/> (K7 or K9).</summary>
    public TailBitingViterbiDecoder(ConvolutionalCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        _code = code;
        _outputs = new byte[1 << code.K];
        for (uint v = 0; v < _outputs.Length; v++)
        {
            int t1 = System.Numerics.BitOperations.PopCount(v & code.PolyT1) & 1;
            int t2 = System.Numerics.BitOperations.PopCount(v & code.PolyT2) & 1;
            _outputs[v] = (byte)((t1 << 1) | t2);
        }
    }

    /// <summary>Decodes a tail-biting block. <paramref name="motherLlrs"/> is the full
    /// rate-1/2 lattice (2 LLRs per decoded bit, erasures = 0 where punctured);
    /// <paramref name="info"/> receives motherLlrs.Length / 2 hard bits.</summary>
    public void Decode(ReadOnlySpan<float> motherLlrs, Span<byte> info)
    {
        int n = info.Length;
        if (motherLlrs.Length != 2 * n)
        {
            throw new ArgumentException("motherLlrs.Length must be 2 * info.Length", nameof(motherLlrs));
        }

        int states = _code.States;
        int w = Math.Min(6 * _code.K, n);
        int steps = n + 2 * w;
        int words = (states + 63) >> 6;

        if (_metricA.Length < states)
        {
            _metricA = new float[states];
            _metricB = new float[states];
        }

        if (_decisions.Length < steps * words)
        {
            _decisions = new ulong[steps * words];
        }

        Array.Clear(_metricA, 0, states);
        float[] cur = _metricA;
        float[] next = _metricB;
        Span<float> bm = stackalloc float[4];
        int half = states >> 1;

        for (int e = 0; e < steps; e++)
        {
            int t = e - w;
            if (t < 0)
            {
                t += n;
            }
            else if (t >= n)
            {
                t -= n;
            }

            float l1 = motherLlrs[2 * t];
            float l2 = motherLlrs[(2 * t) + 1];
            bm[0b00] = l1 + l2;
            bm[0b01] = l1 - l2;
            bm[0b10] = -l1 + l2;
            bm[0b11] = -l1 - l2;

            int decBase = e * words;
            for (int wd = 0; wd < words; wd++)
            {
                _decisions[decBase + wd] = 0;
            }

            for (int s = 0; s < states; s++)
            {
                // Register value v = (prevState << 1) | inputBit; new state s = v & (states−1).
                // The two candidates differ in v's dropped MSB: v0 = s, v1 = s | states.
                int v1 = s | states;
                float m0 = cur[s >> 1] + bm[_outputs[s]];
                float m1 = cur[(s >> 1) + half] + bm[_outputs[v1]];
                if (m1 > m0)
                {
                    next[s] = m1;
                    _decisions[decBase + (s >> 6)] |= 1UL << (s & 63);
                }
                else
                {
                    next[s] = m0;
                }
            }

            (cur, next) = (next, cur);

            if ((e & 1023) == 1023)
            {
                float max = cur[0];
                for (int s = 1; s < states; s++)
                {
                    if (cur[s] > max)
                    {
                        max = cur[s];
                    }
                }

                for (int s = 0; s < states; s++)
                {
                    cur[s] -= max;
                }
            }
        }

        int best = 0;
        for (int s = 1; s < states; s++)
        {
            if (cur[s] > cur[best])
            {
                best = s;
            }
        }

        int state = best;
        for (int e = steps - 1; e >= 0; e--)
        {
            if (e >= w && e < w + n)
            {
                // Input bit = LSB of the post-step state. The encoder's tail-biting wrap
                // makes output pair o carry info[(o + K − 1) mod N] as its newest bit
                // (TailBitingEncoder preloads K−1 bits), so map the step back accordingly.
                info[(e - w + _code.K - 1) % n] = (byte)(state & 1);
            }

            ulong d = (_decisions[(e * words) + (state >> 6)] >> (state & 63)) & 1;
            int v = state | ((int)d << (_code.K - 1));
            state = v >> 1;
        }
    }
}
