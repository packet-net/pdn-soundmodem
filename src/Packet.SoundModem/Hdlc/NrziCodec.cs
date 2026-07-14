namespace Packet.SoundModem.Hdlc;

/// <summary>
/// NRZI (non-return-to-zero inverted) line coding as used by AX.25/HDLC: a logical 0 is
/// transmitted as a change of line state, a logical 1 as no change. Stateful per direction;
/// create one instance per bit stream.
/// </summary>
public sealed class NrziEncoder
{
    private int _level;

    /// <summary>Encodes one logical bit to the next line level (0/1).</summary>
    public int Encode(int bit)
    {
        if (bit == 0)
        {
            _level ^= 1;
        }

        return _level;
    }
}

/// <summary>Decodes NRZI line levels back to logical bits (1 = no change, 0 = change).</summary>
public sealed class NrziDecoder
{
    private int _previous;

    /// <summary>Decodes the next received line level (0/1) to a logical bit.</summary>
    public int Decode(int level)
    {
        int bit = level == _previous ? 1 : 0;
        _previous = level;
        return bit;
    }
}
