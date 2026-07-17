using M0LTE.Fec;

namespace Packet.SoundModem.Hdlc;

/// <summary>
/// Streaming HDLC deframer for AX.25: hunts 0x7E flags in a logical-bit stream
/// (post-NRZI-decode), removes stuffed zeros, assembles LSB-first bytes and emits frames
/// whose CRC-16/X-25 frame check sequence verifies. Seven or more consecutive ones abort
/// the frame in progress. One instance per receive bit stream; not thread-safe.
/// </summary>
public sealed class HdlcDeframer
{
    /// <summary>Shortest emitted frame in bytes, FCS excluded: a v2.2 minimal frame is
    /// destination + source + control = 15 bytes.</summary>
    public const int MinFrameBytes = 15;

    private readonly int _maxFrameBytes;
    private readonly byte[] _buffer;
    private readonly Action<byte[]> _frameReceived;

    private int _flagShift;      // last 8 line bits, newest in bit 0
    private int _onesRun;        // consecutive logical 1s (for destuff/abort)
    private int _byteShift;      // bits of the byte being assembled (LSB first)
    private int _bitCount;       // bits collected into the current byte
    private int _length;         // bytes collected for the current frame
    private bool _inFrame;

    /// <summary>Creates a deframer delivering good frames (FCS stripped) to
    /// <paramref name="frameReceived"/>.</summary>
    /// <param name="frameReceived">Called synchronously from <see cref="PushBit"/> for every
    /// frame whose FCS verifies.</param>
    /// <param name="maxFrameBytes">Upper bound on the frame size collected (FCS included);
    /// larger accumulations are discarded as noise. Default fits AX.25 with a 1023-byte
    /// payload comfortably.</param>
    public HdlcDeframer(Action<byte[]> frameReceived, int maxFrameBytes = 1100)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, MinFrameBytes + 2);
        _frameReceived = frameReceived;
        _maxFrameBytes = maxFrameBytes;
        _buffer = new byte[maxFrameBytes];
    }

    /// <summary>Count of frames dropped for a bad FCS since construction (diagnostics).</summary>
    public long CrcFailures { get; private set; }

    /// <summary>Pushes one logical bit (0/1) through the deframer.</summary>
    public void PushBit(int bit)
    {
        bit &= 1;
        _flagShift = ((_flagShift << 1) | bit) & 0xFF;

        if (_flagShift == 0x7E)
        {
            // Flag: closes any frame in progress and opens the next. The six ones inside
            // the flag will have bumped _onesRun; that state dies with the reset below.
            EndFrame();
            _inFrame = true;
            _byteShift = 0;
            _bitCount = 0;
            _length = 0;
            _onesRun = 0;
            return;
        }

        if (bit == 1)
        {
            _onesRun++;
            if (_onesRun >= 7)
            {
                // Abort sequence: discard and go back to hunting for a flag.
                _inFrame = false;
                return;
            }
        }
        else
        {
            if (_onesRun == 5)
            {
                _onesRun = 0;
                return; // stuffed zero: drop it
            }

            _onesRun = 0;
        }

        if (!_inFrame)
        {
            return;
        }

        // HDLC bytes go over the air least-significant bit first.
        _byteShift = (_byteShift >> 1) | (bit << 7);
        if (++_bitCount == 8)
        {
            _bitCount = 0;
            if (_length == _maxFrameBytes)
            {
                _inFrame = false; // oversize: noise or not for us
                return;
            }

            _buffer[_length++] = (byte)_byteShift;
            _byteShift = 0;
        }
    }

    private void EndFrame()
    {
        // By the time the flag's final bit completes the 0x7E pattern, its first 7 bits
        // have already passed through the byte assembler — so a byte-aligned frame always
        // shows exactly 7 residual bits here. Anything else means a slip mid-frame.
        if (!_inFrame || _length < MinFrameBytes + 2 || _bitCount != 7)
        {
            return; // no frame, runt, or a non-integral byte count — silently discard
        }

        var content = _buffer.AsSpan(0, _length - 2);
        ushort fcs = (ushort)(_buffer[_length - 2] | (_buffer[_length - 1] << 8));
        if (Crc16X25.Compute(content) != fcs)
        {
            CrcFailures++;
            return;
        }

        _frameReceived(content.ToArray());
    }
}
