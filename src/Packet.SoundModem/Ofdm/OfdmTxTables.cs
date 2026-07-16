namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Fixed tables and index maths the datac TX path needs, ported verbatim from codec2 1.2.0
/// (git 310777b, LGPL-2.1, © David Rowe): the Gray QPSK constellation (<c>ofdm.c:76</c>), the
/// BPSK pilot sequence (<c>ofdm.c:88-92</c>), the per-mode unique-word bit words
/// (<c>ofdm_mode.c</c>), the unique-word symbol-placement indices (<c>ofdm_create:445-463</c>),
/// and the preamble/postamble LCG (<c>ofdm.c:2574</c>). See PROVENANCE.md and
/// docs/ofdm-design.md §3.5 / §3.8.
/// </summary>
internal static class OfdmTxTables
{
    /// <summary>Gray-coded QPSK constellation, indexed by <c>(b0&lt;&lt;1)|b1</c>
    /// (<c>ofdm.c:76</c>, <c>qpsk_mod</c> at <c>ofdm.c:106</c>).</summary>
    public static readonly Cf[] Qpsk =
    [
        new(1f, 0f),   // 00 → 0°
        new(0f, 1f),   // 01 → 90°
        new(0f, -1f),  // 10 → 270°
        new(-1f, 0f),  // 11 → 180°
    ];

    /// <summary>The 64 available BPSK pilot values (±1), Octave-compatible
    /// (<c>ofdm.c:88-92</c>). A mode uses the first <c>Nc+2</c>.</summary>
    public static readonly sbyte[] PilotValues =
    [
        -1, -1, 1,  1,  -1, -1, -1, 1,  -1, 1,  -1, 1,  1,  1,  1,  1,
        1,  1,  1,  -1, -1, 1,  -1, 1,  -1, 1,  1,  1,  1,  1,  1,  1,
        1,  1,  1,  -1, 1,  1,  1,  1,  1,  -1, -1, -1, -1, -1, -1, 1,
        -1, 1,  -1, 1,  -1, -1, 1,  -1, 1,  1,  1,  1,  -1, 1,  -1, 1,
    ];

    // The unique-word bit "seed" words (ofdm_mode.c). datac0/datac1 use the 16-bit word copied
    // to the front (datac0 zero-padded to 32, datac1 exact); datac3/4/13/14 use the 24-bit word
    // copied to the front AND again to the tail at [Nuwbits-24].
    private static readonly byte[] Uw16 = [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0];

    private static readonly byte[] Uw24 =
        [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0];

    /// <summary>Builds the resolved <c>tx_uw[Nuwbits]</c> bit array for a mode, reproducing the
    /// <c>memcpy</c> pattern in <c>ofdm_mode.c</c> exactly (front copy always; a second copy at
    /// <c>[Nuwbits-len]</c> for the 24-bit modes, which may overlap the front copy).</summary>
    public static byte[] ResolveTxUw(string name)
    {
        (byte[] seed, int nuwbits, bool tail) = name switch
        {
            "datac0" => (Uw16, 32, false),
            "datac1" => (Uw16, 16, false),
            "datac3" => (Uw24, 40, true),
            "datac4" => (Uw24, 32, true),
            "datac13" => (Uw24, 48, true),
            "datac14" => (Uw24, 32, true),
            _ => throw new ArgumentException($"unknown datac mode '{name}'", nameof(name)),
        };

        var uw = new byte[nuwbits];
        seed.CopyTo(uw, 0);
        if (tail)
        {
            seed.CopyTo(uw, nuwbits - seed.Length);
        }

        return uw;
    }

    /// <summary>Maps the resolved <c>tx_uw</c> bits to QPSK symbols
    /// (<c>ofdm_create:475-480</c>: <c>dibit[1]=tx_uw[2s]; dibit[0]=tx_uw[2s+1]</c>).</summary>
    public static Cf[] TxUwSymbols(ReadOnlySpan<byte> txUw)
    {
        var syms = new Cf[txUw.Length / 2];
        for (int s = 0; s < syms.Length; s++)
        {
            syms[s] = Qpsk[((txUw[2 * s] & 1) << 1) | (txUw[(2 * s) + 1] & 1)];
        }

        return syms;
    }

    /// <summary>Computes the unique-word symbol-placement indices, <c>uw_ind_sym[]</c>
    /// (<c>ofdm_create:445-463</c>). All the divisions are integer (C <c>floorf</c> of an
    /// integer quotient), and <c>bps</c> is 2 for every datac mode.</summary>
    public static int[] UwIndexSymbols(int nuwbits, int nc, int ns, int np)
    {
        const int bps = 2;
        int nuwsyms = nuwbits / bps;
        int nDataSymsPerFrame = (ns - 1) * nc;

        int uwStep = nc + 1;
        int lastSym = nuwsyms * uwStep / bps;
        if (lastSym >= np * nDataSymsPerFrame)
        {
            uwStep = nc - 1;
        }

        var indices = new int[nuwsyms];
        for (int i = 0; i < nuwsyms; i++)
        {
            indices[i] = (i + 1) * uwStep / bps;
        }

        return indices;
    }

    /// <summary>Generates the preamble/postamble payload bits from codec2's linear-congruential
    /// PRNG (<c>ofdm_rand_seed</c>, <c>ofdm.c:2574</c>; seed 2 = preamble, 3 = postamble). A bit
    /// is 1 when the 15-bit state exceeds 16384.</summary>
    public static byte[] LcgBits(int n, long seed)
    {
        var bits = new byte[n];
        for (int i = 0; i < n; i++)
        {
            seed = ((1103515245L * seed) + 12345) % 32768;
            bits[i] = (byte)(seed > 16384 ? 1 : 0);
        }

        return bits;
    }
}
