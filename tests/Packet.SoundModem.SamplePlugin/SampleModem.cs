using Packet.SoundModem.Modems;

namespace Packet.SoundModem.SamplePlugin;

/// <summary>
/// One sample per bit, plus or minus one, behind a 24-bit unique word and a 16-bit length. Not a
/// modem: a frame codec shaped like one, so the plugin seam can be tested on frames.
/// </summary>
/// <remarks>
/// The receive side is a streaming state machine with a bounded payload buffer, which is the
/// property worth having in a sample: it is what a real plugin has to do, and a sample that
/// accumulated the whole stream and searched it would model the wrong thing.
/// </remarks>
internal sealed class SampleModem(string mode, Action<byte[]> frameReceived) : IModem
{
    /// <summary>0xACFFAC, one sample per bit. Long enough not to appear in silence or in a
    /// payload by accident often enough to matter for a test.</summary>
    private const int UniqueWord = 0xACFFAC;
    private const int UniqueWordBits = 24;
    private const int UniqueWordMask = (1 << UniqueWordBits) - 1;
    private const int LengthBits = 16;

    /// <summary>The largest frame this will assemble. A length field is a thing the channel makes
    /// up, so it gets a ceiling rather than a trusting <c>new byte[length]</c>.</summary>
    private const int MaxFrameBytes = 2048;

    private readonly byte[] _payload = new byte[MaxFrameBytes];

    private State _state;
    private int _shift;
    private int _bitsSeen;
    private int _expectedBytes;
    private int _payloadBits;
    private double _energy;

    private enum State
    {
        Hunting,
        ReadingLength,
        ReadingPayload,
    }

    /// <inheritdoc/>
    public string Mode => mode;

    /// <inheritdoc/>
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc/>
    public bool CarrierDetect => _state != State.Hunting;

    /// <inheritdoc/>
    public bool ChannelBusy => _energy > 0.01;

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            // A one-pole envelope, so ChannelBusy answers something about the recent past rather
            // than about the last sample.
            _energy = (_energy * 0.999) + (sample * sample * 0.001);
            Consume(sample >= 0 ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        // TXDELAY at the loopback mode's 12 kHz; the exact count does not matter to anything here,
        // only that it is silence the receiver walks through without syncing on.
        int lead = Math.Max(0, txDelayMilliseconds) * 12;
        int length = Math.Min(ax25Frame.Length, MaxFrameBytes);
        var audio = new float[lead + UniqueWordBits + LengthBits + (length * 8)];
        int at = lead;

        for (int b = UniqueWordBits - 1; b >= 0; b--)
        {
            audio[at++] = ((UniqueWord >> b) & 1) == 1 ? 1f : -1f;
        }

        for (int b = LengthBits - 1; b >= 0; b--)
        {
            audio[at++] = ((length >> b) & 1) == 1 ? 1f : -1f;
        }

        for (int i = 0; i < length; i++)
        {
            for (int b = 7; b >= 0; b--)
            {
                audio[at++] = ((ax25Frame[i] >> b) & 1) == 1 ? 1f : -1f;
            }
        }

        return audio;
    }

    /// <inheritdoc/>
    public void ResetCarrierState()
    {
        _state = State.Hunting;
        _shift = 0;
        _bitsSeen = 0;
        _energy = 0;
    }

    private void Consume(int bit)
    {
        switch (_state)
        {
            case State.Hunting:
                _shift = ((_shift << 1) | bit) & UniqueWordMask;
                if (_shift == UniqueWord)
                {
                    _state = State.ReadingLength;
                    _shift = 0;
                    _bitsSeen = 0;
                }

                return;

            case State.ReadingLength:
                _shift = (_shift << 1) | bit;
                if (++_bitsSeen < LengthBits)
                {
                    return;
                }

                _expectedBytes = _shift;
                _shift = 0;
                _bitsSeen = 0;
                _payloadBits = 0;
                if (_expectedBytes is < 1 or > MaxFrameBytes)
                {
                    // A length the channel made up. Back to hunting rather than trusting it.
                    ResetCarrierState();
                    return;
                }

                _state = State.ReadingPayload;
                return;

            case State.ReadingPayload:
                int index = _payloadBits >> 3;
                int offset = _payloadBits & 7;
                if (offset == 0)
                {
                    _payload[index] = 0;
                }

                _payload[index] |= (byte)(bit << (7 - offset));
                if (++_payloadBits < _expectedBytes * 8)
                {
                    return;
                }

                Deliver();
                return;

            default:
                return;
        }
    }

    private void Deliver()
    {
        byte[] frame = _payload[.._expectedBytes];
        ResetCarrierState();

        // Both paths, in the order a real modem fires them: the monitor event and then the sink.
        // Nothing here is ever MonitorOnly, so the sink always gets it.
        FrameDecoded?.Invoke(
            frame,
            new FrameQuality(
                Mode, frame.Length, CorrectedBytes: 0, CrcValid: null));
        frameReceived(frame);
    }
}
