namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The payload scrambler of <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>: a 31-stage generator (x^31 + x^28 + 1)
/// seeded from the burst's sender, destination and burst number, added bit by bit to everything before the check.
/// Bits are held one to a byte (0 or 1).
/// </summary>
public static class PayloadScrambler
{
    /// <summary>The 31-bit seed: 2^30 + 2^24 x sender + 2^18 x destination + 2^14 x burst number + 0x2A5B. Never zero.</summary>
    public static uint Seed(int sender, int destination, int burstNumber)
    {
        if ((uint)sender > 63 || (uint)destination > 63 || (uint)burstNumber > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(sender), "Sender and destination are 0 to 63, burst number 0 to 15.");
        }

        return (1u << 30) | ((uint)sender << 24) | ((uint)destination << 18) | ((uint)burstNumber << 14) | 0x2A5Bu;
    }

    /// <summary>Writes the sequence into <paramref name="bits"/>, starting after the seed's own 31 bits.</summary>
    public static void Sequence(int sender, int destination, int burstNumber, Span<byte> bits)
    {
        uint w = Seed(sender, destination, burstNumber);
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)Next(ref w);
        }
    }

    /// <summary>Adds the sequence to <paramref name="bits"/> in place. Scrambling and unscrambling are the same operation.</summary>
    public static void Apply(int sender, int destination, int burstNumber, Span<byte> bits)
    {
        uint w = Seed(sender, destination, burstNumber);
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] ^= (byte)Next(ref w);
        }
    }

    /// <summary>
    /// Advances the generator by one bit and returns it. The state holds the last 31 bits, the newest at bit 0, so
    /// b[i-28] is bit 27 and b[i-31] is bit 30.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Next(ref uint state)
    {
        uint next = ((state >> 27) ^ (state >> 30)) & 1;
        state = ((state << 1) | next) & 0x7FFFFFFFu;
        return next;
    }
}
