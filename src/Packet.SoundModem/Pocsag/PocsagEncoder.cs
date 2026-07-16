using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Pocsag;

/// <summary>Baseband polarity of the 2-FSK signal.</summary>
public enum PocsagPolarity
{
    /// <summary>The code's convention: a '0' bit is the HIGH frequency (+4.5 kHz RF
    /// deviation), so at the baseband data port a '0' bit is the positive level. This is
    /// what an FM receiver's discriminator hands multimon-ng ("normal" polarity there)
    /// and inverted relative to this library's AX.25 FSK modes, where '1' is positive.</summary>
    Normal,

    /// <summary>'0' bit at the negative level — for radios whose TX data path inverts.</summary>
    Inverted,
}

/// <summary>
/// POCSAG transmitter (CCIR Radiopaging Code No. 1 / ITU-R M.584-2): pages → preamble +
/// batches of BCH-protected codewords → baseband NRZ 2-FSK audio for an FM radio's data
/// port. 1200 bd is the DAPNET amateur paging network's rate; 512 and 2400 are the other
/// standard rates. This is a paging waveform that lives alongside the AX.25 packet modes —
/// it carries pages, not AX.25 frames, so it is deliberately not an <c>IModem</c>.
/// </summary>
/// <remarks>
/// Structure per the spec: a 576-bit reversal preamble (101010…), then batches of one
/// frame-sync codeword + 16 codewords (8 frames × 2). A page's address codeword sits in
/// the frame selected by its three low address bits; message codewords follow immediately
/// and may run across batch boundaries; unused slots carry the idle codeword. Framing
/// conventions the spec leaves loose (18-codeword preamble, numeric pages padded with
/// 0xC "space" nibbles, alphanumeric pages padded with zero bits) follow DAPNET's
/// transmitters (MMDVMHost <c>POCSAGControl.cpp</c>, UniPager <c>generator.rs</c>) and
/// are verified against multimon-ng decodes.
/// </remarks>
public sealed class PocsagEncoder
{
    /// <summary>Spec minimum preamble: 576 bits of bit reversals (18 codeword times).</summary>
    public const int PreambleBits = 576;

    private const float Amplitude = 0.8f;

    private readonly int _sampleRate;
    private readonly int _baud;
    private readonly PocsagPolarity _polarity;

    /// <summary>Creates the encoder.</summary>
    /// <param name="sampleRate">Output sample rate; need not divide evenly by the baud
    /// rate (bit boundaries are placed by cumulative rounding).</param>
    /// <param name="baud">Bit rate: 512, 1200 (DAPNET) or 2400.</param>
    /// <param name="polarity">Baseband polarity; <see cref="PocsagPolarity.Normal"/> is
    /// the spec convention ('0' = high frequency = positive level).</param>
    public PocsagEncoder(int sampleRate, int baud = 1200, PocsagPolarity polarity = PocsagPolarity.Normal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, baud * 4);
        _sampleRate = sampleRate;
        _baud = baud;
        _polarity = polarity;
    }

    /// <summary>The mode label, e.g. "pocsag1200".</summary>
    public string Mode => $"pocsag{_baud}";

    /// <summary>Builds the full codeword stream for a transmission — frame-sync words,
    /// address/message codewords in their frames, idle fill to the end of the final
    /// batch. No preamble (that is a bit-level affair, see <see cref="Modulate"/>).</summary>
    /// <param name="messages">The pages to send, in order.</param>
    public static uint[] BuildCodewords(IReadOnlyList<PocsagMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfZero(messages.Count);

        var words = new List<uint>();
        int slot = 0; // 0..15 within the current batch
        void Emit(uint word)
        {
            if (slot == 0)
            {
                words.Add(PocsagCodeword.FrameSync);
            }

            words.Add(word);
            slot = (slot + 1) & 15;
        }

        foreach (PocsagMessage message in messages)
        {
            // The three low address bits select the frame (2 codewords) the address
            // codeword must occupy; idle-fill until the batch position reaches it.
            int frame = (int)(message.Address & 7);
            while (slot >> 1 != frame)
            {
                Emit(PocsagCodeword.Idle);
            }

            // Address codeword: flag 0, the 18 high address bits, 2 function bits.
            Emit(PocsagCodeword.Encode(((message.Address >> 3) << 2) | (uint)message.Function));

            // Message codewords: flag 1, 20 content bits each, first-on-air in the
            // field's MSB. Content runs on across frame and batch boundaries.
            uint group = 0;
            int count = 0;
            foreach (int bit in PaddedContentBits(message))
            {
                group = (group << 1) | (uint)bit;
                if (++count == 20)
                {
                    Emit(PocsagCodeword.Encode(0x100000 | group));
                    group = 0;
                    count = 0;
                }
            }
        }

        while (slot != 0)
        {
            Emit(PocsagCodeword.Idle);
        }

        return [.. words];
    }

    /// <summary>Modulates pages into baseband FSK audio: preamble, codewords, NRZ
    /// pulse-shaping through a 0.55·baud low-pass (the same discipline as the AX.25
    /// direct-FSK path), plus filter flush.</summary>
    /// <param name="messages">The pages to send, in order.</param>
    /// <param name="preambleBits">Reversal-preamble length; the spec floor (and default)
    /// is 576 bits, which is also the receiver's settling time budget.</param>
    public float[] Modulate(IReadOnlyList<PocsagMessage> messages, int preambleBits = PreambleBits)
        => Render(BuildBits(messages, preambleBits));

    /// <summary>The complete transmission as wire bits (preamble + codewords MSB-first).
    /// Internal seam so tests can inject exact bit errors ahead of the pulse shaping.</summary>
    internal static byte[] BuildBits(IReadOnlyList<PocsagMessage> messages, int preambleBits = PreambleBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preambleBits, 32);
        uint[] words = BuildCodewords(messages);
        var bits = new byte[preambleBits + (words.Length * 32)];
        for (int i = 0; i < preambleBits; i++)
        {
            bits[i] = (byte)(1 - (i & 1)); // 1010… — the 0xAAAAAAAA reversal pattern
        }

        int position = preambleBits;
        foreach (uint word in words)
        {
            for (int bit = 31; bit >= 0; bit--)
            {
                bits[position++] = (byte)((word >> bit) & 1);
            }
        }

        return bits;
    }

    /// <summary>Renders wire bits to pulse-shaped baseband audio.</summary>
    internal float[] Render(ReadOnlySpan<byte> wireBits)
    {
        double samplesPerBit = (double)_sampleRate / _baud;
        int taps = (int)(8 * samplesPerBit) | 1;
        var shaper = new FirFilter(FilterDesign.LowPass(0.55 * _baud, _sampleRate, taps));
        float zero = _polarity == PocsagPolarity.Normal ? Amplitude : -Amplitude;

        var samples = new float[(int)Math.Round(wireBits.Length * samplesPerBit) + taps];
        int position = 0;
        for (int i = 0; i < wireBits.Length; i++)
        {
            float level = wireBits[i] == 0 ? zero : -zero;
            int end = (int)Math.Round((i + 1) * samplesPerBit);
            while (position < end)
            {
                samples[position++] = shaper.Next(level);
            }
        }

        // Run past the last bit to flush the shaper's group delay (see FskModem).
        while (position < samples.Length)
        {
            samples[position++] = shaper.Next(0f);
        }

        return samples;
    }

    private static IEnumerable<int> PaddedContentBits(PocsagMessage message)
    {
        int count = 0;
        foreach (int bit in message.ContentBits())
        {
            count++;
            yield return bit;
        }

        if (message.Content == PocsagContent.Numeric)
        {
            // Fill the final codeword with 0xC "space" nibbles (LSB-first), DAPNET's
            // convention (MMDVMHost BCD_SPACES; UniPager NUMERIC.trailing = 0xC).
            while (count % 20 != 0)
            {
                for (int bit = 0; bit < 4; bit++)
                {
                    yield return (0xC >> bit) & 1;
                }

                count += 4;
            }
        }
        else
        {
            // Alphanumeric: zero-bit fill (MMDVMHost packASCII; UniPager trailing 0x0).
            // Decoders render whole trailing NUL characters, which readers strip.
            while (count % 20 != 0)
            {
                count++;
                yield return 0;
            }
        }
    }
}
