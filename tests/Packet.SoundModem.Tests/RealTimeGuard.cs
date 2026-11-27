using System.Text;
using System.Text.RegularExpressions;

namespace Packet.SoundModem.Tests;

/// <summary>Which tree a file belongs to. Production code gets the full rule set; tests a strict subset.</summary>
internal enum GuardRoot
{
    Src,
    Tests,
}

/// <summary>One file that is allowed to use one real-time token, and why.</summary>
internal sealed record AllowEntry(string File, string Token, string Reason);

/// <summary>
/// Source-text scanner behind <see cref="RealTimeGuardTests"/>: finds real-time APIs in C# source
/// with comments and string literals blanked out, and compares them with an allow-list that must
/// not go stale. Pure text in, text out: nothing here reads a clock or the disk.
/// </summary>
internal static class RealTimeGuard
{
    /// <summary>The advice printed after every offender.</summary>
    public const string Advice = "use the injected TimeProvider";

    private sealed record Rule(
        string Token,
        Regex Pattern,
        bool InTests,
        Func<Match, IReadOnlyList<string>, string, bool>? Bad);

    private static readonly Regex TokenLike = new("token|cancel|stopping", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeLike = new("time|clock|provider", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceMadeWith =
        new(@"\b(\w+)\s*=\s*new\s+CancellationTokenSource\s*\(", RegexOptions.Compiled);

    private static readonly Rule[] Rules =
    [
        new("wall-clock",
            new(@"\b(?:DateTime|DateTimeOffset)\s*\.\s*(?:UtcNow|Now|Today)\b", RegexOptions.Compiled), true, null),
        new("Stopwatch", new(@"\bStopwatch\b", RegexOptions.Compiled), true, null),
        new("Environment.TickCount",
            new(@"\bEnvironment\s*\.\s*TickCount(?:64)?\b", RegexOptions.Compiled), true, null),
        new("Thread.Sleep", new(@"\bThread\s*\.\s*Sleep\s*\(", RegexOptions.Compiled), true, null),
        new("Timer",
            new(@"\bnew\s+(?:[\w.]+\.)?Timer\s*\(|\bSystem\s*\.\s*Timers\b", RegexOptions.Compiled), false, null),
        new("PeriodicTimer",
            new(@"\bnew\s+PeriodicTimer\s*\(", RegexOptions.Compiled), false, (_, a, _) => a.Count < 2),
        new("Task.Delay",
            new(@"\bTask\s*\.\s*Delay\s*\(", RegexOptions.Compiled), true,
            (_, a, _) => !(a.Count > 0 && a[0].TrimStart().StartsWith("Timeout.Infinite", StringComparison.Ordinal))
                         && !(a.Count >= 2 && Provider(a[1]))),
        new("CancelAfter",
            new(@"\b(\w+)\s*\.\s*CancelAfter\s*\(", RegexOptions.Compiled), false,
            (m, a, text) => !(a.Count > 0 && a[0].TrimStart().StartsWith("Timeout.Infinite", StringComparison.Ordinal))
                            && !MadeWithProvider(text, m.Groups[1].Value)),
        new("CancellationTokenSource(span)",
            new(@"\bnew\s+CancellationTokenSource\s*\(", RegexOptions.Compiled), false,
            (_, a, _) => a.Count == 1 || (a.Count >= 2 && !Provider(a[1]))),
        new("WaitAsync",
            new(@"\.\s*WaitAsync\s*\(", RegexOptions.Compiled), false,
            (_, a, _) => a.Count switch
            {
                0 => false,
                1 => !TokenLike.IsMatch(a[0]),
                _ => !Provider(a[1]),
            }),
        new("Monitor.Wait",
            new(@"\bMonitor\s*\.\s*Wait\s*\(", RegexOptions.Compiled), false,
            (_, a, _) => a.Count >= 2 && !Zero(a[1])),
        new("WaitOne",
            new(@"\.\s*WaitOne\s*\(", RegexOptions.Compiled), false,
            (_, a, _) => a.Count >= 1 && !Zero(a[0])),
        new("Wait(timeout)",
            new(@"\.\s*Wait\s*\(", RegexOptions.Compiled), false,
            (m, a, text) => a.Count >= 1 && !TokenLike.IsMatch(a[0]) && !Zero(a[0]) && !AfterMonitor(text, m.Index)),
        new("Join(timeout)",
            new(@"\.\s*Join\s*\(", RegexOptions.Compiled), false,
            (m, a, text) => a.Count >= 1 && !StringJoin(text, m.Index, a[0])),
        new("socket timeout",
            new(@"\.\s*(?:Read|Write|Receive|Send)Timeout\s*=(?!=)", RegexOptions.Compiled), false, null),
        new("SpinUntil",
            new(@"\bSpinWait\s*\.\s*SpinUntil\s*\(", RegexOptions.Compiled), false, (_, a, _) => a.Count >= 2),
    ];

    /// <summary>Every offence in one file's text: line number and token.</summary>
    public static IReadOnlyList<(int Line, string Token)> Scan(string source, GuardRoot root)
    {
        string text = Strip(source);
        var found = new List<(int, string)>();

        foreach (Rule rule in Rules)
        {
            if (root == GuardRoot.Tests && !rule.InTests) continue;

            foreach (Match m in rule.Pattern.Matches(text))
            {
                if (rule.Bad is not null)
                {
                    IReadOnlyList<string> args = Arguments(text, m.Index + m.Length);
                    if (!rule.Bad(m, args, text)) continue;
                }

                found.Add((LineOf(text, m.Index), rule.Token));
            }
        }

        found.Sort();
        return found;
    }

    /// <summary>
    /// One plain line per problem: each offender not on the allow-list, then each allow-list entry
    /// that no longer matches anything. Empty means clean. Keys of <paramref name="files"/> are
    /// repo-relative paths with forward slashes.
    /// </summary>
    public static IReadOnlyList<string> Check(
        IReadOnlyDictionary<string, string> files, GuardRoot root, IReadOnlyList<AllowEntry> allow)
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, HashSet<string>>();

        foreach ((string file, string source) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var tokens = new HashSet<string>();
            seen[file] = tokens;

            foreach ((int line, string token) in Scan(source, root))
            {
                tokens.Add(token);
                if (!allow.Any(e => e.File == file && e.Token == token))
                {
                    problems.Add($"{file}:{line}: {token}: {Advice}");
                }
            }
        }

        foreach (AllowEntry e in allow)
        {
            if (!seen.TryGetValue(e.File, out HashSet<string>? tokens))
            {
                problems.Add($"stale allow-list entry {e.File} {e.Token}: the file no longer exists; delete the entry");
            }
            else if (!tokens.Contains(e.Token))
            {
                problems.Add($"stale allow-list entry {e.File} {e.Token}: the file no longer uses it; delete the entry");
            }
        }

        return problems;
    }

    private static bool Provider(string arg)
    {
        string a = arg.Trim();
        return TimeLike.IsMatch(a) && !TokenLike.IsMatch(a) && a != "TimeProvider.System";
    }

    private static bool Zero(string arg) => arg.Trim() is "0" or "TimeSpan.Zero";

    private static bool AfterMonitor(string text, int dot)
    {
        int end = dot;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end >= 7 && string.CompareOrdinal(text, end - 7, "Monitor", 0, 7) == 0;
    }

    /// <summary>string.Join and Path.Join are not thread joins: judged by receiver and by a text separator.</summary>
    private static bool StringJoin(string text, int dot, string firstArg)
    {
        int end = dot;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        string receiver = text[start..end];
        string first = firstArg.TrimStart();
        return receiver is "string" or "String" or "Path"
               || first.StartsWith('"') || first.StartsWith('\'') || first.StartsWith("Environment.NewLine", StringComparison.Ordinal);
    }

    private static bool MadeWithProvider(string text, string receiver)
    {
        foreach (Match m in SourceMadeWith.Matches(text))
        {
            if (m.Groups[1].Value != receiver) continue;
            IReadOnlyList<string> a = Arguments(text, m.Index + m.Length);
            if (a.Count >= 2 && Provider(a[1])) return true;
        }

        return false;
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }

        return line;
    }

    /// <summary>Top-level arguments of the call whose opening parenthesis sits just before <paramref name="from"/>.</summary>
    private static IReadOnlyList<string> Arguments(string text, int from)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (current.ToString().Trim().Length > 0) args.Add(current.ToString().Trim());
                    return args;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            else if (c == ';' && depth == 0)
            {
                break;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0) args.Add(current.ToString().Trim());
        return args;
    }

    /// <summary>
    /// The source with comments and the insides of string and char literals replaced by spaces
    /// (newlines kept, so line numbers hold). Code inside interpolation holes is kept.
    /// </summary>
    internal static string Strip(string text)
    {
        var o = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && Next(i) == '/')
            {
                while (i < text.Length && text[i] != '\n') { o.Append(' '); i++; }
            }
            else if (c == '/' && Next(i) == '*')
            {
                o.Append("  ");
                for (i += 2; i < text.Length && !(text[i] == '*' && Next(i) == '/'); i++) o.Append(Blank(text[i]));
                if (i < text.Length) { o.Append("  "); i += 2; }
            }
            else if (RawStart(i, out int dollars, out int quotes))
            {
                string close = new('"', quotes);
                o.Append(text, i, dollars + quotes);
                for (i += dollars + quotes; i < text.Length && !Matches(i, close); i++) o.Append(Blank(text[i]));
                if (i < text.Length) { o.Append(close); i += quotes; }
            }
            else if (c == '"' || c == '@' && Next(i) == '"' || c == '$' && (Next(i) == '"' || Next(i) == '@' && At(i + 2) == '"')
                     || c == '@' && Next(i) == '$' && At(i + 2) == '"')
            {
                bool verbatim = false, interpolated = false;
                while (text[i] != '"')
                {
                    if (text[i] == '@') verbatim = true;
                    if (text[i] == '$') interpolated = true;
                    o.Append(text[i++]);
                }

                o.Append('"');
                i++;
                while (i < text.Length)
                {
                    char d = text[i];
                    if (d == '"')
                    {
                        if (verbatim && Next(i) == '"') { o.Append("  "); i += 2; continue; }
                        break;
                    }

                    if (!verbatim && d == '\\') { o.Append(' ').Append(Blank(At(i + 1))); i += 2; continue; }
                    if (!verbatim && d == '\n') break;   // unterminated: do not swallow the file
                    if (interpolated && d == '{')
                    {
                        if (Next(i) == '{') { o.Append("  "); i += 2; continue; }
                        int depth = 0;
                        for (; i < text.Length; i++)
                        {
                            if (text[i] == '{') depth++;
                            else if (text[i] == '}' && --depth == 0) { o.Append(text[i++]); break; }
                            o.Append(text[i]);
                        }

                        continue;
                    }

                    o.Append(Blank(d));
                    i++;
                }

                if (i < text.Length && text[i] == '"') { o.Append('"'); i++; }
            }
            else if (c == '\'')
            {
                o.Append('\'');
                for (i++; i < text.Length && text[i] != '\'' && text[i] != '\n'; i++)
                {
                    if (text[i] == '\\') { o.Append(' '); i++; }
                    if (i < text.Length) o.Append(Blank(text[i]));
                }

                if (i < text.Length && text[i] == '\'') { o.Append('\''); i++; }
            }
            else
            {
                o.Append(c);
                i++;
            }
        }

        return o.ToString();

        bool RawStart(int at, out int dollars, out int quotes)
        {
            dollars = 0;
            quotes = 0;
            int j = at;
            while (At(j) == '$') { dollars++; j++; }
            while (At(j) == '"') { quotes++; j++; }
            return quotes >= 3;
        }

        char Next(int at) => At(at + 1);
        char At(int at) => at < text.Length ? text[at] : '\0';
        bool Matches(int at, string s) => at + s.Length <= text.Length
                                          && string.CompareOrdinal(text, at, s, 0, s.Length) == 0;
        static char Blank(char ch) => ch == '\n' ? '\n' : ' ';
    }
}
