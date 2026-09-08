using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.SoundModem.TncTest;

/// <summary>One frame, as it survives a round trip through JSON.</summary>
/// <param name="At">Seconds into the track.</param>
/// <param name="Hex">The frame bytes.</param>
/// <param name="OffsetHz">Winning branch offset, where the mode ran a bank.</param>
/// <param name="EmphasisDb">Winning branch emphasis, likewise.</param>
internal sealed record FrameRecord(double At, string Hex, double? OffsetHz, double? EmphasisDb);

/// <summary>One mode's result over one track.</summary>
internal sealed record RunResult(
    string Mode,
    string Receiver,
    int DspRate,
    double? CentreHz,
    int Score,
    int Distinct,
    int Ax25Shaped,
    int Stations,
    long Bytes,
    double ElapsedSeconds,
    IReadOnlyList<FrameRecord> Frames);

/// <summary>
/// One track's whole run, written so that a later run can be diffed against it.
/// </summary>
/// <remarks>
/// The frame list is the point of this file, not the score. A change to a receive path that
/// leaves the count alone and swaps which frames it decoded has done something, and a benchmark
/// that only remembers totals cannot see it. The scores are stored beside the frames so a
/// baseline is readable on its own.
/// </remarks>
internal sealed record RunRecord(
    string Tool,
    string Track,
    int SampleRate,
    int Channels,
    int Channel,
    double AudioSeconds,
    bool? Integrity,
    IReadOnlyList<RunResult> Runs)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static RunRecord From(string path, TrackAudio track, IReadOnlyList<ModeScore> scores) =>
        new(
            "sm-tnctest",
            Path.GetFileName(path),
            track.SampleRate,
            track.Channels,
            track.Channel,
            track.Duration.TotalSeconds,
            track.Integrity,
            [.. scores.Select(s => new RunResult(
                s.Mode, s.Receiver, s.DspRate, s.CentreHz, s.Score, s.Distinct, s.Ax25Shaped,
                s.Stations.Count, s.Bytes, s.Elapsed.TotalSeconds,
                [.. s.Frames.Select(f => new FrameRecord(
                    Math.Round(f.AtSeconds, 2), f.Hex, f.OffsetHz, f.EmphasisDb))]))]);

    public static void Save(string path, IReadOnlyList<RunRecord> records) =>
        File.WriteAllText(path, JsonSerializer.Serialize(records, Format));

    public static RunRecord[] Load(string path) =>
        JsonSerializer.Deserialize<RunRecord[]>(File.ReadAllText(path), Format)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)} holds no runs");
}
