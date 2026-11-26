using System.Collections.Concurrent;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The adaptive payload code for a payload of <c>infoBits</c> sent as <c>codedBits</c>: the fewest
/// code blocks of at most <see cref="OwnLdpcTables.MaxInfoBitsPerBlock"/> information bits, the
/// information and the coded bits each split evenly (larger information blocks first, smaller coded
/// blocks first), and the blocks' coded bits joined in order. It gives what the prototype's
/// <c>fit(infoBits, codedBits, "own").encode(bits)</c> gives. Bits are one per byte, 0 or 1.
/// </summary>
public sealed class PayloadCode
{
    private static readonly ConcurrentDictionary<(int Info, int Coded), Lazy<PayloadCode>> Cache = new();

    private readonly OwnLdpcCodeBlock[] _blocks;
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
        int max = OwnLdpcTables.MaxInfoBitsPerBlock;
        int blocks = (infoBits + max - 1) / max;
        int[] ks = OwnLdpcTables.SplitEvenly(infoBits, blocks);
        Array.Reverse(ks);
        int[] es = OwnLdpcTables.SplitEvenly(codedBits, blocks);
        _blocks = new OwnLdpcCodeBlock[blocks];
        _infoOffset = new int[blocks];
        _codedOffset = new int[blocks];
        int info = 0;
        int coded = 0;
        int scratch = 0;
        for (int i = 0; i < blocks; i++)
        {
            _blocks[i] = OwnLdpcCodeBlock.Get(ks[i], es[i]);
            _infoOffset[i] = info;
            _codedOffset[i] = coded;
            info += ks[i];
            coded += es[i];
            scratch = Math.Max(scratch, _blocks[i].ScratchLength);
        }

        ScratchLength = scratch;
    }

    /// <summary>Information bits the payload takes.</summary>
    public int InfoBits { get; }

    /// <summary>Coded bits the payload gives.</summary>
    public int CodedBits { get; }

    /// <summary>The code blocks, in order.</summary>
    public IReadOnlyList<OwnLdpcCodeBlock> Blocks => _blocks;

    /// <summary>Bytes of scratch <see cref="Encode(ReadOnlySpan{byte}, Span{byte}, Span{byte})"/> needs.</summary>
    public int ScratchLength { get; }

    /// <summary>The cached plan for a payload shape; built on first use.</summary>
    public static PayloadCode Get(int infoBits, int codedBits) =>
        Cache.GetOrAdd((infoBits, codedBits), key => new Lazy<PayloadCode>(() => new PayloadCode(key.Info, key.Coded))).Value;

    /// <summary>Encodes <paramref name="info"/> (<see cref="InfoBits"/> bits) into <paramref name="coded"/> (<see cref="CodedBits"/> bits).</summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded)
    {
        Check(info, coded);
        for (int i = 0; i < _blocks.Length; i++)
        {
            OwnLdpcCodeBlock b = _blocks[i];
            b.Encode(info.Slice(_infoOffset[i], b.InfoBits), coded.Slice(_codedOffset[i], b.CodedBits));
        }
    }

    /// <summary>As <see cref="Encode(ReadOnlySpan{byte}, Span{byte})"/> with caller-supplied scratch of at least <see cref="ScratchLength"/> bytes.</summary>
    public void Encode(ReadOnlySpan<byte> info, Span<byte> coded, Span<byte> scratch)
    {
        Check(info, coded);
        for (int i = 0; i < _blocks.Length; i++)
        {
            OwnLdpcCodeBlock b = _blocks[i];
            b.Encode(info.Slice(_infoOffset[i], b.InfoBits), coded.Slice(_codedOffset[i], b.CodedBits), scratch);
        }
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
