using System.Buffers.Binary;
using ZstdSharp;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// One decoded PCM packet: little-endian interleaved 16-bit samples (I,Q,I,Q… for iq48),
/// the sample rate and channel count in force, and the GPS timestamp (nanoseconds since the
/// Unix epoch) of the first sample. <see cref="Pcm"/> is a view into a buffer owned by the
/// packet and is valid until the next <see cref="PcmBinaryDecoder.Decode"/> call reuses it.
/// </summary>
public readonly record struct PcmPacket(
    ReadOnlyMemory<byte> Pcm,
    int SampleRate,
    int Channels,
    ulong GpsTimestampNanos);

/// <summary>
/// Decodes ka9q_ubersdr binary PCM/IQ packets (the <c>pcm</c> / <c>pcm-zstd</c> WebSocket
/// wire format). Direct port of <c>clients/iq-recorder/pcm_decoder.go</c>
/// (<c>DecodePCMBinary</c>) from https://github.com/madpsy/ka9q_ubersdr (GPL-3.0), which is
/// compatible with this repo's GPL-3.0-or-later. See docs/ms110d/ota-capture-client-plan.md
/// for the documented protocol.
///
/// <para>Hybrid header strategy (after zstd decompression, if any):</para>
/// <list type="bullet">
/// <item>Full header - magic <c>"PC"</c> (0x5043), 29 bytes: [2] magic · [1] version ·
///   [1] format · [8] RTP ts u64-LE · [8] wallclock u64-LE (deprecated) · [4] sample-rate
///   u32-LE · [1] channels u8 · [4] reserved; GPS timestamp is the u64-LE at offset 4.</item>
/// <item>Minimal header - magic <c>"PM"</c> (0x504D), 13 bytes: [2] magic · [1] version ·
///   [8] GPS ts u64-LE (offset 3) · [2] reserved; reuses the last full header's rate/channels.</item>
/// </list>
/// <para>The PCM payload is big-endian int16 and is byte-swapped to little-endian here.</para>
/// </summary>
public sealed class PcmBinaryDecoder : IDisposable
{
    private const ushort MagicFull = 0x5043;    // "PC" read as little-endian uint16
    private const ushort MagicMinimal = 0x504D; // "PM"

    private readonly Decompressor _zstd = new();

    /// <summary>Last sample rate seen in a full header. Pre-seed to 48000 for iq48, since the
    /// server may open with minimal ("PM") frames and sends no initial text status for binary
    /// formats (the upstream Go client does the same).</summary>
    public int LastSampleRate { get; set; }

    /// <summary>Last channel count seen in a full header. Pre-seed to 2 for iq48.</summary>
    public int LastChannels { get; set; }

    /// <summary>
    /// Decodes one binary frame. <paramref name="isZstd"/> must be true for the
    /// <c>format=pcm-zstd</c> stream (every binary WebSocket message is a complete zstd frame).
    /// Throws <see cref="InvalidDataException"/> on a malformed or too-short packet.
    /// </summary>
    public PcmPacket Decode(ReadOnlySpan<byte> frame, bool isZstd)
    {
        // Own a mutable copy: for raw frames we must not mutate the caller's WebSocket buffer
        // during the big-endian → little-endian swap; Unwrap returns a view we also copy.
        byte[] data = isZstd ? _zstd.Unwrap(frame).ToArray() : frame.ToArray();

        if (data.Length < 4)
        {
            throw new InvalidDataException($"binary PCM packet too short: {data.Length} bytes");
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int sampleRate;
        int channels;
        ulong gpsTimestampNanos;
        int pcmOffset;

        if (magic == MagicFull)
        {
            if (data.Length < 29)
            {
                throw new InvalidDataException($"full-header PCM packet too short: {data.Length} bytes");
            }

            gpsTimestampNanos = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4, 8));
            // offset 12..20 is deprecated wall-clock time (GPS time in ms); ignored.
            sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
            channels = data[24];
            pcmOffset = 29;

            LastSampleRate = sampleRate;
            LastChannels = channels;
        }
        else if (magic == MagicMinimal)
        {
            if (data.Length < 13)
            {
                throw new InvalidDataException($"minimal-header PCM packet too short: {data.Length} bytes");
            }

            gpsTimestampNanos = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(3, 8));
            pcmOffset = 13;

            sampleRate = LastSampleRate;
            channels = LastChannels;
            if (sampleRate == 0 || channels == 0)
            {
                throw new InvalidDataException("received minimal header before any full header");
            }
        }
        else
        {
            throw new InvalidDataException($"invalid PCM magic bytes: 0x{magic:X4}");
        }

        // Convert the PCM payload from big-endian int16 to little-endian, in place.
        Span<byte> pcm = data.AsSpan(pcmOffset);
        int wholeSamples = pcm.Length / 2;
        for (int i = 0; i < wholeSamples; i++)
        {
            Span<byte> s = pcm.Slice(i * 2, 2);
            (s[0], s[1]) = (s[1], s[0]);
        }

        return new PcmPacket(
            new ReadOnlyMemory<byte>(data, pcmOffset, wholeSamples * 2),
            sampleRate,
            channels,
            gpsTimestampNanos);
    }

    /// <summary>Releases the zstd decompressor.</summary>
    public void Dispose() => _zstd.Dispose();
}
