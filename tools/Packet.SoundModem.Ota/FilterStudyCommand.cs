using System.Collections.Concurrent;
using System.Globalization;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota filter-study</c> - what a receiver's SSB filter costs an MS110D signal, for each
/// transmit pulse-shaping roll-off. Renders an IL2P+CRC frame with a chosen transmit roll-off,
/// puts it through the Watterson rig at a known SNR, then through a model of the receiver's
/// IF filter (<see cref="ReceiveFilter"/>), and decodes it with the receiver's matched filter
/// either left at the standard 0.35 or matched to the transmitter. Scores frame loss.
/// </summary>
/// <remarks>
/// <para>The noise goes in before the receive filter, as it does on air (it arrives at the
/// antenna with the signal), so the SNR axis is the usual SNR in 3 kHz measured ahead of the
/// filter, and a filter's cost is what it takes away from the signal plus whatever it does to
/// the waveform. Everything runs at the modem's native 9600 Hz, resampler-free.</para>
/// <para>Every point uses the same burst seeds, so two points differ by their configuration and
/// not by luck of the draw (the fade realisations match closely across roll-offs; the burst
/// length moves by a few milliseconds with the pulse span). Configurations run in parallel;
/// within one, the SNR ladder runs upward and stops once two rungs in a row lose nothing, the
/// rest recorded as not run, and a point whose first ten frames are all lost stops there (its
/// row says it ran ten). The CSV is appended point by point and a rerun skips the points it
/// already holds, so an interrupted sweep resumes.</para>
/// </remarks>
internal static class FilterStudyCommand
{
    private const int Rate = Ms110dModulator.NativeRate;
    private const int HeaderBytes = 16;

    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota filter-study [options]

                Frame loss of MS110D through a receiver's SSB filter, per transmit roll-off.

                  --modes <list>        waveform numbers (default 3,4)
                  --tx-rolloff <list>   transmit SRRC roll-offs (default 0.35,0.25,0.15,0.10,0.05)
                  --rx <list>           receive matched filter: standard (0.35) and/or matched
                                        (= transmit roll-off). Default standard,matched; at a
                                        0.35 transmitter the two are the same and run once
                  --filters <list>      none | xtal:LO-HI | dsp:LO-HI, -6 dB edges in Hz audio
                                        (default none,xtal:300-2700,xtal:200-2900,xtal:150-3150)
                  --channels <list>     awgn|good|moderate|poor (default awgn,moderate,poor)
                  --snr <list>          SNR rungs, dB in 3 kHz, ascending (default 0..20 step 2)
                  --snr-<channel> <l>   per-channel override, e.g. --snr-awgn 0,1,2,3
                  --payload-bytes <l>   AX.25 information field sizes (default 960,255); the
                                        frame adds a 16-byte UI header
                  --bursts <n>          frames per point (default 40)
                  --seed <n>            first burst seed (default 1)
                  --workers <n>         configurations in flight (default 8)
                  --csv <path>          append one row per point; points already there are skipped
                  --abandon-after <n>   stop a point early when its first n frames are all lost
                                        (default 10; the row records the n frames it ran)
                  --no-early-stop       run every rung in full: no stop after two clean rungs
                                        and no abandoning
                  --describe            print the filters and transmit spectra, then exit
                """);
            return a is null ? 2 : 0;
        }

        int[] modes = Ints(a.Str("modes", "3,4"));
        double[] txRollOffs = Doubles(a.Str("tx-rolloff", "0.35,0.25,0.15,0.10,0.05"));
        string[] rxModes = a.Str("rx", "standard,matched").Split(',', StringSplitOptions.RemoveEmptyEntries);
        ReceiveFilter[] filters = a.Str("filters", "none,xtal:300-2700,xtal:200-2900,xtal:150-3150")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => ReceiveFilter.Parse(f, Rate)).ToArray();
        SimChannelKind[] channels = a.Str("channels", "awgn,moderate,poor")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(SimChannel.Parse).ToArray();
        double[] defaultSnrs = Doubles(a.Str("snr", "0,2,4,6,8,10,12,14,16,18,20"));
        var snrsByChannel = new Dictionary<SimChannelKind, double[]>();
        foreach (SimChannelKind c in channels)
        {
            string key = $"snr-{c.ToString().ToLowerInvariant()}";
            snrsByChannel[c] = a.Has(key) ? Doubles(a.Req(key)) : defaultSnrs;
        }

        int[] payloads = Ints(a.Str("payload-bytes", "960,255"));
        int bursts = a.Int("bursts", 40);
        int firstSeed = a.Int("seed", 1);
        int workers = a.Int("workers", 8);
        string? csvPath = a.Str("csv", null);
        bool earlyStop = !a.Has("no-early-stop");
        int abandonArg = a.Int("abandon-after", 10);
        int abandonAfter = earlyStop ? abandonArg : 0;
        bool describe = a.Has("describe");
        a.RejectUnknown("filter-study");

        foreach (string rx in rxModes)
        {
            if (rx is not ("standard" or "matched"))
            {
                throw new ArgumentException($"--rx: '{rx}' is not standard or matched");
            }
        }

        if (describe)
        {
            Describe(filters, txRollOffs);
            return 0;
        }

        var configs = new List<StudyConfig>();
        foreach (int payload in payloads)
        {
            foreach (int wn in modes)
            {
                foreach (SimChannelKind channel in channels)
                {
                    foreach (ReceiveFilter filter in filters)
                    {
                        foreach (double tx in txRollOffs)
                        {
                            var rxRollOffs = rxModes
                                .Select(r => r == "standard" ? Ms110dModulator.RollOff : tx)
                                .Distinct()
                                .ToList();
                            foreach (double rx in rxRollOffs)
                            {
                                configs.Add(new StudyConfig(wn, tx, rx, filter, channel, payload));
                            }
                        }
                    }
                }
            }
        }

        HashSet<string> done = csvPath is not null ? ReadDone(csvPath) : [];
        StreamWriter? csv = null;
        if (csvPath is not null)
        {
            bool fresh = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            csv = new StreamWriter(csvPath, append: true) { AutoFlush = true };
            if (fresh)
            {
                csv.WriteLine("mode,txRollOff,rxRollOff,filter,filterKind,filterLoHz,filterHiHz,filterWidthHz,"
                    + "channel,payloadBytes,snrDb,ran,trials,lost,lossRate,lossCiLo,lossCiHi,meanCorrectedBytes,burstSeconds");
            }
        }

        Console.Error.WriteLine($"filter-study: {configs.Count} configurations, {bursts} frames per point, "
            + $"workers {workers}, seeds {firstSeed}..{firstSeed + bursts - 1}");
        object gate = new();
        int finished = 0;
        var results = new ConcurrentBag<StudyPoint>();
        Parallel.ForEach(
            configs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) },
            config =>
            {
                int cleanRun = 0;
                foreach (double snr in snrsByChannel[config.Channel])
                {
                    string key = config.Key(snr);
                    if (done.Contains(key))
                    {
                        continue;
                    }

                    StudyPoint point = cleanRun >= 2 && earlyStop
                        ? new StudyPoint(config, snr, Ran: false, bursts, 0, 0, 0)
                        : RunPoint(config, snr, bursts, firstSeed, abandonAfter);
                    cleanRun = point.Lost == 0 ? cleanRun + 1 : 0;
                    results.Add(point);
                    lock (gate)
                    {
                        csv?.WriteLine(point.CsvRow());
                        if (point.Ran)
                        {
                            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"[{DateTime.UtcNow:HH:mm:ss}] wn{config.Wn} {config.PayloadBytes}B {config.Channel,-8} "
                                + $"{config.Filter.Name,-16} tx {config.TxRollOff:0.00} rx {config.RxRollOff:0.00} "
                                + $"{snr,5:0.0} dB  lost {point.Lost,3}/{point.Trials}"));
                        }
                    }
                }

                int n = Interlocked.Increment(ref finished);
                Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] configuration {n}/{configs.Count} done");
            });

        csv?.Dispose();
        return 0;
    }

    private static StudyPoint RunPoint(StudyConfig config, double snr, int bursts, int firstSeed, int abandonAfter)
    {
        int lost = 0;
        long corrected = 0;
        double seconds = 0;
        var tx = new Ms110dTxSettings { WaveformNumber = config.Wn, RollOff = config.TxRollOff };
        var rx = new Ms110dDemodOptions { RollOff = config.RxRollOff };
        for (int i = 0; i < bursts; i++)
        {
            int seed = firstSeed + i;
            byte[] frame = SimModem.Frame(config.PayloadBytes + HeaderBytes, seed);
            var sender = new Ms110dModem(Rate, static _ => { }, tx, rx);
            float[] active = Trim(sender.Modulate(frame, 0));
            seconds = active.Length / (double)Rate;
            float[] air = SimChannel.Apply(active, Rate, config.Channel, snr, seed + 3_000_000);
            float[] audio = config.Filter.Apply(air);

            bool matched = false;
            int fixedBytes = 0;
            var receiver = new Ms110dModem(Rate, _ => { }, tx, rx);
            receiver.FrameDecoded += (got, quality) =>
            {
                if (got.AsSpan().SequenceEqual(frame))
                {
                    matched = true;
                    fixedBytes = quality.CorrectedBytes ?? 0;
                }
            };

            const int block = Rate / 10;
            for (int pos = 0; pos < audio.Length; pos += block)
            {
                receiver.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            }

            lost += matched ? 0 : 1;
            corrected += fixedBytes;
            if (abandonAfter > 0 && i + 1 == abandonAfter && lost == abandonAfter && bursts > abandonAfter)
            {
                // Every frame so far lost: the point is far past the knee, and the rest of its
                // frames would only narrow an interval nobody reads. Recorded with its own count.
                return new StudyPoint(config, snr, Ran: true, abandonAfter, lost, 0, seconds);
            }
        }

        return new StudyPoint(config, snr, Ran: true, bursts, lost, bursts - lost == 0 ? 0 : (double)corrected / (bursts - lost), seconds);
    }

    private static void Describe(ReceiveFilter[] filters, double[] txRollOffs)
    {
        Console.WriteLine("Receive filters (as realised at 9600 Hz):");
        foreach (ReceiveFilter f in filters)
        {
            Console.WriteLine("  " + f.Describe());
        }

        Console.WriteLine();
        Console.WriteLine("Transmit spectra (WN 4 data section, 1800 Hz centre):");
        foreach (double r in txRollOffs)
        {
            var tx = new Ms110dModulator(new Ms110dTxSettings
            {
                WaveformNumber = 4, RollOff = r, PreambleSuperframes = 2,
            });
            var random = new Random(313);
            var payload = new byte[4000];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2);
            }

            float[] audio = tx.Modulate(payload);
            int skip = (2 * 576 * 4) + 512;
            float[] steady = audio.AsSpan(skip, audio.Length - skip - 1024).ToArray();
            (double lo, double hi, double width, _) = M0LTE.Dsp.OccupiedBandwidth.Measure(steady, Rate);
            double edge = 2400 * (1 + r);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  roll-off {r:0.00}: edge to edge {edge:0} Hz ({1800 - (edge / 2):0}-{1800 + (edge / 2):0}), "
                + $"99% power {width:0} Hz ({lo:0}-{hi:0}), pulse span {Ms110dModulator.PulseSpanSymbols(r)} symbols"));
            foreach (ReceiveFilter f in filters.Where(f => f.Kind != ReceiveFilterKind.None))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      through {f.Name,-16} keeps {PowerKeptDb(steady, f):0.00} dB of the signal"));
            }
        }
    }

    private static double PowerKeptDb(float[] signal, ReceiveFilter filter)
    {
        float[] filtered = filter.Apply(signal);
        double before = 0, after = 0;
        for (int i = 2000; i < signal.Length - 2000; i++)
        {
            before += signal[i] * (double)signal[i];
            after += filtered[i] * (double)filtered[i];
        }

        return 10 * Math.Log10(after / before);
    }

    private static float[] Trim(float[] audio)
    {
        int start = 0;
        while (start < audio.Length && audio[start] == 0f)
        {
            start++;
        }

        int end = audio.Length;
        while (end > start && audio[end - 1] == 0f)
        {
            end--;
        }

        return audio[start..end];
    }

    private static HashSet<string> ReadDone(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return keys;
        }

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] c = line.Split(',');
            if (c.Length > 10)
            {
                // mode,tx,rx,filter,...,channel(8),payload(9),snr(10)
                keys.Add(string.Join('|', c[0], c[1], c[2], c[3], c[8], c[9], c[10]));
            }
        }

        return keys;
    }

    private static int[] Ints(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private static double[] Doubles(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private sealed record StudyConfig(
        int Wn, double TxRollOff, double RxRollOff, ReceiveFilter Filter, SimChannelKind Channel, int PayloadBytes)
    {
        public string ModeName => $"ms110d-wn{Wn}";

        public string Key(double snr) => string.Join('|',
            ModeName, F(TxRollOff), F(RxRollOff), Filter.Name, Channel.ToString().ToLowerInvariant(),
            PayloadBytes.ToString(CultureInfo.InvariantCulture), F(snr));
    }

    private sealed record StudyPoint(
        StudyConfig Config, double SnrDb, bool Ran, int Trials, int Lost, double MeanCorrectedBytes, double BurstSeconds)
    {
        public string CsvRow()
        {
            StudyConfig c = Config;
            double loss = Ran ? (double)Lost / Trials : 0;
            (double lo, double hi) = Ran ? SimStats.Wilson(Lost, Trials) : (double.NaN, double.NaN);
            return string.Join(',',
                c.ModeName, F(c.TxRollOff), F(c.RxRollOff), c.Filter.Name, c.Filter.Kind.ToString().ToLowerInvariant(),
                F(c.Filter.LowHz), F(c.Filter.HighHz), F(c.Filter.WidthHz),
                c.Channel.ToString().ToLowerInvariant(), c.PayloadBytes.ToString(CultureInfo.InvariantCulture), F(SnrDb),
                Ran ? "1" : "0", Ran ? Trials.ToString(CultureInfo.InvariantCulture) : "0",
                Ran ? Lost.ToString(CultureInfo.InvariantCulture) : "0",
                Ran ? F(loss) : "", F(lo), F(hi), Ran ? F(MeanCorrectedBytes) : "", Ran ? F(BurstSeconds) : "");
        }
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
}
