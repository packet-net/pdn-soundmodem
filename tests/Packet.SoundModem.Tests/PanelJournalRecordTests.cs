using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// The panel's half of #473's rule. <c>WaterfallWebServer.ReportFrame</c> and
/// <c>ReportTransmittedFrame</c> exist for exactly one reason: a frame heard or sent by something
/// that is not one of the channel's own sub-channel modems, and so raises none of the events
/// <c>StationFactory.JournalReceivedFrames</c> listens to. #478 found ARDOP calling the first of
/// those on every receive while the journal said nothing about any of them - not a missed
/// upgrade, no released version had ever journalled an ARDOP frame. The rule this pins is the
/// mirror of <see cref="TransmitRecordTests"/>: whatever calls one of these two methods has to
/// say, in an allowlist entry here, how the same frame also reaches the journal, or the next
/// thing that reports itself to the panel this way is silent in journalctl again.
/// </summary>
/// <remarks>
/// <para>Asserted against the source text, like <see cref="TransmitRecordTests"/> and <see
/// cref="SourceTextTests"/>, because the hole it closes is a silence rather than a behaviour: a
/// missing <c>stationJournal.Write</c> produces no exception and no failing assertion anywhere
/// else, only a gap an operator finds by diffing <c>journalctl</c> against the panel weeks
/// later.</para>
/// <para>This does not check that the named journal call actually runs, only that somebody wrote
/// down that it does and where - the same level of rigour <see cref="TransmitRecordTests"/> holds
/// its own allowlist to. A test that inspected control flow to prove the journal write is
/// reachable from every path through a handler would be more thorough and also the fragile,
/// over-fitted kind of test that breaks on every unrelated refactor of the method it is guarding;
/// this is the same trade that test already makes.</para>
/// </remarks>
public class PanelJournalRecordTests
{
    private static readonly string[] ProductionRoots = ["src", "tools", "web"];

    /// <summary>Where the two methods are declared - not a call site.</summary>
    private const string DeclaringFile = "src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs";

    /// <summary>
    /// Every call that reports a frame the channel's own modems did not raise an event for, and
    /// how that same frame reaches the journal. One entry per call site.
    /// </summary>
    private static readonly Allowed[] Allowlist =
    [
        new("src/Packet.SoundModem.Daemon/Program.cs", "ReportFrame", "ardopSub, frame.Name",
            "ARDOP receives: journalled with stationJournal.Write(ActivityLog.ArdopReceived(...)) "
            + "a few lines above, in the same ardopTnc.FrameDecoded handler (#478)"),

        new("src/Packet.SoundModem.Daemon/Program.cs", "ReportTransmittedFrame",
            "ardopSub, frame.Name, from, to, data.Length",
            "ARDOP transmissions: journalled with "
            + "stationJournal.Write(ActivityLog.ArdopTransmitted(...)) a few lines above, in the "
            + "same ardopTnc.FrameTransmitted handler (#478)"),

        new("src/Packet.SoundModem.Daemon/Program.cs", "ReportTransmittedFrame",
            "serviceTxSubChannel, pagingMode",
            "POCSAG paging: Console.WriteLine($\"page[{page.Id}] to {page.Ric} sent...\") two "
            + "lines above, in the same pagingServer.PageSent handler (#473)"),

        new("src/Packet.SoundModem.Daemon/Program.cs", "ReportTransmittedFrame",
            "IdentTransmissionMode",
            "the CW ident: Console.WriteLine($\"id[{sub}] {owed.Text} in CW\") a few lines above, "
            + "on the same successful-transmit path (#473)"),
    ];

    [Fact]
    public void Every_Panel_Only_Report_Says_How_It_Reaches_The_Journal()
    {
        var unlisted = new List<string>();

        foreach (CallSite call in ReportCallSites())
        {
            if (!Allowlist.Any(a => Matches(a, call)))
            {
                unlisted.Add(
                    $"{call.File}:{call.Line} calls WaterfallWebServer.{call.Method}, which exists "
                    + "for a frame heard or sent by something that is not one of the channel's own "
                    + "modems - so nothing journals it unless this call site does. Write a "
                    + "stationJournal.Write(...) line beside it and add an entry to "
                    + "PanelJournalRecordTests.Allowlist: new(\"" + call.File + "\", \"" + call.Method
                    + "\", \"<text unique to this call>\", \"<how it reaches the journal>\"). If it "
                    + "genuinely must not be journalled, the entry is still how you say so, and why");
            }
        }

        unlisted.Should().BeEmpty(
            "journalctl is the one view of a running station most operators have, and a frame "
            + "listed on the panel and never mentioned there is the #478 gap all over again");
    }

    [Fact]
    public void The_Allowlist_Describes_Call_Sites_That_Still_Exist()
    {
        CallSite[] calls = [.. ReportCallSites()];
        var stale = new List<string>();

        foreach (Allowed entry in Allowlist)
        {
            int matched = calls.Count(c => Matches(entry, c));
            if (matched == 0)
            {
                stale.Add(
                    $"{entry.File}: nothing there calls WaterfallWebServer.{entry.Method} with "
                    + $"\"{entry.Call}\" in its arguments any more. If the call site has gone, "
                    + "delete this entry; if it only moved or was renamed, update it");
            }
            else if (matched > 1)
            {
                stale.Add(
                    $"{entry.File}: \"{entry.Call}\" matches {matched} {entry.Method} call sites, "
                    + "so one entry is standing in for several. Pick text unique to each");
            }

            if (entry.How.Trim().Length == 0)
            {
                stale.Add(
                    $"{entry.File}: \"{entry.Call}\" is listed without saying how it reaches the "
                    + "journal, which is the whole content of an entry - an allowlist of bare "
                    + "call sites is a list of exemptions");
            }
        }

        stale.Should().BeEmpty("an allowlist nobody prunes stops being a list of decisions");
    }

    /// <summary>A call site that must be able to state how its transmission is recorded, and how
    /// that is asserted here.</summary>
    /// <param name="File">Repository-relative path, forward slashes.</param>
    /// <param name="Method">Which of the two panel-only methods.</param>
    /// <param name="Call">Text unique to that call, matched inside the call's own parentheses.</param>
    /// <param name="How">How the transmission reaches the journal. For a reader, not the test.</param>
    private sealed record Allowed(string File, string Method, string Call, string How);

    /// <summary>One call to a panel-only report method: where it is, which method, and its
    /// arguments.</summary>
    private sealed record CallSite(string File, int Line, string Method, string Code);

    private static bool Matches(Allowed entry, CallSite call) =>
        call.File == entry.File && call.Method == entry.Method
        && call.Code.Contains(entry.Call, StringComparison.Ordinal);

    private static IEnumerable<CallSite> ReportCallSites()
    {
        string repo = FindRepoRoot();
        foreach (string file in ProductionSources(repo))
        {
            string relative = Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == DeclaringFile)
            {
                continue;   // the definitions, not a call
            }

            string text = File.ReadAllText(file);
            foreach (string method in new[] { "ReportFrame", "ReportTransmittedFrame" })
            {
                foreach ((int line, string code) in CallsIn(text, method))
                {
                    yield return new CallSite(relative, line, method, code);
                }
            }
        }
    }

    /// <summary>
    /// Each call's line number and the text between its parentheses, comments and string
    /// contents removed. A mention in a comment or a doc <c>&lt;see cref&gt;</c> is not a call
    /// and is skipped.
    /// </summary>
    private static IEnumerable<(int Line, string Code)> CallsIn(string text, string method)
    {
        string name = method + "(";
        var found = new List<(int, string)>();

        for (int at = text.IndexOf(name, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int lineStart = text.LastIndexOf('\n', at) + 1;
            string before = text[lineStart..at];

            if (before.Contains("//", StringComparison.Ordinal)
                || before.TrimStart().StartsWith('*'))
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
    /// and the contents of literals dropped - so a comma or a paren inside a comment or a string
    /// is not read as argument structure.
    /// </summary>
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
