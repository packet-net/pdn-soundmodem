using System.Text;

namespace Packet.SoundModem.Pocsag;

/// <summary>
/// One received page. The wire does not label its own content encoding, so both standard
/// interpretations of the content bits are offered; <see cref="Text"/> picks by the
/// DAPNET/multimon-ng convention (function 0 = numeric, everything else = alphanumeric).
/// </summary>
public sealed class PocsagPage
{
    internal PocsagPage(
        uint address, int function, IReadOnlyList<uint> contentGroups,
        int bitErrorsCorrected, bool inverted, bool truncated)
    {
        Address = address;
        Function = function;
        ContentGroups = contentGroups;
        BitErrorsCorrected = bitErrorsCorrected;
        Inverted = inverted;
        Truncated = truncated;
        NumericText = DecodeNumeric(contentGroups);
        AlphaText = DecodeAlpha(contentGroups);
    }

    /// <summary>The pager's 21-bit radio identity code (RIC).</summary>
    public uint Address { get; }

    /// <summary>The 2 function bits (DAPNET: 0 = numeric, 3 = alphanumeric).</summary>
    public int Function { get; }

    /// <summary>The raw 20-bit content fields, one per message codeword, first-on-air
    /// bit in the MSB — for callers that need an interpretation this class doesn't offer.</summary>
    public IReadOnlyList<uint> ContentGroups { get; }

    /// <summary>Content read as 4-bit numeric characters (multimon-ng's display set:
    /// digits, '.', 'U', space, '-', ']', '['), trailing padding spaces trimmed.</summary>
    public string NumericText { get; }

    /// <summary>Content read as 7-bit ASCII (LSB-first), trailing NUL padding trimmed.</summary>
    public string AlphaText { get; }

    /// <summary>The conventional reading: <see cref="NumericText"/> for function 0,
    /// otherwise <see cref="AlphaText"/>. Empty for tone-only pages.</summary>
    public string Text => Function == 0 ? NumericText : AlphaText;

    /// <summary>Bit errors the BCH code corrected across this page's codewords.</summary>
    public int BitErrorsCorrected { get; }

    /// <summary>True when the transmission arrived polarity-inverted.</summary>
    public bool Inverted { get; }

    /// <summary>True when the page was cut short by an uncorrectable codeword or lost
    /// sync rather than ended by an idle/address codeword.</summary>
    public bool Truncated { get; }

    private static string DecodeNumeric(IReadOnlyList<uint> groups)
    {
        var text = new StringBuilder(groups.Count * 5);
        foreach (uint group in groups)
        {
            for (int i = 0; i < 5; i++)
            {
                // Nibbles are sent LSB-first, so the on-air (MSB-first) nibble is the
                // bit-reverse of the character value — multimon-ng bakes the same
                // reversal into its lookup table ("084 2.6]195-3U7[", pocsag.c).
                uint onAir = (group >> (16 - (4 * i))) & 0xF;
                uint value = ((onAir & 1) << 3) | ((onAir & 2) << 1) | ((onAir & 4) >> 1) | (onAir >> 3);
                text.Append(PocsagMessage.NumericCharset[(int)value]);
            }
        }

        return text.ToString().TrimEnd(' ');
    }

    private static string DecodeAlpha(IReadOnlyList<uint> groups)
    {
        int totalBits = groups.Count * 20;
        var text = new StringBuilder(totalBits / 7);
        for (int start = 0; start + 7 <= totalBits; start += 7)
        {
            int value = 0;
            for (int bit = 0; bit < 7; bit++)
            {
                int position = start + bit; // characters are sent LSB-first
                uint group = groups[position / 20];
                value |= (int)((group >> (19 - (position % 20))) & 1) << bit;
            }

            text.Append((char)value);
        }

        return text.ToString().TrimEnd('\0');
    }
}
