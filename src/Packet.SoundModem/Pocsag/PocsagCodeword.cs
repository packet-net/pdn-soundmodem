using System.Numerics;

namespace Packet.SoundModem.Pocsag;

/// <summary>
/// The POCSAG 32-bit codeword — CCIR Radiopaging Code No. 1 (ITU-R M.584-2): 21 data bits
/// (bits 31..11, bit 31 first on air), 10 BCH(31,21) check bits (bits 10..1) and an even
/// parity bit (bit 0). The BCH generator is g(x) = x¹⁰+x⁹+x⁸+x⁶+x⁵+x³+1; with the parity
/// bit the code's minimum distance is 6, so 2-bit errors are corrected and all 3-bit
/// errors are detected and rejected.
/// </summary>
/// <remarks>
/// Implemented from the spec structure. The layout is confirmed three ways: encoding the
/// top 21 bits of the published frame-sync and idle constants reproduces both words
/// bit-for-bit, and the layout matches multimon-ng's <c>bch.c</c> (its comment: "data
/// [31:11], BCH parity [10:1], even parity [0]") and MMDVMHost's
/// <c>POCSAGControl::addBCHAndParity</c>. (DAPNET's UniPager <c>generator.rs</c> runs its
/// division one step past the data bits, which yields different check bits whenever the
/// true remainder's degree-9 coefficient is set — we follow the spec and the other two.)
/// </remarks>
public static class PocsagCodeword
{
    /// <summary>The frame synchronisation codeword sent before every batch.</summary>
    public const uint FrameSync = 0x7CD215D8;

    /// <summary>The idle codeword filling unused codeword slots.</summary>
    public const uint Idle = 0x7A89C197;

    /// <summary>g(x) = x¹⁰+x⁹+x⁸+x⁶+x⁵+x³+1 as its 11-bit coefficient string.</summary>
    private const uint Generator = 0b111_0110_1001;

    /// <summary>Syndrome-keyed error patterns for every 0/1/2-bit error over the full
    /// 32-bit word. Key = (overall parity &lt;&lt; 10) | 10-bit BCH syndrome; distance 6
    /// makes each key unique, and any key not in the table is a ≥3-bit error.</summary>
    private static readonly uint[] ErrorTable = BuildErrorTable();

    /// <summary>Encodes 21 data bits into a 32-bit codeword (data, BCH check bits, even
    /// parity).</summary>
    /// <param name="data21">The 21 data bits (flag bit ≡ bit 20, first on air).</param>
    public static uint Encode(uint data21)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data21, 0x1FFFFFu);

        // Long division of data·x¹⁰ by g(x); the 10-bit remainder is the check field.
        uint remainder = data21 << 10;
        for (int bit = 20; bit >= 0; bit--)
        {
            if ((remainder & (1u << (bit + 10))) != 0)
            {
                remainder ^= Generator << bit;
            }
        }

        uint word = ((data21 << 10) | (remainder & 0x3FF)) << 1;
        return word | ((uint)BitOperations.PopCount(word) & 1);
    }

    /// <summary>Checks a received codeword and corrects up to 2 bit errors in place.</summary>
    /// <param name="word">The received 32-bit codeword; corrected on success.</param>
    /// <param name="bitErrors">Number of bit errors corrected (0–2).</param>
    /// <returns>False when the word is uncorrectable (3 or more bit errors).</returns>
    public static bool TryCorrect(ref uint word, out int bitErrors)
    {
        int key = SyndromeKey(word);
        if (key == 0)
        {
            bitErrors = 0;
            return true;
        }

        uint pattern = ErrorTable[key];
        if (pattern == 0)
        {
            bitErrors = 0;
            return false;
        }

        word ^= pattern;
        bitErrors = BitOperations.PopCount(pattern);
        return true;
    }

    /// <summary>The 21 data bits of a codeword.</summary>
    public static uint Data(uint word) => word >> 11;

    /// <summary>11-bit syndrome key: overall parity in bit 10, BCH syndrome of the 31-bit
    /// codeword (bits 31..1) below. Zero for a valid codeword.</summary>
    private static int SyndromeKey(uint word)
    {
        uint remainder = word >> 1;
        for (int bit = 30; bit >= 10; bit--)
        {
            if ((remainder & (1u << bit)) != 0)
            {
                remainder ^= Generator << (bit - 10);
            }
        }

        return (int)((remainder & 0x3FF) | ((uint)BitOperations.PopCount(word) & 1) << 10);
    }

    private static uint[] BuildErrorTable()
    {
        // The syndrome is linear, so the syndrome of (codeword ^ error) equals the
        // syndrome of the error pattern alone — key every ≤2-bit pattern by its syndrome.
        var table = new uint[2048];
        for (int i = 0; i < 32; i++)
        {
            uint single = 1u << i;
            table[SyndromeKey(single)] = single;
            for (int j = i + 1; j < 32; j++)
            {
                uint pair = single | (1u << j);
                table[SyndromeKey(pair)] = pair;
            }
        }

        return table;
    }
}
