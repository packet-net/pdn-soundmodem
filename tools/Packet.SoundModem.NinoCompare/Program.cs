using System.Buffers;
using System.Globalization;
using M0LTE.Dsp;
using MQTTnet;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.NinoCompare;

// nino-compare: a harness to compare our BPSK decode of a NinoTNC's received audio against the
// frames the NinoTNC itself decodes (published to MQTT), and to characterise per-station carrier
// offset so the frequency-diversity bank's step/span can be tuned to match or beat it.
//
//   nino-compare mqtt-capture --broker HOST[:PORT] --topic T [--out nino.jsonl] [--kiss] [--user U --pass P]
//   nino-compare decode --wav FILE [--centre 1500] [--baud 300] [--pairs 4] [--step HZ]
//                       [--detector coherent|differential] [--out ours.jsonl]
//   nino-compare compare --ours ours.jsonl --nino nino.jsonl
//
// The audio + live MQTT feed arrive later; decode/compare work offline on files now.

return await Dispatch(args);

static async Task<int> Dispatch(string[] args)
{
    if (args.Length == 0)
    {
        return Usage();
    }

    try
    {
        return args[0] switch
        {
            "mqtt-capture" => await MqttCapture(Options(args)),
            "decode" => Decode(Options(args)),
            "compare" => Compare(Options(args)),
            "analyse" => Analyse(Options(args)),
            "shift" => Shift(Options(args)),
            "station-offsets" => StationOffsets(Options(args)),
            _ => Usage(),
        };
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}

static int Usage()
{
    Console.Error.WriteLine(
        """
        nino-compare — compare our BPSK decode against a NinoTNC's MQTT frame feed.

          mqtt-capture --broker HOST[:PORT] --topic T [--out FILE] [--kiss] [--user U --pass P]
              Subscribe to the NinoTNC's frame topic; log each AX.25 frame as JSONL.
          decode --wav FILE [--centre 1500] [--baud 300] [--pairs 4] [--step HZ]
                 [--detector coherent|differential] [--out FILE]
              Decode a recording with the frequency-diversity bank; log frames + offsets.
          compare --ours FILE --nino FILE
              Diff our frames against the NinoTNC's: matched / we-missed / we-extra.
          analyse --chunks DIR --nino FILE [--out-dir DIR] [--centre] [--baud] [--pairs] [--step]
                  [--detector] [--preroll 6] [--window 9]
              Decode every timestamped audio chunk, diff against the NinoTNC feed, and extract a
              WAV snippet around each frame the NinoTNC decoded but we missed (for deep-dive).
          shift --wav FILE --out FILE --from HZ --to HZ [--outrate 12000]
              SSB-translate a slot's audio centre (M0LTE.Dsp.FrequencyShifter) and resample —
              e.g. --from 850 --to 1500 lifts slot-2 ARDOP to ardopcf's fixed 1500 Hz centre,
              since we cannot retune the receiver (slot-3's NinoTNC has a fixed tone centre).
          station-offsets --chunks DIR [--centre 1500] [--baud 300] [--pairs 4] [--step]
                          [--out CSV]
              For every unique station heard across the timestamped chunks, measure the fine
              carrier offset of each transmission and aggregate per callsign (mean / spread /
              drift over time). Writes a (station,unixtime,offsetHz,confidence) CSV for plotting.
        """);
    return 1;
}

// --- mqtt-capture -----------------------------------------------------------------------------

static async Task<int> MqttCapture(Dictionary<string, string> opts)
{
    string brokerArg = Required(opts, "broker");
    string topic = Required(opts, "topic");
    bool stripKiss = opts.ContainsKey("kiss");
    string[] hostPort = brokerArg.Split(':', 2);
    string host = hostPort[0];
    int port = hostPort.Length > 1 ? int.Parse(hostPort[1], CultureInfo.InvariantCulture) : 1883;

    using TextWriter? file = opts.TryGetValue("out", out string? outPath)
        ? new StreamWriter(outPath, append: true) { AutoFlush = true }
        : null;

    var factory = new MqttClientFactory();
    using var client = factory.CreateMqttClient();
    var builder = new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId("nino-compare");
    if (opts.TryGetValue("user", out string? user))
    {
        builder = builder.WithCredentials(user, opts.GetValueOrDefault("pass", string.Empty));
    }

    int count = 0;
    client.ApplicationMessageReceivedAsync += e =>
    {
        byte[] payload = e.ApplicationMessage.Payload.ToArray();
        byte[] frame = stripKiss ? StripKiss(payload) : payload;
        var record = FrameRecord.FromBytes(frame, timeSeconds: UnixNow());
        Console.WriteLine($"[nino] {record.Summary}  ({frame.Length}B)");
        file?.WriteLine(record.ToJsonLine());
        count++;
        return Task.CompletedTask;
    };

    Console.Error.WriteLine($"connecting to mqtt://{host}:{port}, topic '{topic}'…");
    await client.ConnectAsync(builder.Build(), CancellationToken.None);
    var subscribe = factory.CreateSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic)).Build();
    await client.SubscribeAsync(subscribe, CancellationToken.None);
    Console.Error.WriteLine("subscribed — Ctrl-C to stop.");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; stop.TrySetResult(); };
    await stop.Task;
    await client.DisconnectAsync();
    Console.Error.WriteLine($"captured {count} frame(s).");
    return 0;
}

// A NinoTNC KISS frame: 0xC0, port/command byte, data (0xDB/0xDC/0xDD unescaping), 0xC0.
static byte[] StripKiss(byte[] message)
{
    int start = 0;
    int end = message.Length;
    if (end > 0 && message[0] == 0xC0)
    {
        start = 2; // FEND + command byte
        if (end > start && message[end - 1] == 0xC0)
        {
            end--;
        }
    }

    var output = new List<byte>(end - start);
    for (int i = start; i < end; i++)
    {
        if (message[i] == 0xDB && i + 1 < end)
        {
            output.Add(message[++i] == 0xDC ? (byte)0xC0 : (byte)0xDB);
        }
        else
        {
            output.Add(message[i]);
        }
    }

    return [.. output];
}

// --- decode -----------------------------------------------------------------------------------

static int Decode(Dictionary<string, string> opts)
{
    const int dspRate = 12000;
    string wav = Required(opts, "wav");
    double centre = Number(opts, "centre", 1500);
    int baud = (int)Number(opts, "baud", 300);
    int pairs = (int)Number(opts, "pairs", 4);
    double? step = opts.ContainsKey("step") ? Number(opts, "step", 0) : null;
    var detector = opts.GetValueOrDefault("detector", "coherent") == "differential"
        ? PskDetector.Differential
        : PskDetector.Coherent;

    float[] samples = LoadDecimated(wav, dspRate);

    double elapsed = 0;
    var records = new List<FrameRecord>();
    var modem = new BpskMultiModem(dspRate, static _ => { }, crc: true, centre, baud, pairs, step, detector);
    modem.FrameDecoded += (frame, quality) =>
        records.Add(FrameRecord.FromBytes(frame, timeSeconds: elapsed, offsetHz: quality.FrequencyOffsetHz));

    int chunk = dspRate / 10;
    for (int pos = 0; pos < samples.Length; pos += chunk)
    {
        int length = Math.Min(chunk, samples.Length - pos);
        modem.Process(samples.AsSpan(pos, length));
        elapsed += length / (double)dspRate;
    }

    var estimator = new BpskCarrierOffsetEstimator(dspRate, centre, baud);
    estimator.Process(samples);

    using TextWriter? file = opts.TryGetValue("out", out string? outPath) ? new StreamWriter(outPath) : null;
    foreach (FrameRecord record in records)
    {
        Console.WriteLine($"[ours {record.TimeSeconds,7:F2}s {Offset(record.OffsetHz)}] {record.Summary}");
        file?.WriteLine(record.ToJsonLine());
    }

    Console.Error.WriteLine(
        $"decoded {records.Count} frame(s) from {wav} " +
        $"(centre={centre} baud={baud} pairs={pairs} step={step?.ToString("0.#", CultureInfo.InvariantCulture) ?? "auto"} {detector}).");
    Console.Error.WriteLine(
        estimator.HasEstimate
            ? $"fine carrier-offset estimate: {estimator.OffsetHz:+0.0;-0.0} Hz (confidence {estimator.Confidence:F2})"
            : "fine carrier-offset estimate: none (no confident signal)");
    PrintOffsetHistogram(records);
    return 0;
}

static void PrintOffsetHistogram(List<FrameRecord> records)
{
    var offsets = records.Where(r => r.OffsetHz is not null).Select(r => r.OffsetHz!.Value).ToList();
    if (offsets.Count == 0)
    {
        return;
    }

    offsets.Sort();
    Console.Error.WriteLine(
        $"winning-branch offsets: min {offsets[0]:+0;-0} / median {offsets[offsets.Count / 2]:+0;-0} / " +
        $"max {offsets[^1]:+0;-0} Hz  (spread informs the default step)");
    foreach (IGrouping<double, double> group in offsets.GroupBy(o => o).OrderBy(g => g.Key))
    {
        Console.Error.WriteLine($"  {group.Key,+5:+0;-0} Hz : {new string('#', group.Count())} ({group.Count()})");
    }
}

// --- analyse (batch over timestamped chunks) --------------------------------------------------

static int Analyse(Dictionary<string, string> opts)
{
    const int dspRate = 12000;
    const int captureRate = 48000;
    string chunkDir = Required(opts, "chunks");
    List<FrameRecord> nino = FrameRecord.ReadFile(Required(opts, "nino"));
    string outDir = opts.GetValueOrDefault("out-dir", Path.Combine(chunkDir, "..", "deepdive"));
    Directory.CreateDirectory(outDir);
    double centre = Number(opts, "centre", 1500);
    int baud = (int)Number(opts, "baud", 300);
    int pairs = (int)Number(opts, "pairs", 4);
    double? step = opts.ContainsKey("step") ? Number(opts, "step", 0) : null;
    var detector = opts.GetValueOrDefault("detector", "coherent") == "differential"
        ? PskDetector.Differential
        : PskDetector.Coherent;
    double preroll = Number(opts, "preroll", 6);
    double window = Number(opts, "window", 9);

    var chunks = Directory.EnumerateFiles(chunkDir, "*.wav")
        .Select(path => (Path: path, Start: ChunkStartUnix(path)))
        .Where(c => c.Start is not null)
        .Select(c => (c.Path, Start: c.Start!.Value))
        .OrderBy(c => c.Start)
        .ToList();
    if (chunks.Count == 0)
    {
        Console.Error.WriteLine($"no timestamped chunks (…YYYYMMDDTHHMMSSZ.wav) in {chunkDir}");
        return 2;
    }

    // Pass 1: decode every chunk into our frame set, stamped with wall-clock time.
    var ours = new List<FrameRecord>();
    double windowStart = chunks[0].Start;
    double windowEnd = chunks[0].Start;
    foreach ((string path, double start) in chunks)
    {
        double elapsed = 0;
        var modem = new BpskMultiModem(dspRate, static _ => { }, crc: true, centre, baud, pairs, step, detector);
        modem.FrameDecoded += (frame, quality) =>
            ours.Add(FrameRecord.FromBytes(frame, start + elapsed, quality.FrequencyOffsetHz));
        float[] samples = LoadDecimated(path, dspRate);
        int block = dspRate / 10;
        for (int pos = 0; pos < samples.Length; pos += block)
        {
            int length = Math.Min(block, samples.Length - pos);
            modem.Process(samples.AsSpan(pos, length));
            elapsed += length / (double)dspRate;
        }

        // LoadDecimated appends a dspRate/2 flush tail; the real audio ends before it.
        windowEnd = Math.Max(windowEnd, start + (samples.Length - dspRate / 2) / (double)dspRate);
    }

    // Only compare against NinoTNC frames that fall within the analysed audio's time span — a
    // live nino.jsonl keeps growing, and frames received after the last chunk's audio ends (or
    // before the first begins) have no audio here and would be counted as false misses. A small
    // guard absorbs MQTT-vs-audio timestamp skew.
    const double guard = 15;
    int ninoTotal = nino.Count;
    nino = nino.Where(n => n.TimeSeconds is null
        || (n.TimeSeconds >= windowStart - guard && n.TimeSeconds <= windowEnd + guard)).ToList();
    int outOfWindow = ninoTotal - nino.Count;

    // Diff on content; a NinoTNC frame with no counterpart of ours is a miss.
    var oursByHex = ours.GroupBy(r => r.Hex).ToDictionary(g => g.Key, g => new Queue<FrameRecord>(g));
    var missed = new List<FrameRecord>();
    int matched = 0;
    foreach (FrameRecord n in nino)
    {
        if (oursByHex.TryGetValue(n.Hex, out Queue<FrameRecord>? q) && q.Count > 0)
        {
            q.Dequeue();
            matched++;
        }
        else
        {
            missed.Add(n);
        }
    }

    Console.WriteLine($"window {Iso(windowStart)} .. {Iso(windowEnd)} " +
        $"({(windowEnd - windowStart) / 60:F0} min){(outOfWindow > 0 ? $"; {outOfWindow} nino frame(s) outside it excluded" : string.Empty)}");
    Console.WriteLine($"chunks {chunks.Count} | NinoTNC {nino.Count} | ours {ours.Count} | " +
        $"matched {matched} | MISSED {missed.Count} | extra {oursByHex.Values.Sum(q => q.Count)}");
    Console.WriteLine($"copy of NinoTNC: {(nino.Count == 0 ? 0 : 100.0 * matched / nino.Count):F1}%");

    // Pass 2: extract audio around each miss (misses time-ordered so each chunk is read once).
    int extracted = 0;
    string? loadedPath = null;
    float[]? loadedSamples = null;
    foreach (FrameRecord miss in missed.Where(m => m.TimeSeconds is not null).OrderBy(m => m.TimeSeconds))
    {
        double t = miss.TimeSeconds!.Value;
        (string Path, double Start)? chunk = chunks
            .Where(c => t >= c.Start - preroll)
            .OrderByDescending(c => c.Start)
            .Cast<(string Path, double Start)?>()
            .FirstOrDefault();
        if (chunk is null)
        {
            Console.WriteLine($"  MISS {Iso(t)} {miss.Summary} — no chunk covers this time");
            continue;
        }

        if (loadedPath != chunk.Value.Path)
        {
            var (samples, rate) = WavFile.ReadMono(chunk.Value.Path);
            loadedSamples = samples;
            loadedPath = chunk.Value.Path;
            if (rate != captureRate)
            {
                Console.Error.WriteLine($"  (note: {chunk.Value.Path} is {rate} Hz, not {captureRate})");
            }
        }

        double offset = t - chunk.Value.Start;
        int from = Math.Max(0, (int)((offset - preroll) * captureRate));
        int to = Math.Min(loadedSamples!.Length, (int)((offset - preroll + window) * captureRate));
        if (to <= from)
        {
            Console.WriteLine($"  MISS {Iso(t)} {miss.Summary} — outside chunk audio range");
            continue;
        }

        string stamp = Iso(t).Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        string name = $"miss-{stamp}-{Sanitise(miss.From)}-to-{Sanitise(miss.To)}.wav";
        string outPath = Path.Combine(outDir, name);
        WavFile.WriteMono(outPath, loadedSamples.AsSpan(from, to - from), captureRate);
        Console.WriteLine($"  MISS {Iso(t)} {miss.Summary}  [{miss.Hex}]\n       audio → {outPath}");
        extracted++;
    }

    if (missed.Count > 0)
    {
        Console.WriteLine($"\nextracted {extracted} miss snippet(s) to {outDir} — deep-dive with " +
            "`decode --wav <snippet> --detector …` and sweep centre/step until it copies.");
    }

    return 0;
}

// Parse the UTC start time encoded in a chunk filename (…YYYYMMDDTHHMMSSZ.wav) to unix seconds.
static double? ChunkStartUnix(string path)
{
    var match = System.Text.RegularExpressions.Regex.Match(
        Path.GetFileName(path), @"(\d{8})T(\d{6})Z");
    if (!match.Success)
    {
        return null;
    }

    var utc = DateTime.ParseExact(
        match.Groups[1].Value + match.Groups[2].Value, "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal);
    return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000.0;
}

static string Iso(double unixSeconds) =>
    DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000))
        .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

static string Sanitise(string? call) =>
    string.IsNullOrEmpty(call) ? "unknown" : new string(call.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

// --- compare ----------------------------------------------------------------------------------

static int Compare(Dictionary<string, string> opts)
{
    List<FrameRecord> ours = FrameRecord.ReadFile(Required(opts, "ours"));
    List<FrameRecord> nino = FrameRecord.ReadFile(Required(opts, "nino"));

    // Match on frame content (hex), consuming each match once so duplicate transmissions pair up
    // one-for-one rather than all collapsing to a single match.
    var oursByHex = ours.GroupBy(r => r.Hex).ToDictionary(g => g.Key, g => new Queue<FrameRecord>(g));
    var matched = new List<(FrameRecord Nino, FrameRecord Ours)>();
    var missed = new List<FrameRecord>();
    foreach (FrameRecord n in nino)
    {
        if (oursByHex.TryGetValue(n.Hex, out Queue<FrameRecord>? queue) && queue.Count > 0)
        {
            matched.Add((n, queue.Dequeue()));
        }
        else
        {
            missed.Add(n);
        }
    }

    var extra = oursByHex.Values.SelectMany(q => q).ToList();

    Console.WriteLine($"NinoTNC frames : {nino.Count}");
    Console.WriteLine($"our frames     : {ours.Count}");
    Console.WriteLine($"matched        : {matched.Count}");
    Console.WriteLine($"we MISSED      : {missed.Count}  (NinoTNC decoded, we did not)");
    Console.WriteLine($"we had EXTRA   : {extra.Count}  (we decoded, NinoTNC did not)");
    double rate = nino.Count == 0 ? 0 : 100.0 * matched.Count / nino.Count;
    Console.WriteLine($"copy of NinoTNC: {rate:F1}%");

    if (missed.Count > 0)
    {
        Console.WriteLine("\n--- MISSED (capture the audio around these times and deep-dive) ---");
        foreach (FrameRecord m in missed)
        {
            Console.WriteLine($"  {Time(m.TimeSeconds)}  {m.Summary}  [{m.Hex}]");
        }
    }

    if (extra.Count > 0)
    {
        Console.WriteLine("\n--- EXTRA (we decoded, NinoTNC did not — verify these are real) ---");
        foreach (FrameRecord x in extra)
        {
            Console.WriteLine($"  {Time(x.TimeSeconds)}  {x.Summary}  [{x.Hex}]");
        }
    }

    return 0;
}

// --- station-offsets --------------------------------------------------------------------------

static int StationOffsets(Dictionary<string, string> opts)
{
    const int dspRate = 12000;
    string chunkDir = Required(opts, "chunks");
    double centre = Number(opts, "centre", 1500);
    int baud = (int)Number(opts, "baud", 300);
    int pairs = (int)Number(opts, "pairs", 4);
    double? step = opts.ContainsKey("step") ? Number(opts, "step", 0) : null;

    var chunks = Directory.EnumerateFiles(chunkDir, "*.wav")
        .Select(path => (Path: path, Start: ChunkStartUnix(path)))
        .Where(c => c.Start is not null)
        .Select(c => (c.Path, Start: c.Start!.Value))
        .OrderBy(c => c.Start)
        .ToList();
    if (chunks.Count == 0)
    {
        Console.Error.WriteLine($"no timestamped chunks in {chunkDir}");
        return 2;
    }

    // (unixTime, offsetHz, confidence) per station callsign.
    var perStation = new Dictionary<string, List<(double Time, double OffsetHz, double Confidence)>>(StringComparer.Ordinal);

    foreach ((string path, double start) in chunks)
    {
        // The live capture service is writing the newest chunk as we read; skip any chunk whose
        // WAV header/data isn't complete yet (or is otherwise unreadable) rather than aborting.
        float[] samples;
        try
        {
            samples = LoadDecimated(path, dspRate);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            Console.Error.WriteLine($"skip {Path.GetFileName(path)}: {ex.Message}");
            continue;
        }

        // Pass 1: decode, capturing each frame's decode position, sender, and length.
        var events = new List<(double Elapsed, string From, int Bytes)>();
        double elapsed = 0;
        var modem = new BpskMultiModem(dspRate, static _ => { }, crc: true, centre, baud, pairs, step);
        modem.FrameDecoded += (frame, _) =>
            events.Add((elapsed, Ax25Text.Describe(frame).From ?? "?", frame.Length));
        int block = dspRate / 10;
        for (int pos = 0; pos < samples.Length; pos += block)
        {
            int length = Math.Min(block, samples.Length - pos);
            modem.Process(samples.AsSpan(pos, length));
            elapsed += length / (double)dspRate;
        }

        // Pass 2: for each frame, measure the fine carrier offset over the audio window it
        // occupies (peak coherence locks to that transmission's carrier). The estimator's
        // ±baud/4 range covers any realistic offset, so it reads the absolute offset directly.
        foreach ((double frameElapsed, string from, int bytes) in events)
        {
            double frameSeconds = (bytes * 8 + 48) / (double)baud;        // payload + ~preamble
            int end = Math.Min(samples.Length, (int)((frameElapsed + 0.5) * dspRate));
            int begin = Math.Max(0, end - (int)((frameSeconds + 2.0) * dspRate));
            if (end - begin < dspRate / 2)
            {
                continue;
            }

            var estimator = new BpskCarrierOffsetEstimator(dspRate, centre, baud);
            estimator.Process(samples.AsSpan(begin, end - begin));
            if (!estimator.HasEstimate)
            {
                continue;
            }

            if (!perStation.TryGetValue(from, out var list))
            {
                perStation[from] = list = [];
            }

            list.Add((start + frameElapsed, estimator.OffsetHz, estimator.Confidence));
        }
    }

    if (perStation.Count == 0)
    {
        Console.Error.WriteLine("no stations decoded across the chunks");
        return 0;
    }

    // Report, most-heard first.
    Console.WriteLine($"{"station",-12} {"n",4}  {"mean",7} {"min",6} {"max",6} {"spread",6}  drift");
    foreach ((string station, var samplesList) in perStation.OrderByDescending(kv => kv.Value.Count))
    {
        var offs = samplesList.Select(s => s.OffsetHz).ToList();
        double mean = offs.Average();
        double min = offs.Min();
        double max = offs.Max();
        double sd = Math.Sqrt(offs.Select(o => (o - mean) * (o - mean)).Average());
        // "Drift" if the spread across time exceeds a few Hz beyond per-frame measurement jitter.
        string drift = max - min > 6 ? $"yes (±{(max - min) / 2:F0} Hz over {Span(samplesList)})" : "steady";
        Console.WriteLine($"{station,-12} {samplesList.Count,4}  {mean,6:+0.0;-0.0} {min,6:+0.0;-0.0} "
            + $"{max,6:+0.0;-0.0} {sd,6:F1}  {drift}");
    }

    if (opts.TryGetValue("out", out string? csv))
    {
        using var w = new StreamWriter(csv);
        w.WriteLine("station,unixtime,iso,offsetHz,confidence");
        foreach ((string station, var list) in perStation)
        {
            foreach ((double time, double off, double conf) in list.OrderBy(s => s.Time))
            {
                w.WriteLine($"{station},{time:F1},{Iso(time)},{off:F1},{conf:F2}");
            }
        }

        Console.Error.WriteLine($"wrote per-transmission CSV → {csv}");
    }

    return 0;
}

// Human span of a station's observation window.
static string Span(List<(double Time, double OffsetHz, double Confidence)> s)
{
    double minutes = (s.Max(x => x.Time) - s.Min(x => x.Time)) / 60;
    return minutes < 90 ? $"{minutes:F0} min" : $"{minutes / 60:F1} h";
}

// --- shift ------------------------------------------------------------------------------------

static int Shift(Dictionary<string, string> opts)
{
    string wav = Required(opts, "wav");
    string outPath = Required(opts, "out");
    double from = Number(opts, "from", 0);
    double to = Number(opts, "to", 0);
    int outRate = (int)Number(opts, "outrate", 12000);

    var (samples, rate) = WavFile.ReadMono(wav);

    // SSB-translate at the capture rate (most headroom, Hilbert nulls at DC/Nyquist are far off).
    var shifter = new FrequencyShifter(rate, to - from);
    var shifted = new float[samples.Length];
    shifter.Process(samples, shifted);

    // Resample to the reference decoder's rate (ardopcf/QtSM run at 12 kHz).
    float[] output = shifted;
    if (rate != outRate)
    {
        if (rate % outRate != 0)
        {
            throw new ArgumentException($"WAV rate {rate} is not a multiple of --outrate {outRate}");
        }

        var decimator = new Decimator(rate, rate / outRate);
        output = new float[decimator.MaxOutput(shifted.Length)];
        int produced = decimator.Process(shifted, output);
        Array.Resize(ref output, produced);
    }

    WavFile.WriteMono(outPath, output, outRate);
    Console.Error.WriteLine(
        $"shifted {wav} by {to - from:+0;-0} Hz ({from}->{to}), {rate}->{outRate} Hz -> {outPath} "
        + $"({output.Length / (double)outRate:F1}s)");
    return 0;
}

// --- helpers ----------------------------------------------------------------------------------

static float[] LoadDecimated(string wav, int dspRate)
{
    var (samples, rate) = WavFile.ReadMono(wav);
    if (rate != dspRate)
    {
        if (rate % dspRate != 0)
        {
            throw new ArgumentException($"WAV rate {rate} is not a multiple of the {dspRate} Hz DSP rate");
        }

        var decimator = new Decimator(rate, rate / dspRate);
        var output = new float[decimator.MaxOutput(samples.Length)];
        int produced = decimator.Process(samples, output);
        Array.Resize(ref output, produced);
        samples = output;
    }

    Array.Resize(ref samples, samples.Length + dspRate / 2); // flush tail
    return samples;
}

static Dictionary<string, string> Options(string[] args)
{
    var opts = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unexpected argument '{args[i]}'");
        }

        string key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            opts[key] = args[++i];
        }
        else
        {
            opts[key] = "true"; // a flag
        }
    }

    return opts;
}

static string Required(Dictionary<string, string> opts, string key) =>
    opts.TryGetValue(key, out string? value)
        ? value
        : throw new ArgumentException($"missing required --{key}");

static double Number(Dictionary<string, string> opts, string key, double fallback) =>
    opts.TryGetValue(key, out string? value)
        ? double.Parse(value, CultureInfo.InvariantCulture)
        : fallback;

static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

static string Offset(double? hz) => hz is null ? "  ?  " : $"{hz.Value,+4:+0;-0}Hz";

static string Time(double? seconds) => seconds is null
    ? "        "
    : $"{seconds.Value,8:F2}s";
