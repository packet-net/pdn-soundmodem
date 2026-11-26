namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The payload code for a payload of <c>infoBits</c> sent as <c>codedBits</c>: the fewest code blocks of
/// at most <see cref="LinkLdpcTables.MaxInfoBitsPerBlock"/> information bits, the information and the coded
/// bits each split evenly (larger information blocks first, smaller coded blocks first), and the blocks'
/// coded bits joined in order. It gives what the prototype's <c>fit(infoBits, codedBits, "own").encode(bits)</c>
/// gives. Bits are one per byte, 0 or 1.
/// </summary>
/// <remarks>
/// A payload plan owns its block plans: the blocks differ in at most three distinct shapes, each built once
/// and shared within the payload. <see cref="Get"/> keeps the most recently used <see cref="CacheCapacity"/>
/// payload plans; a plan is cheap to rebuild, so an older one is simply dropped.
/// </remarks>
public sealed class PayloadCode
{
    /// <summary>Most payload plans <see cref="Get"/> keeps.</summary>
    public const int CacheCapacity = 32;

    private static readonly object Gate = new();
    private static readonly List<PayloadCode> Recent = new(CacheCapacity + 1);

    private readonly LinkLdpcCodeBlock[] _blocks;
    private readonly int[] _infoOffset;
    private readonly int[] _codedOffset;

    /// <summary>Builds the plan for a payload shape.</summary>
    public PayloadCode(int infoBits, int codedBits)
    {
        if (infoBits <= 0 || codedBits < infoBits)
        {
            throw new ArgumentOutOfRangeException(nameof(codedBits), "need 0 < infoBits <= codedBits");
        }

        InfoBits = infoBits;
        CodedBits = codedBits;
        int max = LinkLdpcTables.MaxInfoBitsPerBlock;
        int blocks = (infoBits + max - 1) / max;
        int[] ks = LinkLdpcTables.SplitEvenly(infoBits, blocks);
        Array.Reverse(ks);
        int[] es = LinkLdpcTables.SplitEvenly(codedBits, blocks);
        _blocks = new LinkLdpcCodeBlock[blocks];
        _infoOffset = new int[blocks];
        _codedOffset = new int[blocks];
        int info = 0;
        int coded = 0;
        int scratch = 0;
        int decodeScratch = 0;
        for (int i = 0; i < blocks; i++)
        {
            LinkLdpcCodeBlock? shared = null;
            for (int j = i - 1; j >= 0 && shared is null; j--)
            {
                if (_blocks[j].InfoBits == ks[i] && _blocks[j].CodedBits == es[i])
                {
                    shared = _blocks[j];
                }
            }

            _blocks[i] = shared ?? new LinkLdpcCodeBlock(ks[i], es[i]);
            _infoOffset[i] = info;
            _codedOffset[i] = coded;
            info += ks[i];
            coded += es[i];
            scratch = Math.Max(scratch, _blocks[i].ScratchLength);
            decodeScratch = Math.Max(decodeScratch, _blocks[i].DecodeScratchLength);
        }

        ScratchLength = scratch;
        DecodeScratchLength = decodeScratch;
    }

    /// <summary>Information bits the payload takes.</summary>
    public int InfoBits { get; }

    /// <summary>Coded bits the payload gives.</summary>
    public int CodedBits { get; }

    /// <summary>The code blocks, in order.</summary>
    public IReadOnlyList<LinkLdpcCodeBlock> Blocks => _blocks;

    /// <summary>Bytes of scratch <see cref="Encode(ReadOnlySpan{byte}, Span{byte}, Span{byte})"/> needs.</summary>
    public int ScratchLength { get; }

    /// <summary>Floats of scratch <see cref="Decode"/> needs.</summary>
    public int DecodeScratchLength { get; }

    /// <summary>Payload plans currently cached.</summary>
    internal static int CachedCount
    {
        get
        {
            lock (Gate)
            {
                return Recent.Count;
            }
        }
    }

    /// <summary>Approximate bytes this plan holds: its distinct block plans and its tables (not the shared structures).</summary>
    internal long PlanBytes
    {
        get
        {
            long total = 64 + 16L * _blocks.Length;
            for (int i = 0; i < _blocks.Length; i++)
            {
                bool first = true;
                for (int j = 0; j < i && first; j++)
                {
                    first = !ReferenceEquals(_blocks[j], _blocks[i]);
                }

                if (first)
                {
                    total += _blocks[i].PlanBytes;
                }
            }

            return total;
        }
    }

    /// <summary>The plan for a payload shape: from the most recently used set if it is there, otherwise built (and the least recently used dropped).</summary>
    public static PayloadCode Get(int infoBits, int codedBits)
    {
        lock (Gate)
        {
            for (int i = 0; i < Recent.Count; i++)
            {
                PayloadCode p = Recent[i];
                if (p.InfoBits == infoBits && p.CodedBits == codedBits)
                {
                    Recent.RemoveAt(i);
                    Recent.Insert(0, p);
                    return p;
                }
            }

            var built = new PayloadCode(infoBits, codedBits);
            Recent.Insert(0, built);
            if (Recent.Count > CacheCapacity)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }

            return built;
        }
    }

    /// <summary>Encodes <paramref name="info"/> (<see cref="InfoBits"/> bits) into <paramref name="coded"/> (<see cref="CodedBits"/> bits).</summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded)
    {
        Check(info, coded);
        for (int i = 0; i < _blocks.Length; i++)
        {
            LinkLdpcCodeBlock b = _blocks[i];
            b.Encode(info.Slice(_infoOffset[i], b.InfoBits), coded.Slice(_codedOffset[i], b.CodedBits));
        }
    }

    /// <summary>As <see cref="Encode(ReadOnlySpan{byte}, Span{byte})"/> with caller-supplied scratch of at least <see cref="ScratchLength"/> bytes.</summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded, Span<byte> scratch)
    {
        Check(info, coded);
        for (int i = 0; i < _blocks.Length; i++)
        {
            LinkLdpcCodeBlock b = _blocks[i];
            b.Encode(info.Slice(_infoOffset[i], b.InfoBits), coded.Slice(_codedOffset[i], b.CodedBits), scratch);
        }
    }

    /// <summary>
    /// Decodes a burst's <paramref name="soft"/> values (<see cref="CodedBits"/> of them, positive meaning 0), block by
    /// block, as the prototype's <c>modem.decode_blocks</c>. Writes the information bits of every block into
    /// <paramref name="info"/> (satisfied or not) and returns whether every block was satisfied. <paramref name="codedGuess"/>
    /// (<see cref="CodedBits"/> bits) gets the best guess at every coded bit: the block re-encoded where it was
    /// satisfied, the signs of its soft values (a negative value is a 1) where it was not. <paramref name="floats"/> needs
    /// <see cref="DecodeScratchLength"/> floats and <paramref name="bytes"/> <see cref="ScratchLength"/> bytes.
    /// </summary>
    public bool Decode(ReadOnlySpan<float> soft, Span<byte> info, Span<byte> codedGuess, Span<float> floats, Span<byte> bytes)
    {
        if (soft.Length != CodedBits)
        {
            throw new ArgumentException($"expected {CodedBits} soft values", nameof(soft));
        }

        Check(info, codedGuess);
        if (floats.Length < DecodeScratchLength)
        {
            throw new ArgumentException($"expected {DecodeScratchLength} floats of scratch", nameof(floats));
        }

        if (bytes.Length < ScratchLength)
        {
            throw new ArgumentException($"expected {ScratchLength} bytes of scratch", nameof(bytes));
        }

        bool all = true;
        for (int i = 0; i < _blocks.Length; i++)
        {
            LinkLdpcCodeBlock b = _blocks[i];
            ReadOnlySpan<float> part = soft.Slice(_codedOffset[i], b.CodedBits);
            Span<byte> blockInfo = info.Slice(_infoOffset[i], b.InfoBits);
            Span<byte> blockCoded = codedGuess.Slice(_codedOffset[i], b.CodedBits);
            if (b.Decode(part, blockInfo, floats, out _))
            {
                b.Encode(blockInfo, blockCoded, bytes);
            }
            else
            {
                for (int j = 0; j < part.Length; j++)
                {
                    blockCoded[j] = part[j] < 0 ? (byte)1 : (byte)0;
                }

                all = false;
            }
        }

        return all;
    }

    private void Check(ReadOnlySpan<byte> info, Span<byte> coded)
    {
        if (info.Length != InfoBits)
        {
            throw new ArgumentException($"expected {InfoBits} information bits", nameof(info));
        }

        if (coded.Length != CodedBits)
        {
            throw new ArgumentException($"expected room for {CodedBits} coded bits", nameof(coded));
        }
    }
}
