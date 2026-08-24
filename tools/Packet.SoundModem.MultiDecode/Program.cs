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
        bool blind = args.Contains("--sweep");

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

        int centreAt = Array.IndexOf(args, "--centre");
        double? centre = null;
        if (centreAt >= 0)
        {
            if (centreAt + 1 >= args.Length
                || !double.TryParse(args[centreAt + 1], out double requested))
            {
                Console.Error.WriteLine("pdn-decode: --centre needs an audio frequency in Hz");
                return ExitUsage;
            }

            centre = requested;
        }

        if (centre is not null && blind)
        {
            Console.Error.WriteLine(
                "pdn-decode: --centre says where the signal is and --sweep says nobody knows; "
                + "use one");
            return ExitUsage;
        }

        if (args.Contains("--list"))
        {
            IReadOnlyList<SweepEntry> listed = centre is double pinned
                ? Sweep.AtCentres(entries, [pinned])
                : blind ? Sweep.AtCentres(entries, Sweep.BlindCentres(), keepDefault: true)
                : entries;
            foreach (SweepEntry entry in listed)
            {
                Console.WriteLine(
                    $"{entry.Label,-32} {ModemCatalog.DspRateFor(entry.Mode)} Hz  "
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

        string[] files = ExpandFiles(args, channelAt, centreAt);
        if (files.Length == 0)
        {
            Console.Error.WriteLine("pdn-decode: no files matched");
            return ExitUsage;
        }

        int decodedFiles = 0;
        int totalFrames = 0;
        foreach (string file in files)
        {
            int frames = Report(file, entries, channel, quiet, centre, blind);
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
    /// <param name="path">The recording.</param>
    /// <param name="modes">The sweep set, at each mode's catalogue centre.</param>
    /// <param name="channel">Which channel of a multi-channel file to read.</param>
    /// <param name="quiet">One summary line, no frames.</param>
    /// <param name="centre">An audio centre from <c>--centre</c>, which overrides any sidecar.</param>
    /// <param name="blind">Whether <c>--sweep</c> asked for the whole centre grid.</param>
    private static int Report(
        string path,
        IReadOnlyList<SweepEntry> modes,
        int? channel,
        bool quiet,
        double? centre,
        bool blind)
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

        var notes = new List<string>();
        IReadOnlyList<SweepEntry> entries = Retune(path, modes, centre, blind, notes);
        foreach (string note in notes)
        {
            header += "\n  " + note;
        }

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

        string tried = entries.Count == modes.Count
            ? $"{entries.Count} modes tried"
            : $"{entries.Count} runs over {modes.Count} modes";

        if (quiet)
        {
            Console.WriteLine(Summary(order.Count, result, tried));
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

        Console.WriteLine("  " + Summary(order.Count, result, tried));

        foreach (SweepFailure failure in result.Failures.Where(f => !f.OutOfBand))
        {
            Console.WriteLine($"  {failure.Label} could not run: {failure.Reason}");
        }

        // The wide waveforms refusing an off-centre placement is arithmetic, not a fault, and
        // there are a dozen of them: one line naming the modes, not a dozen restating the
        // passband.
        string[] tooWide = [.. result.Failures.Where(f => f.OutOfBand)
            .Select(f => f.Mode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (tooWide.Length > 0)
        {
            Console.WriteLine(
                $"  {tooWide.Length} mode(s) too wide to sit where they were pointed, skipped: "
                + string.Join(", ", tooWide));
        }

        Console.WriteLine();
        return order.Count;
    }

    private static string Summary(int frames, SweepResult result, string tried)
    {
        string counted = frames == 0
            ? "nothing decoded"
            : $"{frames} distinct frame(s), {result.Decodes.Count} decode(s)";
        return $"{counted}; {tried}, {result.Silent.Count} silent, "
            + $"{result.Elapsed.TotalSeconds:F1} s.";
    }

    /// <summary>
    /// Points the sweep at where the signal actually is, and says in <paramref name="notes"/>
    /// how it was decided.
    /// </summary>
    /// <remarks>
    /// Three sources, in order. <c>--centre</c> is the operator saying so and wins outright.
    /// Otherwise the survey's own JSON sidecar, if one sits beside the WAV: a survey capture is
    /// by definition a signal nothing was tuned to, the survey measured where it was, and
    /// sweeping catalogue centres over one is the case this tool is guaranteed to get wrong.
    /// Otherwise <c>--sweep</c>'s grid, if asked for. With none of the three, every mode runs
    /// where its catalogue says it lives, which is right for a recording of a station that was
    /// on frequency.
    /// </remarks>
    private static IReadOnlyList<SweepEntry> Retune(
        string path,
        IReadOnlyList<SweepEntry> modes,
        double? centre,
        bool blind,
        List<string> notes)
    {
        if (centre is double pinned)
        {
            notes.Add($"centre {pinned:F0} Hz (--centre)");
            return Sweep.AtCentres(modes, [pinned]);
        }

        Sidecar? sidecar = CaptureSidecar.Beside(path);
        if (sidecar?.Problem is string problem)
        {
            notes.Add($"sidecar {Path.GetFileName(sidecar.Path)}: {problem} - ignoring it");
        }

        if (blind)
        {
            var grid = new List<double>(Sweep.BlindCentres());
            if (sidecar?.CentreHz is double measured
                && !grid.Exists(already => Math.Abs(already - measured) < 1))
            {
                grid.Add(measured);
                grid.Sort();
            }

            notes.Add(
                $"blind centre sweep: {grid.Count} centres, {grid[0]:F0} to {grid[^1]:F0} Hz, "
                + "plus each mode's own");
            return Sweep.AtCentres(modes, grid, keepDefault: true);
        }

        if (sidecar?.CentreHz is double fromSidecar)
        {
            string what = sidecar.Verdict is string verdict
                ? $"{verdict.ToLowerInvariant()} capture"
                : "capture";
            string width = sidecar.WidthHz is double hz ? $", {hz:F0} Hz wide" : "";
            notes.Add(
                $"centre {fromSidecar:F0} Hz from {Path.GetFileName(sidecar.Path)} "
                + $"({what}{width})");
            return Sweep.AtCentres(modes, [fromSidecar]);
        }

        return modes;
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
    /// <param name="args">The command line.</param>
    /// <param name="valueAt">Positions of the flags that take a value, whose next argument is
    /// therefore that value and not a file. Each is guarded on being present: unguarded, an
    /// absent flag sits at index -1 and its "value" at 0, which silently eats the first
    /// filename.</param>
    private static string[] ExpandFiles(string[] args, params int[] valueAt)
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

            if (Array.Exists(valueAt, at => at >= 0 && i == at + 1))
            {
                continue; // a flag's value
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
              --centre HZ       point every mode that has a centre at this audio frequency
                                instead of its catalogue one
              --sweep           try a grid of centres, 500 to 2500 Hz, as well as each mode's
                                own: for a signal nothing knows the frequency of. Multiplies
                                the running time by the number of centres, so pair it with
                                --packet or --modes
              --list            print the sweep set and exit
              --channel N       read channel N (default: the loudest channel in the file)
              --quiet           one summary line per file, no frames
              -h, --help        this

            By default it sweeps every mode the modem has. Narrow it with --packet or --fm if you
            know roughly what you are looking at and want the answer sooner.

            Where it listens: each mode's catalogue centre, unless told otherwise. A signal
            survey capture carries its own measured centre in the JSON sidecar beside the WAV,
            and that is read and used automatically - which is the whole point, since a survey
            capture is a signal nothing was tuned to. --centre overrides it; --sweep tries a
            grid when nothing knows.

            Exit status: 0 if anything decoded, 1 if nothing did, 2 for a usage or input error.
            """);
    }
}
