using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

// sm-iqcapture - capture one IQ48 (or PCM) session from a ka9q_ubersdr / UberSDR instance to a
// 16-bit stereo WAV + JSON sidecar. One session per invocation; drive per-pass reconnect from a
// script (a fresh process per ladder pass). See docs/ms110d/ota-capture-client-plan.md.
//
// Subcommand `convert` runs the offline IQ→audio converter (C2): an IQ48 capture WAV → the
// 9600 Hz real audio the MS110D demodulator consumes.

if (args.Length > 0 && args[0] == "convert")
{
    return RunConvert(args[1..]);
}

var args2 = Args.Parse(args);
if (args2 is null || args2.ContainsKey("help"))
{
    Console.Error.WriteLine("""
        sm-iqcapture - record IQ from a ka9q_ubersdr / UberSDR instance.

        Usage:
          sm-iqcapture --host <h> [--port 443] [--no-ssl] --frequency <Hz>
                       [--duration <s>] [--mode iq48] [--name <label>]
                       [--password <pw>] [--out-dir <dir>] [--startup-guard-ms 1000]

        Defaults: port 443, SSL on, mode iq48, duration 0 (until Ctrl+C or server close),
        out-dir ".", startup-guard 1000 ms. Writes <name|host>_<freq>_<sample0UTC>.wav + .json.

        Example:
          sm-iqcapture --host m9psy.tunnel.ubersdr.org --frequency 7074000 --duration 30 --out-dir captures
        """);
    return args2 is null ? 2 : 0;
}

try
{
    var opt = new UberSdrCaptureOptions
    {
        Host = args2.Req("host"),
        Port = args2.Int("port", 443),
        Ssl = !args2.ContainsKey("no-ssl"),
        FrequencyHz = args2.Int("frequency", 0) is var f && f > 0 ? f : throw new ArgumentException("--frequency <Hz> is required"),
        Mode = args2.Str("mode", "iq48"),
        Password = args2.Str("password", null),
        Name = args2.Str("name", null),
        OutputDir = args2.Str("out-dir", "."),
        DurationSeconds = args2.Int("duration", 0),
        StartupGuardMs = args2.Int("startup-guard-ms", 1000),
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.Error.WriteLine("interrupt - finalising current capture…");
        cts.Cancel();
    };

    var client = new UberSdrIqClient(msg => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {msg}"));
    CaptureResult r = await client.CaptureAsync(opt, cts.Token);

    Console.WriteLine(r.WavPath);
    Console.Error.WriteLine(
        $"done: {r.Frames} frames @ {r.SampleRate} Hz, sample0 {r.Sample0Utc:yyyy-MM-ddTHH:mm:ss.fffZ}, sha256 {r.WavSha256}");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException
                           or IOException or UberSdrRefusedException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// convert: IQ48 capture WAV → 9600 Hz MS110D audio WAV, streamed in constant memory (a campaign
// pass is an hour, 691 MB, and does not fit).
static int RunConvert(string[] argv)
{
    var a = Args.Parse(argv);
    if (a is null || a.ContainsKey("help") || !a.ContainsKey("in"))
    {
        Console.Error.WriteLine("""
            sm-iqcapture convert --in <iq48.wav> [--out <audio.wav>]
                          [--dial-hz <Hz>] [--ssb-low 150] [--ssb-high 3450] [--out-rate 9600]
                          [--gain auto|<factor>]

            --dial-hz: IQ-baseband frequency of the SSB suppressed carrier (our TX dial − RX tune
                       frequency); 0 when the RX is tuned exactly to our dial. Narrow the SSB
                       edges to emulate a tighter RX filter for comparison.
            --gain:    auto (default) makes a first pass to find the peak and scales it to 0.7,
                       which is a PER-FILE scale - levels are then not comparable between files.
                       Give a number instead when comparing captures. The peak, the gain applied
                       and any clipping are always reported.
            """);
        return a is null ? 2 : 0;
    }
    try
    {
        string inPath = a.Req("in");
        string outPath = a.Str("out", Path.ChangeExtension(inPath, ".audio9600.wav"));
        string gainArg = a.Str("gain", "auto");
        int outRate = a.Int("out-rate", 9600);
        double dialHz = double.Parse(a.Str("dial-hz", "0"));
        double ssbLow = double.Parse(a.Str("ssb-low", "150"));
        double ssbHigh = double.Parse(a.Str("ssb-high", "3450"));

        SsbDemodulatorOptions Options(int rate) => new()
        {
            InputRate = rate, OutputRate = outRate, DialHz = dialHz,
            SsbLowHz = ssbLow, SsbHighHz = ssbHigh,
        };

        int inputRate;
        long inputFrames;
        using (var probe = new PcmWavReader(inPath))
        {
            inputRate = probe.SampleRate;
            inputFrames = probe.FrameCount;
            if (probe.Channels != 2)
            {
                throw new InvalidDataException($"expected a 2-channel IQ capture, found {probe.Channels}");
            }
        }

        double gain;
        if (gainArg.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            double raw = StreamCapture(inPath, Options(inputRate), null, 1.0, out _);
            gain = raw > 1e-12 ? 0.7 / raw : 1.0;
        }
        else
        {
            gain = double.Parse(gainArg);
        }

        double peak;
        int clipped;
        using (var writer = new PcmWavWriter(outPath, outRate, channels: 1))
        {
            peak = StreamCapture(inPath, Options(inputRate), writer, gain, out clipped);
        }

        long outFrames = inputFrames / (inputRate / outRate);
        Console.Error.WriteLine(
            $"converted {inputFrames} IQ frames @ {inputRate} Hz → {outFrames} audio samples @ " +
            $"{outRate} Hz (dial {dialHz:F0} Hz, SSB {ssbLow:F0}-{ssbHigh:F0} Hz), " +
            $"gain {gain:G4}, peak out {peak:F4}" + (clipped > 0 ? $", CLIPPED {clipped} samples" : ""));
        Console.WriteLine(outPath);
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

/// <summary>Streams one capture through the converter, optionally writing it. Returns the peak
/// output magnitude (after <paramref name="gain"/>).</summary>
static double StreamCapture(
    string path, SsbDemodulatorOptions opt, PcmWavWriter? writer, double gain, out int clipped)
{
    const int blockFrames = 1 << 16;
    using var reader = new PcmWavReader(path);
    var converter = new StreamingSsbDemodulator(opt);
    var input = new short[blockFrames * 2];
    var output = new float[converter.MaxOutputFor(blockFrames) + converter.MaxFlushOutput];

    double peak = 0;
    int clips = 0;
    void Consume(int count)
    {
        for (int k = 0; k < count; k++)
        {
            float v = (float)(output[k] * gain);
            output[k] = v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        if (writer is not null)
        {
            clips += writer.WriteSamples(output.AsSpan(0, count));
        }
    }

    int frames;
    while ((frames = reader.ReadFrames(input)) > 0)
    {
        Consume(converter.Process(input.AsSpan(0, frames * 2), output));
    }

    Consume(converter.Flush(output));
    clipped = clips;
    return peak;
}

/// <summary>Tiny <c>--key value</c> / <c>--flag</c> argument parser.</summary>
internal sealed class Args : Dictionary<string, string>
{
    public static Args? Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unexpected argument: {argv[i]}");
                return null;
            }
            string key = argv[i][2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                a[key] = argv[++i];
            }
            else
            {
                a[key] = "true"; // bare flag
            }
        }
        return a;
    }

    public string Req(string k) => TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
    public string Str(string k, string? d) => TryGetValue(k, out var v) ? v : d!;
    public int Int(string k, int d) => TryGetValue(k, out var v) ? int.Parse(v) : d;
}
