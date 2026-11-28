namespace Packet.SoundModem.OfdmFmTool;

/// <summary>
/// The ping's echo, this tool's own convention, carried as the datagram's payload under protocol byte <see cref="Protocol"/>: a kind byte, a two-byte sequence number (most significant first) and filler to the size asked for.
/// A reply is the request with the kind changed, so it is the same length.
/// </summary>
internal static class Echo
{
    /// <summary>The protocol byte. Not one of the five the modem's protocol document names, and not one AX.25's PID field assigns: the rest is the host's to use.</summary>
    public const byte Protocol = 0xC1;

    /// <summary>Kind byte, then the sequence number.</summary>
    public const int HeaderSize = 3;

    public const byte Request = 1;

    public const byte Reply = 2;

    public static byte[] MakeRequest(ushort sequence, int size)
    {
        var payload = new byte[Math.Max(size, HeaderSize)];
        payload[0] = Request;
        payload[1] = (byte)(sequence >> 8);
        payload[2] = (byte)sequence;
        for (int i = HeaderSize; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31) + sequence);
        }

        return payload;
    }

    public static byte[] MakeReply(ReadOnlySpan<byte> request)
    {
        byte[] reply = request.ToArray();
        reply[0] = Reply;
        return reply;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out byte kind, out ushort sequence)
    {
        kind = 0;
        sequence = 0;
        if (payload.Length < HeaderSize || payload[0] is not (Request or Reply))
        {
            return false;
        }

        kind = payload[0];
        sequence = (ushort)((payload[1] << 8) | payload[2]);
        return true;
    }
}
