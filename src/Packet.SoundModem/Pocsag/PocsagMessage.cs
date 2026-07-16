using System.Text;

namespace Packet.SoundModem.Pocsag;

/// <summary>How a page's content field is encoded.</summary>
public enum PocsagContent
{
    /// <summary>No message codewords — the address codeword alone (a "beep" page).</summary>
    Tone,

    /// <summary>4-bit numeric characters, five per message codeword, each nibble sent
    /// LSB-first. DAPNET sends these with function 0.</summary>
    Numeric,

    /// <summary>7-bit ASCII sent LSB-first, packed continuously across message codewords.
    /// DAPNET sends these with function 3.</summary>
    Alphanumeric,
}

/// <summary>
/// One page to transmit: a 21-bit pager address (RIC), 2 function bits and the content.
/// The three low address bits select the batch frame the address codeword occupies and
/// are not carried in the codeword itself.
/// </summary>
public sealed record PocsagMessage
{
    /// <summary>The numeric character set, indexed by 4-bit value. 0xA has no assignment
    /// in the code's numeric table beyond "spare"; multimon-ng displays it as '.', and
    /// DAPNET's encoders (UniPager, MMDVMHost) write it from '*'. We accept both on
    /// encode and render '.' on decode, matching the decoder everyone runs.</summary>
    internal const string NumericCharset = "0123456789.U -][";

    private PocsagMessage(uint address, int function, PocsagContent content, string text)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address, 0x1FFFFFu, nameof(address));
        ArgumentOutOfRangeException.ThrowIfNegative(function, nameof(function));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(function, 3, nameof(function));
        Address = address;
        Function = function;
        Content = content;
        Text = text;
    }

    /// <summary>The 21-bit radio identity code (RIC), 0…2097151.</summary>
    public uint Address { get; }

    /// <summary>The 2 function bits, 0…3.</summary>
    public int Function { get; }

    /// <summary>The content encoding.</summary>
    public PocsagContent Content { get; }

    /// <summary>The message text (empty for tone-only pages).</summary>
    public string Text { get; }

    /// <summary>Creates a numeric page. Valid characters: digits, '*'/'.', 'U', space,
    /// '-', ')' or ']', '(' or '['.</summary>
    /// <param name="address">The 21-bit RIC.</param>
    /// <param name="text">The numeric message.</param>
    /// <param name="function">Function bits; 0 is the numeric convention.</param>
    public static PocsagMessage Numeric(uint address, string text, int function = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            NumericNibble(c); // validate: throws on characters outside the numeric set
        }

        return new PocsagMessage(address, function, PocsagContent.Numeric, text);
    }

    /// <summary>Creates an alphanumeric (7-bit ASCII) page.</summary>
    /// <param name="address">The 21-bit RIC.</param>
    /// <param name="text">The message; characters above 0x7F throw.</param>
    /// <param name="function">Function bits; 3 is the alphanumeric convention.</param>
    public static PocsagMessage Alphanumeric(uint address, string text, int function = 3)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(c => c > 0x7F))
        {
            throw new ArgumentException("alphanumeric pages carry 7-bit ASCII only", nameof(text));
        }

        return new PocsagMessage(address, function, PocsagContent.Alphanumeric, text);
    }

    /// <summary>Creates a tone-only ("beep") page — an address codeword with no content.</summary>
    /// <param name="address">The 21-bit RIC.</param>
    /// <param name="function">Function bits selecting the beep cadence on the pager.</param>
    public static PocsagMessage Tone(uint address, int function = 0) =>
        new(address, function, PocsagContent.Tone, "");

    /// <summary>Maps a numeric character to its 4-bit code (before LSB-first bit order
    /// is applied on air).</summary>
    internal static uint NumericNibble(char c) => c switch
    {
        >= '0' and <= '9' => (uint)(c - '0'),
        '*' or '.' => 0xA,
        'U' or 'u' => 0xB,
        ' ' => 0xC,
        '-' => 0xD,
        ')' or ']' => 0xE,
        '(' or '[' => 0xF,
        _ => throw new ArgumentException($"'{c}' is not in the POCSAG numeric character set"),
    };

    /// <summary>Content as the sequence of on-air bits (before 20-bit grouping). Numeric
    /// nibbles and ASCII characters are both sent least-significant bit first.</summary>
    internal IEnumerable<int> ContentBits()
    {
        if (Content == PocsagContent.Numeric)
        {
            foreach (char c in Text)
            {
                uint nibble = NumericNibble(c);
                for (int bit = 0; bit < 4; bit++)
                {
                    yield return (int)(nibble >> bit) & 1;
                }
            }
        }
        else if (Content == PocsagContent.Alphanumeric)
        {
            foreach (byte c in Encoding.ASCII.GetBytes(Text))
            {
                for (int bit = 0; bit < 7; bit++)
                {
                    yield return (c >> bit) & 1;
                }
            }
        }
    }
}
