using System.Security.Cryptography;

namespace Packet.SoundModem.TncTest;

/// <summary>What one FLAC file turned out to hold.</summary>
/// <param name="Channels">One normalised (-1..1) array per channel, in file order.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="BitsPerSample">Source word length, before normalisation.</param>
/// <param name="Md5Verified">
/// Whether the decoded audio hashed to the MD5 the encoder wrote into STREAMINFO. Null when the
/// file carries no signature (all-zero, which is legal). This is the whole reason a decoder can
/// be written here rather than shelled out to: the format ships its own end-to-end proof, so
/// "did we decode this correctly" is answered by the file itself rather than by inspection, and
/// a benchmark whose audio path is wrong is worse than no benchmark.
/// </param>
internal sealed record FlacAudio(
    float[][] Channels, int SampleRate, int BitsPerSample, bool? Md5Verified)
{
    /// <summary>Sample instants, not samples times channels.</summary>
    public int Length => Channels.Length == 0 ? 0 : Channels[0].Length;
}

/// <summary>
/// A FLAC decoder, because the WA8LMF TNC Test CD corpus is distributed as FLAC and a benchmark
/// that cannot open its own corpus without a pre-conversion step is a benchmark nobody runs the
/// same way twice.
/// </summary>
/// <remarks>
/// <para>Written from the format specification (RFC 9639, which pins the same bit layout the
/// long-standing xiph.org format description defined). It covers the whole of what a frame may
/// contain - CONSTANT, VERBATIM, FIXED and LPC subframes, both Rice partition methods including
/// the escape to raw residuals, wasted bits, and all four channel assignments - rather than the
/// subset this corpus happens to use, because narrowing it would only move the failure to the
/// next file somebody points at it.</para>
/// <para>Correctness is not asserted, it is checked: every frame is verified against its own
/// CRC-16, and the whole decode is hashed and compared against the MD5 the encoder left in
/// STREAMINFO. <see cref="Read"/> reports the verdict and the harness prints it, because a
/// benchmark whose audio path is silently wrong is worse than no benchmark.</para>
/// </remarks>
internal static class FlacReader
{
    /// <summary>Sample rates the 4-bit frame-header code names directly; 0 means "as
    /// STREAMINFO said" and 12 to 14 mean "read it from the end of the header".</summary>
    private static readonly int[] RateTable =
        [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    /// <summary>Word lengths the 3-bit frame-header code names; 0 means "as STREAMINFO said".</summary>
    private static readonly int[] DepthTable = [0, 8, 12, 0, 16, 20, 24, 32];

    /// <summary>x^8 + x^2 + x + 1, the FLAC frame-header check.</summary>
    private static readonly byte[] Crc8Table = BuildCrc8();

    /// <summary>x^16 + x^15 + x^2 + 1, the FLAC whole-frame check.</summary>
    private static readonly ushort[] Crc16Table = BuildCrc16();

    /// <summary>Decodes a whole FLAC file to normalised per-channel audio.</summary>
    public static FlacAudio Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 8 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a FLAC stream");
        }

        var reader = new FlacBitReader(data);
        reader.SeekByte(4);
        var stream = ReadMetadata(reader);

        var channels = new ChannelWriter[stream.Channels];
        var block = new int[stream.Channels][];
        for (int c = 0; c < stream.Channels; c++)
        {
            channels[c] = new ChannelWriter(stream.TotalSamples);
        }

        bool signed = stream.Md5.Any(b => b != 0);
        using var hash = signed ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        byte[] interleaved = [];

        while (reader.BitsRemaining >= 32)
        {
            int frameStart = reader.BytePosition;
            if (!TryReadFrameHeader(reader, stream, out FrameHeader header))
            {
                break;
            }

            VerifyCrc8(data, frameStart, reader.BytePosition);

            if (header.Channels != stream.Channels)
            {
                throw new InvalidDataException(
                    $"FLAC frame at byte {frameStart} has {header.Channels} channels, "
                    + $"STREAMINFO says {stream.Channels}");
            }

            for (int c = 0; c < stream.Channels; c++)
            {
                if (block[c] is null || block[c].Length < header.BlockSize)
                {
                    block[c] = new int[header.BlockSize];
                }

                DecodeSubframe(
                    reader, block[c], header.BlockSize, header.BitsPerSample + SideBits(header, c));
            }

            Decorrelate(block, header);

            reader.AlignToByte();
            int declared = (int)reader.ReadBits(16);
            int computed = Crc16(data, frameStart, reader.BytePosition - 2);
            if (declared != computed)
            {
                throw new InvalidDataException(
                    $"FLAC frame at byte {frameStart} failed its CRC-16 "
                    + $"(read 0x{declared:X4}, computed 0x{computed:X4})");
            }

            for (int c = 0; c < stream.Channels; c++)
            {
                channels[c].Append(block[c], header.BlockSize, header.BitsPerSample);
            }

            if (hash is not null)
            {
                int bytesPerSample = header.BitsPerSample / 8;
                int needed = header.BlockSize * stream.Channels * bytesPerSample;
                if (interleaved.Length < needed)
                {
                    interleaved = new byte[needed];
                }

                Interleave(block, header.BlockSize, stream.Channels, bytesPerSample, interleaved);
                hash.AppendData(interleaved, 0, needed);
            }
        }

        bool? verified = hash is null
            ? null
            : hash.GetHashAndReset().AsSpan().SequenceEqual(stream.Md5);

        return new FlacAudio(
            [.. channels.Select(c => c.ToArray())], stream.SampleRate, stream.BitsPerSample, verified);
    }

    /// <summary>What STREAMINFO promised about the stream.</summary>
    private readonly record struct StreamInfo(
        int SampleRate, int Channels, int BitsPerSample, long TotalSamples, byte[] Md5);

    /// <summary>What one frame's header declared about itself.</summary>
    private readonly record struct FrameHeader(
        int BlockSize, int Channels, int ChannelAssignment, int BitsPerSample);

    /// <summary>The side channel of a stereo pair carries a difference and so needs one bit more
    /// than the frame's declared depth; which of the two it is depends on the assignment.</summary>
    private static int SideBits(FrameHeader header, int channel) => header.ChannelAssignment switch
    {
        8 or 10 => channel == 1 ? 1 : 0,
        9 => channel == 0 ? 1 : 0,
        _ => 0,
    };

    private static StreamInfo ReadMetadata(FlacBitReader reader)
    {
        int rate = 0, channels = 0, depth = 0;
        long totalSamples = 0;
        var md5 = new byte[16];
        bool seenStreamInfo = false;

        while (true)
        {
            bool last = reader.ReadBits(1) != 0;
            uint type = reader.ReadBits(7);
            int length = (int)reader.ReadBits(24);

            if (type == 0)
            {
                if (length != 34)
                {
                    throw new InvalidDataException($"STREAMINFO is {length} bytes, expected 34");
                }

                reader.ReadBits(16);                        // minimum block size
                reader.ReadBits(16);                        // maximum block size
                reader.ReadBits(24);                        // minimum frame size
                reader.ReadBits(24);                        // maximum frame size
                rate = (int)reader.ReadBits(20);
                channels = (int)reader.ReadBits(3) + 1;
                depth = (int)reader.ReadBits(5) + 1;
                totalSamples = (long)reader.ReadBits64(36);
                for (int i = 0; i < 16; i++)
                {
                    md5[i] = (byte)reader.ReadBits(8);
                }

                seenStreamInfo = true;
            }
            else
            {
                reader.SkipBytes(length);
            }

            if (last)
            {
                break;
            }
        }

        if (!seenStreamInfo || rate == 0 || channels == 0)
        {
            throw new InvalidDataException("FLAC stream has no usable STREAMINFO");
        }

        if (depth % 8 != 0)
        {
            // Only the MD5 check needs this, being defined over whole bytes per sample; no
            // encoder in the wild writes anything but 8, 16, 24 or 32.
            throw new NotSupportedException($"{depth}-bit FLAC is not supported");
        }

        return new StreamInfo(rate, channels, depth, totalSamples, md5);
    }

    private static bool TryReadFrameHeader(
        FlacBitReader reader, StreamInfo stream, out FrameHeader header)
    {
        header = default;

        if (reader.ReadBits(14) != 0x3FFE)
        {
            // Not a sync code. A well-formed stream only reaches here past the last frame,
            // where an ID3v1 tag or padding may follow the audio.
            return false;
        }

        if (reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("FLAC frame header reserved bit set");
        }

        reader.ReadBits(1);                                 // blocking strategy
        int blockSizeCode = (int)reader.ReadBits(4);
        int rateCode = (int)reader.ReadBits(4);
        int assignment = (int)reader.ReadBits(4);
        int depthCode = (int)reader.ReadBits(3);
        if (reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("FLAC frame header reserved bit set");
        }

        SkipUtf8Number(reader);

        int blockSize = blockSizeCode switch
        {
            0 => throw new InvalidDataException("reserved FLAC block size code"),
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            _ => 256 << (blockSizeCode - 8),
        };

        // Read and discard: the rate is a property of the stream, and a frame that disagreed
        // with STREAMINFO would be a stream nothing can play.
        _ = rateCode switch
        {
            <= 11 => rateCode == 0 ? stream.SampleRate : RateTable[rateCode],
            12 => (int)reader.ReadBits(8) * 1000,
            13 => (int)reader.ReadBits(16),
            14 => (int)reader.ReadBits(16) * 10,
            _ => throw new InvalidDataException("invalid FLAC sample rate code"),
        };

        int depth = DepthTable[depthCode] == 0 ? stream.BitsPerSample : DepthTable[depthCode];
        int channels = assignment switch
        {
            <= 7 => assignment + 1,
            <= 10 => 2,
            _ => throw new InvalidDataException($"reserved FLAC channel assignment {assignment}"),
        };

        reader.ReadBits(8);                                 // header CRC-8, checked by the caller
        header = new FrameHeader(blockSize, channels, assignment, depth);
        return true;
    }

    /// <summary>Steps over the frame or sample number, which is UTF-8-shaped but up to 36 bits
    /// wide. Nothing here needs its value: frames are decoded in the order they appear.</summary>
    private static void SkipUtf8Number(FlacBitReader reader)
    {
        uint first = reader.ReadBits(8);
        int ones = 0;
        while (ones < 8 && (first & (0x80u >> ones)) != 0)
        {
            ones++;
        }

        if (ones == 0)
        {
            return;
        }

        if (ones is 1 or > 7)
        {
            throw new InvalidDataException("malformed UTF-8 coded number in FLAC frame header");
        }

        for (int i = 1; i < ones; i++)
        {
            if ((reader.ReadBits(8) & 0xC0) != 0x80)
            {
                throw new InvalidDataException("malformed UTF-8 coded number in FLAC frame header");
            }
        }
    }

    private static void DecodeSubframe(FlacBitReader reader, int[] output, int blockSize, int bits)
    {
        if (reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("FLAC subframe padding bit set");
        }

        int type = (int)reader.ReadBits(6);
        int wasted = reader.ReadBits(1) != 0 ? reader.ReadUnary() + 1 : 0;
        bits -= wasted;
        if (bits <= 0)
        {
            throw new InvalidDataException("FLAC subframe wastes every bit it has");
        }

        switch (type)
        {
            case 0:
                Array.Fill(output, reader.ReadSigned(bits), 0, blockSize);
                break;
            case 1:
                for (int i = 0; i < blockSize; i++)
                {
                    output[i] = reader.ReadSigned(bits);
                }

                break;
            case >= 8 and <= 12:
                DecodeFixed(reader, output, blockSize, bits, type - 8);
                break;
            case >= 32:
                DecodeLpc(reader, output, blockSize, bits, type - 31);
                break;
            default:
                throw new InvalidDataException($"reserved FLAC subframe type {type}");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < blockSize; i++)
            {
                output[i] <<= wasted;
            }
        }
    }

    private static void DecodeFixed(
        FlacBitReader reader, int[] output, int blockSize, int bits, int order)
    {
        for (int i = 0; i < order; i++)
        {
            output[i] = reader.ReadSigned(bits);
        }

        DecodeResidual(reader, output, blockSize, order);

        // The fixed predictors are successive differences of a polynomial fit, so restoring one
        // is running the difference back up: each order adds the previous order's running sum.
        switch (order)
        {
            case 1:
                for (int i = 1; i < blockSize; i++)
                {
                    output[i] += output[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < blockSize; i++)
                {
                    output[i] += (2 * output[i - 1]) - output[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < blockSize; i++)
                {
                    output[i] += (3 * output[i - 1]) - (3 * output[i - 2]) + output[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < blockSize; i++)
                {
                    output[i] += (4 * output[i - 1]) - (6 * output[i - 2])
                        + (4 * output[i - 3]) - output[i - 4];
                }

                break;
        }
    }

    private static void DecodeLpc(
        FlacBitReader reader, int[] output, int blockSize, int bits, int order)
    {
        for (int i = 0; i < order; i++)
        {
            output[i] = reader.ReadSigned(bits);
        }

        int precision = (int)reader.ReadBits(4) + 1;
        if (precision == 16)
        {
            throw new InvalidDataException("invalid FLAC LPC coefficient precision");
        }

        int shift = reader.ReadSigned(5);
        if (shift < 0)
        {
            throw new InvalidDataException("negative FLAC LPC shift");
        }

        var coefficients = new int[order];
        for (int i = 0; i < order; i++)
        {
            coefficients[i] = reader.ReadSigned(precision);
        }

        DecodeResidual(reader, output, blockSize, order);

        for (int i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * output[i - 1 - j];
            }

            output[i] += (int)(sum >> shift);
        }
    }

    private static void DecodeResidual(
        FlacBitReader reader, int[] output, int blockSize, int predictorOrder)
    {
        uint method = reader.ReadBits(2);
        if (method > 1)
        {
            throw new InvalidDataException($"reserved FLAC residual coding method {method}");
        }

        int parameterBits = method == 0 ? 4 : 5;
        uint escape = method == 0 ? 15u : 31u;
        int partitionOrder = (int)reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int partitionSize = blockSize >> partitionOrder;

        if (partitionSize << partitionOrder != blockSize || partitionSize < predictorOrder)
        {
            throw new InvalidDataException(
                $"a FLAC block of {blockSize} does not divide into {partitions} partitions");
        }

        int at = predictorOrder;
        for (int p = 0; p < partitions; p++)
        {
            int count = partitionSize - (p == 0 ? predictorOrder : 0);
            uint parameter = reader.ReadBits(parameterBits);

            if (parameter == escape)
            {
                int raw = (int)reader.ReadBits(5);
                for (int i = 0; i < count; i++)
                {
                    output[at++] = raw == 0 ? 0 : reader.ReadSigned(raw);
                }

                continue;
            }

            int shift = (int)parameter;
            for (int i = 0; i < count; i++)
            {
                uint folded = ((uint)reader.ReadUnary() << shift) | reader.ReadBits(shift);
                // Rice codes carry a zig-zag folded signed value: even positive, odd negative.
                output[at++] = (int)(folded >> 1) ^ -(int)(folded & 1);
            }
        }
    }

    private static void Decorrelate(int[][] block, FrameHeader header)
    {
        int n = header.BlockSize;
        switch (header.ChannelAssignment)
        {
            case 8:                                          // left, side = left - right
                for (int i = 0; i < n; i++)
                {
                    block[1][i] = block[0][i] - block[1][i];
                }

                break;
            case 9:                                          // side = left - right, right
                for (int i = 0; i < n; i++)
                {
                    block[0][i] += block[1][i];
                }

                break;
            case 10:                                         // mid, side
                for (int i = 0; i < n; i++)
                {
                    int side = block[1][i];
                    // The mid channel dropped the low bit of left + right; the side channel's own
                    // low bit is that parity, because left + right and left - right agree on it.
                    int mid = (block[0][i] << 1) | (side & 1);
                    block[0][i] = (mid + side) >> 1;
                    block[1][i] = (mid - side) >> 1;
                }

                break;
        }
    }

    private static void Interleave(
        int[][] block, int blockSize, int channels, int bytesPerSample, byte[] destination)
    {
        int at = 0;
        for (int i = 0; i < blockSize; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int value = block[c][i];
                for (int b = 0; b < bytesPerSample; b++)
                {
                    destination[at++] = (byte)(value >> (8 * b));
                }
            }
        }
    }

    private static void VerifyCrc8(byte[] data, int from, int to)
    {
        int crc = 0;
        for (int i = from; i < to - 1; i++)
        {
            crc = Crc8Table[crc ^ data[i]];
        }

        if (crc != data[to - 1])
        {
            throw new InvalidDataException(
                $"FLAC frame header at byte {from} failed its CRC-8 "
                + $"(read 0x{data[to - 1]:X2}, computed 0x{crc:X2})");
        }
    }

    private static int Crc16(byte[] data, int from, int to)
    {
        int crc = 0;
        for (int i = from; i < to; i++)
        {
            crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ data[i]) & 0xFF]) & 0xFFFF;
        }

        return crc;
    }

    private static byte[] BuildCrc8()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }

            table[i] = (byte)crc;
        }

        return table;
    }

    private static ushort[] BuildCrc16()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }

            table[i] = (ushort)crc;
        }

        return table;
    }

    /// <summary>Grows one channel's normalised samples, sized up front when STREAMINFO said how
    /// many there would be (it nearly always does) and doubling when it did not.</summary>
    private sealed class ChannelWriter(long expected)
    {
        private float[] _samples =
            new float[expected is > 0 and < int.MaxValue ? expected : 1 << 16];

        private int _count;

        public void Append(int[] block, int blockSize, int bitsPerSample)
        {
            if (_count + blockSize > _samples.Length)
            {
                Array.Resize(ref _samples, Math.Max(_count + blockSize, _samples.Length * 2));
            }

            float scale = 1f / (1 << (bitsPerSample - 1));
            for (int i = 0; i < blockSize; i++)
            {
                _samples[_count++] = block[i] * scale;
            }
        }

        public float[] ToArray()
        {
            if (_samples.Length != _count)
            {
                Array.Resize(ref _samples, _count);
            }

            return _samples;
        }
    }
}
