using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;

// sm-iqcapture — capture one IQ48 (or PCM) session from a ka9q_ubersdr / UberSDR instance to a
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
        sm-iqcapture — record IQ from a ka9q_ubersdr / UberSDR instance.

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
        Console.Error.WriteLine("interrupt — finalising current capture…");
        cts.Cancel();
    };

    var client = new UberSdrIqClient(msg => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {msg}"));
    CaptureResult r = await client.CaptureAsync(opt, cts.Token);

    Console.WriteLine(r.WavPath);
    Console.Error.WriteLine(
        $"done: {r.Frames} frames @ {r.SampleRate} Hz, sample0 {r.Sample0Utc:yyyy-MM-ddTHH:mm:ss.fffZ}, sha256 {r.WavSha256}");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// convert: IQ48 capture WAV → 9600 Hz MS110D audio WAV. Reuses the repo's WavFile IO
// (whole-file; slice per burst for very long captures).
static int RunConvert(string[] argv)
{
    var a = Args.Parse(argv);
    if (a is null || a.ContainsKey("help") || !a.ContainsKey("in"))
    {
        Console.Error.WriteLine("""
            sm-iqcapture convert --in <iq48.wav> [--out <audio.wav>]
                          [--dial-hz <Hz>] [--ssb-low 150] [--ssb-high 3450] [--out-rate 9600]

            --dial-hz: IQ-baseband frequency of the SSB suppressed carrier (our TX dial − RX tune
                       frequency); 0 when the RX is tuned exactly to our dial. Narrow the SSB
                       edges to emulate a tighter RX filter for comparison.
            """);
        return a is null ? 2 : 0;
    }
    try
    {
        string inPath = a.Req("in");
        string outPath = a.Str("out", Path.ChangeExtension(inPath, ".audio9600.wav"));
        var opt = new IqToAudioOptions
        {
            OutputRate = a.Int("out-rate", 9600),
            DialHz = double.Parse(a.Str("dial-hz", "0")),
            SsbLowHz = double.Parse(a.Str("ssb-low", "150")),
            SsbHighHz = double.Parse(a.Str("ssb-high", "3450")),
        };

        (float[] iF, int rate) = WavFile.ReadMono(inPath, channel: 0);
        (float[] qF, _) = WavFile.ReadMono(inPath, channel: 1);
        var opt2 = rate == opt.InputRate ? opt : new IqToAudioOptions
        {
            InputRate = rate, OutputRate = opt.OutputRate, DialHz = opt.DialHz,
            SsbLowHz = opt.SsbLowHz, SsbHighHz = opt.SsbHighHz,
        };
        var iD = Array.ConvertAll(iF, x => (double)x);
        var qD = Array.ConvertAll(qF, x => (double)x);

        float[] audio = new IqToAudioConverter(opt2).Convert(iD, qD);
        WavFile.WriteMono(outPath, audio, opt2.OutputRate);
        Console.Error.WriteLine(
            $"converted {iF.Length} IQ samples @ {rate} Hz → {audio.Length} audio samples @ " +
            $"{opt2.OutputRate} Hz (dial {opt2.DialHz:F0} Hz, SSB {opt2.SsbLowHz:F0}–{opt2.SsbHighHz:F0} Hz)");
        Console.WriteLine(outPath);
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
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
