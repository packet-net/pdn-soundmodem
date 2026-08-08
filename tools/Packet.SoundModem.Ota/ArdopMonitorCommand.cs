using System.Globalization;
using M0LTE.Ardop;
using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Off-air ARDOP monitor - the ARDOP campaign's A0 instrument (docs/ardop/plan.md). Runs the
/// receive chain in RXO (receive-only monitor) mode over recorded audio and emits one verdict
/// row per acquired frame: type, RS/CRC outcome, quality, the third-party session ID the RXO
/// decoder attributes it to, and the station fields where the frame carries them. Wild
/// monitoring hears the two sides of a session at very different strengths and meets
/// implementations beyond ardopcf; the instrument records what it read and what it saw but
/// could not classify (type acquired, body failed), and pretends nothing.
/// </summary>
/// <remarks>
/// <para><b>Centre handling.</b> ARDOP's waveforms are pinned to a 1500 Hz centre; the A1
/// sweep measured wild slot-2 sessions on the 40 m capture clustering at 1500-1560 Hz audio
/// (the survey estimator's ~1650 was off by ~150), so the default is native and unshifted.
/// For a non-native centre the shift discipline is
/// <c>ArdopChannelBridge</c>'s (daemon-internal, so the ~15 lines are replicated here with the
/// same order): bandpass to the on-air band FIRST, then unshift - unshifting a centre above
/// 1500 is a downshift by delta, which folds all channel noise below delta onto delta - f
/// unless the bandpass has already made that region stopband (the noise-folding lesson,
/// measured at +2.9 dB in the ARDOP bridge audit, docs/mode-validation.md 2026-08-06).</para>
/// <para><b>Tuning range.</b> Defaults to ±200 Hz rather than ardopcf's ±100: a monitor does
/// not choose its stations, and the harvest measured wild session centres spread across
/// 1500-1800 Hz; ±200 about the configured centre is the spec's own capture requirement
/// (spec §4.1) and covers the measured spread.</para>
/// </remarks>
internal static class ArdopMonitorCommand
{
    private const int EngineRate = 12000;
    private const double NativeCentreHz = 1500.0;
    private const double WidestBandwidthHz = 2000.0;
    private const double BandpassMarginHz = 300.0;
    private const int BandpassTaps = 639;
    private const int BlockMilliseconds = 100;

    public static int Run(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help")
        {
            Console.WriteLine(
                """
                usage: sm-ota ardop-monitor --raw <dir|file[,file...]> [--centre <Hz>]
                                            [--from <UTC>] [--to <UTC>] [--csv <path>]
                                            [--squelch <0-10>] [--tuning <Hz>] [--quiet]
                       sm-ota ardop-monitor --wav <file[,file...]> [--centre <Hz>] ...

                Monitors recorded audio for ARDOP frames in RXO mode and reports per-frame
                verdicts and per-session grouping.

                  --raw <dir>          raw-<UTC>.wav chunk directory (or explicit files), fed
                                       continuously in name order with absolute timestamps
                  --wav <files>        isolated mode: a FRESH demodulator per file (survey
                                       bursts); times from the filename where parseable
                  --centre <Hz>        audio centre of the ARDOP activity (default 1500,
                                       native, no shift - the A1 sweep measured wild session
                                       centres clustering at 1500-1560, not the ~1650 the
                                       survey estimator suggested; docs/ardop/plan.md)
                  --squelch <n>        leader-detect squelch 0-10 (default 5, the ardopcf
                                       default)
                  --tuning <Hz>        leader capture range (default 200; see source remarks)
                  --from/--to <UTC>    bound the chunk list (ISO or yyyymmddTHHMMSSZ)
                  --csv <path>         one row per acquired frame
                  --emit <path>        write the post-chain stream (bandpassed, unshifted
                                       to the 1500 Hz native centre) that the demodulator
                                       actually heard, as a 12 kHz 16-bit WAV - the seam
                                       for external reference decoders (--wav, one file)
                  --quiet              suppress per-frame lines
                """);
            return argv.Length == 0 ? 2 : 0;
        }

        var a = Args.Parse(argv);
        if (a is null)
        {
            return 2;
        }

        // Default was 1650 (the survey estimator's number) until the A1 centre sweep
        // measured true session centres at 1500-1560: the corrected default acquired
        // 749 frames against 636 over the same corpus (docs/ardop/plan.md, A1 addendum).
        double centre = a.Dbl("centre", 1500);
        int squelch = a.Int("squelch", 5);
        int tuning = a.Int("tuning", 200);
        bool quiet = a.Has("quiet");
        string? csv = a.Str("csv", null);
        string? emit = a.Str("emit", null);

        var rows = new List<Verdict>();
        if (a.Str("wav", null) is { } wavArg)
        {
            string[] paths = wavArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (emit is not null && paths.Length != 1)
            {
                Console.Error.WriteLine("--emit needs exactly one --wav file");
                return 2;
            }

            foreach (string path in paths)
            {
                MonitorOne(path, FileUtc(path), centre, squelch, tuning, rows, quiet, emit);
            }
        }
        else if (emit is not null)
        {
            Console.Error.WriteLine("--emit needs --wav mode");
            return 2;
        }
        else
        {
            string rawArg = a.Req("raw");
            string[] chunks = ReplayCommand.ResolveChunks(
                rawArg, ReplayCommand.ParseUtc(a.Str("from", null)), ReplayCommand.ParseUtc(a.Str("to", null)));
            if (chunks.Length == 0)
            {
                Console.Error.WriteLine($"no raw-*.wav chunks under '{rawArg}' in the requested window");
                return 2;
            }

            // One demodulator across the whole sequence: RXO state (session attribution,
            // Memory ARQ) is what a live monitor would carry, and chunks are contiguous.
            var monitor = new Monitor(centre, squelch, tuning, rows, quiet);
            foreach (string chunk in chunks)
            {
                monitor.Feed(chunk, ReplayCommand.ChunkUtc(chunk));
            }
        }

        Report(rows, centre);
        if (csv is not null)
        {
            WriteCsv(csv, rows);
            Console.WriteLine($"csv: {csv}");
        }

        return 0;
    }

    private sealed record Verdict(
        DateTimeOffset Utc, string Source, byte Type, string Name, bool Ok, int Quality,
        byte SessionId, string? Caller, string? Target, string? Grid, int LeaderMs);

    private sealed class Monitor
    {
        private readonly ArdopDemodulator _demodulator;
        private readonly FirFilter? _bandpass;
        private readonly FrequencyShifter? _unshift;
        private readonly List<Verdict> _rows;
        private readonly bool _quiet;
        private float[] _banded = [];
        private float[] _engine = [];
        private DateTimeOffset _blockTime;
        private string _source = "";

        /// <summary>When set, every sample handed to the demodulator is also appended
        /// here - the --emit seam. What external decoders get is bit-identical to what
        /// ours heard.</summary>
        public List<float>? EmitSink { get; init; }

        public Monitor(double centre, int squelch, int tuning, List<Verdict> rows, bool quiet)
        {
            _rows = rows;
            _quiet = quiet;
            _demodulator = new ArdopDemodulator(squelch, tuning) { RxoMode = true };
            double delta = centre - NativeCentreHz;
            if (delta != 0)
            {
                // Bandpass BEFORE unshift - see the type remarks for why the order is
                // load-bearing. Same numbers as ArdopChannelBridge.
                _bandpass = new FirFilter(FilterDesign.BandPass(
                    Math.Max(50, centre - (WidestBandwidthHz / 2) - BandpassMarginHz),
                    Math.Min((EngineRate / 2.0) - 50, centre + (WidestBandwidthHz / 2) + BandpassMarginHz),
                    EngineRate, BandpassTaps));
                _unshift = new FrequencyShifter(EngineRate, -delta);
            }

            _demodulator.FrameDecoded += frame =>
            {
                var verdict = new Verdict(
                    _blockTime, _source, frame.Type, frame.Name, frame.Ok, frame.Quality,
                    _demodulator.RxoSessionId,
                    frame.Caller?.ToString(), frame.Target?.ToString(), frame.GridSquare,
                    frame.LeaderReceivedMs);
                _rows.Add(verdict);
                if (!_quiet)
                {
                    Console.WriteLine(Describe(verdict));
                }
            };
        }

        public void Feed(string path, DateTimeOffset start)
        {
            _source = Path.GetFileName(path);
            (float[] audio, int rate) = WavFile.ReadMono(path);
            if (rate != EngineRate)
            {
                Console.Error.WriteLine($"{_source}: rate {rate} != {EngineRate} - skipped");
                return;
            }

            int block = EngineRate * BlockMilliseconds / 1000;
            for (int pos = 0; pos < audio.Length; pos += block)
            {
                int length = Math.Min(block, audio.Length - pos);
                _blockTime = start + TimeSpan.FromSeconds((pos + length) / (double)EngineRate);
                var slice = audio.AsSpan(pos, length);
                if (_unshift is null)
                {
                    _demodulator.ProcessSamples(slice);
                    EmitSink?.AddRange(slice);
                    continue;
                }

                if (_banded.Length < length)
                {
                    _banded = new float[length];
                    _engine = new float[length];
                }

                for (int i = 0; i < length; i++)
                {
                    _banded[i] = _bandpass!.Next(slice[i]);
                }

                _unshift.Process(_banded.AsSpan(0, length), _engine.AsSpan(0, length));
                _demodulator.ProcessSamples(_engine.AsSpan(0, length));
                EmitSink?.AddRange(_engine.AsSpan(0, length));
            }
        }
    }

    private static void MonitorOne(
        string path, DateTimeOffset utc, double centre, int squelch, int tuning,
        List<Verdict> rows, bool quiet, string? emit)
    {
        var monitor = new Monitor(centre, squelch, tuning, rows, quiet)
        {
            EmitSink = emit is not null ? [] : null,
        };
        monitor.Feed(path, utc);
        if (emit is not null)
        {
            WavFile.WriteMono(emit, monitor.EmitSink!.ToArray(), EngineRate);
            Console.WriteLine($"emitted: {emit}");
        }
    }

    private static string Describe(Verdict v)
    {
        string stations = v.Caller is not null
            ? $" {v.Caller}>{v.Target ?? "-"}{(v.Grid is not null ? $" [{v.Grid}]" : "")}"
            : "";
        return $"{v.Utc:HH:mm:ss}Z [{v.SessionId:X2}] {v.Name,-14} "
            + $"{(v.Ok ? "ok" : "FAIL"),-4} q{v.Quality,3}{stations} leader {v.LeaderMs} ms";
    }

    private static void Report(List<Verdict> rows, double centre)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ardop-monitor @{centre:F0} Hz: {rows.Count} frames acquired, "
            + $"{rows.Count(v => v.Ok)} decoded ok ===");
        foreach (var group in rows.GroupBy(v => v.Name).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key,-16} {group.Count(),4} acquired, {group.Count(v => v.Ok),4} ok");
        }

        // Session grouping: consecutive frames sharing the RXO session ID within 120 s.
        var sessions = new List<(byte Id, DateTimeOffset From, DateTimeOffset To, int Frames, int Ok, string Calls)>();
        foreach (var frame in rows.OrderBy(v => v.Utc))
        {
            if (sessions.Count > 0 && sessions[^1].Id == frame.SessionId
                && frame.Utc - sessions[^1].To <= TimeSpan.FromSeconds(120))
            {
                var s = sessions[^1];
                string calls = frame.Caller is not null && !s.Calls.Contains(frame.Caller)
                    ? (s.Calls.Length > 0 ? s.Calls + "+" + frame.Caller : frame.Caller)
                    : s.Calls;
                sessions[^1] = (s.Id, s.From, frame.Utc, s.Frames + 1, s.Ok + (frame.Ok ? 1 : 0), calls);
            }
            else
            {
                sessions.Add((frame.SessionId, frame.Utc, frame.Utc, 1, frame.Ok ? 1 : 0,
                    frame.Caller ?? ""));
            }
        }

        List<(byte, DateTimeOffset, DateTimeOffset, int, int, string)> real =
            [.. sessions.Where(s => s.Frames >= 2)];
        Console.WriteLine($"session groups (>=2 frames, same RXO id within 120 s): {real.Count}");
        foreach (var s in real)
        {
            Console.WriteLine($"  [{s.Item1:X2}] {s.Item2:MM-dd HH:mm:ss}Z .. {s.Item3:HH:mm:ss}Z "
                + $"{s.Item4} frames ({s.Item5} ok){(s.Item6.Length > 0 ? " " + s.Item6 : "")}");
        }
    }

    private static void WriteCsv(string path, List<Verdict> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("utc,source,type,name,ok,quality,session_id,caller,target,grid,leader_ms");
        foreach (Verdict v in rows)
        {
            writer.WriteLine(string.Join(',',
                v.Utc.ToString("O"), v.Source, $"0x{v.Type:X2}", v.Name, v.Ok ? 1 : 0,
                v.Quality, $"0x{v.SessionId:X2}", v.Caller ?? "", v.Target ?? "",
                v.Grid ?? "", v.LeaderMs));
        }
    }

    /// <summary>Best-effort UTC from a survey/burst filename (yyyyMMdd-HHmmss prefix) or the
    /// raw-chunk form; epoch when neither parses (isolated rows still carry the source).</summary>
    private static DateTimeOffset FileUtc(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("raw-", StringComparison.Ordinal))
        {
            return ReplayCommand.ChunkUtc(path);
        }

        string[] parts = name.Split('-');
        if (parts.Length >= 2
            && DateTimeOffset.TryParseExact(parts[0] + parts[1], "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t))
        {
            return t;
        }

        return DateTimeOffset.UnixEpoch;
    }
}
