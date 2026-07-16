namespace Packet.SoundModem.Ofdm;

/// <summary>
/// QPSK soft-demapping: received symbols + per-symbol amplitudes → bit LLRs, a port of codec2's
/// <c>symbols_to_llrs</c> / <c>Demod2D</c> / <c>Somap</c> / <c>max_star0</c>
/// (codec2 1.2.0, git 310777b, <c>mpdecode_core.c:567-650</c>; LGPL-2.1 — see PROVENANCE.md).
/// The output convention matches <see cref="Fec.Ldpc.LdpcFrameCodec"/>: a positive LLR means bit 0.
/// </summary>
public static class OfdmSoftDemap
{
    // QPSK constellation for symbol likelihoods (mpdecode_core.c:32-33).
    private static readonly Cf[] SMatrix =
    [
        new(1.0f, 0.0f), new(0.0f, 1.0f), new(0.0f, -1.0f), new(-1.0f, 0.0f),
    ];

    // Linear-log-MAP constants (mpdecode_core.c:122-123).
    private const float Ajian = -0.24904163195436f;
    private const float Tjian = 2.50681740420944f;

    /// <summary>The linear-log-MAP <c>max*</c> operator (codec2 <c>max_star0</c>,
    /// <c>mpdecode_core.c:127-140</c>).</summary>
    private static float MaxStar0(float delta1, float delta2)
    {
        float diff = delta2 - delta1;
        if (diff > Tjian)
        {
            return delta2;
        }

        if (diff < -Tjian)
        {
            return delta1;
        }

        if (diff > 0)
        {
            return delta2 + (Ajian * (diff - Tjian));
        }

        return delta1 - (Ajian * (diff + Tjian));
    }

    /// <summary>Converts <paramref name="nsyms"/> QPSK symbols (with real fading
    /// <paramref name="amps"/>) to <c>2·nsyms</c> LLRs. <paramref name="llr"/> ordering is
    /// <c>llr[2i+0] ↔ bit1</c> (constellation-index MSB), <c>llr[2i+1] ↔ bit0</c> — the order the
    /// LDPC decoder expects. Positive ⇒ bit&#160;0.</summary>
    public static void SymbolsToLlrs(
        Span<float> llr, ReadOnlySpan<Cf> syms, ReadOnlySpan<float> amps,
        float esNo, float meanAmp, int nsyms)
    {
        const int m = 4;    // QPSK constellation size
        const int bps = 2;

        Span<float> symLik = nsyms * m <= 512 ? stackalloc float[nsyms * m] : new float[nsyms * m];

        // Demod2D (mpdecode_core.c:567-591)
        for (int i = 0; i < nsyms; i++)
        {
            for (int j = 0; j < m; j++)
            {
                float tempsr = amps[i] * SMatrix[j].Re / meanAmp;
                float tempsi = amps[i] * SMatrix[j].Im / meanAmp;
                float er = (syms[i].Re / meanAmp) - tempsr;
                float ei = (syms[i].Im / meanAmp) - tempsi;
                symLik[(i * m) + j] = -esNo * ((er * er) + (ei * ei));
            }
        }

        // Somap (mpdecode_core.c:593-634) with bps=2, then llr = -bit_likelihood (636-650).
        Span<float> num = stackalloc float[bps];
        Span<float> den = stackalloc float[bps];
        for (int n = 0; n < nsyms; n++)
        {
            num[0] = num[1] = -1_000_000.0f;
            den[0] = den[1] = -1_000_000.0f;

            for (int i = 0; i < m; i++)
            {
                float metric = symLik[(n * m) + i];
                int mask = 1 << (bps - 1);
                for (int k = 0; k < bps; k++)
                {
                    if ((mask & i) != 0)
                    {
                        num[k] = MaxStar0(num[k], metric);
                    }
                    else
                    {
                        den[k] = MaxStar0(den[k], metric);
                    }

                    mask >>= 1;
                }
            }

            for (int k = 0; k < bps; k++)
            {
                llr[(bps * n) + k] = -(num[k] - den[k]);
            }
        }
    }
}
