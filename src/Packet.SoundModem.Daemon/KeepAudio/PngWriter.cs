using System.Buffers.Binary;
using System.IO.Compression;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// The smallest PNG writer that does the job: 8-bit RGB, no filtering, one IDAT chunk, deflated
/// with the framework's own zlib.
/// </summary>
/// <remarks>
/// Written here rather than taken from an image library because a waterfall picture is the only
/// image this daemon makes, and the libraries that would do it carry licences or weight this repo
/// does not want. The format is the PNG specification's (ISO/IEC 15948, W3C PNG 2nd edition):
/// signature, IHDR, IDAT, IEND, each chunk with its CRC-32.
/// </remarks>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes an image given as rows of R, G, B bytes, top row first.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (rgb.Length != checked(width * height * 3))
        {
            throw new ArgumentException($"expected {width * height * 3} bytes of RGB, got {rgb.Length}", nameof(rgb));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering, of which only "none" is used
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR"u8, header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 3;
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter type none
                    zlib.Write(rgb.Slice(y * stride, stride));
                }
            }

            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>The PNG chunk CRC (CRC-32, ISO 3309) over the type and data.</summary>
    internal static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(type, data));
        output.Write(word);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
