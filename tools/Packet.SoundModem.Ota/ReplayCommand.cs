using System.Globalization;
using Microsoft.Data.Sqlite;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Replays a raw-capture chunk sequence (the daemon's <c>rawCapture</c> record - continuous
/// 16-bit mono WAVs named <c>raw-&lt;UTC&gt;.wav</c>) through catalogue receivers, and
/// optionally diffs what decoded against the live station's frame log. This is the 40 m
/// capture campaign's harvest instrument (docs/rx-roadmap.md): the raw chunks are the
/// re-scorable ground truth, so every future receiver change can be measured against every
/// hour of the band, and a detector A/B on real traffic is two runs of this command with
/// different <c>--detector</c> values diffed.
/// </summary>
/// <remarks>
/// <para>The chunk sequence is fed to ONE modem instance per requested mode, continuously,
/// in chunk order - the chunks are contiguous audio (the writer opens the next chunk as the
/// previous one fills), and a per-chunk receiver would lose every frame that straddles a
/// boundary. A gap between chunks (a daemon restart) is reported and fed through as the
/// discontinuity it is: the receivers treat it like any other click.</para>
/// <para>Frame times are the chunk's filename UTC plus the sample offset of the block that
/// delivered the frame - accurate to the 100 ms feed block, which is far finer than the
/// matching window the frame-log diff uses. Matching is by exact payload bytes within a
/// +-45 s window, nearest first, so a retransmission seconds later cannot cross-match.</para>
/// </remarks>
internal static class ReplayCommand
{
    private const int BlockMilliseconds = 100;

    /// <summary>Matching window between a replayed frame and a frame-log row. The live
    /// station stamps a frame when its burst ends and the replay stamps it at the delivering
    /// block, so the honest skew is seconds; 45 s stays far under the tens of seconds between
    /// genuine retransmissions while absorbing every clock quirk seen so far.</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(45);

    public static int Run(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help")
        {
            Console.WriteLine(
                """
                usage: sm-ota replay --raw <dir|file[,file...]> --mode <mode@centreHz>[,...]
                                     [--detector <name>] [--framelog <frames.sqlite>]
                                     [--from <UTC>] [--to <UTC>] [--csv <path>] [--quiet]

                Replays raw-capture WAV chunks through catalogue receivers and reports every
                frame that decodes; with --framelog, diffs the result against what the live
                station logged over the same window.

                  --raw <dir>          directory of raw-<UTC>.wav chunks (or explicit files,
                                       comma-separated); fed continuously in name order
                  --mode <m@Hz>        catalogue mode at an audio centre, e.g. the 40 m
                                       campaign's bpsk300@2150,afsk300-il2pc@850
                                       (dial 7.049450 USB: 7.051600 and 7.050300)
                  --detector <name>    coherent|differential|mlse override for bpsk*/qpsk*
                  --framelog <path>    the station's frames.sqlite; adds the matched /
                                       log-only / replay-only diff over the replayed window
                  --from/--to <UTC>    bound the chunk list (ISO or yyyyMMddTHHmmssZ)
                  --csv <path>         one row per replayed frame, full payload hex - the
                                       stable artefact two detector runs diff against
                  --quiet              suppress the per-frame lines
                """);
            return argv.Length == 0 ? 2 : 0;
        }

        var a = Args.Parse(argv);
        if (a is null)
        {
            return 2;
        }

        string rawArg = a.Req("raw");
        string[] chunks = ResolveChunks(rawArg, ParseUtc(a.Str("from", null)), ParseUtc(a.Str("to", null)));
        if (chunks.Length == 0)
        {
            Console.Error.WriteLine($"no raw-*.wav chunks under '{rawArg}' in the requested window");
            return 2;
        }

        PskDetector? detector = a.Str("detector", null) is { } d
            ? d.StartsWith('d') ? PskDetector.Differential
                : d.StartsWith('m') ? PskDetector.Mlse
                : PskDetector.Coherent
            : null;

        (string Mode, double CentreHz)[] specs = a.Req("mode")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s =>
            {
                string[] parts = s.Split('@');
                if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hz))
                {
                    throw new ArgumentException($"--mode wants mode@centreHz, got '{s}'");
                }

                return (parts[0], hz);
            })
            .ToArray();

        bool quiet = a.Has("quiet");
        (_, int rate) = WavFile.ReadMono(chunks[0]);

        // One pass per mode, in parallel: each pass owns its modem, its WAV reads and its
        // frame list, so there is no shared receiver state to protect - the duplicate file
        // IO is nothing beside two multi-bank FIR chains, and the full-capture harvest is
        // compute-bound enough to want every core it can justify.
        var perSpec = new List<ReplayedFrame>[specs.Length];
        var ends = new DateTimeOffset[specs.Length];
        DateTimeOffset runStart = ChunkUtc(chunks[0]);
        Parallel.For(0, specs.Length, i =>
            (perSpec[i], ends[i]) = RunPass(specs[i], chunks, rate, detector, reportGaps: i == 0));
        DateTimeOffset runEnd = ends[0];
        List<ReplayedFrame> frames = perSpec.SelectMany(f => f).OrderBy(f => f.Utc).ToList();
        if (!quiet)
        {
            foreach (ReplayedFrame frame in frames)
            {
                Console.WriteLine(frame.Describe());
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== replay {runStart:yyyy-MM-dd HH:mm}Z .. {runEnd:HH:mm}Z "
            + $"({chunks.Length} chunks, {(runEnd - runStart).TotalHours:F1} h) ===");
        foreach ((string mode, double centre) in specs)
        {
            int total = frames.Count(f => f.SpecMode == mode);
            int delivered = frames.Count(f => f.SpecMode == mode && !f.Quality.MonitorOnly);
            Console.WriteLine($"{mode}@{centre:F0}: {total} decoded, {delivered} deliverable"
                + (detector is { } det ? $" (detector {det})" : ""));
        }

        if (a.Str("csv", null) is { } csv)
        {
            WriteCsv(csv, frames);
            Console.WriteLine($"csv: {csv}");
        }

        if (a.Str("framelog", null) is { } log)
        {
            DiffAgainstLog(log, frames, specs.Select(s => s.Mode).ToArray(), runStart, runEnd);
        }

        return 0;
    }

    /// <summary>One mode's continuous pass over the whole chunk sequence.</summary>
    private static (List<ReplayedFrame> Frames, DateTimeOffset End) RunPass(
        (string Mode, double CentreHz) spec, string[] chunks, int rate,
        PskDetector? detector, bool reportGaps)
    {
        var frames = new List<ReplayedFrame>();
        DateTimeOffset blockTime = default;
        IModem modem = ModemCatalog.Create(spec.Mode, rate, static _ => { },
            new ModemOptions(CentreFrequencyHz: spec.CentreHz, Detector: detector));
        modem.FrameDecoded += (frame, quality) =>
            frames.Add(new ReplayedFrame(blockTime, spec.Mode, frame, quality));

        int block = Math.Max(1, rate * BlockMilliseconds / 1000);
        DateTimeOffset expected = ChunkUtc(chunks[0]);
        for (int c = 0; c < chunks.Length; c++)
        {
            (float[] audio, int chunkRate) = WavFile.ReadMono(chunks[c]);
            if (chunkRate != rate)
            {
                if (reportGaps)
                {
                    Console.Error.WriteLine(
                        $"{Path.GetFileName(chunks[c])}: rate {chunkRate} != {rate} - stopping here");
                }

                break;
            }

            DateTimeOffset start = ChunkUtc(chunks[c]);
            if (reportGaps && c > 0 && (start - expected).Duration() > TimeSpan.FromSeconds(5))
            {
                Console.Error.WriteLine(
                    $"note: {(start - expected).Duration().TotalSeconds:F0} s gap before "
                    + $"{Path.GetFileName(chunks[c])} (daemon restart?) - feeding through");
            }

            for (int pos = 0; pos < audio.Length; pos += block)
            {
                int length = Math.Min(block, audio.Length - pos);
                blockTime = start + TimeSpan.FromSeconds((pos + length) / (double)rate);
                modem.Process(audio.AsSpan(pos, length));
            }

            expected = start + TimeSpan.FromSeconds(audio.Length / (double)rate);
        }

        return (frames, expected);
    }

    private sealed record ReplayedFrame(
        DateTimeOffset Utc, string SpecMode, byte[] Payload, FrameQuality Quality)
    {
        public bool Matched { get; set; }

        public string Describe()
        {
            Ax25AddressParser.TryParse(Payload, out string source, out string destination);
            string verdict = Quality.CrcValid == true ? "crc"
                : Quality.TrailerNearBits is { } near ? $"corr{near}"
                : Quality.MonitorOnly ? "monitor" : "plain";
            return $"{Utc:HH:mm:ss}Z {SpecMode,-14} {verdict,-8} "
                + $"{(string.IsNullOrEmpty(source) ? "?" : source)}>"
                + $"{(string.IsNullOrEmpty(destination) ? "?" : destination),-10} "
                + $"len {Payload.Length,3}"
                + (Quality.FrequencyOffsetHz is { } hz ? $" {hz:+0.0;-0.0} Hz" : "");
        }
    }

    private static string[] ResolveChunks(string rawArg, DateTimeOffset? from, DateTimeOffset? to)
    {
        string[] files = Directory.Exists(rawArg)
            ? Directory.GetFiles(rawArg, "raw-*.wav")
            : rawArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(files, StringComparer.Ordinal);

        if (from is null && to is null)
        {
            return files;
        }

        // A chunk belongs if any of its (nominal 15 min) span overlaps the window; the
        // filename carries only the start, so the end is over-estimated by one chunk length.
        return files.Where(f =>
        {
            DateTimeOffset start = ChunkUtc(f);
            return (to is not { } t || start <= t)
                && (from is not { } fr || start + TimeSpan.FromMinutes(16) >= fr);
        }).ToArray();
    }

    private static DateTimeOffset ChunkUtc(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string stamp = name.StartsWith("raw-", StringComparison.Ordinal) ? name[4..] : name;
        return DateTimeOffset.ParseExact(
            stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
    }

    private static DateTimeOffset? ParseUtc(string? s) =>
        s is null ? null
        : DateTimeOffset.TryParseExact(
            s, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset exact) ? exact
        : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static void WriteCsv(string path, List<ReplayedFrame> frames)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("utc,mode,delivered,crc_valid,trailer_near_bits,corrected,erased,offset_hz,length,payload_hex");
        foreach (ReplayedFrame f in frames)
        {
            writer.WriteLine(string.Join(',',
                f.Utc.ToString("O"),
                f.SpecMode,
                f.Quality.MonitorOnly ? 0 : 1,
                f.Quality.CrcValid is { } crc ? (crc ? "1" : "0") : "",
                f.Quality.TrailerNearBits?.ToString(CultureInfo.InvariantCulture) ?? "",
                f.Quality.CorrectedBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                f.Quality.ErasedBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                f.Quality.FrequencyOffsetHz?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
                f.Payload.Length,
                Convert.ToHexString(f.Payload)));
        }
    }

    private sealed record LogRow(DateTimeOffset Utc, string Mode, byte[] Payload, string? Source)
    {
        public bool Matched { get; set; }
    }

    private static void DiffAgainstLog(
        string path, List<ReplayedFrame> frames, string[] specModes,
        DateTimeOffset runStart, DateTimeOffset runEnd)
    {
        var rows = new List<LogRow>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand select = connection.CreateCommand();
            select.CommandText = """
                SELECT heard_at, mode, payload, source FROM frames
                WHERE direction != 'tx' AND heard_at >= $from AND heard_at <= $to
                ORDER BY heard_at
                """;
            select.Parameters.AddWithValue("$from", (runStart - MatchWindow).ToString("O"));
            select.Parameters.AddWithValue("$to", (runEnd + MatchWindow).ToString("O"));
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                string mode = reader.GetString(1);
                // Only the modes being replayed take part in the diff - the live station may
                // also log ghosts or other sub-channels this replay never listened for.
                if (!specModes.Any(m => mode.StartsWith(m, StringComparison.Ordinal)))
                {
                    continue;
                }

                rows.Add(new LogRow(
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                    mode,
                    (byte[])reader.GetValue(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        // Nearest-first payload matching inside the window, each row and frame used once.
        foreach (ReplayedFrame frame in frames.OrderBy(f => f.Utc))
        {
            LogRow? best = null;
            TimeSpan bestSkew = MatchWindow;
            foreach (LogRow row in rows)
            {
                if (row.Matched || !row.Payload.AsSpan().SequenceEqual(frame.Payload))
                {
                    continue;
                }

                TimeSpan skew = (row.Utc - frame.Utc).Duration();
                if (skew <= bestSkew)
                {
                    best = row;
                    bestSkew = skew;
                }
            }

            if (best is not null)
            {
                best.Matched = true;
                frame.Matched = true;
            }
        }

        List<LogRow> logOnly = rows.Where(r => !r.Matched).ToList();
        List<ReplayedFrame> replayOnly = frames.Where(f => !f.Matched).ToList();
        Console.WriteLine();
        Console.WriteLine($"=== frame-log diff ({rows.Count} logged, {frames.Count} replayed) ===");
        Console.WriteLine($"matched: {rows.Count - logOnly.Count}");
        Console.WriteLine($"log-only (live heard, replay missed): {logOnly.Count}");
        foreach (LogRow row in logOnly)
        {
            Console.WriteLine($"  {row.Utc:HH:mm:ss}Z {row.Mode,-14} {row.Source ?? "?",-10} len {row.Payload.Length}");
        }

        Console.WriteLine($"replay-only (replay heard, live missed): {replayOnly.Count}");
        foreach (ReplayedFrame frame in replayOnly)
        {
            Console.WriteLine($"  {frame.Describe()}");
        }
    }
}
