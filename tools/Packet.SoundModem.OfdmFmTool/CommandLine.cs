using System.Globalization;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>The three commands.</summary>
internal enum Verb
{
    Listen,
    Send,
    Ping,
}

/// <summary>What the command line asked for. Addresses are upper case, as they go on the air.</summary>
internal sealed record ToolOptions
{
    public required Verb Verb { get; init; }

    public required string Device { get; init; }

    public string? Ptt { get; init; }

    public required string Call { get; init; }

    public string? To { get; init; }

    public string Text { get; init; } = "";

    public int Count { get; init; } = DefaultCount;

    public int Size { get; init; } = DefaultSize;

    public bool Detail { get; init; }

    public string? LogFile { get; init; }

    public string? RecordFile { get; init; }

    public const int DefaultCount = 5;

    public const int DefaultSize = 32;
}

/// <summary>The outcome of reading the command line: the options, the help text wanted, or one line saying what is wrong.</summary>
internal sealed record ParseResult(ToolOptions? Options, bool Help, string? Error)
{
    public static ParseResult Ok(ToolOptions options) => new(options, false, null);

    public static ParseResult ShowHelp() => new(null, true, null);

    public static ParseResult Fail(string error) => new(null, false, error);
}

/// <summary>Reads the command line. Pure: nothing is opened and nothing is printed.</summary>
internal static class CommandLine
{
    private const string SeeHelp = ": see pdn-ofdm --help";

    private static readonly string[] ValueOptions = ["--device", "--ptt", "--call", "--to", "--count", "--size", "--log", "--record"];

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            return ParseResult.ShowHelp();
        }

        Verb verb;
        switch (args[0])
        {
            case "listen":
                verb = Verb.Listen;
                break;
            case "send":
                verb = Verb.Send;
                break;
            case "ping":
                verb = Verb.Ping;
                break;
            default:
                return ParseResult.Fail($"unknown command {args[0]}{SeeHelp}");
        }

        var values = new Dictionary<string, string>();
        bool detail = false;
        var words = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                words.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg == "--help" || arg == "-h")
            {
                return ParseResult.ShowHelp();
            }

            if (arg == "--detail")
            {
                detail = true;
            }
            else if (ValueOptions.Contains(arg))
            {
                if (i + 1 >= args.Count)
                {
                    return ParseResult.Fail($"{arg} needs a value{SeeHelp}");
                }

                if (!values.TryAdd(arg, args[++i]))
                {
                    return ParseResult.Fail($"{arg} given twice");
                }
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Fail($"unknown option {arg}{SeeHelp}");
            }
            else if (verb == Verb.Send)
            {
                words.Add(arg);
            }
            else
            {
                return ParseResult.Fail($"unexpected argument {arg}{SeeHelp}");
            }
        }

        foreach (string option in values.Keys)
        {
            bool applies = option switch
            {
                "--to" => verb != Verb.Listen,
                "--count" or "--size" => verb == Verb.Ping,
                _ => true,
            };
            if (!applies)
            {
                return ParseResult.Fail($"{option} does not apply to {args[0]}{SeeHelp}");
            }
        }

        if (!values.TryGetValue("--device", out string? device))
        {
            return ParseResult.Fail($"--device is needed{SeeHelp}");
        }

        if (!values.TryGetValue("--call", out string? call))
        {
            return ParseResult.Fail($"--call is needed{SeeHelp}");
        }

        call = call.ToUpperInvariant();
        if (!ValidCall(call))
        {
            return ParseResult.Fail("--call must be 1 to 16 characters of A-Z, 0-9, / and -");
        }

        values.TryGetValue("--ptt", out string? ptt);
        string? to = null;
        if (verb != Verb.Listen)
        {
            if (ptt is null)
            {
                return ParseResult.Fail($"--ptt is needed{SeeHelp}");
            }

            if (!values.TryGetValue("--to", out to))
            {
                return ParseResult.Fail($"--to is needed{SeeHelp}");
            }

            to = to.ToUpperInvariant();
            if (to.Length is < 1 or > 32 || to.Any(c => c is < '!' or > '~'))
            {
                return ParseResult.Fail("--to must be 1 to 32 printable characters, no spaces");
            }
        }

        string text = "";
        if (verb == Verb.Send)
        {
            text = string.Join(' ', words);
            if (text.Length == 0)
            {
                return ParseResult.Fail($"no text to send{SeeHelp}");
            }
        }

        int count = ToolOptions.DefaultCount;
        int size = ToolOptions.DefaultSize;
        if (values.TryGetValue("--count", out string? countText) && !TryWhole(countText, 1, 1000, out count))
        {
            return ParseResult.Fail("--count needs a whole number from 1 to 1000");
        }

        if (values.TryGetValue("--size", out string? sizeText) && !TryWhole(sizeText, Echo.HeaderSize, 4000, out size))
        {
            return ParseResult.Fail($"--size needs a whole number from {Echo.HeaderSize} to 4000");
        }

        return ParseResult.Ok(new ToolOptions
        {
            Verb = verb,
            Device = device,
            Ptt = ptt,
            Call = call,
            To = to,
            Text = text,
            Count = count,
            Size = size,
            Detail = detail,
            LogFile = values.GetValueOrDefault("--log"),
            RecordFile = values.GetValueOrDefault("--record"),
        });
    }

    private static bool ValidCall(string call) =>
        call.Length is >= 1 and <= 16 && call.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '/' or '-');

    private static bool TryWhole(string text, int low, int high, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= low && value <= high;
}
