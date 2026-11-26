using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The prototype's station-rule vectors (Vectors/station_*.json and .bin.gz, from proto/vectors/station_vectors.py).</summary>
internal static class StationVectorFile
{
    public const int Carriers = 333;

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors");

    private static readonly Lazy<double[]> ProfileValues = new(() => ReadDoubles("station_profiles.bin.gz"));

    private static readonly Lazy<double[]> ScenarioValues = new(() => ReadDoubles("station_values.bin.gz"));

    private static readonly Lazy<JsonElement> FunctionDoc = new(() => LinkVectorFile.Load("station_function_vectors.json"));

    private static readonly Lazy<JsonElement> ScenarioDoc = new(() => LinkVectorFile.Load("station_scenarios.json"));

    public static JsonElement Functions => FunctionDoc.Value;

    public static JsonElement Scenarios => ScenarioDoc.Value;

    /// <summary>Profile number <paramref name="index"/> of the function vectors: a power ratio on each of the 333 carriers.</summary>
    public static ReadOnlySpan<double> Profile(int index) => ProfileValues.Value.AsSpan(index * Carriers, Carriers);

    /// <summary>Array number <paramref name="index"/> of the scenario values: a call's quality in, or a record's estimate out (NaN: unknown).</summary>
    public static ReadOnlySpan<double> Value(int index) => ScenarioValues.Value.AsSpan(index * Carriers, Carriers);

    public static int ValueCount => ScenarioValues.Value.Length / Carriers;

    private static double[] ReadDoubles(string name)
    {
        using var file = File.OpenRead(Path.Combine(Dir, name));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] raw = ms.ToArray();
        var values = new double[raw.Length / 8];
        MemoryMarshal.Cast<byte, double>(raw.AsSpan(0, values.Length * 8)).CopyTo(values);
        return values;
    }
}
