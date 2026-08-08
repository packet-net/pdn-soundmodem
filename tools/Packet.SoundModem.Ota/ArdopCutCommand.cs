using System.Globalization;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Cuts per-session audio windows out of the raw capture around frames named in an
/// ardop-monitor CSV - the ARDOP campaign's A1 autopsy extractor (docs/ardop/plan.md).
/// Frames are grouped into sessions by time alone (a gap over 120 s starts a new group)
/// and the whole group is cut as one WAV, so a decoder replaying the cut sees the same
/// Memory-ARQ context (retries of the same frame) the A0 baseline run saw. The manifest
/// carries every baseline verdict that falls inside each cut, with its offset into the
/// cut, so per-frame outcomes from any decoder run over the cut can be aligned back to
/// the baseline row they answer.
/// </summary>
/// <remarks>
/// Capture gaps inside a window are filled with silence of the measured length rather
/// than spliced out: offsets into the cut stay truthful against the manifest, and a
/// silence fill cannot fake a decode. Fills are reported per cut.
/// </remarks>
internal static class ArdopCutCommand
{
    private const int EngineRate = 12000;
    private const double SessionGapSeconds = 120;

    public static int Run(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help")
        {
            Console.WriteLine(
                """
                usage: sm-ota ardop-cut --csv <baseline.csv> --raw <dir> --out <dir>
                                        [--families <substr,...>] [--all]
                                        [--pre <seconds>] [--post <seconds>]

                Cuts one WAV per frame group (frames within 120 s of each other) from the
                raw capture, keeping groups that contain at least one failed frame, and
                writes <out>/manifest.csv aligning every baseline verdict to its cut.

                  --csv <path>         an ardop-monitor CSV (the A0 baseline)
                  --raw <dir>          the raw-<UTC>.wav chunk directory
                  --out <dir>          cut WAVs and manifest.csv land here
                  --families <list>    keep only groups whose FAILED frames match any
                                       substring (e.g. 16QAM,8PSK); default any failure
                  --all                keep every group, failures or not
                  --pre/--post <s>     window margin before the first / after the last
                                       frame stamp (defaults 20 / 5; stamps mark frame
                                       END, so pre must cover leader plus longest frame)
                """);
            return argv.Length == 0 ? 2 : 0;
        }

        var a = Args.Parse(argv);
        if (a is null)
        {
            return 2;
        }

        string csvPath = a.Req("csv");
        string rawDir = a.Req("raw");
        string outDir = a.Req("out");
        double pre = a.Dbl("pre", 20);
        double post = a.Dbl("post", 5);
        bool all = a.Has("all");
        string[] families = (a.Str("families", null) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<Row> rows = ReadBaseline(csvPath);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"no verdict rows in '{csvPath}'");
            return 2;
        }

        var groups = new List<List<Row>>();
        foreach (Row row in rows.OrderBy(r => r.Utc))
        {
            if (groups.Count > 0
                && (row.Utc - groups[^1][^1].Utc).TotalSeconds <= SessionGapSeconds)
            {
                groups[^1].Add(row);
            }
            else
            {
                groups.Add([row]);
            }
        }

        bool Wanted(Row r) => !r.Ok
            && (families.Length == 0 || families.Any(f => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        List<List<Row>> kept = all ? groups : [.. groups.Where(g => g.Any(Wanted))];
        Console.WriteLine($"{rows.Count} verdicts in {groups.Count} groups; cutting {kept.Count}");

        Directory.CreateDirectory(outDir);
        using var manifest = new StreamWriter(Path.Combine(outDir, "manifest.csv"));
        manifest.WriteLine("cut,offset_s,utc,type,name,ok,quality,session_id,caller,target");

        int sequence = 0;
        foreach (List<Row> group in kept)
        {
            DateTimeOffset from = group[0].Utc - TimeSpan.FromSeconds(pre);
            DateTimeOffset to = group[^1].Utc + TimeSpan.FromSeconds(post);
            string name = $"cut-{sequence:D3}-{from:yyyyMMdd-HHmmss}Z.wav";
            string path = Path.Combine(outDir, name);
            (float[] audio, double fillSeconds, DateTimeOffset actualFrom) =
                Stitch(rawDir, from, to);
            if (audio.Length == 0)
            {
                Console.Error.WriteLine($"{name}: no capture audio in window - skipped");
                continue;
            }

            WavFile.WriteMono(path, audio, EngineRate);
            foreach (Row row in group)
            {
                manifest.WriteLine(string.Join(',',
                    name, ((row.Utc - actualFrom).TotalSeconds).ToString("F1", CultureInfo.InvariantCulture),
                    row.Utc.ToString("O"), row.Type, row.Name, row.Ok ? 1 : 0, row.Quality,
                    row.SessionId, row.Caller, row.Target));
            }

            int fails = group.Count(r => !r.Ok);
            string failNames = string.Join('+', group.Where(r => !r.Ok).Select(r => r.Name).Distinct());
            Console.WriteLine($"  {name}: {(to - actualFrom).TotalSeconds:F0} s, "
                + $"{group.Count} frames, {fails} failed{(fails > 0 ? $" ({failNames})" : "")}"
                + (fillSeconds > 0 ? $", {fillSeconds:F1} s gap-fill" : ""));
            sequence++;
        }

        Console.WriteLine($"manifest: {Path.Combine(outDir, "manifest.csv")}");
        return 0;
    }

    private sealed record Row(
        DateTimeOffset Utc, string Type, string Name, bool Ok, int Quality,
        string SessionId, string Caller, string Target);

    private static List<Row> ReadBaseline(string path)
    {
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] f = line.Split(',');
            if (f.Length < 11)
            {
                continue;
            }

            rows.Add(new Row(
                DateTimeOffset.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                f[2], f[3], f[4] == "1", int.Parse(f[5], CultureInfo.InvariantCulture),
                f[6], f[7], f[8]));
        }

        return rows;
    }

    /// <summary>Reads the capture chunks overlapping [from, to] and returns the window's
    /// samples, silence-filling any capture gap. Returns the actual window start, clamped
    /// forward if the capture starts inside the requested window.</summary>
    private static (float[] Audio, double FillSeconds, DateTimeOffset From) Stitch(
        string rawDir, DateTimeOffset from, DateTimeOffset to)
    {
        string[] chunks = ReplayCommand.ResolveChunks(rawDir, from, to);
        var pieces = new List<float[]>();
        double fill = 0;
        DateTimeOffset? windowStart = null;
        DateTimeOffset cursor = from;
        foreach (string chunk in chunks)
        {
            DateTimeOffset chunkStart = ReplayCommand.ChunkUtc(chunk);
            (float[] audio, int rate) = WavFile.ReadMono(chunk);
            if (rate != EngineRate)
            {
                continue;
            }

            DateTimeOffset chunkEnd = chunkStart + TimeSpan.FromSeconds(audio.Length / (double)EngineRate);
            DateTimeOffset sliceFrom = cursor > chunkStart ? cursor : chunkStart;
            DateTimeOffset sliceTo = to < chunkEnd ? to : chunkEnd;
            if (sliceTo <= sliceFrom)
            {
                continue;
            }

            windowStart ??= sliceFrom;
            if (sliceFrom > cursor && pieces.Count > 0)
            {
                int gap = (int)((sliceFrom - cursor).TotalSeconds * EngineRate);
                pieces.Add(new float[gap]);
                fill += gap / (double)EngineRate;
            }

            int start = (int)((sliceFrom - chunkStart).TotalSeconds * EngineRate);
            int length = (int)((sliceTo - sliceFrom).TotalSeconds * EngineRate);
            length = Math.Min(length, audio.Length - start);
            pieces.Add(audio[start..(start + length)]);
            cursor = sliceTo;
        }

        if (pieces.Count == 0)
        {
            return ([], 0, from);
        }

        float[] joined = new float[pieces.Sum(p => p.Length)];
        int at = 0;
        foreach (float[] piece in pieces)
        {
            piece.CopyTo(joined, at);
            at += piece.Length;
        }

        return (joined, fill, windowStart ?? from);
    }
}
