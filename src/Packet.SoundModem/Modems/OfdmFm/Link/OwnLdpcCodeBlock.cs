using System.Collections.Concurrent;
using System.Numerics;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The encoder plan for one block of the adaptive payload code: <c>k</c> information bits sent as
/// <c>e</c> coded bits, with the lifting size, the shortened positions, the transmit order (wrapping
/// round past the end of the mother code into repetition) and the truncated parity-check structure
/// that the prototype's block gives. Bits are one per byte, 0 or 1.
/// </summary>
/// <remarks>
/// Building a plan allocates and may take a while; <see cref="Get"/> caches plans. Encoding allocates
/// nothing when the caller supplies the scratch buffer (<see cref="ScratchLength"/>); the overload
/// without one uses a per-thread buffer that grows once.
/// </remarks>
public sealed class OwnLdpcCodeBlock
{
    private const int KindSum = 0;
    private const int KindRow = 1;
    private const int KindDense = 2;

    private static readonly ConcurrentDictionary<(int K, int E), Lazy<OwnLdpcCodeBlock>> Cache = new();

    [ThreadStatic]
    private static byte[]? _threadScratch;

    private readonly int _kb;
    private readonly int _rows;
    private readonly int[] _rowStart;
    private readonly int[] _edgeColumn;
    private readonly int[] _edgeShift;
    private readonly int[] _infoPositions;
    private readonly int[] _shortened;
    private readonly int[] _transmitOrder;
    private readonly Step[] _steps;

    /// <summary>Builds the plan for a block of <paramref name="infoBits"/> sent as <paramref name="codedBits"/>.</summary>
    public OwnLdpcCodeBlock(int infoBits, int codedBits)
        : this(infoBits, codedBits, structured: true)
    {
    }

    internal OwnLdpcCodeBlock(int infoBits, int codedBits, bool structured)
    {
        if (infoBits <= 0 || codedBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(infoBits), "a block needs information bits and coded bits");
        }

        int kb = OwnLdpcTables.Kb;
        int punctured = OwnLdpcTables.Punctured;
        int nb = OwnLdpcTables.Nb;
        int z = OwnLdpcTables.SelectLifting(infoBits);
        InfoBits = infoBits;
        CodedBits = codedBits;
        LiftingSize = z;
        _kb = kb;
        K = kb * z;

        _shortened = ShortenedPositions(infoBits, z);
        var isShort = new bool[K];
        foreach (int p in _shortened)
        {
            isShort[p] = true;
        }

        _infoPositions = new int[K - _shortened.Length];
        int n = 0;
        for (int i = 0; i < K; i++)
        {
            if (!isShort[i])
            {
                _infoPositions[n++] = i;
            }
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
        _rows = Math.Min(OwnLdpcTables.Mb, Math.Max(OwnLdpcTables.CoreRows, columnsNeeded - kb));
        Columns = kb + _rows;
        CodewordLength = Columns * z;

        // The check rows kept, as edge lists.
        var start = new List<int> { 0 };
        var cols = new List<int>();
        var shifts = new List<int>();
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                int s = OwnLdpcTables.Shift(z, r, c);
                if (s >= 0)
                {
                    cols.Add(c);
                    shifts.Add(s);
                }
            }

            start.Add(cols.Count);
        }

        _rowStart = [.. start];
        _edgeColumn = [.. cols];
        _edgeShift = [.. shifts];
        _steps = MakeSteps(structured);
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
    public int CheckRows => _rows;

    /// <summary>Columns of the kept code: the information columns plus one per check row.</summary>
    public int Columns { get; }

    /// <summary>Length of the full (kept) codeword, in bits.</summary>
    public int CodewordLength { get; }

    /// <summary>Rows of the high-rate core as the encoder splits it (the rest are single-parity extension rows).</summary>
    public int CoreSize { get; private set; }

    /// <summary>How the core is solved: "sum" (the dual-diagonal peel), "dense" (a stored GF(2) inverse) or "none".</summary>
    public string EncoderKind => CoreSize == 0 ? "none" : _steps[0].Kind == KindSum ? "sum" : "dense";

    /// <summary>The known-zero information positions, ascending.</summary>
    public ReadOnlySpan<int> Shortened => _shortened;

    /// <summary>The codeword position of each coded bit, in the order sent.</summary>
    public ReadOnlySpan<int> TransmitOrder => _transmitOrder;

    /// <summary>Bytes of scratch <see cref="Encode(ReadOnlySpan{byte}, Span{byte}, Span{byte})"/> needs.</summary>
    public int ScratchLength => CodewordLength + _rows * LiftingSize;

    /// <summary>The cached plan for a block shape; built on first use.</summary>
    public static OwnLdpcCodeBlock Get(int infoBits, int codedBits) =>
        Cache.GetOrAdd((infoBits, codedBits), key => new Lazy<OwnLdpcCodeBlock>(() => new OwnLdpcCodeBlock(key.K, key.E))).Value;

    /// <summary>
    /// The known-zero positions for <paramref name="infoBits"/> at lifting size <paramref name="z"/>: spread over the
    /// sent information columns (the last bits of each), then if need be over the punctured ones. Ascending.
    /// </summary>
    public static int[] ShortenedPositions(int infoBits, int z)
    {
        int kb = OwnLdpcTables.Kb;
        int punctured = OwnLdpcTables.Punctured;
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

        int z = LiftingSize;
        Span<byte> x = codeword[..CodewordLength];
        Span<byte> lam = work[..(_rows * z)];
        x.Clear();
        lam.Clear();
        for (int i = 0; i < _infoPositions.Length; i++)
        {
            x[_infoPositions[i]] = (byte)(info[i] & 1);
        }

        // lam[r] = contribution of the information bits to check row r.
        for (int r = 0; r < _rows; r++)
        {
            Span<byte> row = lam.Slice(r * z, z);
            for (int e = _rowStart[r]; e < _rowStart[r + 1]; e++)
            {
                int c = _edgeColumn[e];
                if (c < _kb)
                {
                    XorShifted(row, x.Slice(c * z, z), _edgeShift[e]);
                }
            }
        }

        Span<byte> v = stackalloc byte[z];
        foreach (Step step in _steps)
        {
            switch (step.Kind)
            {
                case KindSum:
                    {
                        v.Clear();
                        for (int r = 0; r < step.Rows; r++)
                        {
                            XorShifted(v, lam.Slice(r * z, z), 0);
                        }

                        StoreShifted(x.Slice(step.Column * z, z), v, step.Shift);
                        break;
                    }

                case KindRow:
                    {
                        lam.Slice(step.Row * z, z).CopyTo(v);
                        for (int e = _rowStart[step.Row]; e < _rowStart[step.Row + 1]; e++)
                        {
                            int c = _edgeColumn[e];
                            if (c >= _kb && c != step.Column)
                            {
                                XorShifted(v, x.Slice(c * z, z), _edgeShift[e]);
                            }
                        }

                        StoreShifted(x.Slice(step.Column * z, z), v, step.Shift);
                        break;
                    }

                default:
                    SolveDense(step, lam, x);
                    break;
            }
        }
    }

    /// <summary>True when every check row of the kept code is satisfied by the full codeword (<see cref="CodewordLength"/> bits).</summary>
    public bool SyndromeIsZero(ReadOnlySpan<byte> codeword)
    {
        if (codeword.Length != CodewordLength)
        {
            throw new ArgumentException($"expected {CodewordLength} bits", nameof(codeword));
        }

        int z = LiftingSize;
        for (int r = 0; r < _rows; r++)
        {
            for (int i = 0; i < z; i++)
            {
                int sum = 0;
                for (int e = _rowStart[r]; e < _rowStart[r + 1]; e++)
                {
                    int j = i + _edgeShift[e];
                    if (j >= z)
                    {
                        j -= z;
                    }

                    sum ^= codeword[_edgeColumn[e] * z + j] & 1;
                }

                if (sum != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // dst[i] ^= src[(i + s) % Z]
    private static void XorShifted(Span<byte> dst, ReadOnlySpan<byte> src, int s)
    {
        int z = dst.Length;
        int head = z - s;
        for (int i = 0; i < head; i++)
        {
            dst[i] ^= src[i + s];
        }

        for (int i = head; i < z; i++)
        {
            dst[i] ^= src[i - head];
        }
    }

    // dst[(i + s) % Z] = v[i]
    private static void StoreShifted(Span<byte> dst, ReadOnlySpan<byte> v, int s)
    {
        int z = v.Length;
        int head = z - s;
        for (int i = 0; i < head; i++)
        {
            dst[i + s] = v[i];
        }

        for (int i = head; i < z; i++)
        {
            dst[i - head] = v[i];
        }
    }

    private void SolveDense(Step step, ReadOnlySpan<byte> lam, Span<byte> x)
    {
        int z = LiftingSize;
        int n = step.Rows * z;
        int words = (n + 63) / 64;
        Span<ulong> packed = words <= 256 ? stackalloc ulong[words] : new ulong[words];
        packed.Clear();
        for (int i = 0; i < n; i++)
        {
            if (lam[i] != 0)
            {
                packed[i >> 6] |= 1UL << (i & 63);
            }
        }

        ulong[] inv = step.Inverse!;
        int firstColumn = _kb;
        for (int a = 0; a < n; a++)
        {
            ulong acc = 0;
            int off = a * words;
            for (int w = 0; w < words; w++)
            {
                acc ^= inv[off + w] & packed[w];
            }

            x[firstColumn * z + a] = (byte)(BitOperations.PopCount(acc) & 1);
        }
    }

    // The prototype's QCLDPC._make_encoder_plan.
    private Step[] MakeSteps(bool structured)
    {
        int kb = _kb;
        int mb = _rows;
        int z = LiftingSize;

        // Entry lookup (row, column) -> shift or -1, of the kept code.
        int Base(int r, int c) => OwnLdpcTables.Shift(z, r, c);

        int g = mb;
        for (int cand = 0; cand <= mb; cand++)
        {
            bool ok = true;
            for (int i = cand; i < mb; i++)
            {
                bool bad = Base(i, kb + i) < 0;
                for (int c = kb + i + 1; !bad && c < Columns; c++)
                {
                    bad = Base(i, c) >= 0;
                }

                if (bad)
                {
                    ok = false;
                    break;
                }
            }

            if (ok && cand > 0)
            {
                for (int r = 0; ok && r < cand; r++)
                {
                    for (int c = kb + cand; c < Columns; c++)
                    {
                        if (Base(r, c) >= 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
            }

            if (ok)
            {
                g = cand;
                break;
            }
        }

        var steps = new List<Step>();
        Step[]? core = structured && g > 0 ? PeelCore(g) : null;
        if (g > 0)
        {
            if (core is null)
            {
                core = [new Step(KindDense, g, 0, kb, 0, DenseCoreInverse(g))];
            }

            steps.AddRange(core);
        }

        for (int i = g; i < mb; i++)
        {
            steps.Add(new Step(KindRow, 0, i, kb + i, Base(i, kb + i), null));
        }

        CoreSize = g;
        return [.. steps];
    }

    // The prototype's QCLDPC._peel_core, for core rows 0..g-1 and columns kb..kb+g-1; null when it does not apply.
    private Step[]? PeelCore(int g)
    {
        int kb = _kb;
        int z = LiftingSize;
        int Base(int r, int c) => OwnLdpcTables.Shift(z, r, c);

        // Sum over the core rows of each core column, as a GF(2) polynomial in the cyclic shift.
        int nonzeroColumns = 0;
        int c0 = -1;
        int c0Shift = -1;
        for (int j = 0; j < g; j++)
        {
            var parity = new Dictionary<int, int>();
            for (int r = 0; r < g; r++)
            {
                int s = Base(r, kb + j);
                if (s >= 0)
                {
                    parity[s] = parity.GetValueOrDefault(s) ^ 1;
                }
            }

            var odd = parity.Where(p => p.Value != 0).Select(p => p.Key).ToList();
            if (odd.Count > 0)
            {
                nonzeroColumns++;
                c0 = kb + j;
                c0Shift = odd[0];
                if (odd.Count != 1)
                {
                    return null;
                }
            }
        }

        if (nonzeroColumns != 1)
        {
            return null;
        }

        var steps = new List<Step> { new(KindSum, g, 0, c0, c0Shift, null) };
        var known = new HashSet<int> { c0 };
        var used = new HashSet<int>();
        while (known.Count < g)
        {
            bool progress = false;
            for (int r = 0; r < g; r++)
            {
                if (used.Contains(r))
                {
                    continue;
                }

                int unknownColumn = -1;
                int unknownCount = 0;
                for (int j = 0; j < g; j++)
                {
                    int c = kb + j;
                    if (Base(r, c) >= 0 && !known.Contains(c))
                    {
                        unknownCount++;
                        unknownColumn = c;
                    }
                }

                if (unknownCount == 1)
                {
                    steps.Add(new Step(KindRow, 0, r, unknownColumn, Base(r, unknownColumn), null));
                    known.Add(unknownColumn);
                    used.Add(r);
                    progress = true;
                }
            }

            if (!progress)
            {
                return null;
            }
        }

        return [.. steps];
    }

    // The prototype's QCLDPC._dense_core_inverse and gf2_inverse: A is (g*Z) square, A[a*Z+i, b*Z+(i+s)%Z] = 1.
    // Returned as packed rows (ulong words, bit j of a row is column j).
    private ulong[] DenseCoreInverse(int g)
    {
        int z = LiftingSize;
        int kb = _kb;
        int n = g * z;
        int words = (n + 63) / 64;
        int width = 2 * words;
        var m = new ulong[n * width];
        for (int a = 0; a < g; a++)
        {
            for (int b = 0; b < g; b++)
            {
                int s = OwnLdpcTables.Shift(z, a, kb + b);
                if (s < 0)
                {
                    continue;
                }

                for (int i = 0; i < z; i++)
                {
                    int row = a * z + i;
                    int col = b * z + (i + s) % z;
                    m[row * width + (col >> 6)] |= 1UL << (col & 63);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            m[i * width + words + (i >> 6)] |= 1UL << (i & 63);
        }

        for (int col = 0; col < n; col++)
        {
            int piv = -1;
            for (int r = col; r < n; r++)
            {
                if ((m[r * width + (col >> 6)] >> (col & 63) & 1) != 0)
                {
                    piv = r;
                    break;
                }
            }

            if (piv < 0)
            {
                throw new InvalidOperationException("the core matrix is singular over GF(2)");
            }

            if (piv != col)
            {
                for (int w = 0; w < width; w++)
                {
                    (m[col * width + w], m[piv * width + w]) = (m[piv * width + w], m[col * width + w]);
                }
            }

            for (int r = 0; r < n; r++)
            {
                if (r != col && (m[r * width + (col >> 6)] >> (col & 63) & 1) != 0)
                {
                    for (int w = 0; w < width; w++)
                    {
                        m[r * width + w] ^= m[col * width + w];
                    }
                }
            }
        }

        var inverse = new ulong[n * words];
        for (int r = 0; r < n; r++)
        {
            Array.Copy(m, r * width + words, inverse, r * words, words);
        }

        return inverse;
    }

    private sealed record Step(int Kind, int Rows, int Row, int Column, int Shift, ulong[]? Inverse);
}
