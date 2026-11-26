using System.IO.Compression;
using System.Text.Json;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The prototype's decoder vectors (Vectors/decode_vectors.json and decode_soft.bin.gz, from proto/vectors/decode_vectors.py).</summary>
internal static class DecodeVectorFile
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");

    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "decode_vectors.json"))));

    private static readonly Lazy<float[]> SoftValues = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(Dir, Doc.Value.RootElement.GetProperty("softFile").GetString()!));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] raw = ms.ToArray();
        var floats = new float[raw.Length / 4];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = BitConverter.ToSingle(raw, 4 * i);
        }

        return floats;
    });

    public static JsonElement Root => Doc.Value.RootElement;

    public static IEnumerable<JsonElement> Blocks => Root.GetProperty("blocks").EnumerateArray();

    public static IEnumerable<JsonElement> Payloads => Root.GetProperty("payloads").EnumerateArray();

    public static JsonElement Opening(string name) =>
        Root.GetProperty("headers").EnumerateArray().First(h => h.GetProperty("opening").GetString() == name);

    public static IEnumerable<object[]> BlockData() => Blocks.Select(c => new object[] { c.GetProperty("name").GetString()! });

    public static IEnumerable<object[]> PayloadData() => Payloads.Select(c => new object[] { c.GetProperty("name").GetString()! });

    public static JsonElement Block(string name) => Blocks.First(c => c.GetProperty("name").GetString() == name);

    public static JsonElement Payload(string name) => Payloads.First(c => c.GetProperty("name").GetString() == name);

    /// <summary>The float values of a case's "soft" entry (offset and count into the binary file).</summary>
    public static float[] Soft(JsonElement caseElement)
    {
        JsonElement s = caseElement.GetProperty("soft");
        return SoftValues.Value.AsSpan(s.GetProperty("offset").GetInt32(), s.GetProperty("count").GetInt32()).ToArray();
    }
}
