using System.Buffers.Binary;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// Streaming writer for 16-bit PCM WAV — the IQ48 capture container (2 channels, I = left,
/// Q = right) and the converted-audio container (1 channel). Writes a placeholder 44-byte header
/// up front and patches the two size fields on <see cref="Dispose"/>, so arbitrarily long
/// captures never buffer in memory. Little-endian throughout; byte payloads must already be
/// little-endian int16 (see <see cref="PcmBinaryDecoder"/>).
/// </summary>
public sealed class PcmWavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private readonly int _channels;
    private byte[] _scratch = [];
    private long _dataBytes;
    private bool _disposed;

    public PcmWavWriter(string path, int sampleRate, int channels = 2)
    {
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "1 or 2 channels");
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Span<byte> header = stackalloc byte[44];
        WriteHeader(header, sampleRate, channels, dataBytes: 0);
        _stream.Write(header);
    }

    /// <summary>Number of frames (one sample per channel) written so far.</summary>
    public long FramesWritten => _dataBytes / (_channels * 2);

    /// <summary>Appends raw little-endian interleaved 16-bit PCM.</summary>
    public void Write(ReadOnlySpan<byte> littleEndianPcm)
    {
        _stream.Write(littleEndianPcm);
        _dataBytes += littleEndianPcm.Length;
    }

    /// <summary>Appends normalised float samples, clipped to −1..1. Returns how many of them
    /// clipped — a converted capture that clips is a measurement fault rather than a cosmetic
    /// one, so the caller is expected to report it rather than swallow it.</summary>
    public int WriteSamples(ReadOnlySpan<float> samples)
    {
        if (_scratch.Length < samples.Length * 2)
        {
            _scratch = new byte[samples.Length * 2];
        }

        var bytes = _scratch.AsSpan(0, samples.Length * 2);
        int clipped = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (v is > 1f or < -1f)
            {
                clipped++;
                v = Math.Clamp(v, -1f, 1f);
            }

            BinaryPrimitives.WriteInt16LittleEndian(bytes[(i * 2)..], (short)MathF.Round(v * 32767f));
        }

        Write(bytes);
        return clipped;
    }

    private static void WriteHeader(Span<byte> h, int sampleRate, int channels, long dataBytes)
    {
        const int bits = 16;
        int byteRate = sampleRate * channels * (bits / 8);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);      // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(channels * (bits / 8))); // block align
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], bits);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)dataBytes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Span<byte> header = stackalloc byte[44];
        WriteHeader(header, _sampleRate, _channels, _dataBytes);
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(header);
        _stream.Flush();
        _stream.Dispose();
    }
}
