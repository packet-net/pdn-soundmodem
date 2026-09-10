using System.Globalization;
using Packet.SoundModem.Modems;
using Packet.SoundModem.TncTest;

// sm-tnctest: the corpus benchmark. Plays a recorded track through one or more of the
// catalogue's modems and reports what came out.
//
// The measure it exists for is the one the packet world has used since the 1990s: WA8LMF's
// TNC Test CD, a real off-air recording of a busy 144.39 MHz APRS channel, played into a TNC,
// scored on how many frames it recovers. It is a leaderboard rather than a pass/fail, because
// the recording contains an unknown number of packets - some of them unrecoverable by anything -
// so the only meaningful statement is "this decoder got N, that one got M, from the same audio".
// Everything here follows from that: the same track has to reach the modem the same way every
// time, and the run has to be comparable against another run rather than only readable.
//
// See docs/dev/bench/tnc-test-cd.md for the corpus, the scoring convention, and the standing scores.

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Options.Usage);
    return args.Length == 0 ? 2 : 0;
}

if (!Options.TryParse(args, out Options options, out string parseError))
{
    Console.Error.WriteLine($"sm-tnctest: {parseError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

foreach (string mode in options.Modes)
{
    if (!ModemCatalog.IsKnown(mode))
    {
        Console.Error.WriteLine(
            $"sm-tnctest: unknown mode '{mode}'. Did you mean "
            + $"{string.Join(", ", ModemCatalog.NearestModes(mode))}?");
        return 2;
    }
}

RunRecord[]? baseline = null;
if (options.Baseline is not null)
{
    try
    {
        baseline = RunRecord.Load(options.Baseline);
    }
    catch (Exception failure) when (failure is IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"sm-tnctest: cannot read baseline '{options.Baseline}': {failure.Message}");
        return 2;
    }
}

var written = new List<RunRecord>();

foreach (string path in options.Tracks)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"sm-tnctest: no such track '{path}'");
        return 2;
    }

    Report.Progress(options, $"reading {Path.GetFileName(path)}");
    TrackAudio track;
    try
    {
        track = TrackReader.Read(path, options.Channel);
    }
    catch (Exception failure) when (failure is IOException or InvalidDataException
                                        or NotSupportedException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine($"sm-tnctest: cannot read '{path}': {failure.Message}");
        return 1;
    }

    track = TrackReader.Slice(track, options.SkipSeconds, options.LengthSeconds);
    Report.Track(path, track, options);

    var scores = new List<ModeScore>();
    foreach (string mode in options.Modes)
    {
        int dspRate = options.DspRate ?? ModemCatalog.DspRateFor(mode);

        Report.Progress(options, $"{mode}: resampling {track.SampleRate} Hz to {dspRate} Hz");
        float[] audio = TrackReader.AtRate(track, dspRate);

        ModeScore score;
        try
        {
            score = Scorer.Run(
                mode, audio, dspRate, options.CentreHz, options.OffsetPairs,
                fraction => Report.Progress(
                    options,
                    $"{mode}: {fraction * 100:F0}%",
                    overwrite: true));
        }
        catch (ArgumentException refused)
        {
            // The catalogue refuses knobs a mode cannot honour - a centre frequency for the
            // baseband fsk family, an ensemble detector for anything but the PSK banks. That is
            // a command line to correct, not a crash to read a stack trace out of.
            Report.ClearProgress(options);
            Console.Error.WriteLine($"sm-tnctest: {mode} will not take those options: {refused.Message}");
            return 2;
        }

        Report.ClearProgress(options);
        scores.Add(score);
        Report.Score(score, options);
    }

    if (scores.Count > 1)
    {
        Report.Comparison(scores);
    }

    var record = RunRecord.From(path, track, scores);
    written.Add(record);

    if (baseline is not null)
    {
        Report.Diff(record, baseline);
    }
}

if (options.JsonPath is not null)
{
    RunRecord.Save(options.JsonPath, written);
    Console.WriteLine();
    Console.WriteLine($"written {options.JsonPath}");
}

return 0;

/// <summary>Everything this tool prints, kept together so the report has one shape.</summary>
internal static class Report
{
    private static bool _progressShowing;

    public static void Progress(Options options, string message, bool overwrite = false)
    {
        if (options.Quiet || Console.IsErrorRedirected)
        {
            return;
        }

        if (overwrite)
        {
            Console.Error.Write($"\r  {message}    ");
            _progressShowing = true;
            return;
        }

        ClearProgress(options);
        Console.Error.WriteLine($"  {message}");
    }

    public static void ClearProgress(Options options)
    {
        if (_progressShowing && !options.Quiet && !Console.IsErrorRedirected)
        {
            Console.Error.Write($"\r{new string(' ', 60)}\r");
            _progressShowing = false;
        }
    }

    public static void Track(string path, TrackAudio track, Options options)
    {
        Console.WriteLine();
        Console.WriteLine(Path.GetFileName(path));
        Console.WriteLine(new string('=', Path.GetFileName(path).Length));

        string integrity = track.Integrity switch
        {
            true => ", MD5 verified",
            false => ", MD5 MISMATCH - the decode of this file is not trustworthy",
            null => "",
        };

        Console.WriteLine(
            $"  audio    {track.SampleRate} Hz, {track.Channels} ch, {track.BitsPerSample} bit, "
            + $"{Duration(track.Duration)}{integrity}");

        string levels = string.Join(
            ", ", track.ChannelLevels.Select(l => $"{l,6:F1}"));
        Console.WriteLine(
            $"  channel  {track.Channel} of {track.Channels} (peak dBFS per channel: {levels})");

        if (options.SkipSeconds > 0 || options.LengthSeconds is not null)
        {
            Console.WriteLine(
                $"  window   from {options.SkipSeconds:F1} s"
                + (options.LengthSeconds is double length ? $" for {length:F1} s" : " to the end"));
        }
    }

    public static void Score(ModeScore score, Options options)
    {
        string centre = score.CentreHz is double hz ? $", centre {hz:F0} Hz" : "";
        Console.WriteLine();
        Console.WriteLine($"  {score.Mode} at {score.DspRate} Hz{centre}");
        Console.WriteLine($"    receiver                 {score.Receiver}");
        Console.WriteLine($"    SCORE (frames decoded)   {score.Score}");
        Console.WriteLine($"    distinct frame contents  {score.Distinct}");
        Console.WriteLine($"    AX.25-shaped             {score.Ax25Shaped}");
        Console.WriteLine($"    source callsigns heard   {score.Stations.Count}");
        Console.WriteLine($"    decoded bytes            {score.Bytes}");
        Console.WriteLine(
            $"    wall clock               {score.Elapsed.TotalSeconds:F1} s "
            + $"({score.RealTimeFactor:F1}x real time)");

        if (score.Delivered != score.Score)
        {
            // Only a mode with a monitor-only decode path can get here, and on this corpus none
            // can. Printed rather than swallowed because the two numbers mean different things
            // to whoever is reading a leaderboard: heard, and passed to the host.
            Console.WriteLine($"    passed to host           {score.Delivered}");
        }

        // Not "which branch won": the modem reports the winning branch's offset plus that
        // branch's own residual measurement, which together are where the station sat relative
        // to the channel centre however far out the branch that copied it happened to be.
        Histogram(
            "station offset, centred",
            score.Frames.Where(f => f.OffsetHz is not null)
                .GroupBy(f => (int)Math.Round(f.OffsetHz!.Value / 30.0) * 30)
                .OrderBy(g => g.Key)
                .Select(g => ($"{g.Key:+#;-#;0} Hz", g.Count())));

        Histogram(
            "winning emphasis branch",
            score.Frames.Where(f => f.EmphasisDb is not null)
                .GroupBy(f => f.EmphasisDb!.Value)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key == 0 ? "flat" : $"+{g.Key:F0} dB/oct", g.Count())));

        if (options.ListFrames)
        {
            Console.WriteLine();
            foreach (ScoredFrame frame in score.Frames)
            {
                string header = Packet.SoundModem.MultiDecode.FrameText.Ax25Header(frame.Bytes)
                    ?? $"[not AX.25-shaped, {frame.Bytes.Length} bytes]";
                string info = Packet.SoundModem.MultiDecode.FrameText.InfoField(frame.Bytes) ?? "";
                Console.WriteLine($"    {Timestamp(frame.AtSeconds)}  {header}");
                if (info.Length > 0)
                {
                    Console.WriteLine($"                {info}");
                }
            }
        }
    }

    public static void Comparison(IReadOnlyList<ModeScore> scores)
    {
        int best = scores.Max(s => s.Score);
        Console.WriteLine();
        Console.WriteLine("  mode comparison");
        foreach (ModeScore score in scores.OrderByDescending(s => s.Score))
        {
            string relative = score.Score == best
                ? ""
                : $"  ({score.Score - best}, {100.0 * score.Score / best:F1}% of best)";
            Console.WriteLine($"    {score.Mode,-24} {score.Score,6}{relative}");
        }
    }

    public static void Diff(RunRecord current, IReadOnlyList<RunRecord> baseline)
    {
        RunRecord? previous = baseline.FirstOrDefault(
            r => string.Equals(r.Track, current.Track, StringComparison.OrdinalIgnoreCase));

        if (previous is null)
        {
            Console.WriteLine();
            Console.WriteLine($"  baseline holds no run of {current.Track}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  against baseline");

        foreach (RunResult run in current.Runs)
        {
            RunResult? was = previous.Runs.FirstOrDefault(
                r => string.Equals(r.Mode, run.Mode, StringComparison.Ordinal));

            if (was is null)
            {
                Console.WriteLine($"    {run.Mode,-24} new (baseline did not run it)");
                continue;
            }

            // Frame sets, not counts: two runs can score the same and have decoded different
            // frames, which is the thing a change to a receive path most often does and the
            // thing a count alone hides.
            var mine = run.Frames.Select(f => f.Hex).ToList();
            var theirs = was.Frames.Select(f => f.Hex).ToList();
            int gained = mine.Except(theirs, StringComparer.Ordinal).Count();
            int lost = theirs.Except(mine, StringComparer.Ordinal).Count();

            Console.WriteLine(
                $"    {run.Mode,-24} {was.Score} -> {run.Score} "
                + $"({run.Score - was.Score:+#;-#;+0}), {gained} newly decoded, {lost} no longer decoded");
        }
    }

    private static void Histogram(string label, IEnumerable<(string Key, int Count)> bins)
    {
        var materialised = bins.ToList();
        if (materialised.Count == 0)
        {
            return;
        }

        Console.WriteLine(
            $"    {label,-24} "
            + string.Join("  ", materialised.Select(b => $"{b.Key}: {b.Count}")));
    }

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    private static string Timestamp(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
}
