using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.MultiDecode;

// pdn-decode: try every modem against recordings whose mode nobody wrote down.
//
//   pdn-decode *.wav
//
// The counterpart to sm-decode, which decodes one file with one mode you already know. This one
// answers the other question: here is a capture something else could not read, what is in it? It
// sweeps every mode the modem has over each file, reports every frame any mode recovered as hex
// and printable ASCII, and says which modes read it and how cleanly.

return Cli.Run(args);

internal static class Cli
{
    private const int ExitDecoded = 0;
    private const int ExitNothingDecoded = 1;
    private const int ExitUsage = 2;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Usage();
            return args.Length == 0 ? ExitUsage : ExitDecoded;
        }

        bool quiet = args.Contains("--quiet");

        IReadOnlyList<SweepEntry> entries;
        try
        {
            entries = SelectModes(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"pdn-decode: {error.Message}");
            return ExitUsage;
        }

        if (args.Contains("--list"))
        {
            foreach (SweepEntry entry in entries)
            {
                Console.WriteLine(
                    $"{entry.Label,-24} {ModemCatalog.DspRateFor(entry.Mode)} Hz  "
                    + ModeNames.Display(entry.Mode));
            }

            return ExitDecoded;
        }

        int? channel = null;
        int channelAt = Array.IndexOf(args, "--channel");
        if (channelAt >= 0)
        {
            if (channelAt + 1 >= args.Length || !int.TryParse(args[channelAt + 1], out int requested))
            {
                Console.Error.WriteLine("pdn-decode: --channel needs a channel number (0 is the first)");
                return ExitUsage;
            }

            channel = requested;
        }

        string[] files = ExpandFiles(args, channelAt);
        if (files.Length == 0)
        {
            Console.Error.WriteLine("pdn-decode: no files matched");
            return ExitUsage;
        }

        int decodedFiles = 0;
        int totalFrames = 0;
        foreach (string file in files)
        {
            int frames = Report(file, entries, channel, quiet);
            if (frames > 0)
            {
                decodedFiles++;
                totalFrames += frames;
            }
        }

        if (files.Length > 1)
        {
            Console.WriteLine(
                $"{totalFrames} frame(s) from {decodedFiles} of {files.Length} files, "
                + $"{entries.Count} modes tried.");
        }

        return totalFrames > 0 ? ExitDecoded : ExitNothingDecoded;
    }

    /// <summary>Decodes one file and prints its report. Returns the number of distinct frames.</summary>
    private static int Report(
        string path, IReadOnlyList<SweepEntry> entries, int? channel, bool quiet)
    {
        float[] samples;
        int sampleRate;
        int channels;
        int used;
        try
        {
            (samples, sampleRate, channels, used) = LoadAudio(path, channel);
        }
        catch (Exception error) when (error is IOException or InvalidDataException
                                          or ArgumentOutOfRangeException or UnauthorizedAccessException)
        {
            Console.WriteLine($"{Path.GetFileName(path)}  cannot read: {error.Message}");
            Console.WriteLine();
            return 0;
        }

        string channelNote = channels > 1 ? $", {channels} ch (using {used})" : " mono";
        string header = $"{Path.GetFileName(path)}  {sampleRate} Hz{channelNote}, "
            + $"{samples.Length / (double)sampleRate:F2} s";

        Action<string>? progress = Console.IsErrorRedirected
            ? null
            : label => Console.Error.Write($"\r  trying {label,-28}");

        SweepResult result = Sweep.Run(samples, sampleRate, entries, progress);
        if (progress is not null)
        {
            Console.Error.Write("\r" + new string(' ', 40) + "\r");
        }

        // One entry per distinct frame, in the order the sweep first heard it, carrying every
        // mode that read it. Several modes reading the same burst is the normal case, not a
        // problem: afsk1200 and its diversity bank and the FX.25 receiver all read plain AX.25.
        var byFrame = new Dictionary<string, List<Decode>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Decode decode in result.Decodes)
        {
            string key = Convert.ToHexString(decode.Frame);
            if (!byFrame.TryGetValue(key, out List<Decode>? group))
            {
                group = [];
                byFrame[key] = group;
                order.Add(key);
            }

            group.Add(decode);
        }

        Console.WriteLine(header);

        if (quiet)
        {
            Console.WriteLine(Summary(order.Count, result, entries.Count));
            Console.WriteLine();
            return order.Count;
        }

        int number = 0;
        foreach (string key in order)
        {
            List<Decode> group = byFrame[key];
            Decode best = group.MinBy(Sweep.Confidence)!;
            byte[] frame = best.Frame;
            number++;

            Console.WriteLine(
                $"  frame {number}  {frame.Length} bytes  via {best.Label}  ({Diagnostics(best.Quality)})");

            // Every other mode that read the identical bytes. Worth printing: it is the answer to
            // "was this really 9600, or did it just also fit?" and to "would my other TNC have
            // seen it?".
            string[] others = [.. group.Select(d => d.Label).Distinct(StringComparer.Ordinal)
                .Where(label => !string.Equals(label, best.Label, StringComparison.Ordinal))];
            if (others.Length > 0)
            {
                Console.WriteLine($"          also read by: {string.Join(", ", others)}");
            }

            if (FrameText.Ax25Header(frame) is string ax25)
            {
                Console.WriteLine($"    {ax25}");
            }

            Console.Write(FrameText.HexDump(frame, indent: 4));

            if (FrameText.InfoField(frame) is string info && info.Length > 0)
            {
                Console.WriteLine($"    text  {info}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("  " + Summary(order.Count, result, entries.Count));

        foreach (SweepFailure failure in result.Failures)
        {
            Console.WriteLine($"  {failure.Label} could not run: {failure.Reason}");
        }

        Console.WriteLine();
        return order.Count;
    }

    private static string Summary(int frames, SweepResult result, int modes)
    {
        string counted = frames == 0
            ? "nothing decoded"
            : $"{frames} distinct frame(s), {result.Decodes.Count} decode(s)";
        return $"{counted}; {modes} modes tried, {result.Silent.Count} silent, "
            + $"{result.Elapsed.TotalSeconds:F1} s.";
    }

    /// <summary>The receiver's own account of how hard the frame was to read.</summary>
    private static string Diagnostics(FrameQuality quality)
    {
        var parts = new List<string>();

        parts.Add(quality switch
        {
            { CrcValid: true } => "il2p+crc ok",
            { CrcValid: false } => "il2p crc FAILED",
            { PlainIl2p: true, TrailerNearBits: int near } => $"plain il2p, trailer within {near} bits",
            { PlainIl2p: true } => "plain il2p, reed-solomon only",
            _ => "fcs ok",
        });

        if (quality.CorrectedBytes is int corrected and > 0)
        {
            parts.Add($"{corrected} byte(s) FEC-corrected");
        }

        if (quality.ErasedBytes is int erased and > 0)
        {
            parts.Add($"{erased} erased");
        }

        if (quality.ChasedBits is int chased and > 0)
        {
            parts.Add($"{chased} bit(s) chased");
        }

        if (quality.FrequencyOffsetHz is double offset && Math.Abs(offset) >= 1)
        {
            parts.Add($"{offset:+0;-0} Hz off centre");
        }

        if (quality.EmphasisDb is int emphasis and > 0)
        {
            parts.Add($"+{emphasis} dB/oct pre-emphasis");
        }

        if (quality.HeaderType is { } headerType)
        {
            // Which IL2P encapsulation this arrived in decides where the AX.25 addresses are, so
            // it is the first thing to look at when a frame decodes cleanly and then will not
            // yield callsigns.
            parts.Add($"il2p {headerType}");
        }

        if (quality.MonitorOnly)
        {
            // The distinction that matters to somebody comparing against another TNC: this frame
            // was read, and a link running IL2P+CRC would not have passed it to its host.
            parts.Add("MONITOR ONLY, a crc link would not deliver this");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Reads the file, choosing a channel. Without an explicit one, the loudest channel wins:
    /// a two-channel capture with the radio on one side is ordinary, and reading the silent side
    /// is indistinguishable from a file with nothing in it.
    /// </summary>
    private static (float[] Samples, int SampleRate, int Channels, int Used) LoadAudio(
        string path, int? requested)
    {
        var (samples, sampleRate, channels) = WavFile.ReadChannel(path, requested ?? 0);
        if (requested is not null || channels == 1)
        {
            return (samples, sampleRate, channels, requested ?? 0);
        }

        int best = 0;
        double loudest = Rms(samples);
        float[] chosen = samples;
        for (int channel = 1; channel < channels; channel++)
        {
            var (candidate, _, _) = WavFile.ReadChannel(path, channel);
            double level = Rms(candidate);
            if (level > loudest)
            {
                (loudest, best, chosen) = (level, channel, candidate);
            }
        }

        return (chosen, sampleRate, channels, best);
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += (double)sample * sample;
        }

        return samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
    }

    private static IReadOnlyList<SweepEntry> SelectModes(string[] args)
    {
        int at = Array.IndexOf(args, "--modes");
        if (at < 0)
        {
            // Everything, by default. Sweeping the HF data waveforms over a VHF capture cannot
            // find anything a narrower set would have found, but it costs only wall clock on a
            // short file, and the whole point of this tool is not having to have guessed right.
            return args.Contains("--fm") ? Sweep.FmNativeModes()
                : args.Contains("--packet") ? Sweep.PacketModes()
                : Sweep.AllModes();
        }

        if (at + 1 >= args.Length)
        {
            throw new ArgumentException("--modes needs a comma-separated list of mode names");
        }

        var entries = new List<SweepEntry>();
        foreach (string mode in args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = mode.Trim();
            if (!ModemCatalog.IsKnown(trimmed))
            {
                string[] near = ModemCatalog.NearestModes(trimmed);
                string hint = near.Length > 0 ? $" - did you mean {string.Join(" or ", near)}?" : "";
                throw new ArgumentException($"unknown mode '{trimmed}'{hint}");
            }

            entries.Add(new SweepEntry(trimmed, trimmed));
        }

        return entries;
    }

    /// <summary>
    /// The file arguments. A glob is expanded here as well as by the shell, so a pattern that
    /// arrived quoted, or from a shell that leaves unmatched patterns alone, still works.
    /// </summary>
    private static string[] ExpandFiles(string[] args, int channelAt)
    {
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
            {
                if (args[i] == "--modes")
                {
                    i++; // its value is not a file
                }

                continue;
            }

            // Guarded on channelAt >= 0: unguarded, an absent --channel puts this at index 0 and
            // silently eats the first filename.
            if (channelAt >= 0 && i == channelAt + 1)
            {
                continue; // the channel number
            }

            string argument = args[i];
            if (argument.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                string directory = Path.GetDirectoryName(argument) is { Length: > 0 } d ? d : ".";
                string pattern = Path.GetFileName(argument);
                if (Directory.Exists(directory))
                {
                    files.AddRange(Directory.GetFiles(directory, pattern).OrderBy(f => f, StringComparer.Ordinal));
                }

                continue;
            }

            if (Directory.Exists(argument))
            {
                files.AddRange(Directory.GetFiles(argument, "*.wav").OrderBy(f => f, StringComparer.Ordinal));
                continue;
            }

            files.Add(argument);
        }

        return [.. files.Distinct(StringComparer.Ordinal)];
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            """
            pdn-decode - decode recordings whose mode nobody wrote down.

              pdn-decode [options] <file.wav|glob|directory> ...

            Sweeps every pdn modem over each file and prints every frame any of them recovered,
            as hex and printable ASCII, with the modes that read it and how cleanly.

            Options:
              --packet          skip the HF data waveforms (freedv-*, ms110d-*), which are most
                                of the running time and which no VHF or UHF radio carries
              --fm              sweep only the FM-native modes: fastest, and narrower than you
                                probably want - it leaves out the shaped-PSK modes, which an FM
                                radio carries perfectly well
              --modes a,b,c     sweep only these modes
              --list            print the sweep set and exit
              --channel N       read channel N (default: the loudest channel in the file)
              --quiet           one summary line per file, no frames
              -h, --help        this

            By default it sweeps every mode the modem has. Narrow it with --packet or --fm if you
            know roughly what you are looking at and want the answer sooner.

            Exit status: 0 if anything decoded, 1 if nothing did, 2 for a usage or input error.
            """);
    }
}
