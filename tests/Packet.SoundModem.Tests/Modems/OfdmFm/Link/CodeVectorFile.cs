using System.Text.Json;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The prototype's encoder vectors (Vectors/code_vectors.json, from proto/vectors/code_vectors.py).</summary>
internal static class CodeVectorFile
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors", "code_vectors.json"))));

    public static IEnumerable<string> CaseNames()
    {
        foreach (JsonElement c in Doc.Value.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return c.GetProperty("name").GetString()!;
        }
    }

    public static IEnumerable<object[]> CaseData() => CaseNames().Select(n => new object[] { n });

    public static IEnumerable<object[]> PlanData()
    {
        int i = 0;
        foreach (JsonElement p in Doc.Value.RootElement.GetProperty("plans").EnumerateArray())
        {
            yield return new object[] { i++ };
        }
    }

    public static JsonElement Case(string name) =>
        Doc.Value.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    public static JsonElement Plan(int index) => Doc.Value.RootElement.GetProperty("plans")[index];

    /// <summary>Bits packed eight to a byte, first bit in the high place, to one bit per byte.</summary>
    public static byte[] Bits(string hex, int count)
    {
        byte[] packed = Convert.FromHexString(hex);
        var bits = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bits[i] = (byte)((packed[i >> 3] >> (7 - (i & 7))) & 1);
        }

        return bits;
    }

    public static int[] Ints(JsonElement array)
    {
        var r = new int[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement e in array.EnumerateArray())
        {
            r[i++] = e.GetInt32();
        }

        return r;
    }
}
