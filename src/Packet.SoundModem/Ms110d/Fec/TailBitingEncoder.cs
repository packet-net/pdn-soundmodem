namespace Packet.SoundModem.Ms110d.Fec;

/// <summary>
/// Full-tail-biting rate-1/2 convolutional encoder (MIL-STD-188-110D D.5.3.2.3, doc p. 220):
/// the register is preloaded with the first K−1 input bits taking no output; the first output
/// pair is produced as the K-th bit shifts in; after the last input bit, the saved K−1 bits are
/// shifted back in (earliest first) and those pairs are the final output bits. The encoder
/// therefore starts and ends in the same state and an N-bit block yields exactly 2N coded bits.
/// </summary>
public static class TailBitingEncoder
{
    /// <summary>Encodes <paramref name="info"/> (bits as 0/1 bytes) into
    /// <paramref name="coded"/>, which must be exactly twice as long. For each input bit the
    /// T1 output (b0) is emitted first, then T2 (b1).</summary>
    public static void Encode(ConvolutionalCode code, ReadOnlySpan<byte> info, Span<byte> coded)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (coded.Length != 2 * info.Length)
        {
            throw new ArgumentException("coded.Length must be 2 * info.Length", nameof(coded));
        }

        if (info.Length < code.K)
        {
            throw new ArgumentException($"block must be at least K={code.K} bits", nameof(info));
        }

        uint mask = (1u << code.K) - 1;
        uint state = 0;
        int k1 = code.K - 1;
        for (int i = 0; i < k1; i++)
        {
            state = ((state << 1) | info[i]) & mask; // preload, no output
        }

        int o = 0;
        for (int n = k1; n < info.Length + k1; n++)  // wrap over the saved K−1 bits
        {
            state = ((state << 1) | info[n % info.Length]) & mask;
            coded[o++] = Parity(state & code.PolyT1); // b0 = T1 first
            coded[o++] = Parity(state & code.PolyT2); // b1 = T2
        }
    }

    private static byte Parity(uint v)
    {
        return (byte)(System.Numerics.BitOperations.PopCount(v) & 1);
    }
}
