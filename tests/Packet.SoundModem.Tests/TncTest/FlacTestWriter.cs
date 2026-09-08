using System.Security.Cryptography;

namespace Packet.SoundModem.Tests.TncTest;

/// <summary>Which shape of subframe the test encoder should emit.</summary>
public enum FlacEncoding
{
    /// <summary>A CONSTANT subframe: one value, repeated.</summary>
    Constant,

    /// <summary>A VERBATIM subframe: the samples, uncompressed.</summary>
    Verbatim,

    /// <summary>FIXED order 0 (the residual is the signal), Rice method 0.</summary>
    Fixed0,

    /// <summary>FIXED order 1, Rice method 0.</summary>
    Fixed1,

    /// <summary>FIXED order 2, Rice method 0, split into four partitions.</summary>
    Fixed2,

    /// <summary>FIXED order 3, Rice method 0.</summary>
    Fixed3,

    /// <summary>FIXED order 4, Rice method 1 (5-bit parameters).</summary>
    Fixed4,

    /// <summary>FIXED order 2 with the residual escaped to raw fixed-width values.</summary>
    FixedEscaped,

    /// <summary>FIXED order 2 where every sample is a multiple of four, coded with two
    /// wasted bits.</summary>
    FixedWasted,

    /// <summary>An order-3 LPC subframe with quantised coefficients.</summary>
    Lpc,
}

/// <summary>
/// A minimal FLAC encoder, written only so that <c>FlacReader</c> can be tested against streams
/// this repository produced rather than against files it happens to have.
/// </summary>
/// <remarks>
/// It exists because the decoder's job is to read what other encoders write, and the parts of the
/// format that are easy to get wrong - the fixed and LPC predictors, Rice partitioning, the
/// escape to raw residuals, wasted bits, and the four stereo decorrelations - are exactly the
/// parts a single sample file will not exercise. Nothing here tries to compress well: it picks
/// whichever encoding the test asked for and writes it correctly, which is the only property
/// under test. The MD5 in STREAMINFO is real, so a decode that goes wrong anywhere fails the
/// stream's own check as well as the assertion.
/// </remarks>
public static class FlacTestWriter
{
    /// <summary>
    /// Encodes <paramref name="channels"/> as a FLAC stream.
    /// </summary>
    /// <param name="channels">One array of samples per channel, all the same length, each
    /// value inside <paramref name="bitsPerSample"/> bits signed.</param>
    /// <param name="sampleRate">Rate to declare.</param>
    /// <param name="bitsPerSample">Word length; a multiple of 8.</param>
    /// <param name="encoding">The subframe shape to use throughout.</param>
    /// <param name="channelAssignment">0 to 7 for independent channels, 8 left/side,
    /// 9 right/side, 10 mid/side.</param>
    /// <param name="blockSize">Samples per frame; the last frame may be shorter.</param>
    public static byte[] Write(
        int[][] channels,
        int sampleRate,
        int bitsPerSample,
        FlacEncoding encoding,
        int channelAssignment = 0,
        int blockSize = 64)
    {
        int count = channels[0].Length;
        var stream = new MemoryStream();
        stream.Write("fLaC"u8);

        var streamInfo = new BitWriter();
        streamInfo.Write(blockSize, 16);                     // minimum block size
        streamInfo.Write(blockSize, 16);                     // maximum block size
        streamInfo.Write(0, 24);                             // minimum frame size, unknown
        streamInfo.Write(0, 24);                             // maximum frame size, unknown
        streamInfo.Write(sampleRate, 20);
        streamInfo.Write(channels.Length - 1, 3);
        streamInfo.Write(bitsPerSample - 1, 5);
        streamInfo.Write((int)((uint)count >> 16), 20);      // total samples, high 20 of 36
        streamInfo.Write(count & 0xFFFF, 16);
        foreach (byte b in Md5Of(channels, bitsPerSample))
        {
            streamInfo.Write(b, 8);
        }

        byte[] streamInfoBytes = streamInfo.ToArray();
        stream.WriteByte(0x80);                              // last metadata block, type 0
        stream.WriteByte((byte)(streamInfoBytes.Length >> 16));
        stream.WriteByte((byte)(streamInfoBytes.Length >> 8));
        stream.WriteByte((byte)streamInfoBytes.Length);
        stream.Write(streamInfoBytes);

        int frameNumber = 0;
        for (int at = 0; at < count; at += blockSize, frameNumber++)
        {
            int length = Math.Min(blockSize, count - at);
            int[][] coded = Decorrelate(channels, at, length, channelAssignment);
            stream.Write(Frame(coded, length, frameNumber, channelAssignment, bitsPerSample, encoding));
        }

        return stream.ToArray();
    }

    /// <summary>Turns left and right into whatever pair the assignment says goes on the wire.</summary>
    private static int[][] Decorrelate(int[][] channels, int at, int length, int assignment)
    {
        var coded = new int[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            coded[c] = channels[c][at..(at + length)];
        }

        if (assignment <= 7)
        {
            return coded;
        }

        int[] left = coded[0];
        int[] right = coded[1];
        var first = new int[length];
        var second = new int[length];

        for (int i = 0; i < length; i++)
        {
            switch (assignment)
            {
                case 8:
                    first[i] = left[i];
                    second[i] = left[i] - right[i];
                    break;
                case 9:
                    first[i] = left[i] - right[i];
                    second[i] = right[i];
                    break;
                default:
                    // The mid channel is the sum with its low bit dropped; the side channel's own
                    // low bit is what lets the decoder put it back.
                    first[i] = (left[i] + right[i]) >> 1;
                    second[i] = left[i] - right[i];
                    break;
            }
        }

        return [first, second];
    }

    private static byte[] Frame(
        int[][] coded, int length, int frameNumber, int assignment, int bitsPerSample,
        FlacEncoding encoding)
    {
        var frame = new BitWriter();
        frame.Write(0x3FFE, 14);                             // sync
        frame.Write(0, 1);                                   // reserved
        frame.Write(0, 1);                                   // fixed block size, so a frame number
        frame.Write(7, 4);                                   // block size follows, 16 bit
        frame.Write(0, 4);                                   // sample rate: as STREAMINFO said
        frame.Write(assignment, 4);
        frame.Write(0, 3);                                   // sample size: as STREAMINFO said
        frame.Write(0, 1);                                   // reserved
        frame.Write(frameNumber, 8);                         // UTF-8 coded, one byte below 128
        frame.Write(length - 1, 16);

        byte[] header = frame.ToArray();
        frame.Write(Crc8(header), 8);

        for (int c = 0; c < coded.Length; c++)
        {
            int bits = bitsPerSample + SideBits(assignment, c);
            Subframe(frame, coded[c], length, bits, encoding);
        }

        frame.AlignToByte();
        byte[] body = frame.ToArray();
        frame.Write(Crc16(body), 16);
        return frame.ToArray();
    }

    private static int SideBits(int assignment, int channel) => assignment switch
    {
        8 or 10 => channel == 1 ? 1 : 0,
        9 => channel == 0 ? 1 : 0,
        _ => 0,
    };

    private static void Subframe(
        BitWriter writer, int[] samples, int length, int bits, FlacEncoding encoding)
    {
        int wasted = encoding == FlacEncoding.FixedWasted ? 2 : 0;
        var values = samples;
        if (wasted > 0)
        {
            values = new int[length];
            for (int i = 0; i < length; i++)
            {
                values[i] = samples[i] >> wasted;
            }
        }

        int valueBits = bits - wasted;

        writer.Write(0, 1);                                  // padding
        writer.Write(TypeCode(encoding), 6);
        writer.Write(wasted > 0 ? 1 : 0, 1);
        if (wasted > 0)
        {
            writer.WriteUnary(wasted - 1);
        }

        switch (encoding)
        {
            case FlacEncoding.Constant:
                writer.Write(values[0], valueBits);
                return;

            case FlacEncoding.Verbatim:
                for (int i = 0; i < length; i++)
                {
                    writer.Write(values[i], valueBits);
                }

                return;

            case FlacEncoding.Lpc:
            {
                const int order = 3;
                const int shift = 8;
                int[] coefficients = [180, -90, 20];
                for (int i = 0; i < order; i++)
                {
                    writer.Write(values[i], valueBits);
                }

                writer.Write(11 - 1, 4);                     // coefficient precision
                writer.Write(shift, 5);
                foreach (int coefficient in coefficients)
                {
                    writer.Write(coefficient, 11);
                }

                var residual = new int[length];
                for (int i = order; i < length; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++)
                    {
                        sum += (long)coefficients[j] * values[i - 1 - j];
                    }

                    residual[i] = values[i] - (int)(sum >> shift);
                }

                Residual(writer, residual, length, order, method: 0, partitionOrder: 0, escape: false);
                return;
            }

            default:
            {
                int order = FixedOrder(encoding);
                for (int i = 0; i < order; i++)
                {
                    writer.Write(values[i], valueBits);
                }

                var residual = new int[length];
                for (int i = order; i < length; i++)
                {
                    residual[i] = values[i] - Predict(values, i, order);
                }

                int method = encoding == FlacEncoding.Fixed4 ? 1 : 0;
                int partitionOrder = encoding == FlacEncoding.Fixed2 ? 2 : 0;
                Residual(
                    writer, residual, length, order, method, partitionOrder,
                    escape: encoding == FlacEncoding.FixedEscaped);
                return;
            }
        }
    }

    private static int Predict(int[] values, int i, int order) => order switch
    {
        0 => 0,
        1 => values[i - 1],
        2 => (2 * values[i - 1]) - values[i - 2],
        3 => (3 * values[i - 1]) - (3 * values[i - 2]) + values[i - 3],
        _ => (4 * values[i - 1]) - (6 * values[i - 2]) + (4 * values[i - 3]) - values[i - 4],
    };

    private static int FixedOrder(FlacEncoding encoding) => encoding switch
    {
        FlacEncoding.Fixed0 => 0,
        FlacEncoding.Fixed1 => 1,
        FlacEncoding.Fixed3 => 3,
        FlacEncoding.Fixed4 => 4,
        _ => 2,
    };

    private static int TypeCode(FlacEncoding encoding) => encoding switch
    {
        FlacEncoding.Constant => 0,
        FlacEncoding.Verbatim => 1,
        FlacEncoding.Lpc => 31 + 3,
        _ => 8 + FixedOrder(encoding),
    };

    private static void Residual(
        BitWriter writer, int[] residual, int length, int order, int method, int partitionOrder,
        bool escape)
    {
        writer.Write(method, 2);
        writer.Write(partitionOrder, 4);

        int partitions = 1 << partitionOrder;
        int partitionSize = length >> partitionOrder;
        if (partitionSize << partitionOrder != length)
        {
            throw new ArgumentException(
                $"a test block of {length} does not divide into {partitions} partitions",
                nameof(partitionOrder));
        }

        int parameterBits = method == 0 ? 4 : 5;
        int escapeCode = method == 0 ? 15 : 31;

        int at = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = partitionSize - (p == 0 ? order : 0);
            var slice = residual.AsSpan(at, count);

            if (escape)
            {
                int width = 1;
                foreach (int value in slice)
                {
                    while (value < -(1 << (width - 1)) || value >= 1 << (width - 1))
                    {
                        width++;
                    }
                }

                writer.Write(escapeCode, parameterBits);
                writer.Write(width, 5);
                foreach (int value in slice)
                {
                    writer.Write(value, width);
                }
            }
            else
            {
                // Any parameter decodes correctly; a small one just makes a long unary run. Pick
                // the one that keeps the runs short so a test stream stays a sensible size.
                int parameter = 0;
                while (parameter < escapeCode - 1 && !FitsIn(slice, parameter))
                {
                    parameter++;
                }

                writer.Write(parameter, parameterBits);
                foreach (int value in slice)
                {
                    uint folded = (uint)((value << 1) ^ (value >> 31));
                    writer.WriteUnary((int)(folded >> parameter));
                    writer.Write((int)(folded & ((1u << parameter) - 1)), parameter);
                }
            }

            at += count;
        }
    }

    private static bool FitsIn(ReadOnlySpan<int> values, int parameter)
    {
        foreach (int value in values)
        {
            uint folded = (uint)((value << 1) ^ (value >> 31));
            if (folded >> parameter > 32)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Md5Of(int[][] channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        var raw = new byte[channels[0].Length * channels.Length * bytesPerSample];
        int at = 0;
        for (int i = 0; i < channels[0].Length; i++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                for (int b = 0; b < bytesPerSample; b++)
                {
                    raw[at++] = (byte)(channels[c][i] >> (8 * b));
                }
            }
        }

        return MD5.HashData(raw);
    }

    private static int Crc8(byte[] data)
    {
        int crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }

        return crc;
    }

    private static int Crc16(byte[] data)
    {
        int crc = 0;
        foreach (byte value in data)
        {
            crc ^= value << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return crc;
    }

    /// <summary>MSB-first bit writer over a growing buffer.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _partial;
        private int _partialBits;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _partial = (_partial << 1) | ((value >> i) & 1);
                if (++_partialBits == 8)
                {
                    _bytes.Add((byte)_partial);
                    _partial = 0;
                    _partialBits = 0;
                }
            }
        }

        public void WriteUnary(int zeroes)
        {
            for (int i = 0; i < zeroes; i++)
            {
                Write(0, 1);
            }

            Write(1, 1);
        }

        public void AlignToByte()
        {
            while (_partialBits != 0)
            {
                Write(0, 1);
            }
        }

        /// <summary>Everything written so far, which must be a whole number of bytes.</summary>
        public byte[] ToArray() => _partialBits == 0
            ? [.. _bytes]
            : throw new InvalidOperationException("bit writer is mid-byte");
    }
}
