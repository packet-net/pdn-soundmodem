namespace Packet.SoundModem.TncTest;

/// <summary>What the command line asked for.</summary>
internal sealed record Options
{
    /// <summary>The tracks to score, in the order given.</summary>
    public required IReadOnlyList<string> Tracks { get; init; }

    /// <summary>The modes to score each track with.</summary>
    public required IReadOnlyList<string> Modes { get; init; }

    /// <summary>DSP rate override; null runs each mode at its catalogue rate.</summary>
    public int? DspRate { get; init; }

    /// <summary>Audio centre override; null uses each mode's own.</summary>
    public double? CentreHz { get; init; }

    /// <summary>Diversity-bank width override, where the mode runs a bank.</summary>
    public int? OffsetPairs { get; init; }

    /// <summary>Which channel of a multi-channel recording; null picks the loudest.</summary>
    public int? Channel { get; init; }

    /// <summary>Seconds to skip at the start of each track.</summary>
    public double SkipSeconds { get; init; }

    /// <summary>Seconds of each track to score; null runs to the end.</summary>
    public double? LengthSeconds { get; init; }

    /// <summary>List every decoded frame, not just the counts.</summary>
    public bool ListFrames { get; init; }

    /// <summary>Where to write the run as JSON, for a later <see cref="Baseline"/> comparison.</summary>
    public string? JsonPath { get; init; }

    /// <summary>An earlier run's JSON to diff this one against.</summary>
    public string? Baseline { get; init; }

    /// <summary>Suppress the progress line (it goes to stderr either way).</summary>
    public bool Quiet { get; init; }

    public const string Usage = """
        sm-tnctest - score the AX.25 decoders against a recorded corpus, WA8LMF TNC Test CD style

        usage: sm-tnctest <track.flac|track.wav> [more tracks...] [options]

          --mode NAME[,NAME]  modes to score (default afsk1200-multi, the daemon's AFSK bank)
          --rate HZ           DSP rate override (default: each mode's catalogue rate)
          --centre HZ         audio centre override (default: each mode's own)
          --pairs N           diversity-bank width override, where the mode runs a bank
          --channel N         which channel of a multi-channel recording (default: the loudest)
          --skip SECONDS      start this far into each track
          --seconds SECONDS   score only this much of each track
          --frames            list every decoded frame
          --json PATH         write every track's run to PATH, frames included
          --baseline PATH     diff this run's frame sets against a run written by --json
          --quiet             no progress line
        """;

    /// <summary>Parses the command line, or returns the reason it will not parse.</summary>
    public static bool TryParse(string[] args, out Options options, out string error)
    {
        (Options? parsed, string? problem) = Parse(args);
        options = parsed!;
        error = problem ?? "";
        return problem is null;
    }

    /// <summary>The parse itself, returning one or the other. Separate from
    /// <see cref="TryParse"/> because a local function cannot write to an <c>out</c>
    /// parameter, and the option loop wants one for "this flag needs a value".</summary>
    private static (Options? Parsed, string? Problem) Parse(string[] args)
    {
        string? problem = null;

        var tracks = new List<string>();
        var modes = new List<string> { "afsk1200-multi" };
        int? rate = null, pairs = null, channel = null;
        double? centre = null, length = null;
        double skip = 0;
        bool frames = false, quiet = false;
        string? json = null, baseline = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                tracks.Add(argument);
                continue;
            }

            bool NeedsValue(out string value)
            {
                if (i + 1 < args.Length)
                {
                    value = args[++i];
                    return true;
                }

                value = "";
                problem = $"{argument} needs a value";
                return false;
            }

            switch (argument)
            {
                case "--mode" or "--modes":
                    if (!NeedsValue(out string modeList))
                    {
                        return (null, problem);
                    }

                    modes = [.. modeList.Split(',', StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)];
                    break;

                case "--rate":
                    if (!NeedsValue(out string rateText))
                    {
                        return (null, problem);
                    }

                    if (!int.TryParse(rateText, out int parsedRate) || parsedRate <= 0)
                    {
                        problem = $"--rate wants a sample rate in Hz, not '{rateText}'";
                        return (null, problem);
                    }

                    rate = parsedRate;
                    break;

                case "--centre" or "--center":
                    if (!NeedsValue(out string centreText))
                    {
                        return (null, problem);
                    }

                    if (!double.TryParse(centreText, out double parsedCentre))
                    {
                        problem = $"--centre wants a frequency in Hz, not '{centreText}'";
                        return (null, problem);
                    }

                    centre = parsedCentre;
                    break;

                case "--pairs":
                    if (!NeedsValue(out string pairsText))
                    {
                        return (null, problem);
                    }

                    if (!int.TryParse(pairsText, out int parsedPairs) || parsedPairs < 0)
                    {
                        problem = $"--pairs wants a count, not '{pairsText}'";
                        return (null, problem);
                    }

                    pairs = parsedPairs;
                    break;

                case "--channel":
                    if (!NeedsValue(out string channelText))
                    {
                        return (null, problem);
                    }

                    if (!int.TryParse(channelText, out int parsedChannel) || parsedChannel < 0)
                    {
                        problem = $"--channel wants a channel index, not '{channelText}'";
                        return (null, problem);
                    }

                    channel = parsedChannel;
                    break;

                case "--skip":
                    if (!NeedsValue(out string skipText))
                    {
                        return (null, problem);
                    }

                    if (!double.TryParse(skipText, out skip) || skip < 0)
                    {
                        problem = $"--skip wants seconds, not '{skipText}'";
                        return (null, problem);
                    }

                    break;

                case "--seconds":
                    if (!NeedsValue(out string lengthText))
                    {
                        return (null, problem);
                    }

                    if (!double.TryParse(lengthText, out double parsedLength) || parsedLength <= 0)
                    {
                        problem = $"--seconds wants seconds, not '{lengthText}'";
                        return (null, problem);
                    }

                    length = parsedLength;
                    break;

                case "--frames":
                    frames = true;
                    break;

                case "--json":
                    if (!NeedsValue(out string jsonPath))
                    {
                        return (null, problem);
                    }

                    json = jsonPath;
                    break;

                case "--baseline":
                    if (!NeedsValue(out string baselinePath))
                    {
                        return (null, problem);
                    }

                    baseline = baselinePath;
                    break;

                case "--quiet":
                    quiet = true;
                    break;

                default:
                    return (null, $"unknown option '{argument}'");
            }
        }

        if (tracks.Count == 0)
        {
            return (null, "no tracks given");
        }

        if (modes.Count == 0)
        {
            return (null, "--mode was given nothing to run");
        }

        return (new Options
        {
            Tracks = tracks,
            Modes = modes,
            DspRate = rate,
            CentreHz = centre,
            OffsetPairs = pairs,
            Channel = channel,
            SkipSeconds = skip,
            LengthSeconds = length,
            ListFrames = frames,
            JsonPath = json,
            Baseline = baseline,
            Quiet = quiet,
        }, null);
    }
}
