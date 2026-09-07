using System.Numerics;

namespace Packet.SoundModem.TncTest;

/// <summary>
/// MSB-first bit reader over a whole FLAC file held in memory, with the two operations the
/// format's inner loop is made of: a fixed-width field, and a unary count.
/// </summary>
/// <remarks>
/// A 40 minute 44.1 kHz stereo track is around 140 million residuals, so the per-value cost
/// here sets the tool's running time. Bits are pulled into a 64-bit accumulator MSB-aligned
/// and taken off the top, which makes a field read a shift and a mask, and a unary count a
/// single <c>LeadingZeroCount</c> in the common case where the run does not straddle a refill.
/// </remarks>
internal sealed class FlacBitReader(byte[] data)
{
    private readonly byte[] _data = data;
    private int _next;
    private ulong _cache;
    private int _cacheBits;

    /// <summary>Bytes in the file, whether or not they have been read.</summary>
    public int Length => _data.Length;

    /// <summary>The file itself, for the CRCs, which are defined over byte ranges rather
    /// than over the bit stream.</summary>
    public byte[] Data => _data;

    /// <summary>How many whole bits are left, cached plus unread.</summary>
    public long BitsRemaining => _cacheBits + (8L * (_data.Length - _next));

    /// <summary>
    /// The offset of the next unread byte. Only meaningful when the reader is byte-aligned,
    /// which every FLAC frame boundary is.
    /// </summary>
    public int BytePosition => _next - (_cacheBits >> 3);

    /// <summary>Restarts the reader at a byte offset, dropping whatever is cached.</summary>
    public void SeekByte(int position)
    {
        _next = position;
        _cache = 0;
        _cacheBits = 0;
    }

    private void Refill()
    {
        while (_cacheBits <= 56 && _next < _data.Length)
        {
            _cache |= (ulong)_data[_next++] << (56 - _cacheBits);
            _cacheBits += 8;
        }
    }

    /// <summary>Reads <paramref name="count"/> bits (0 to 32) as an unsigned value.</summary>
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_cacheBits < count)
        {
            Refill();
            if (_cacheBits < count)
            {
                throw new InvalidDataException("FLAC stream ended mid-value");
            }
        }

        uint value = (uint)(_cache >> (64 - count));
        _cache <<= count;
        _cacheBits -= count;
        return value;
    }

    /// <summary>Reads <paramref name="count"/> bits (1 to 32) as a two's-complement value.</summary>
    public int ReadSigned(int count)
    {
        uint raw = ReadBits(count);
        // Sign-extend from the field width: shifting an int left then arithmetic-right is the
        // cheapest way to do it and the only one that is correct at count == 32.
        return count == 32 ? (int)raw : (int)(raw << (32 - count)) >> (32 - count);
    }

    /// <summary>Reads up to 64 bits as an unsigned value; used only for the 36-bit sample count.</summary>
    public ulong ReadBits64(int count) =>
        count <= 32 ? ReadBits(count) : ((ulong)ReadBits(count - 32) << 32) | ReadBits(32);

    /// <summary>Counts zero bits up to and including the terminating one bit, returning the
    /// number of zeroes.</summary>
    public int ReadUnary()
    {
        int zeroes = 0;
        while (true)
        {
            if (_cacheBits == 0)
            {
                Refill();
                if (_cacheBits == 0)
                {
                    throw new InvalidDataException("FLAC stream ended inside a unary code");
                }
            }

            int leading = BitOperations.LeadingZeroCount(_cache);
            if (leading < _cacheBits)
            {
                zeroes += leading;
                int consumed = leading + 1;
                _cache <<= consumed;
                _cacheBits -= consumed;
                return zeroes;
            }

            // Every cached bit is a zero: spend them all and go round again.
            zeroes += _cacheBits;
            _cache = 0;
            _cacheBits = 0;
        }
    }

    /// <summary>Discards bits up to the next byte boundary.</summary>
    public void AlignToByte()
    {
        int stray = _cacheBits & 7;
        if (stray != 0)
        {
            _cache <<= stray;
            _cacheBits -= stray;
        }
    }

    /// <summary>Reads a whole byte-aligned run, for metadata blocks the decoder skips.</summary>
    public void SkipBytes(int count) => SeekByte(BytePosition + count);
}
