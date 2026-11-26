namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>What a data burst's prelude says. A burst to everyone has none.</summary>
/// <param name="Wanted">"Report wanted".</param>
/// <param name="Recommended">The format recommended to the destination (0 to 255), or null when none is given.</param>
/// <param name="Failed">"Failed".</param>
/// <param name="Capability">The sender's 16-bit capability word, or null when it is not sent.</param>
public readonly record struct PayloadPrelude(bool Wanted, int? Recommended, bool Failed, int? Capability);

/// <summary>Walks the datagrams of an unpacked payload, without allocating.</summary>
public ref struct DatagramReader
{
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    internal DatagramReader(ReadOnlySpan<byte> payload, int position)
    {
        _payload = payload;
        _position = position;
    }

    /// <summary>The next datagram. False at a length of 0, a length that runs past the room left, or too little room for another length.</summary>
    public bool TryNext(out ReadOnlySpan<byte> datagram)
    {
        datagram = default;
        if (_position + 2 > _payload.Length)
        {
            return false;
        }

        int n = (_payload[_position] << 8) | _payload[_position + 1];
        if (n == 0 || _position + 2 + n > _payload.Length)
        {
            return false;
        }

        datagram = _payload.Slice(_position + 2, n);
        _position += 2 + n;
        return true;
    }
}

/// <summary>
/// Between a payload's bytes (prelude, capability word, datagrams) and a burst's information bits (padding,
/// scrambling, 32-bit check), as <c>docs/dev/ofdm-fm/adaptation-protocol.md</c> sets out. Pinned to the prototype's
/// <c>link.py</c>. Bits are held one to a byte (0 or 1). Nothing here allocates.
/// </summary>
public static class PayloadCodec
{
    /// <summary>Bits of check at the end of the information bits.</summary>
    public const int CheckBits = 32;

    private const int Wanted = 0x80;
    private const int HasCapability = 0x40;
    private const int Given = 0x20;
    private const int FailedFlag = 0x10;

    /// <summary>Bytes of prelude and datagrams (lengths included) that a format carries in this many payload symbols. Negative when the burst is too small to hold even its check.</summary>
    public static int Capacity(int format, int payloadSymbols) => (StandardFormats.InfoBits(format, payloadSymbols) - CheckBits) >> 3;

    /// <summary>The shortest burst of the format, in payload symbols (a multiple of four), that carries <paramref name="loadBytes"/> (never fewer than 8 are asked for), or null if even the longest does not.</summary>
    public static int? SymbolsFor(int format, int loadBytes)
    {
        int need = Math.Max(loadBytes, 8);
        for (int symbols = 4; symbols <= StandardFormats.MaxSymbols; symbols += 4)
        {
            if (Capacity(format, symbols) >= need)
            {
                return symbols;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the payload's bytes before padding: the prelude (unless <paramref name="prelude"/> is null, a burst to
    /// everyone) and the datagrams, each behind a two-byte length. Returns the length, or -1 when it does not fit
    /// in <paramref name="destination"/>. Datagrams are 1 to 65535 bytes.
    /// </summary>
    public static int Pack(PayloadPrelude? prelude, ReadOnlySpan<ReadOnlyMemory<byte>> datagrams, Span<byte> destination)
    {
        int p = 0;
        if (prelude is { } pre)
        {
            if (pre.Recommended is < 0 or > 255 || pre.Capability is < 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(prelude));
            }

            int need = pre.Capability is null ? 2 : 4;
            if (destination.Length < need)
            {
                return -1;
            }

            destination[0] = (byte)((pre.Wanted ? Wanted : 0) | (pre.Capability is not null ? HasCapability : 0) | (pre.Recommended is not null ? Given : 0) | (pre.Failed ? FailedFlag : 0));
            destination[1] = (byte)(pre.Recommended ?? 0);
            p = 2;
            if (pre.Capability is { } cap)
            {
                destination[2] = (byte)(cap >> 8);
                destination[3] = (byte)cap;
                p = 4;
            }
        }

        foreach (ReadOnlyMemory<byte> d in datagrams)
        {
            if (d.Length is 0 or > 65535)
            {
                throw new ArgumentException("A datagram is 1 to 65535 bytes.", nameof(datagrams));
            }

            if (p + 2 + d.Length > destination.Length)
            {
                return -1;
            }

            destination[p] = (byte)(d.Length >> 8);
            destination[p + 1] = (byte)d.Length;
            d.Span.CopyTo(destination[(p + 2)..]);
            p += 2 + d.Length;
        }

        return p;
    }

    /// <summary>
    /// Reads a payload's bytes: the prelude (default values when <paramref name="toEveryone"/>) and a reader for the
    /// datagrams, which stop at a zero length. False when the payload is too short for its prelude (or the capability
    /// word it flags); the prototype would read a short word there, which only a payload that no station sends can show.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<byte> payload, bool toEveryone, out PayloadPrelude prelude, out DatagramReader datagrams)
    {
        prelude = new PayloadPrelude(false, null, false, null);
        datagrams = new DatagramReader(payload, 0);
        if (toEveryone)
        {
            return true;
        }

        if (payload.Length < 2)
        {
            return false;
        }

        int flags = payload[0];
        int position = 2;
        int? cap = null;
        if ((flags & HasCapability) != 0)
        {
            if (payload.Length < 4)
            {
                return false;
            }

            cap = (payload[2] << 8) | payload[3];
            position = 4;
        }

        prelude = new PayloadPrelude((flags & Wanted) != 0, (flags & Given) != 0 ? payload[1] : null, (flags & FailedFlag) != 0, cap);
        datagrams = new DatagramReader(payload, position);
        return true;
    }

    /// <summary>
    /// The information bits for a payload: its bytes, zero padding up to the burst's size, scrambling by the burst's
    /// sender, destination and burst number, and the 32-bit check over the scrambled bits. <paramref name="bits"/>
    /// must be exactly <paramref name="infoBits"/> long (see <see cref="StandardFormats.InfoBits"/>) and the payload
    /// no longer than (infoBits - 32) / 8 bytes.
    /// </summary>
    public static void Encode(ReadOnlySpan<byte> payload, int infoBits, int sender, int destination, int burstNumber, Span<byte> bits)
    {
        int room = (infoBits - CheckBits) >> 3;
        if (bits.Length != infoBits || infoBits < CheckBits || payload.Length > room)
        {
            throw new ArgumentException("The bits span must be infoBits long and the payload must fit.");
        }

        int dataBits = infoBits - CheckBits;
        bits.Clear();
        for (int i = 0; i < payload.Length; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                bits[(i * 8) + j] = (byte)((payload[i] >> (7 - j)) & 1);
            }
        }

        PayloadScrambler.Apply(sender, destination, burstNumber, bits[..dataBits]);
        uint crc = BitCrc.Crc32(bits[..dataBits]);
        for (int i = 0; i < CheckBits; i++)
        {
            bits[dataBits + i] = (byte)((crc >> (CheckBits - 1 - i)) & 1);
        }
    }

    /// <summary>
    /// Checks the 32-bit check over the scrambled bits and, if it holds, writes the unscrambled payload bytes
    /// (all of the burst's room, padding included) into <paramref name="payload"/>, which must be at least
    /// (bits.Length - 32) / 8 bytes. False when the check fails.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bits, int sender, int destination, int burstNumber, Span<byte> payload, out int length)
    {
        length = 0;
        if (bits.Length < CheckBits)
        {
            return false;
        }

        int dataBits = bits.Length - CheckBits;
        uint crc = BitCrc.Crc32(bits[..dataBits]);
        for (int i = 0; i < CheckBits; i++)
        {
            if ((bits[dataBits + i] & 1) != ((crc >> (CheckBits - 1 - i)) & 1))
            {
                return false;
            }
        }

        int room = dataBits >> 3;
        if (payload.Length < room)
        {
            throw new ArgumentException("The payload span is too small.", nameof(payload));
        }

        uint w = PayloadScrambler.Seed(sender, destination, burstNumber);
        for (int i = 0; i < room; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
            {
                b = (b << 1) | ((bits[(i * 8) + j] & 1) ^ (int)PayloadScrambler.Next(ref w));
            }

            payload[i] = (byte)b;
        }

        length = room;
        return true;
    }
}
