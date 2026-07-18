using M0LTE.Pocsag;
using System.Globalization;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Pocsag;

// sm-pocsag: offline POCSAG paging encoder/decoder — this project's companion to
// sm-decode, for the DAPNET / pager waveform rather than the AX.25 modes.
//
//   sm-pocsag encode <out.wav> <page> [<page> ...] [--baud 512|1200|2400]
//             [--rate 48000] [--invert] [--preamble 576]
//   sm-pocsag decode <in.wav> [--baud 512|1200|2400] [--quiet]
//
// A <page> is <ric>:<function>:<type>[:<text>], where <type> is a (alphanumeric),
// n (numeric) or t (tone-only): e.g. "133703:3:a:Hello DAPNET" or "8:0:n:555 0100".
// The default 1200 baud is DAPNET's rate. Decode prints one line per page.

static int Usage()
{
    Console.Error.WriteLine("usage: sm-pocsag encode <out.wav> <ric>:<func>:<a|n|t>[:<text>] [...] [--baud 1200] [--rate 48000] [--invert] [--preamble 576]");
    Console.Error.WriteLine("       sm-pocsag decode <in.wav> [--baud 1200] [--quiet]");
    return 2;
}

if (args.Length < 2)
{
    return Usage();
}

string verb = args[0];
string path = args[1];
var rest = args.Skip(2).ToArray();

int Option(string name, int fallback)
{
    int i = Array.IndexOf(rest, name);
    return i >= 0 && i + 1 < rest.Length
        ? int.Parse(rest[i + 1], CultureInfo.InvariantCulture)
        : fallback;
}

int baud = Option("--baud", 1200);

switch (verb)
{
    case "encode":
    {
        int rate = Option("--rate", 48000);
        int preamble = Option("--preamble", PocsagEncoder.PreambleBits);
        var polarity = rest.Contains("--invert") ? PocsagPolarity.Inverted : PocsagPolarity.Normal;

        var messages = new List<PocsagMessage>();
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (rest[i] is "--baud" or "--rate" or "--preamble")
                {
                    i++; // skip the option's value
                }

                continue;
            }

            string[] parts = rest[i].Split(':', 4);
            if (parts.Length < 3
                || !uint.TryParse(parts[0], CultureInfo.InvariantCulture, out uint ric)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int function))
            {
                Console.Error.WriteLine($"bad page spec '{rest[i]}'");
                return Usage();
            }

            string text = parts.Length > 3 ? parts[3] : "";
            messages.Add(parts[2] switch
            {
                "a" => PocsagMessage.Alphanumeric(ric, text, function),
                "n" => PocsagMessage.Numeric(ric, text, function),
                "t" => PocsagMessage.Tone(ric, function),
                _ => throw new ArgumentException($"unknown page type '{parts[2]}' (use a, n or t)"),
            });
        }

        if (messages.Count == 0)
        {
            Console.Error.WriteLine("no pages given");
            return Usage();
        }

        var encoder = new PocsagEncoder(rate, baud, polarity);
        float[] audio = encoder.Modulate(messages, preamble);
        WavFile.WriteMono(path, audio, rate);
        Console.WriteLine(
            $"{messages.Count} page(s) → {Path.GetFileName(path)} ({encoder.Mode}, {rate} Hz, " +
            $"{polarity switch { PocsagPolarity.Inverted => "inverted", _ => "normal" }} polarity, " +
            $"{audio.Length / (double)rate:F2} s)");
        return 0;
    }

    case "decode":
    {
        bool quiet = rest.Contains("--quiet");
        var (samples, sampleRate) = WavFile.ReadMono(path);

        // Flush tail: a file can end flush with the final batch, which would otherwise
        // strand the last codewords inside the FIR pipeline (a live stream never "ends").
        Array.Resize(ref samples, samples.Length + sampleRate / 2);

        int count = 0;
        var decoder = new PocsagDecoder(sampleRate, page =>
        {
            count++;
            if (!quiet)
            {
                string kind = page.Function == 0 ? "Numeric" : "Alpha";
                string text = page.ContentGroups.Count == 0 ? "(tone only)" : $"{kind}: {page.Text}";
                string notes = (page.BitErrorsCorrected > 0 ? $" [{page.BitErrorsCorrected} bits corrected]" : "")
                    + (page.Inverted ? " [inverted]" : "")
                    + (page.Truncated ? " [truncated]" : "");
                Console.WriteLine($"[{count}] RIC {page.Address}  Function {page.Function}  {text}{notes}");
            }
        }, baud);

        decoder.Process(samples);
        decoder.Flush();
        Console.WriteLine($"{count} page(s) decoded from {Path.GetFileName(path)} (pocsag{baud})");
        return 0;
    }

    default:
        return Usage();
}
