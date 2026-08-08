using System.Globalization;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota oracle</c> - the ceiling probe for rx-roadmap workstream 4: decode each burst twice,
/// once with the symbol clock the receiver can actually recover from noisy audio and once with the
/// clock a noise-free view of that same burst says is right, and report the gap.
/// </summary>
/// <remarks>
/// The gap is the whole of what any symbol-timing work could return - two-pass non-causal
/// smoothing included, since a second pass over a buffered burst is still estimating from noisy
/// observations while the oracle is not. Size before building, as workstream 3 was:
/// see <see cref="TimingOracleProbe"/> for the construction and its falsification arm.
/// </remarks>
internal static class OracleCommand
{
    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota oracle --mode bpsk300 --snr <a,b,c> [options]

                Symbol-timing ceiling probe (rx-roadmap workstream 4). Each burst is decoded with
                the recovered clock (causal, as deployed) and with the clock read off a noise-free
                copy of the same burst (oracle). The gap bounds every timing improvement there is.

                  --mode <name>        bpsk* catalogue mode (default bpsk300)
                  --snr <a,b,c>        SNR rungs in dB (3 kHz), ascending
                  --channel <list>     awgn|good|moderate|poor, comma-separated (default awgn)
                  --cfo <list>         carrier-offset Hz, comma-separated (default 0)
                  --bursts <n>         independent trials per point (default 100)
                  --frame-bytes <n>    AX.25 frame size (default 60)
                  --txdelay <ms>       preamble run-in (default 0)
                  --seed <n>           first burst seed (default 1)
                  --workers <n>        bounded parallelism (default 4 - shared box)
                  --phase-profile      instead of the arm table, sweep a CONSTANT sampling
                                       displacement (in samples, applied identically to every
                                       burst) across a whole symbol and print the success curve.
                                       A displacement that wins at the same value everywhere is a
                                       systematic clock bias worth correcting; a flat curve says
                                       the hindsight arm below is only lottery diversity
                  --phase-sweep        add the hindsight arm: decode at EVERY sampling phase and
                                       count the burst if any phase delivers. Costs one decode per
                                       sample-per-symbol (40x at 300 Bd), and bounds every
                                       fixed-phase scheme there could ever be
                  --no-control         skip the half-symbol-slipped falsification arm (faster, and
                                       forfeits the instrument's own proof that it is driving the
                                       receiver at all - do not skip it on a run you will quote)
                  --csv <path>         write one row per point
                  --quiet              suppress the per-point progress lines

                The probe drives a single branch, not the deployed diversity bank, so its absolute
                rates are its own; read the causal-vs-oracle gap, not the level.
                """);
            return a is null ? 2 : 0;
        }

        string mode = a.Str("mode", "bpsk300");
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        SimChannelKind[] channels = a.Str("channel", "awgn").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(SimChannel.Parse).ToArray();
        double[] cfos = a.Str("cfo", "0").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        int bursts = a.Int("bursts", 100);
        int frameBytes = a.Int("frame-bytes", 60);
        int txDelayMs = a.Int("txdelay", 0);
        int firstSeed = a.Int("seed", 1);
        int workers = a.Int("workers", 4);
        bool control = !a.Has("no-control");
        bool phaseSweep = a.Has("phase-sweep");
        bool phaseProfile = a.Has("phase-profile");
        bool quiet = a.Has("quiet");
        string? csvPath = a.Str("csv", null);
        a.RejectUnknown("oracle");

        if (!TimingOracleProbe.IsSupported(mode))
        {
            Console.Error.WriteLine($"the timing oracle drives the BPSK receive chain; '{mode}' is not a bpsk mode");
            return 2;
        }

        void Log(string m)
        {
            if (!quiet)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");
            }
        }

        Log($"oracle {mode} bursts={bursts} frameBytes={frameBytes} control={control} workers={workers}");
        Log($"channels: {string.Join(',', channels)}  snrs: {string.Join(',', snrs)}");

        if (phaseProfile)
        {
            return PhaseProfile(mode, channels, snrs, cfos, bursts, frameBytes, txDelayMs,
                firstSeed, workers, Log);
        }

        var rows = new List<OraclePoint>();
        foreach (SimChannelKind kind in channels)
        {
            foreach (double snr in snrs)
            {
                foreach (double cfo in cfos)
                {
                    OraclePoint p = RunPoint(
                        mode, kind, snr, cfo, bursts, frameBytes, txDelayMs, firstSeed, workers,
                        control, phaseSweep);
                    rows.Add(p);
                    string cfoTag = cfos.Length > 1 || cfos[0] != 0 ? $" cfo {cfo,+6:+0.0;-0.0} Hz" : "";
                    Log($"  {kind,-8} {snr,6:+0.0;-0.0} dB{cfoTag}  causal {p.Causal,4}/{p.Trials}  "
                        + $"oracle {p.Oracle,4}  grid {p.Grid,4}  gridGap {p.GridGapPoints,+5:+0;-0}  "
                        + $"anyPhase {(phaseSweep ? $"{p.AnyPhase,4}" : "   -")}  "
                        + $"control {(control ? $"{p.Control}" : "-")}");
                }
            }
        }

        Report(mode, control, rows);
        if (csvPath is not null)
        {
            WriteCsv(csvPath, mode, frameBytes, rows);
            Console.Error.WriteLine($"wrote {csvPath}");
        }

        return 0;
    }

    /// <summary>
    /// The success curve against a constant sampling displacement, one column per rung. The
    /// question it settles: does the recovered clock sample in the wrong place systematically?
    /// </summary>
    private static int PhaseProfile(
        string mode, SimChannelKind[] channels, double[] snrs, double[] cfos, int bursts,
        int frameBytes, int txDelayMs, int firstSeed, int workers, Action<string> log)
    {
        var probe = new TimingOracleProbe(mode, frameBytes, txDelayMs);
        int sps = probe.SamplesPerSymbol;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) };
        Console.WriteLine();
        Console.WriteLine($"=== constant-displacement profile {mode} (single branch, "
            + $"{sps} samples/symbol) ===");
        Console.WriteLine("displacement is in samples from the transmitted grid; 0 is the grid itself");

        foreach (SimChannelKind kind in channels)
        {
            foreach (double snr in snrs)
            {
                foreach (double cfo in cfos)
                {
                    var counts = new int[sps];
                    for (int d = 0; d < sps; d++)
                    {
                        int offset = d - (sps / 2);
                        int hits = 0;
                        object gate = new();
                        Parallel.For(
                            0, bursts, options, () => 0,
                            (i, _, acc) =>
                            {
                                var p = new TimingOracleProbe(mode, frameBytes, txDelayMs);
                                return acc + (p.DisplacedGridDelivers(
                                    firstSeed + i, kind, snr, offset, cfo) ? 1 : 0);
                            },
                            acc =>
                            {
                                lock (gate)
                                {
                                    hits += acc;
                                }
                            });
                        counts[d] = hits;
                    }

                    log($"  {kind} {snr:+0.0;-0.0} dB profiled");
                    Console.WriteLine();
                    Console.WriteLine($"{kind} {snr:+0.0;-0.0} dB, N={bursts}:");
                    for (int d = 0; d < sps; d++)
                    {
                        int offset = d - (sps / 2);
                        Console.WriteLine($"  {offset,+4:+0;-0} samples  {counts[d],4}/{bursts}  "
                            + new string('#', counts[d] * 40 / Math.Max(1, bursts)));
                    }

                    int best = 0;
                    for (int d = 1; d < sps; d++)
                    {
                        if (counts[d] > counts[best])
                        {
                            best = d;
                        }
                    }

                    Console.WriteLine($"  best displacement {best - (sps / 2),+0:+0;-0} samples "
                        + $"({counts[best]}/{bursts}) against {counts[sps / 2]}/{bursts} at the grid");
                }
            }
        }

        return 0;
    }

    private static OraclePoint RunPoint(
        string mode, SimChannelKind kind, double snrDb, double cfoHz, int bursts, int frameBytes,
        int txDelayMs, int firstSeed, int workers, bool control, bool phaseSweep)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) };
        int causal = 0, oracle = 0, grid = 0, slipped = 0, anyPhase = 0;
        object gate = new();

        Parallel.For(
            0, bursts, options,
            () => (Causal: 0, Oracle: 0, Grid: 0, Control: 0, AnyPhase: 0),
            (i, _, acc) =>
            {
                var probe = new TimingOracleProbe(mode, frameBytes, txDelayMs);
                OracleBurstResult r = probe.Run(firstSeed + i, kind, snrDb, cfoHz, control);
                bool any = phaseSweep && probe.AnyPhaseDelivers(firstSeed + i, kind, snrDb, cfoHz);
                return (acc.Causal + (r.Causal ? 1 : 0),
                    acc.Oracle + (r.Oracle ? 1 : 0),
                    acc.Grid + (r.Grid ? 1 : 0),
                    acc.Control + (r.Control ? 1 : 0),
                    acc.AnyPhase + (any ? 1 : 0));
            },
            acc =>
            {
                lock (gate)
                {
                    causal += acc.Causal;
                    oracle += acc.Oracle;
                    grid += acc.Grid;
                    slipped += acc.Control;
                    anyPhase += acc.AnyPhase;
                }
            });

        return new OraclePoint(
            mode, kind, snrDb, cfoHz, bursts, causal, oracle, grid, slipped, anyPhase);
    }

    private static void Report(string mode, bool control, IReadOnlyList<OraclePoint> rows)
    {
        Console.WriteLine();
        Console.WriteLine($"=== timing oracle {mode} (single branch) ===");
        Console.WriteLine($"{"channel",-8} {"SNR",6} {"cfoHz",6} {"causal",9} {"oracle",9} {"grid",9} "
            + $"{"gridGap",8} {"95% CI",16} {"control",9}");
        foreach (OraclePoint r in rows)
        {
            (double lo, double hi) = r.GapCi;
            Console.WriteLine($"{r.Channel,-8} {r.SnrDb,6:+0.0;-0.0} {r.CfoHz,6:+0;-0;0} "
                + $"{$"{r.Causal}/{r.Trials}",9} {$"{r.Oracle}/{r.Trials}",9} {$"{r.Grid}/{r.Trials}",9} "
                + $"{r.GridGapPoints,+8:+0;-0} {$"{lo:+0.0;-0.0}..{hi:+0.0;-0.0}",16} "
                + $"{(control ? $"{r.Control}/{r.Trials}" : "-"),9}");
        }

        Console.WriteLine();
        if (control)
        {
            // The falsification arm first: a schedule slipped half a symbol must wreck the decode.
            // Until that holds, the causal-vs-oracle comparison above means nothing at all.
            int worstControl = rows.Max(r => r.Control);
            int lowestCausal = rows.Min(r => r.Causal);
            Console.WriteLine(worstControl <= lowestCausal / 4
                ? $"instrument OK: the half-symbol-slipped arm collapses (worst {worstControl} vs "
                    + $"causal {lowestCausal}+) - the schedule is driving the receiver."
                : $"INSTRUMENT SUSPECT: the slipped arm still decodes ({worstControl}) - the "
                    + "schedule may not be driving the receiver; read no verdict from this run.");
        }
        else
        {
            Console.WriteLine("no control arm was run - this run cannot show that the oracle drove "
                + "the receiver at all.");
        }

        int totalCausal = rows.Sum(r => r.Causal);
        int totalOracle = rows.Sum(r => r.Oracle);
        int totalGrid = rows.Sum(r => r.Grid);
        int totalTrials = rows.Sum(r => r.Trials);
        double pooledOracle = (totalOracle - totalCausal) * 100.0 / Math.Max(1, totalTrials);
        double pooledGrid = (totalGrid - totalCausal) * 100.0 / Math.Max(1, totalTrials);
        Console.WriteLine($"pooled over {totalTrials}: causal {totalCausal}, "
            + $"oracle {totalOracle} ({pooledOracle:+0.0;-0.0} pts), grid {totalGrid} ({pooledGrid:+0.0;-0.0} pts)");
    }

    private static void WriteCsv(string path, string mode, int frameBytes, IReadOnlyList<OraclePoint> rows)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("mode,channel,frameBytes,snrDb,cfoHz,trials,causal,oracle,grid,control,anyPhase,"
            + "oracleGap,gridGap,gridGapCiLo,gridGapCiHi");
        foreach (OraclePoint r in rows)
        {
            (double lo, double hi) = r.GapCi;
            w.WriteLine(string.Join(',',
                mode, r.Channel, frameBytes, F(r.SnrDb), F(r.CfoHz), r.Trials,
                r.Causal, r.Oracle, r.Grid, r.Control, r.AnyPhase,
                r.GapPoints, r.GridGapPoints, F(lo), F(hi)));
        }
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>One (mode, channel, SNR, CFO) cell of the timing-oracle probe.</summary>
internal sealed record OraclePoint(
    string Mode, SimChannelKind Channel, double SnrDb, double CfoHz,
    int Trials, int Causal, int Oracle, int Grid, int Control, int AnyPhase = 0)
{
    /// <summary>Oracle minus causal, in frames.</summary>
    public int GapPoints => Oracle - Causal;

    /// <summary>Grid (the perfect constant-rate clock) minus causal, in frames - the number
    /// workstream 4's timing half actually rests on.</summary>
    public int GridGapPoints => Grid - Causal;

    /// <summary>95 % interval on the GRID gap in percentage points, from the two arms' Wilson intervals
    /// treated as independent - conservative here, since the arms share their bursts and their
    /// channel realisations and are therefore positively correlated.</summary>
    public (double Lo, double Hi) GapCi
    {
        get
        {
            (double cLo, double cHi) = SimStats.Wilson(Causal, Trials);
            (double gLo, double gHi) = SimStats.Wilson(Grid, Trials);
            return ((gLo - cHi) * 100, (gHi - cLo) * 100);
        }
    }
}
