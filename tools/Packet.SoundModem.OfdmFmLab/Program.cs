// sm-ofdmlab: a bench instrument for the OFDM-FM modem. Not a deployment surface.
//
//   sm-ofdmlab gen --mode ofdm-fm-8k --bytes 1024 --seeds 1-20 --out DIR [--leadin 2]
//       Renders one burst per seed exactly as the modem would transmit it, as a stereo 16-bit
//       48 kHz WAV a sound card can play directly, with a JSON file beside it naming the payload.
//
//   sm-ofdmlab decode --mode ofdm-fm-8k CAPTURE.wav [--block 960]
//       Runs the shipping streaming receiver over a recording and prints one JSON line per burst
//       it heard (decoded or not) and a summary line.
//
//   sm-ofdmlab sim --mode ofdm-fm-8k --bytes 1024 --cnr 30,24,20,18,16,14 --seeds 32 [--dev 4500] [--ifbw 15000]
//       The same bursts through the simulated FM link at each carrier-to-noise ratio, read by the
//       receiver as shipped, by the clock-tracked receiver, and by the clock-tracked receiver
//       with the pilot-fitted reads still tried after it: the weak-signal regime, clicks and all.
//
//   sm-ofdmlab at --mode ofdm-fm-8k CAPTURE.wav
//       Whole-buffer read: the sync search and the decode at the position it finds, with the
//       stage each burst got to.
using System.Security.Cryptography;
using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems.OfdmFm;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: sm-ofdmlab gen|decode|at ...");
    return 2;
}

string command = args[0];
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        options[args[i][2..]] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

string mode = options.GetValueOrDefault("mode", "ofdm-fm-8k");
OfdmFmBurstCodec.DecodeFeedbackPasses = int.Parse(options.GetValueOrDefault("feedback-passes", "1"));
OfdmFmBurstCodec.OlderReadsAfterTracked = options.GetValueOrDefault("older-reads", "off") == "on";
OfdmFmModem.TrackedReadAttempts = int.Parse(options.GetValueOrDefault("attempts", "2"));

// Receiver switches, for telling apart what each stage is worth: --off tracking,ddchannel,noiseweight
foreach (string off in options.GetValueOrDefault("off", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
{
    switch (off)
    {
        case "tracking": OfdmFmBurstCodec.ClockTracking = false; break;
        case "ddchannel": OfdmFmBurstCodec.DecisionDirectedChannel = false; break;
        case "noiseweight": OfdmFmBurstCodec.MeasuredNoiseWeighting = false; break;
        case "tilt": OfdmFmBurstCodec.ClockTiltCorrection = false; break;
        case "fine": OfdmFmBurstCodec.SubSymbolTracking = false; break;
        case "spur": OfdmFmBurstCodec.MeasuredSpurWeighting = false; break;
        case "feedback": OfdmFmBurstCodec.DecodeFeedback = false; break;
        case "satguard": OfdmFmBurstCodec.SaturationGuard = false; break;
        case "plainretry": OfdmFmBurstCodec.PlainWeightRetry = false; break;
        default: Console.Error.WriteLine($"unknown switch {off}"); return 2;
    }
}
if (!OfdmFmPresets.ByMode.TryGetValue(mode, out OfdmFmParameters? parameters))
{
    Console.Error.WriteLine($"unknown mode {mode}");
    return 2;
}

switch (command)
{
    case "gen":
        return Gen();
    case "decode":
        return Decode();
    case "at":
        return At();
    case "sim":
        return Sim();
    case "keyup":
        return Keyup();
    default:
        Console.Error.WriteLine($"unknown command {command}");
        return 2;
}

int Gen()
{
    int bytes = int.Parse(options.GetValueOrDefault("bytes", "1024"));
    int leadIn = int.Parse(options.GetValueOrDefault("leadin", "2"));
    string outDir = options.GetValueOrDefault("out", ".");
    string[] range = options.GetValueOrDefault("seeds", "1-1").Split('-');
    int first = int.Parse(range[0]);
    int last = range.Length > 1 ? int.Parse(range[1]) : first;
    Directory.CreateDirectory(outDir);
    var codec = new OfdmFmBurstCodec(parameters, OfdmFmPresets.Table);
    // --hex FILE renders the payloads named in a file, one hex string per line, instead of
    // seeded random ones: for reconstructing what a live station sent from its frame log.
    string[]? hexLines = options.TryGetValue("hex", out string? hexFile)
        ? File.ReadLines(hexFile).Where(l => l.Length > 0).ToArray()
        : null;
    if (hexLines is not null)
    {
        first = 0;
        last = hexLines.Length - 1;
    }

    for (int seed = first; seed <= last; seed++)
    {
        byte[] payload;
        if (hexLines is not null)
        {
            payload = Convert.FromHexString(hexLines[seed]);
            bytes = payload.Length;
        }
        else
        {
            payload = new byte[bytes];
            new Random(seed).NextBytes(payload);
        }

        float[] audio = codec.Modulate(payload, parameters.Constellation, leadIn);
        string stem = Path.Combine(outDir, $"{options.GetValueOrDefault("prefix", mode)}_{bytes}_{seed}");
        double peak = 0;
        double power = 0;
        int active = 0;
        var pcm = new short[audio.Length];
        for (int n = 0; n < audio.Length; n++)
        {
            double x = audio[n];
            peak = Math.Max(peak, Math.Abs(x));
            if (n >= leadIn * parameters.SymbolSamples)
            {
                power += x * x;
                active++;
            }

            pcm[n] = (short)Math.Clamp(Math.Round(x * 32767.0), -32767, 32767);
        }

        WriteStereoWav(stem + ".wav", pcm, parameters.SampleRate);
        var meta = new
        {
            mode,
            bytes,
            seed,
            leadInSymbols = leadIn,
            symbolSamples = parameters.SymbolSamples,
            samples = audio.Length,
            peak,
            rmsDbfs = 10 * Math.Log10(power / Math.Max(1, active)),
            sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload = Convert.ToHexString(payload).ToLowerInvariant(),
        };
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(meta));
    }

    return 0;
}

int Decode()
{
    // One recording, or with --list a file naming one recording per line: each is read by a fresh
    // receiver, and each gets one JSON line.
    int block = int.Parse(options.GetValueOrDefault("block", "960"));
    IEnumerable<string> files = options.TryGetValue("list", out string? list)
        ? File.ReadLines(list).Where(l => l.Length > 0)
        : positional;
    foreach (string file in files)
    {
        (float[] audio, int rate) = WavFile.ReadMono(file);
        if (rate != parameters.SampleRate)
        {
            Console.Error.WriteLine($"capture is {rate} Hz, mode wants {parameters.SampleRate}");
            return 2;
        }

        var heard = new List<object>();
        var modem = new OfdmFmModem(mode, parameters, _ => { }, geometryTable: OfdmFmPresets.Table);
        modem.BurstHeard += burst => heard.Add(Describe(burst));
        for (int at = 0; at < audio.Length; at += block)
        {
            modem.Process(audio.AsSpan(at, Math.Min(block, audio.Length - at)));
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            file,
            bursts = heard,
            headerReads = modem.HeaderReads,
            lastSync = modem.LastSyncAtSample,
            samples = audio.Length,
        }));
    }

    return 0;
}

int At()
{
    (float[] audio, int _) = WavFile.ReadMono(positional[0]);
    var codec = new OfdmFmBurstCodec(parameters, OfdmFmPresets.Table);
    int sync = codec.FindSyncIn(audio);
    if (sync < 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "no-sync" }));
        return 0;
    }

    OfdmFmHeader? header = codec.ReadHeader(audio, sync);
    if (header is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "header-failed", sync }));
        return 0;
    }

    OfdmFmBurst? burst = codec.DecodeAt(audio, sync);
    if (burst is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "truncated", sync }));
        return 0;
    }

    Console.WriteLine(JsonSerializer.Serialize(Describe(burst)));
    return 0;
}

int Sim()
{
    int bytes = int.Parse(options.GetValueOrDefault("bytes", "1024"));
    int seeds = int.Parse(options.GetValueOrDefault("seeds", "32"));
    double deviation = double.Parse(options.GetValueOrDefault("dev", "4500"));
    double ifBandwidth = double.Parse(options.GetValueOrDefault("ifbw", "15000"));
    double[] cnrs = [.. options.GetValueOrDefault("cnr", "30,24,20,18,16,14").Split(',').Select(double.Parse)];
    var link = M0LTE.Fm.FmLinkProfile.DataPort(deviation, ifBandwidthHz: ifBandwidth);
    var codec = new OfdmFmBurstCodec(parameters, OfdmFmPresets.Table);
    Console.WriteLine($"{mode} {bytes} B, {seeds} bursts a cell, data-port link, {deviation} Hz peak deviation, {ifBandwidth} Hz IF");
    Console.WriteLine("CNR dB | shipped | tracked | tracked, older reads after | decoded only by the older reads");
    foreach (double cnr in cnrs)
    {
        int shipped = 0;
        int tracked = 0;
        int both = 0;
        int olderOnly = 0;
        for (int seed = 0; seed < seeds; seed++)
        {
            var payload = new byte[bytes];
            new Random(7000 + seed).NextBytes(payload);
            float[] clean = codec.Modulate(payload, parameters.Constellation, 2);
            float[] heard = new M0LTE.Fm.FmChannel(link, parameters.SampleRate, seed).Apply(clean, cnr);

            bool Read(bool tracking, bool older)
            {
                OfdmFmBurstCodec.ClockTracking = tracking;
                OfdmFmBurstCodec.OlderReadsAfterTracked = older;
                byte[]? got = codec.Demodulate(heard)?.Payload;
                return got is not null && got.AsSpan().SequenceEqual(payload);
            }

            bool a = Read(false, false);
            bool b = Read(true, false);
            bool c = Read(true, true);
            shipped += a ? 1 : 0;
            tracked += b ? 1 : 0;
            both += c ? 1 : 0;
            olderOnly += c && !b ? 1 : 0;
        }

        Console.WriteLine($"{cnr,6} | {shipped,7} | {tracked,7} | {both,26} | {olderOnly}");
    }

    return 0;
}

int Keyup()
{
    // sm-ofdmlab keyup --mode RX-PRESET [--tx TX-PRESET] --ppm 200 [--zigzag 300] [--snr 30]
    //                  [--frames 8] [--bytes 1024] [--seeds 1] [--gap SAMPLES] [--quiet]
    // A keyup of several frames from one station (the follow-on preset unless told otherwise),
    // heard through a clock difference, a clock that zig-zags and noise, by another.
    int bytes = int.Parse(options.GetValueOrDefault("bytes", "1024"));
    int count = int.Parse(options.GetValueOrDefault("frames", "8"));
    int seeds = int.Parse(options.GetValueOrDefault("seeds", "1"));
    double ppm = double.Parse(options.GetValueOrDefault("ppm", "200"));
    double zigzag = double.Parse(options.GetValueOrDefault("zigzag", "0"));
    double snr = double.Parse(options.GetValueOrDefault("snr", "100"));
    int gap = int.Parse(options.GetValueOrDefault("gap", "0"));
    bool quiet = options.ContainsKey("quiet") || positional.Contains("--quiet");
    string txMode = options.GetValueOrDefault("tx", "ofdm-fm-8k-follow");
    OfdmFmParameters tx = OfdmFmPresets.ByMode[txMode];
    int total = 0;
    var readings = new SortedDictionary<string, int>(StringComparer.Ordinal);
    for (int seed = 0; seed < seeds; seed++)
    {
        var sender = new OfdmFmModem("tx", tx, _ => { }, geometryTable: OfdmFmPresets.Table);
        int delivered = 0;
        var receiver = new OfdmFmModem("rx", parameters, _ => delivered++, geometryTable: OfdmFmPresets.Table);
        receiver.BurstHeard += b =>
        {
            string key = (b.FollowOn ? "follow-on " : "full ") + b.Reading;
            readings[key] = readings.GetValueOrDefault(key) + 1;
            if (!quiet)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = b.Payload is not null, followOn = b.FollowOn, reading = b.Reading,
                    preFec = b.PreFecBitErrorRate, ppm = b.Clock?.RatePpm, wanderUs = b.Clock?.WanderMicroseconds,
                }));
            }
        };
        var audio = new List<float>(new float[4096]);
        for (int i = 0; i < count; i++)
        {
            var frame = new byte[bytes];
            new Random((1000 * seed) + 800 + i).NextBytes(frame);
            if (i > 0 && gap > 0)
            {
                audio.AddRange(new float[gap]);
            }

            audio.AddRange(sender.Modulate(frame, i == 0 ? 300 : OfdmFmModem.HostLeadInWithinKeyupMs));
        }

        audio.AddRange(new float[parameters.SymbolSamples * 6]);
        float[] source = [.. audio];
        double power = 0;
        int active = 0;
        foreach (float x in source)
        {
            if (x != 0)
            {
                power += x * x;
                active++;
            }
        }

        double sigma = Math.Sqrt(power / Math.Max(1, active)) * Math.Pow(10, -snr / 20);
        var noise = new Random(99 + seed);
        var heard = new List<float>(source.Length);
        double at = 20;
        int n = 0;
        while (at < source.Length - 20)
        {
            int whole = (int)at;
            double sum = 0;
            for (int t = -15; t <= 16; t++)
            {
                double x = t - (at - whole);
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                sum += source[whole + t] * sinc * (0.5 + (0.5 * Math.Cos(Math.PI * x / 16.5)));
            }

            double u1 = 1.0 - noise.NextDouble();
            double gauss = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * noise.NextDouble());
            heard.Add((float)(sum + (sigma * gauss)));
            double now = ppm + ((((int)(n / 2880.0)) % 2) == 0 ? zigzag : -zigzag);
            at += 1.0 + (now * 1e-6);
            n++;
        }

        float[] samples = [.. heard];
        if (options.ContainsKey("whole"))
        {
            // The whole-buffer read of the same audio, for telling the streaming search's choice
            // of sync position apart from everything after it.
            var codec = new OfdmFmBurstCodec(parameters, OfdmFmPresets.Table);
            int sync = codec.FindSyncIn(samples);
            OfdmFmBurst? whole = sync < 0 ? null : codec.DecodeAt(samples, sync);
            Console.WriteLine($"whole-buffer: sync {sync} ok {whole?.Payload is not null} reading {whole?.Reading} ppm {whole?.Clock?.RatePpm:F0}");
            receiver.BurstHeard += b => Console.WriteLine($"streaming: start {b.StartSample} ok {b.Payload is not null}");
        }

        for (int i = 0; i < samples.Length; i += 960)
        {
            receiver.Process(samples.AsSpan(i, Math.Min(960, samples.Length - i)));
        }

        total += delivered;
    }

    Console.WriteLine(
        $"rx {mode}, tx {txMode}, {ppm} ppm, zigzag {zigzag}, snr {snr}: delivered {total} of {count * seeds}; "
        + string.Join(", ", readings.Select(r => $"{r.Key} {r.Value}")));
    return 0;
}

static object Describe(OfdmFmBurst burst) => new
{
    start = burst.StartSample,
    ok = burst.Payload is not null,
    length = burst.Payload?.Length,
    sha256 = burst.Payload is null
        ? null
        : Convert.ToHexString(SHA256.HashData(burst.Payload)).ToLowerInvariant(),
    bits = (int)burst.Constellation,
    coding = $"{burst.Coding.RateNumerator}/{burst.Coding.RateDenominator}",
    geometry = burst.Geometry,
    preFec = burst.PreFecBitErrorRate,
    reading = burst.Reading,
    ppm = burst.Clock?.RatePpm,
    wanderUs = burst.Clock?.WanderMicroseconds,
    delays = burst.Clock?.DelaySamples.Select(v => Math.Round(v, 3)).ToArray(),
    snrDb = burst.CarrierSnrDb?.Select(v => Math.Round(v, 1)).ToArray(),
};

static void WriteStereoWav(string path, short[] mono, int rate)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int dataBytes = mono.Length * 4;
    writer.Write("RIFF"u8);
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)2);
    writer.Write(rate);
    writer.Write(rate * 4);
    writer.Write((short)4);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(dataBytes);
    foreach (short sample in mono)
    {
        writer.Write(sample);
        writer.Write(sample);
    }
}
