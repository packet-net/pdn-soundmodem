using System.Text.Json;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>Reads the JSON vectors the lab prototype generated (proto/vectors/link_vectors.py).</summary>
internal static class LinkVectorFile
{
    public static JsonElement Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors", name);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>Bits held one to a byte, from a hex string whose first bit is the top bit of the first byte.</summary>
    public static byte[] Bits(string hex, int count)
    {
        byte[] bytes = Convert.FromHexString(hex);
        var bits = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bits[i] = (byte)((bytes[i >> 3] >> (7 - (i & 7))) & 1);
        }

        return bits;
    }

    public static string Hex(ReadOnlySpan<byte> bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            bytes[i >> 3] |= (byte)(bits[i] << (7 - (i & 7)));
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
