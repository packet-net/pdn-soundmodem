namespace Packet.SoundModem.Kiss;

/// <summary>A KISS frame: command nibble, port (sub-channel) nibble, payload.</summary>
/// <param name="Port">Port / sub-channel (0–15) — this daemon's logical modem index.</param>
/// <param name="Command">Command nibble (<see cref="KissCommand"/>).</param>
/// <param name="Payload">Command payload (frame bytes for <see cref="KissCommand.Data"/>).</param>
public readonly record struct KissFrame(int Port, KissCommand Command, byte[] Payload);

/// <summary>KISS command nibbles (K5JB/KA9Q spec + the common ACKMODE extension).</summary>
public enum KissCommand
{
    /// <summary>Data frame.</summary>
    Data = 0,

    /// <summary>TX delay, ×10 ms.</summary>
    TxDelay = 1,

    /// <summary>Persistence parameter p (0–255 ≈ p·256−1).</summary>
    Persistence = 2,

    /// <summary>Slot time, ×10 ms.</summary>
    SlotTime = 3,

    /// <summary>TX tail, ×10 ms (obsolescent but widely sent).</summary>
    TxTail = 4,

    /// <summary>Full duplex (0 = half).</summary>
    FullDuplex = 5,

    /// <summary>Hardware-specific.</summary>
    SetHardware = 6,

    /// <summary>ACKMODE data frame (BPQ extension: first two payload bytes are an id the
    /// TNC echoes back when the frame has been transmitted).</summary>
    AckModeData = 12,

    /// <summary>
    /// Receive-quality report (this daemon's extension, TNC→host only, OFF by default):
    /// emitted after a data frame when the host opts in, carrying that frame's decode
    /// diagnostics as UTF-8 JSON on the same port nibble. A distinct command — never a
    /// synthetic data frame — so hosts that have not opted in ignore it as an unknown
    /// KISS command instead of parsing phantom traffic. (The NinoTNC's own habit of
    /// sending diagnostics as fake <c>TNC&gt;USB</c> data frames is the cautionary tale:
    /// every host needs a special case to avoid treating them as channel traffic.)
    /// </summary>
    RxQuality = 7,
}

/// <summary>
/// KISS byte-stream framing (FEND/FESC transparency), implemented here from the KISS
/// specification so the standalone daemon carries no external dependencies.
/// </summary>
public static class KissCodec
{
    /// <summary>Frame delimiter.</summary>
    public const byte Fend = 0xC0;
    private const byte Fesc = 0xDB;
    private const byte Tfend = 0xDC;
    private const byte Tfesc = 0xDD;

    /// <summary>Encodes one frame, delimited fore and aft.</summary>
    public static byte[] Encode(in KissFrame frame)
    {
        var output = new List<byte>(frame.Payload.Length + 8) { Fend };
        AppendEscaped(output, (byte)((frame.Port << 4) | (int)frame.Command));
        foreach (byte value in frame.Payload)
        {
            AppendEscaped(output, value);
        }

        output.Add(Fend);
        return [.. output];
    }

    private static void AppendEscaped(List<byte> output, byte value)
    {
        switch (value)
        {
            case Fend:
                output.Add(Fesc);
                output.Add(Tfend);
                break;
            case Fesc:
                output.Add(Fesc);
                output.Add(Tfesc);
                break;
            default:
                output.Add(value);
                break;
        }
    }
}

/// <summary>Streaming KISS decoder: push received bytes, get frames. One per connection.</summary>
public sealed class KissDecoder
{
    private readonly Action<KissFrame> _frameSink;
    private readonly List<byte> _buffer = [];
    private readonly int _maxFrame;
    private bool _escaped;
    private bool _inFrame;

    /// <summary>Creates a decoder delivering frames to <paramref name="frameSink"/>.</summary>
    public KissDecoder(Action<KissFrame> frameSink, int maxFrame = 2048)
    {
        ArgumentNullException.ThrowIfNull(frameSink);
        _frameSink = frameSink;
        _maxFrame = maxFrame;
    }

    /// <summary>Consumes received bytes.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (value == KissCodec.Fend)
            {
                if (_inFrame && _buffer.Count > 0)
                {
                    int type = _buffer[0];
                    int command = type & 0x0F;
                    if (Enum.IsDefined((KissCommand)command))
                    {
                        _frameSink(new KissFrame(type >> 4, (KissCommand)command, [.. _buffer.Skip(1)]));
                    }
                }

                _buffer.Clear();
                _escaped = false;
                _inFrame = true;
                continue;
            }

            if (!_inFrame)
            {
                continue;
            }

            byte decoded = value;
            if (_escaped)
            {
                decoded = value switch
                {
                    0xDC => KissCodec.Fend,
                    0xDD => 0xDB,
                    _ => value, // protocol violation; take the byte as-is
                };
                _escaped = false;
            }
            else if (value == 0xDB)
            {
                _escaped = true;
                continue;
            }

            if (_buffer.Count >= _maxFrame)
            {
                _buffer.Clear();
                _inFrame = false; // oversize: resynchronise at the next FEND
                continue;
            }

            _buffer.Add(decoded);
        }
    }
}
