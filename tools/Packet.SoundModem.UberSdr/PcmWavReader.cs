using System.Buffers.Binary;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// Chunked reader for 16-bit PCM WAV - the read counterpart of <see cref="PcmWavWriter"/>, for
/// captures too large to hold in memory.
/// </summary>
/// <remarks>
/// <para><see cref="Packet.SoundModem.Audio.WavFile.ReadMono"/> reads the whole file into a byte
/// array and then materialises one float per frame per channel. An hour of iq48 is 691 MB on
/// disk and 172.8 M frames, so reading I and Q that way costs the file twice over plus two
/// 1.4 GB <c>double[]</c> downstream - it runs out of memory on this box long before it runs out
/// of patience. This reader hands out fixed-size blocks instead and never grows with the
/// file.</para>
/// <para>Only what the capture format actually is: RIFF/WAVE, PCM, 16-bit, 1 or 2 channels.
/// Unknown chunks are skipped, and a <c>data</c> chunk whose declared size overruns the file (a
/// capture killed mid-write, which happens whenever a session is interrupted) is truncated to
/// what is really there rather than refused.</para>
/// </remarks>
public sealed class PcmWavReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataStart;
    private byte[] _scratch = [];
    private long _framesRead;
    private bool _disposed;

    public PcmWavReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> head = stackalloc byte[12];
        ReadExactly(_stream, head);
        if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        int channels = 0, sampleRate = 0;
        long dataStart = -1, dataBytes = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        while (dataStart < 0 && _stream.Position + 8 <= _stream.Length)
        {
            ReadExactly(_stream, chunkHeader);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            long body = _stream.Position;

            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    throw new InvalidDataException("truncated fmt chunk");
                }

                ReadExactly(_stream, fmt);
                int format = BinaryPrimitives.ReadInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                int bits = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..]);
                if (format != 1 || bits != 16)
                {
                    throw new InvalidDataException(
                        $"only 16-bit PCM is supported (format {format}, {bits} bits)");
                }
                if (channels is < 1 or > 2)
                {
                    throw new InvalidDataException($"expected 1 or 2 channels, found {channels}");
                }
            }
            else if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("data chunk before fmt chunk");
                }

                dataStart = body;
                // A capture interrupted mid-write leaves the declared size larger than the file.
                dataBytes = Math.Min(size, _stream.Length - body);
                break;
            }

            _stream.Seek(body + size + (size & 1), SeekOrigin.Begin); // chunks are word-aligned
        }

        if (dataStart < 0 || sampleRate == 0)
        {
            _stream.Dispose();
            throw new InvalidDataException("missing fmt or data chunk");
        }

        Channels = channels;
        SampleRate = sampleRate;
        _dataStart = dataStart;
        FrameCount = dataBytes / (channels * 2);
        _stream.Seek(_dataStart, SeekOrigin.Begin);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Total frames in the file (one sample per channel each).</summary>
    public long FrameCount { get; }

    /// <summary>Frames handed out so far.</summary>
    public long FramesRead => _framesRead;

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved samples and returns the number of
    /// <em>frames</em> written. Short only at end of data. The span length must be a whole number
    /// of frames.
    /// </summary>
    public int ReadFrames(Span<short> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length % Channels != 0)
        {
            throw new ArgumentException(
                $"destination must be a whole number of {Channels}-channel frames", nameof(destination));
        }

        int wanted = (int)Math.Min(destination.Length / Channels, FrameCount - _framesRead);
        if (wanted <= 0)
        {
            return 0;
        }

        int bytes = wanted * Channels * 2;
        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        var raw = _scratch.AsSpan(0, bytes);
        ReadExactly(_stream, raw);
        for (int i = 0; i < wanted * Channels; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(raw[(i * 2)..]);
        }

        _framesRead += wanted;
        return wanted;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = stream.Read(buffer[got..]);
            if (n == 0)
            {
                throw new EndOfStreamException("unexpected end of WAV data");
            }
            got += n;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }
}
