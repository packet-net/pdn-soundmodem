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
                                     [--from <UTC>] [--to <UTC>] [--csv <path>]
                                     [--bursts <path>] [--quiet]

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
                  --workers <n>        parallel receiver passes (default cores-2): the chunk
                                       timeline splits into contiguous segments, each fed
                                       one warmup chunk before its own window so receiver
                                       state is hot at the boundary; a +-2 s payload dedupe
                                       absorbs the boundary jitter
                  --csv <path>         one row per replayed frame, full payload hex - the
                                       stable artefact two detector runs diff against
                  --bursts <path>      one row per DCD burst - the receiver's own per-burst
                                       verdict: burst times, seconds DCD held, decode
                                       outcome, carrier offset, sync/CRC failure deltas
                                       (offset and deltas are BPSK-bank readings; see
                                       DcdBurstTracker). Single --mode only.
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
        string? burstsPath = a.Str("bursts", null);
        if (burstsPath is not null && specs.Length != 1)
        {
            Console.Error.WriteLine(
                "--bursts wants exactly one --mode: the verdict rows are one receiver's");
            return 2;
        }

        (_, int rate) = WavFile.ReadMono(chunks[0]);

        // The compute scales two ways at once: one independent pass per (mode, timeline
        // segment). A segment is a contiguous chunk range fed with the chunk before it as
        // warmup - 15 minutes, far beyond any receiver's memory - and it keeps only the
        // frames whose delivery time lands inside its own window, so each frame has exactly
        // one owner. Delivery-time jitter of the same frame between two warm receivers is
        // under a feed block, so a burst that straddles a boundary within that jitter can
        // surface from both sides; the +-2 s payload dedupe below absorbs it (a genuine
        // retransmission cannot arrive that fast at these symbol rates).
        int workers = a.Int("workers", Math.Clamp(Environment.ProcessorCount - 2, 1, 16));
        string? csvPath = a.Str("csv", null);
        string? framelogPath = a.Str("framelog", null);

        // Every flag replay recognises has now been read at least once - reject anything left
        // over before the (expensive, parallel) receiver pass starts, so a mistyped flag cannot
        // spend minutes of compute replaying the wrong experiment.
        a.RejectUnknown("replay");

        int segments = Math.Clamp(workers / specs.Length, 1, chunks.Length);
        var starts = new int[segments + 1];
        for (int s = 0; s <= segments; s++)
        {
            starts[s] = s * chunks.Length / segments;
        }

        DateTimeOffset runStart = ChunkUtc(chunks[0]);
        var perJob = new List<ReplayedFrame>[specs.Length * segments];
        var burstsPerJob = new List<DcdBurst>[specs.Length * segments];
        var orphansPerJob = new int[specs.Length * segments];
        var ends = new DateTimeOffset[specs.Length * segments];
        Parallel.For(0, specs.Length * segments,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            job =>
            {
                int spec = job / segments;
                int segment = job % segments;
                int ownStart = starts[segment];
                int feedStart = Math.Max(0, ownStart - 1);
                string[] feed = chunks[feedStart..starts[segment + 1]];
                DateTimeOffset windowStart = ChunkUtc(chunks[ownStart]);
                DateTimeOffset windowEnd = segment == segments - 1
                    ? DateTimeOffset.MaxValue
                    : ChunkUtc(chunks[starts[segment + 1]]);
                DcdBurstTracker? tracker = burstsPath is null ? null : new DcdBurstTracker();
                (perJob[job], ends[job]) = RunPass(
                    specs[spec], feed, rate, detector, windowStart, windowEnd,
                    reportGaps: spec == 0, tracker,
                    // A burst that opens near the window's end outlives it; feeding a bounded
                    // slice of the next segment's first chunk lets it close here, on the side
                    // that owns its start (see the ownership rule below).
                    cooldownChunk: tracker is not null && starts[segment + 1] < chunks.Length
                        ? chunks[starts[segment + 1]]
                        : null);
                // Each burst has exactly one owner: the segment whose window contains its
                // start. The neighbouring segment sees the same burst begin during its warmup
                // chunk and drops it here.
                burstsPerJob[job] = tracker is null
                    ? []
                    : tracker.Bursts
                        .Where(b => b.UtcStart >= windowStart && b.UtcStart < windowEnd)
                        .ToList();
                orphansPerJob[job] = tracker?.OrphanFrames ?? 0;
            });
        DateTimeOffset runEnd = ends.Max();
        List<ReplayedFrame> frames = Deduplicate(
            perJob.SelectMany(f => f).OrderBy(f => f.Utc).ToList());
        if (!quiet)
        {
            foreach (ReplayedFrame frame in frames)
            {
                Console.WriteLine(frame.Describe());
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== replay {runStart:yyyy-MM-dd HH:mm}Z .. {runEnd:HH:mm}Z "
            + $"({chunks.Length} chunks, {(runEnd - runStart).TotalHours:F1} h, "
            + $"{segments} segments x {specs.Length} modes on {workers} workers) ===");
        foreach ((string mode, double centre) in specs)
        {
            int total = frames.Count(f => f.SpecMode == mode);
            int delivered = frames.Count(f => f.SpecMode == mode && !f.Quality.MonitorOnly);
            Console.WriteLine($"{mode}@{centre:F0}: {total} decoded, {delivered} deliverable"
                + (detector is { } det ? $" (detector {det})" : ""));
        }

        if (burstsPath is not null)
        {
            List<DcdBurst> bursts = burstsPerJob.SelectMany(b => b)
                .OrderBy(b => b.UtcStart).ToList();
            int orphans = orphansPerJob.Sum();
            int decodedBursts = bursts.Count(b => b.Decoded);
            int syncedUndecoded = bursts.Count(b => !b.Decoded && b.RsFailures > 0);
            Console.WriteLine(
                $"bursts: {bursts.Count} dcd, {decodedBursts} decoded, "
                + $"{bursts.Count - decodedBursts} undecoded "
                + $"({syncedUndecoded} of those synced-then-RS-failed), "
                + $"{orphans} orphan frames");
            WriteBurstsCsv(burstsPath, bursts);
            Console.WriteLine($"bursts csv: {burstsPath}");
        }

        if (csvPath is not null)
        {
            WriteCsv(csvPath, frames);
            Console.WriteLine($"csv: {csvPath}");
        }

        if (framelogPath is not null)
        {
            DiffAgainstLog(framelogPath, frames, specs.Select(s => s.Mode).ToArray(), runStart, runEnd);
        }

        return 0;
    }

    /// <summary>Removes the segment-boundary twins: the same payload on the same mode WITH
    /// THE SAME DELIVERY STATUS within 2 s is one transmission surfaced by both sides of a
    /// boundary - never a genuine retransmission (a minimum frame takes over a second at
    /// 300 Bd, and real retries are tens of seconds apart), and never the bank's deliberate
    /// monitor-plus-delivered double emission of one transmission, which differs in
    /// MonitorOnly (matching on payload alone ate exactly one frame of each such pair -
    /// the 708-vs-701 discrepancy that gated this instrument's first parallel run). Input
    /// must be time-sorted.</summary>
    private static List<ReplayedFrame> Deduplicate(List<ReplayedFrame> sorted)
    {
        var result = new List<ReplayedFrame>(sorted.Count);
        foreach (ReplayedFrame frame in sorted)
        {
            bool twin = false;
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if ((frame.Utc - result[i].Utc) > TimeSpan.FromSeconds(2))
                {
                    break;
                }

                if (result[i].SpecMode == frame.SpecMode
                    && result[i].Quality.MonitorOnly == frame.Quality.MonitorOnly
                    && result[i].Payload.AsSpan().SequenceEqual(frame.Payload))
                {
                    twin = true;
                    break;
                }
            }

            if (!twin)
            {
                result.Add(frame);
            }
        }

        return result;
    }

    /// <summary>Audio fed past a segment's window when bursts are tracked, so a burst that
    /// opens just inside the window can close on this side of the boundary. Bursts run seconds;
    /// a minute is beyond generous without costing a whole extra chunk per segment.</summary>
    private const int CooldownSeconds = 60;

    /// <summary>One mode's continuous pass over one contiguous chunk range, keeping only
    /// the frames delivered inside [<paramref name="windowStart"/>,
    /// <paramref name="windowEnd"/>) - the range ahead of the window is warmup. With a
    /// <paramref name="tracker"/>, the receiver's burst-level signals are polled once per feed
    /// block (100 ms, against bursts of 0.7 s and up) and every delivered frame is offered to
    /// it, warmup and cooldown included - burst ownership is settled by the caller on burst
    /// start times, not here.</summary>
    private static (List<ReplayedFrame> Frames, DateTimeOffset End) RunPass(
        (string Mode, double CentreHz) spec, string[] chunks, int rate,
        PskDetector? detector, DateTimeOffset windowStart, DateTimeOffset windowEnd,
        bool reportGaps, DcdBurstTracker? tracker = null, string? cooldownChunk = null)
    {
        var frames = new List<ReplayedFrame>();
        DateTimeOffset blockTime = default;
        IModem modem = ModemCatalog.Create(spec.Mode, rate, static _ => { },
            new ModemOptions(CentreFrequencyHz: spec.CentreHz, Detector: detector));
        var bank = modem as BpskMultiModem;
        modem.FrameDecoded += (frame, quality) =>
        {
            if (blockTime >= windowStart && blockTime < windowEnd)
            {
                frames.Add(new ReplayedFrame(blockTime, spec.Mode, frame, quality));
            }

            tracker?.OnFrame(blockTime, quality);
        };

        int block = Math.Max(1, rate * BlockMilliseconds / 1000);
        void Feed(float[] audio, DateTimeOffset start, int limit)
        {
            for (int pos = 0; pos < limit; pos += block)
            {
                int length = Math.Min(block, limit - pos);
                blockTime = start + TimeSpan.FromSeconds((pos + length) / (double)rate);
                modem.Process(audio.AsSpan(pos, length));
                tracker?.Observe(blockTime, modem.CarrierDetect, bank?.CarrierOffsetHz,
                    bank?.RsFailures ?? 0, bank?.CrcFailures ?? 0);
            }
        }

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

            Feed(audio, start, audio.Length);
            expected = start + TimeSpan.FromSeconds(audio.Length / (double)rate);
        }

        if (tracker is not null)
        {
            if (cooldownChunk is not null)
            {
                (float[] audio, int chunkRate) = WavFile.ReadMono(cooldownChunk);
                if (chunkRate == rate)
                {
                    Feed(audio, ChunkUtc(cooldownChunk),
                        Math.Min(audio.Length, rate * CooldownSeconds));
                }
            }

            tracker.Flush();
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

    private static void WriteBurstsCsv(string path, List<DcdBurst> bursts)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("utc_start,utc_end,dcd_seconds,decoded,frames,offset_hz,rs_failures,crc_failures");
        foreach (DcdBurst b in bursts)
        {
            writer.WriteLine(string.Join(',',
                b.UtcStart.ToString("O"),
                b.UtcEnd.ToString("O"),
                b.DcdSeconds.ToString("F1", CultureInfo.InvariantCulture),
                b.Decoded ? 1 : 0,
                b.Frames,
                b.OffsetHz?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
                b.RsFailures,
                b.CrcFailures));
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
