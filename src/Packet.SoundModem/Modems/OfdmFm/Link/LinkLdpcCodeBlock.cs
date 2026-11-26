namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The encoder plan for one block of the link's LDPC family: <c>k</c> information bits sent as <c>e</c>
/// coded bits, with the lifting size, the shortened positions, the transmit order (wrapping round past the
/// end of the mother code into repetition) and the truncated parity-check structure that the prototype's
/// block gives. Bits are one per byte, 0 or 1.
/// </summary>
/// <remarks>
/// Building a plan allocates (about 4 bytes per coded bit) and is cheap; <see cref="PayloadCode"/> keeps a
/// bounded set of them. The parity-check structure, which depends only on lifting size and rows kept, is
/// shared between plans. Encoding allocates nothing when the caller supplies the scratch buffer
/// (<see cref="ScratchLength"/>); the overload without one uses a per-thread buffer that grows once.
/// </remarks>
public sealed class LinkLdpcCodeBlock
{
    [ThreadStatic]
    private static byte[]? _threadScratch;

    private readonly LinkLdpcStructure _structure;
    private readonly int[] _shortened;
    private readonly int[] _transmitOrder;

    /// <summary>Builds the plan for a block of <paramref name="infoBits"/> sent as <paramref name="codedBits"/>.</summary>
    public LinkLdpcCodeBlock(int infoBits, int codedBits)
        : this(infoBits, codedBits, structured: true)
    {
    }

    internal LinkLdpcCodeBlock(int infoBits, int codedBits, bool structured)
    {
        if (infoBits <= 0 || codedBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(infoBits), "a block needs information bits and coded bits");
        }

        int kb = LinkLdpcTables.Kb;
        int punctured = LinkLdpcTables.Punctured;
        int nb = LinkLdpcTables.Nb;
        int z = LinkLdpcTables.SelectLifting(infoBits);
        InfoBits = infoBits;
        CodedBits = codedBits;
        LiftingSize = z;
        K = kb * z;

        _shortened = ShortenedPositions(infoBits, z);
        var isShort = new bool[K];
        foreach (int p in _shortened)
        {
            isShort[p] = true;
        }

        // Candidates: the sent information bits (not the punctured columns, not shortened), then all parity.
        int sentInfo = 0;
        for (int i = punctured * z; i < K; i++)
        {
            if (!isShort[i])
            {
                sentInfo++;
            }
        }

        var candidates = new int[sentInfo + (nb - kb) * z];
        int ci = 0;
        for (int i = punctured * z; i < K; i++)
        {
            if (!isShort[i])
            {
                candidates[ci++] = i;
            }
        }

        for (int i = K; i < nb * z; i++)
        {
            candidates[ci++] = i;
        }

        _transmitOrder = new int[codedBits];
        int max = 0;
        for (int j = 0, c = 0; j < codedBits; j++)
        {
            int pos = candidates[c];
            _transmitOrder[j] = pos;
            max = Math.Max(max, pos);
            if (++c == candidates.Length)
            {
                c = 0;
            }
        }

        int columnsNeeded = (max + 1 + z - 1) / z;
        int rows = Math.Min(LinkLdpcTables.Mb, Math.Max(LinkLdpcTables.CoreRows, columnsNeeded - kb));
        _structure = LinkLdpcStructure.Get(z, rows, structured);
    }

    /// <summary>Information bits the block takes.</summary>
    public int InfoBits { get; }

    /// <summary>Coded bits the block gives.</summary>
    public int CodedBits { get; }

    /// <summary>The lifting size Z.</summary>
    public int LiftingSize { get; }

    /// <summary>Information positions of the mother code at this lifting size (24 columns of Z).</summary>
    public int K { get; }

    /// <summary>Check rows the encoder keeps: the core, or as many as the last coded parity bit needs.</summary>
    public int CheckRows => _structure.Rows;

    /// <summary>Columns of the kept code: the information columns plus one per check row.</summary>
    public int Columns => _structure.Columns;

    /// <summary>Length of the full (kept) codeword, in bits.</summary>
    public int CodewordLength => _structure.CodewordLength;

    /// <summary>Rows of the high-rate core as the encoder splits it (the rest are single-parity extension rows).</summary>
    public int CoreSize => _structure.CoreSize;

    /// <summary>How the core is solved: "sum" (the dual-diagonal peel), "dense" (a stored GF(2) inverse) or "none".</summary>
    public string EncoderKind => _structure.EncoderKind;

    /// <summary>The known-zero information positions, ascending.</summary>
    public ReadOnlySpan<int> Shortened => _shortened;

    /// <summary>The codeword position of each coded bit, in the order sent.</summary>
    public ReadOnlySpan<int> TransmitOrder => _transmitOrder;

    /// <summary>Bytes of scratch <see cref="Encode(ReadOnlySpan{byte}, Span{byte}, Span{byte})"/> needs.</summary>
    public int ScratchLength => CodewordLength + CheckRows * LiftingSize;

    /// <summary>Approximate bytes this plan holds itself (not the shared structure).</summary>
    internal long PlanBytes => 4L * (_shortened.Length + _transmitOrder.Length) + 96;

    internal long StructureBytes => _structure.Bytes;

    /// <summary>
    /// The known-zero positions for <paramref name="infoBits"/> at lifting size <paramref name="z"/>: spread over the
    /// sent information columns (the last bits of each), then if need be over the punctured ones. Ascending.
    /// </summary>
    public static int[] ShortenedPositions(int infoBits, int z)
    {
        int kb = LinkLdpcTables.Kb;
        int punctured = LinkLdpcTables.Punctured;
        int nShort = kb * z - infoBits;
        int sent = kb - punctured;
        int cap = sent * z;
        int main = Math.Min(nShort, cap);
        var per = new int[kb];
        int q = main / sent;
        int rem = main % sent;
        for (int c = punctured; c < kb; c++)
        {
            per[c] = q;
        }

        for (int c = punctured; c < punctured + rem; c++)
        {
            per[c]++;
        }

        int extra = nShort - main;
        if (extra > 0)
        {
            int q2 = extra / punctured;
            int rem2 = extra % punctured;
            for (int c = 0; c < punctured; c++)
            {
                per[c] = q2;
            }

            for (int c = 0; c < rem2; c++)
            {
                per[c]++;
            }
        }

        var result = new List<int>(nShort);
        for (int c = 0; c < kb; c++)
        {
            for (int i = z - per[c]; i < z; i++)
            {
                result.Add(c * z + i);
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Encodes <paramref name="info"/> (<see cref="InfoBits"/> bits, one per byte) into <paramref name="coded"/>
    /// (<see cref="CodedBits"/> bits), using a per-thread scratch buffer.
    /// </summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded)
    {
        int need = ScratchLength;
        byte[] scratch = _threadScratch ??= new byte[need];
        if (scratch.Length < need)
        {
            scratch = _threadScratch = new byte[need];
        }

        Encode(info, coded, scratch);
    }

    /// <summary>As <see cref="Encode(ReadOnlySpan{byte}, Span{byte})"/> with caller-supplied scratch of at least <see cref="ScratchLength"/> bytes.</summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded, Span<byte> scratch)
    {
        if (coded.Length != CodedBits)
        {
            throw new ArgumentException($"expected room for {CodedBits} coded bits", nameof(coded));
        }

        Span<byte> x = scratch[..CodewordLength];
        EncodeCodeword(info, x, scratch[CodewordLength..]);
        for (int j = 0; j < _transmitOrder.Length; j++)
        {
            coded[j] = x[_transmitOrder[j]];
        }
    }

    /// <summary>
    /// Writes the full codeword (<see cref="CodewordLength"/> bits: information with zeros at the shortened
    /// positions, then parity) into <paramref name="codeword"/>. <paramref name="work"/> needs
    /// <c>CheckRows * LiftingSize</c> bytes.
    /// </summary>
    public void EncodeCodeword(ReadOnlySpan<byte> info, Span<byte> codeword, Span<byte> work)
    {
        if (info.Length != InfoBits)
        {
            throw new ArgumentException($"expected {InfoBits} information bits", nameof(info));
        }

        Span<byte> x = codeword[..CodewordLength];
        x.Clear();

        // Information bits fill the non-shortened positions of the first K, in order.
        int next = 0;
        int n = 0;
        for (int i = 0; i < K; i++)
        {
            if (next < _shortened.Length && _shortened[next] == i)
            {
                next++;
            }
            else
            {
                x[i] = (byte)(info[n++] & 1);
            }
        }

        _structure.Complete(x, work[..(CheckRows * LiftingSize)]);
    }

    /// <summary>True when every check row of the kept code is satisfied by the full codeword (<see cref="CodewordLength"/> bits).</summary>
    public bool SyndromeIsZero(ReadOnlySpan<byte> codeword) => _structure.SyndromeIsZero(codeword);

    /// <summary>Floats of scratch the decoders need.</summary>
    public int DecodeScratchLength => _structure.DecodeScratchLength;

    /// <summary>
    /// Decodes <paramref name="soft"/> (one value per coded bit as sent, positive meaning 0) into
    /// <paramref name="info"/> (<see cref="InfoBits"/> bits, one per byte), as the prototype's <c>FittedCode.decode</c>:
    /// the values are added into their codeword positions in transmit order (so a repeated bit accumulates), the
    /// shortened positions are known zeros, and up to 30 layered normalised min-sum iterations run in 32-bit floats.
    /// Returns whether every check was satisfied; <paramref name="iterations"/> is 0 when the input already
    /// satisfied them and 30 when it never did. <paramref name="scratch"/> needs <see cref="DecodeScratchLength"/> floats.
    /// </summary>
    public bool Decode(ReadOnlySpan<float> soft, Span<byte> info, Span<float> scratch, out int iterations)
    {
        if (info.Length != InfoBits)
        {
            throw new ArgumentException($"expected room for {InfoBits} information bits", nameof(info));
        }

        bool ok = Run(soft, scratch, out iterations);
        int next = 0;
        int n = 0;
        for (int i = 0; i < K; i++)
        {
            if (next < _shortened.Length && _shortened[next] == i)
            {
                next++;
            }
            else
            {
                info[n++] = scratch[i] < 0 ? (byte)1 : (byte)0;
            }
        }

        return ok;
    }

    /// <summary>As <see cref="Decode(ReadOnlySpan{float}, Span{byte}, Span{float}, out int)"/> but writes the hard decision of every bit of the full codeword (<see cref="CodewordLength"/> bits) instead.</summary>
    public bool DecodeCodeword(ReadOnlySpan<float> soft, Span<byte> codeword, Span<float> scratch, out int iterations)
    {
        if (codeword.Length != CodewordLength)
        {
            throw new ArgumentException($"expected room for {CodewordLength} bits", nameof(codeword));
        }

        bool ok = Run(soft, scratch, out iterations);
        for (int i = 0; i < codeword.Length; i++)
        {
            codeword[i] = scratch[i] < 0 ? (byte)1 : (byte)0;
        }

        return ok;
    }

    private bool Run(ReadOnlySpan<float> soft, Span<float> scratch, out int iterations)
    {
        if (soft.Length != CodedBits)
        {
            throw new ArgumentException($"expected {CodedBits} soft values", nameof(soft));
        }

        if (scratch.Length < _structure.DecodeScratchLength)
        {
            throw new ArgumentException($"expected {_structure.DecodeScratchLength} floats of scratch", nameof(scratch));
        }

        Span<float> l = scratch[..CodewordLength];
        l.Clear();
        foreach (int p in _shortened)
        {
            l[p] = LinkLdpcStructure.KnownZero;
        }

        int[] order = _transmitOrder;
        for (int j = 0; j < order.Length; j++)
        {
            l[order[j]] += soft[j];
        }

        return _structure.Decode(scratch, out iterations);
    }
}
