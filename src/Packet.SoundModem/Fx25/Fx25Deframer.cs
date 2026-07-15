using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Fx25;

/// <summary>
/// Streaming FX.25 receiver: hunts the 64-bit correlation tags in the logical
/// (NRZI-decoded) bit stream, collects the block, repairs it with Reed-Solomon, then
/// runs the corrected bytes through a normal HDLC deframer to extract the embedded
/// frame. Feed it the same bit stream as the plain <see cref="HdlcDeframer"/>; on clean
/// signals both will produce the frame, so deduplicate downstream.
/// </summary>
public sealed class Fx25Deframer
{
    private readonly Action<byte[], int> _frameReceived;
    private ulong _accumulator;
    private int _formatIndex = -1;
    private byte[] _block = [];
    private int _byteIndex;
    private int _bitMask;

    /// <summary>Creates a deframer delivering (frame, correctedBytes) for every FX.25
    /// block whose RS decode and embedded HDLC check out.</summary>
    public Fx25Deframer(Action<byte[], int> frameReceived)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _frameReceived = frameReceived;
    }

    /// <summary>Blocks that matched a tag but failed Reed-Solomon decoding.</summary>
    public long RsFailures { get; private set; }

    /// <summary>Pushes one logical bit (post-NRZI-decode).</summary>
    public void PushBit(int bit)
    {
        bit &= 1;
        if (_formatIndex < 0)
        {
            // Tag hunt: bits arrive LSB-first, so shift right into the top.
            _accumulator = (_accumulator >> 1) | ((ulong)bit << 63);
            int match = Fx25Codec.FindTag(_accumulator);
            if (match >= 0)
            {
                var format = Fx25Codec.Formats[match];
                _formatIndex = match;
                _block = new byte[format.RsDataBytes + format.ParityBytes];
                _byteIndex = 0;
                _bitMask = 1;
            }

            return;
        }

        var current = Fx25Codec.Formats[_formatIndex];
        if (bit != 0)
        {
            _block[_byteIndex] |= (byte)_bitMask;
        }

        _bitMask <<= 1;
        if (_bitMask <= 0x80)
        {
            return;
        }

        _bitMask = 1;
        _byteIndex++;

        // Untransmitted shortening region stays zero; jump from the end of the radio
        // data straight to the parity bytes.
        if (_byteIndex == current.RadioDataBytes && current.RadioDataBytes < current.RsDataBytes)
        {
            _byteIndex = current.RsDataBytes;
        }

        if (_byteIndex < _block.Length)
        {
            return;
        }

        Complete(current);
        _formatIndex = -1;
        _accumulator = 0;
    }

    private void Complete(in Fx25Codec.TagFormat format)
    {
        int corrected = Fx25Codec.RsFor(format.ParityBytes).Decode(_block);
        if (corrected < 0)
        {
            RsFailures++;
            return;
        }

        // Corrections landing in the implicit zero padding mean a miscorrection.
        for (int i = format.RadioDataBytes; i < format.RsDataBytes; i++)
        {
            if (_block[i] != 0)
            {
                RsFailures++;
                return;
            }
        }

        byte[]? frame = null;
        var deframer = new HdlcDeframer(f => frame ??= f);
        for (int i = 0; i < format.RadioDataBytes; i++)
        {
            for (int mask = 1; mask <= 0x80; mask <<= 1)
            {
                deframer.PushBit((_block[i] & mask) != 0 ? 1 : 0);
            }
        }

        if (frame is not null)
        {
            _frameReceived(frame, corrected);
        }
    }
}
