using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.Ax25;
using Packet.Core;

namespace Packet.SoundModem.NinoCompare;

/// <summary>One decoded AX.25 frame, as captured from either side of the comparison (the NinoTNC's
/// MQTT feed, or our own decode of the same audio). The <see cref="Hex"/> of the raw frame bytes is
/// the content key both sides are matched on; the rest is human-readable context.</summary>
public sealed record FrameRecord(
    [property: JsonPropertyName("t")] double? TimeSeconds,
    [property: JsonPropertyName("hex")] string Hex,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("offsetHz")] double? OffsetHz)
{
    private static readonly JsonSerializerOptions Json =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Builds a record from raw AX.25 frame bytes, parsing addresses for display.</summary>
    public static FrameRecord FromBytes(byte[] frame, double? timeSeconds = null, double? offsetHz = null)
    {
        var (from, to, summary) = Ax25Text.Describe(frame);
        return new FrameRecord(timeSeconds, Convert.ToHexString(frame), from, to, summary, offsetHz);
    }

    public string ToJsonLine() => JsonSerializer.Serialize(this, Json);

    public static FrameRecord? FromJsonLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<FrameRecord>(line, Json);

    /// <summary>Reads a JSONL frame file (one record per line; blank lines skipped).</summary>
    public static List<FrameRecord> ReadFile(string path)
    {
        var records = new List<FrameRecord>();
        foreach (string line in File.ReadLines(path))
        {
            if (FromJsonLine(line) is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }
}

/// <summary>Renders raw AX.25 frame bytes to human-readable form using the packet.net codec.</summary>
public static class Ax25Text
{
    /// <summary>Parses a frame's addresses and a one-line summary. Falls back gracefully for bytes
    /// the codec cannot parse (still comparable by hex).</summary>
    public static (string? From, string? To, string Summary) Describe(byte[] frame)
    {
        if (!Ax25Frame.TryParse(frame, out Ax25Frame? parsed) || parsed is null)
        {
            return (null, null, $"<unparsed {frame.Length} bytes>");
        }

        string from = Format(parsed.Source);
        string to = Format(parsed.Destination);
        string path = parsed.Digipeaters.Count > 0
            ? " via " + string.Join(",", parsed.Digipeaters.Select(Format))
            : string.Empty;
        string kind = parsed.IsUi ? "UI" : "S/I/U";
        string info = InfoPreview(parsed.Info);
        return (from, to, $"{from}>{to}{path} {kind}{info}");
    }

    private static string Format(Ax25Address address)
    {
        Callsign call = address.Callsign;
        return call.Ssid == 0 ? call.Base : $"{call.Base}-{call.Ssid}";
    }

    private static string InfoPreview(ReadOnlyMemory<byte> info)
    {
        if (info.Length == 0)
        {
            return string.Empty;
        }

        Span<char> chars = stackalloc char[Math.Min(info.Length, 40)];
        ReadOnlySpan<byte> span = info.Span;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = (char)span[i];
            chars[i] = c is >= ' ' and < (char)127 ? c : '.';
        }

        return $" \"{new string(chars)}{(info.Length > 40 ? "…" : string.Empty)}\"";
    }
}
