using System.Buffers.Binary;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// Streaming writer for 2-channel 16-bit PCM WAV — the IQ48 capture container (I = left,
/// Q = right). Writes a placeholder 44-byte header up front and patches the two size fields on
/// <see cref="Dispose"/>, so arbitrarily long captures never buffer in memory. Little-endian
/// throughout; the payload must already be little-endian int16 (see <see cref="PcmBinaryDecoder"/>).
/// </summary>
public sealed class StereoPcmWavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private long _dataBytes;
    private bool _disposed;

    public StereoPcmWavWriter(string path, int sampleRate)
    {
        _sampleRate = sampleRate;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Span<byte> header = stackalloc byte[44];
        WriteHeader(header, sampleRate, dataBytes: 0);
        _stream.Write(header);
    }

    /// <summary>Number of stereo frames (I/Q pairs) written so far.</summary>
    public long FramesWritten => _dataBytes / 4;

    /// <summary>Appends raw little-endian interleaved 16-bit stereo PCM.</summary>
    public void Write(ReadOnlySpan<byte> littleEndianStereoPcm)
    {
        _stream.Write(littleEndianStereoPcm);
        _dataBytes += littleEndianStereoPcm.Length;
    }

    private static void WriteHeader(Span<byte> h, int sampleRate, long dataBytes)
    {
        const int channels = 2;
        const int bits = 16;
        int byteRate = sampleRate * channels * (bits / 8);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);      // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], channels * (bits / 8)); // block align
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
        WriteHeader(header, _sampleRate, _dataBytes);
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(header);
        _stream.Flush();
        _stream.Dispose();
    }
}
