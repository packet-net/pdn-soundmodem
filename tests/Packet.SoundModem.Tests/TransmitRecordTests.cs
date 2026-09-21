using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// Everything this station puts on the air has to be written down, and the rule is asserted
/// against the source text because the hole it closes is a silence rather than a behaviour.
/// </summary>
/// <remarks>
/// <para><see cref="Channel.SoundModemChannel"/> raises <c>FrameTransmitted</c> only from the
/// byte-frame overload of <c>EnqueueTransmit</c>. The other overload takes rendered audio and
/// raises nothing, so a transmitter that goes through it leaves no trace anywhere unless its
/// author remembers to write one: that is not three separate oversights but one rule, and it
/// predicted all three of the gaps issue #473 found (ARDOP, the CW ident, POCSAG paging). The
/// raw capture does not cover for it either - the receiver is muted while transmitting, so a
/// station's own bursts are absent from that as well, and the frame log is the only place they
/// can ever appear.</para>
/// <para>The same trick as <see cref="SourceTextTests"/> and <see cref="StartUpOrderTests"/>, for
/// the same reason: a new audio-path transmitter that records nothing is invisible in CI, on the
/// bench and on the air, and is noticed only when somebody asks what the station transmitted and
/// the answer is an empty table. Listing the call sites makes adding one a deliberate decision
/// and a one-line entry instead.</para>
/// <para>Production code only. A test that transmits is not a station transmitting, and the
/// suite's own call sites are the reason this reads <c>src</c>, <c>tools</c> and <c>web</c>
/// rather than the whole tree.</para>
/// </remarks>
public class TransmitRecordTests
{
    /// <summary>Where a station's own code lives. Tests are deliberately not included.</summary>
    private static readonly string[] ProductionRoots = ["src", "tools", "web"];

    /// <summary>
    /// Every call that hands <see cref="Channel.SoundModemChannel"/> audio rather than a frame,
    /// and how that transmission reaches the frame log. One entry per call site.
    /// </summary>
    /// <remarks>
    /// <c>Call</c> is any text unique to that call - the <c>source:</c> argument is usually the
    /// clearest, since each of these transmitters passes its own identity. It is matched against
    /// the call's own text, so it survives the lines above and below it moving.
    /// </remarks>
    private static readonly Allowed[] Allowlist =
    [
        new("src/Packet.SoundModem/Channel/SoundModemChannel.cs",
            "rejection => TransmitRejected?.Invoke",
            "the byte-frame overload's own call: it raises FrameTransmitted and "
            + "FrameTransmittedWithTrim once the burst has gone out, which is what the daemon "
            + "writes every modem's traffic down from (Program.cs, channel.FrameTransmittedWithTrim)"),

        new("src/Packet.SoundModem/Channel/SoundModemChannel.cs",
            "noted: null",
            "not a transmitter at all: the published EnqueueTransmit handing straight to the "
            + "private overload that also carries the wait breakdown. Whatever it is passed is "
            + "recorded wherever its own caller records it, which is the entry above for the "
            + "byte-frame path and one of the entries below for everything else"),

        new("src/Packet.SoundModem/Pocsag/PagingTcpServer.cs",
            "source: _encoder",
            "POCSAG paging: raises PageSent once the page has actually gone out, and the daemon "
            + "records it there (Program.cs, pagingServer.PageSent)"),

        new("src/Packet.SoundModem.Daemon/Program.cs",
            "source: ardopShift",
            "ARDOP: only the virtual TNC knows what the burst was, so the record is written from "
            + "its own FrameTransmitted event (Program.cs, ardopTnc.FrameTransmitted), not here"),

        new("src/Packet.SoundModem.Daemon/Program.cs",
            "source: owed",
            "the CW ident: recorded beside owed.NoteIdentified(), with the ident text as the "
            + "payload because an ident is not a frame"),

        new("src/Packet.SoundModem.Daemon/TxTest.cs",
            "source: this",
            "the operator's test tone: TxTestOptions.Recorded, which Program.cs turns into "
            + "frameLog.RecordTransmitted with the sentence describing what went out"),
    ];

    [Fact]
    public void Every_Transmitter_That_Hands_The_Channel_Audio_Says_How_It_Reaches_The_Frame_Log()
    {
        var unlisted = new List<string>();

        foreach (CallSite call in AudioTransmitCallSites())
        {
            if (!Allowlist.Any(a => Matches(a, call)))
            {
                unlisted.Add(
                    $"{call.File}:{call.Line} hands the channel audio rather than a frame, so the "
                    + "channel raises nothing for it and this transmission is written down "
                    + "nowhere. Record it - FrameLog.RecordTransmitted, the way the CW ident and "
                    + "a POCSAG page do in Program.cs, once the burst has actually gone out and "
                    + "not when it was queued - and then add a line to "
                    + "TransmitRecordTests.Allowlist: new(\"" + call.File + "\", \"<text unique "
                    + "to this call, e.g. its source: argument>\", \"<how it reaches the frame "
                    + "log>\"). If it genuinely must not be recorded, the entry is still how you "
                    + "say so, and why");
            }
        }

        unlisted.Should().BeEmpty(
            "a station has to be able to answer \"what did I put on the air, and when\" from its "
            + "own frame log (issue #473)");
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("Task<bool>")]
    public void Queue_Method_Declarations_Are_Not_Calls_But_Their_Bodies_Are_Scanned(string returnType)
    {
        string source = $$"""
            private {{returnType}} EnqueueTransmitCore(Func<int, float[]> modulate)
            {
                return EnqueueTransmitCore(modulate, source: this);
            }
            """;

        var call = CallsIn(source).Should().ContainSingle().Which;
        call.Line.Should().Be(3);
        call.Code.Should().Contain("source: this");
    }

    [Fact]
    public void The_Allowlist_Describes_Call_Sites_That_Still_Exist()
    {
        CallSite[] calls = [.. AudioTransmitCallSites()];
        var stale = new List<string>();

        foreach (Allowed entry in Allowlist)
        {
            int matched = calls.Count(c => Matches(entry, c));
            if (matched == 0)
            {
                stale.Add(
                    $"{entry.File}: nothing in that file calls EnqueueTransmit with audio and the "
                    + $"text \"{entry.Call}\" in it any more. If the transmitter has gone, delete "
                    + "this entry; if it only moved or was renamed, update the entry");
            }
            else if (matched > 1)
            {
                stale.Add(
                    $"{entry.File}: \"{entry.Call}\" matches {matched} call sites, so one entry is "
                    + "standing in for several transmitters. Pick text unique to each");
            }

            if (entry.How.Trim().Length == 0)
            {
                stale.Add(
                    $"{entry.File}: \"{entry.Call}\" is listed without saying how the "
                    + "transmission reaches the frame log, which is the whole content of an "
                    + "entry - an allowlist of bare paths is a list of exemptions");
            }
        }

        stale.Should().BeEmpty("an allowlist nobody prunes stops being a list of decisions");
    }

    /// <summary>
    /// A call site that must be able to state how its transmission is recorded, and how that is
    /// asserted here.
    /// </summary>
    /// <param name="File">Repository-relative path, forward slashes.</param>
    /// <param name="Call">Text unique to that call, matched inside the call's own parentheses.</param>
    /// <param name="How">How the transmission reaches the frame log. For a reader, not the test.</param>
    private sealed record Allowed(string File, string Call, string How);

    /// <summary>One call to the audio overload: where it is, and its text.</summary>
    private sealed record CallSite(string File, int Line, string Code);

    private static bool Matches(Allowed entry, CallSite call) =>
        call.File == entry.File && call.Code.Contains(entry.Call, StringComparison.Ordinal);

    /// <summary>
    /// Every call to <c>EnqueueTransmit</c> in production code that hands over audio rather than a
    /// frame.
    /// </summary>
    /// <remarks>
    /// A call whose shape says neither is reported as an audio one rather than skipped: an
    /// unrecognised call is exactly the case where somebody should be made to say what they meant,
    /// and silently ignoring it would put the hole back.
    /// </remarks>
    private static IEnumerable<CallSite> AudioTransmitCallSites()
    {
        string repo = FindRepoRoot();
        foreach (string file in ProductionSources(repo))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');

            foreach ((int line, string code) in CallsIn(text))
            {
                if (!IsFrameCall(code))
                {
                    yield return new CallSite(relative, line, code);
                }
            }
        }
    }

    /// <summary>
    /// The byte-frame overload: two positional arguments, neither of them a lambda. Everything
    /// else is treated as an audio transmission, including a call this cannot make sense of.
    /// </summary>
    private static bool IsFrameCall(string code)
    {
        string[] arguments = SplitArguments(code);
        return arguments.Length == 2
               && !code.Contains("=>", StringComparison.Ordinal)
               && arguments.All(a => !a.Contains(':', StringComparison.Ordinal));
    }

    /// <summary>
    /// Each call's line number and the text between its parentheses, comments and string
    /// contents removed. Declarations and mentions in comments are not calls and are skipped.
    /// </summary>
    private static IEnumerable<(int Line, string Code)> CallsIn(string text)
    {
        var found = new List<(int, string)>();
        foreach (string name in TransmitEntryPoints)
        {
            found.AddRange(CallsIn(text, name));
        }

        found.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return found;
    }

    /// <summary>
    /// Both ways into the channel's transmit queue. <c>EnqueueTransmitCore</c> is the private one
    /// the published overload hands to, and it is scanned as well because a transmitter added
    /// inside this assembly would reach for it and would otherwise never be asked how it writes
    /// itself down. Neither string matches the other: the character after "Transmit" differs.
    /// </summary>
    private static readonly string[] TransmitEntryPoints = ["EnqueueTransmit(", "EnqueueTransmitCore("];

    private static IEnumerable<(int Line, string Code)> CallsIn(string text, string name)
    {
        var found = new List<(int, string)>();

        for (int at = text.IndexOf(name, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int lineStart = text.LastIndexOf('\n', at) + 1;
            string before = text[lineStart..at];

            // A cref in a doc comment, a sentence in a block comment, and the two declarations
            // themselves: none of them transmits anything.
            if (before.Contains("//", StringComparison.Ordinal)
                || before.TrimStart().StartsWith('*')
                || before.TrimEnd().EndsWith("Task", StringComparison.Ordinal)
                || before.TrimEnd().EndsWith("Task<bool>", StringComparison.Ordinal))
            {
                continue;
            }

            int line = 1;
            for (int i = 0; i < at; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            found.Add((line, CodeInsideParentheses(text, at + name.Length)));
        }

        return found;
    }

    /// <summary>
    /// The argument text of a call whose opening parenthesis has just been passed, with comments
    /// and the contents of literals dropped.
    /// </summary>
    /// <remarks>
    /// Dropping them is not tidiness: these call sites are heavily commented, an apostrophe in
    /// one of those comments would otherwise open a character literal that swallows the rest of
    /// the call, and a comma or bracket inside a comment would be read as argument structure.
    /// </remarks>
    private static string CodeInsideParentheses(string text, int start)
    {
        var code = new System.Text.StringBuilder();
        int depth = 1;

        for (int i = start; i < text.Length && depth > 0; i++)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                for (i += 2; i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'); i++)
                {
                }

                i++;
            }
            else if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }
            }
            else
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')' && --depth == 0)
                {
                    break;
                }

                code.Append(c);
            }
        }

        return code.ToString();
    }

    /// <summary>The call's arguments, split on commas outside brackets.</summary>
    private static string[] SplitArguments(string code)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        int depth = 0;

        foreach (char c in code)
        {
            // Parentheses and brackets only. A lambda arrow would make nonsense of counting
            // angle brackets, and a generic argument with a comma in it here would simply split
            // into more arguments than there are, which lands on the safe side: the call stops
            // looking like the byte-frame overload and somebody is asked to say what it is.
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }

            if (c == ',' && depth == 0)
            {
                arguments.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return [.. arguments];
    }

    private static IEnumerable<string> ProductionSources(string repo)
    {
        char sep = Path.DirectorySeparatorChar;

        foreach (string root in ProductionRoots)
        {
            string directory = Path.Combine(repo, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(repo, file);
                if (!relative.Contains($"{sep}bin{sep}") && !relative.Contains($"{sep}obj{sep}"))
                {
                    yield return file;
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
