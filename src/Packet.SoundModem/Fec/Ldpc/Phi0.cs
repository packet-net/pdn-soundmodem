namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>
/// Exact port of codec2's <c>phi0.c</c> — the tabulated Gallager φ function
/// φ(x) = −log(tanh(x/2)) the sum-product decoder uses (codec2 includes <c>phi0.h</c> at
/// <c>mpdecode_core.c:17-19</c>, so this table version, not the inline log/exp, is
/// authoritative). Bit-exactness hinges on <see cref="Si16"/> doing the multiply in
/// <b>float</b> then truncating toward zero, so the <c>&gt;&gt;15</c>/<c>&gt;&gt;12</c> shifts
/// land on the same case boundaries as the C. Transcribed verbatim (codec2 1.2.0, git 310777b,
/// LGPL-2.1 — see PROVENANCE.md) and pinned by a golden boundary test.
/// </summary>
internal static class Phi0
{
    // C: #define SI16(f) ((int32_t)(f * (1 << 16)))  — float multiply, truncate toward zero.
    private static int Si16(float f) => (int)(f * 65536f);

    // x >= SI16(5.0f): i = 19 - (x >> 15), cases 0..9  (phi0.c:22-41)
    private static readonly float[] Hi =
    {
        0.000116589f, 0.000192223f, 0.000316923f, 0.000522517f, 0.000861485f,
        0.001420349f, 0.002341760f, 0.003860913f, 0.006365583f, 0.010495133f,
    };

    // x >= SI16(1.0f): i = 79 - (x >> 12), cases 0..63  (phi0.c:47-174)
    private static readonly float[] Mid =
    {
        0.013903889f, 0.014800644f, 0.015755242f, 0.016771414f, 0.017853133f, 0.019004629f,
        0.020230403f, 0.021535250f, 0.022924272f, 0.024402903f, 0.025976926f, 0.027652501f,
        0.029436184f, 0.031334956f, 0.033356250f, 0.035507982f, 0.037798579f, 0.040237016f,
        0.042832850f, 0.045596260f, 0.048538086f, 0.051669874f, 0.055003924f, 0.058553339f,
        0.062332076f, 0.066355011f, 0.070637993f, 0.075197917f, 0.080052790f, 0.085221814f,
        0.090725463f, 0.096585578f, 0.102825462f, 0.109469985f, 0.116545700f, 0.124080967f,
        0.132106091f, 0.140653466f, 0.149757747f, 0.159456024f, 0.169788027f, 0.180796343f,
        0.192526667f, 0.205028078f, 0.218353351f, 0.232559308f, 0.247707218f, 0.263863255f,
        0.281099022f, 0.299492155f, 0.319127030f, 0.340095582f, 0.362498271f, 0.386445235f,
        0.412057648f, 0.439469363f, 0.468828902f, 0.500301872f, 0.534073947f, 0.570354566f,
        0.609381573f, 0.651427083f, 0.696805010f, 0.745880827f,
    };

    /// <summary>φ0(<paramref name="xf"/>); callers always pass a non-negative argument.</summary>
    public static float Compute(float xf)
    {
        int x = Si16(xf);
        if (x >= Si16(10.0f))
        {
            return 0.0f;
        }

        if (x >= Si16(5.0f))
        {
            int i = 19 - (x >> 15);
            return i is >= 0 and < 10 ? Hi[i] : 10.0f;
        }

        if (x >= Si16(1.0f))
        {
            int i = 79 - (x >> 12);
            return i is >= 0 and < 64 ? Mid[i] : 10.0f;
        }

        // Low tree: nested-if binary search on SI16 thresholds (phi0.c:177-284), verbatim.
        if (x > Si16(0.007812f))
        {
            if (x > Si16(0.088388f))
            {
                if (x > Si16(0.250000f))
                {
                    if (x > Si16(0.500000f))
                    {
                        return x > Si16(0.707107f) ? 0.922449644f : 1.241248638f;
                    }

                    return x > Si16(0.353553f) ? 1.573515241f : 1.912825912f;
                }

                if (x > Si16(0.125000f))
                {
                    return x > Si16(0.176777f) ? 2.255740095f : 2.600476919f;
                }

                return 2.946130351f;
            }

            if (x > Si16(0.022097f))
            {
                if (x > Si16(0.044194f))
                {
                    return x > Si16(0.062500f) ? 3.292243417f : 3.638586634f;
                }

                return x > Si16(0.031250f) ? 3.985045009f : 4.331560985f;
            }

            if (x > Si16(0.011049f))
            {
                return x > Si16(0.015625f) ? 4.678105767f : 5.024664952f;
            }

            return 5.371231340f;
        }

        if (x > Si16(0.000691f))
        {
            if (x > Si16(0.001953f))
            {
                if (x > Si16(0.003906f))
                {
                    return x > Si16(0.005524f) ? 5.717801329f : 6.064373119f;
                }

                return x > Si16(0.002762f) ? 6.410945809f : 6.757518949f;
            }

            if (x > Si16(0.000977f))
            {
                return x > Si16(0.001381f) ? 7.104092314f : 7.450665792f;
            }

            return 7.797239326f;
        }

        if (x > Si16(0.000173f))
        {
            if (x > Si16(0.000345f))
            {
                return x > Si16(0.000488f) ? 8.143812888f : 8.490386464f;
            }

            return x > Si16(0.000244f) ? 8.836960047f : 9.183533634f;
        }

        if (x > Si16(0.000086f))
        {
            return x > Si16(0.000122f) ? 9.530107222f : 9.876680812f;
        }

        return 10.000000000f;
    }
}
