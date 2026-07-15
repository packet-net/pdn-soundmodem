namespace Packet.SoundModem.Modems;

/// <summary>
/// G3RUH x¹⁷+x¹²+1 multiplicative scrambler for classic 9600 baud packet, matching the
/// Dire Wolf implementation bit-for-bit (gen_tone.c / demod_9600): transmit is
/// free-running, receive is self-synchronising off the received bit history. Not used by
/// IL2P, which scrambles packet-synchronously inside its own frame structure.
/// </summary>
public sealed class G3ruhScrambler
{
    private int _state;

    /// <summary>Scrambles one transmit bit.</summary>
    public int Scramble(int bit)
    {
        int output = (bit ^ (_state >> 16) ^ (_state >> 11)) & 1;
        _state = (_state << 1) | output;
        return output;
    }

    /// <summary>Descrambles one received bit (self-synchronising).</summary>
    public int Descramble(int bit)
    {
        bit &= 1;
        int output = (bit ^ (_state >> 16) ^ (_state >> 11)) & 1;
        _state = (_state << 1) | bit;
        return output;
    }
}
