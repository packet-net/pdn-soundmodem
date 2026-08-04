using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// House rules about the characters this repository is written with.
/// </summary>
/// <remarks>
/// Two rules, two reasons. Anything the daemon prints has to be ASCII, because journalctl pipes
/// through `less` and `less` under a C/POSIX locale renders every byte above 0x7F as
/// `&lt;E2&gt;&lt;80&gt;&lt;94&gt;`; that locale is the default on a minimal Debian install and
/// is what systemd hands the pager when LANG is unset, so it is what a station operator sees.
/// Separately, no file here uses an em or en dash, in code or prose: it is not Tom's punctuation,
/// and every one that had accumulated came from an agent rather than from him.
/// </remarks>
public class SourceTextTests
{
    /// <summary>Extensions we author. Anything else in the tree is data, not text we write.</summary>
    private static readonly string[] Authored =
        [".cs", ".md", ".sh", ".py", ".yml", ".yaml", ".csproj", ".props", ".slnx",
         ".mjs", ".json", ".html", ".svg", ".service"];

    /// <summary>
    /// Records, not prose: run evidence is what the run actually emitted, and `docs/refs` holds
    /// verbatim transcriptions of other people's specifications. Rewriting either would be
    /// falsifying it, so both keep whatever characters they were written with.
    /// </summary>
    private static readonly string[] Frozen =
        [Path.Combine("docs", "ms110d", "evidence"), Path.Combine("docs", "refs")];

    [Fact]
    public void No_String_The_Daemon_Can_Print_Carries_A_Byte_Above_Ascii()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles("src", ".cs"))
        {
            foreach ((int line, char c) in NonAsciiInLiterals(File.ReadAllText(file)))
            {
                offenders.Add($"{Relative(file)}:{line}: U+{(int)c:X4} '{c}'");
            }
        }

        offenders.Should().BeEmpty(
            "journalctl's pager renders non-ASCII as <XX> hex escapes under a C locale, so "
            + "printable output has to be ASCII (use - for a dash, -> for an arrow)");
    }

    [Fact]
    public void No_File_In_The_Repository_Uses_An_Em_Dash_Or_An_En_Dash()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles(".", Authored))
        {
            int line = 1;
            foreach (char c in File.ReadAllText(file))
            {
                if (c == '\n') line++;
                // Escaped, not literal: this file has to obey the rule it enforces.
                else if (c is '\u2014' or '\u2013') offenders.Add($"{Relative(file)}:{line}");
            }
        }

        offenders.Should().BeEmpty(
            "em and en dashes are not the house style: use a hyphen, a comma or a full stop");
    }

    /// <summary>Every file we author under <paramref name="root"/>, skipping build output and records.</summary>
    private static IEnumerable<string> SourceFiles(string root, params string[] extensions)
    {
        string repo = FindRepoRoot();
        char sep = Path.DirectorySeparatorChar;

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(repo, root), "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = Path.GetRelativePath(repo, file);

            // Dot-directories are tooling, not source: `.git`, and `.claude`, which is where
            // agent worktrees land and would otherwise be judged as if they were this checkout.
            // `.github` is ours and is checked.
            if (relative.Contains($"{sep}bin{sep}") || relative.Contains($"{sep}obj{sep}")
                || (relative.StartsWith('.') && !relative.StartsWith(".github", StringComparison.Ordinal))
                || Frozen.Any(f => relative.StartsWith(f, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    /// Every non-ASCII character inside a string or char literal, with its line number. Comments
    /// are skipped, which is why this parses the source rather than grepping it: maths notation
    /// (sigma, pi, plus-minus) is welcome in DSP prose and none of it is ever printed.
    /// </summary>
    private static IEnumerable<(int Line, char Char)> NonAsciiInLiterals(string text)
    {
        var found = new List<(int, char)>();
        int i = 0, line = 1;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '/' && Next(i) == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && Next(i) == '*')
            {
                for (i += 2; i < text.Length && !(text[i] == '*' && Next(i) == '/'); i++)
                {
                    if (text[i] == '\n') line++;
                }

                i += 2;
            }
            else if (Matches(i, "\"\"\""))
            {
                for (i += 3; i < text.Length && !Matches(i, "\"\"\""); i++) Take();
                i += 3;
            }
            else if (c == '@' && Next(i) == '"')
            {
                for (i += 2; i < text.Length; i++)
                {
                    if (text[i] == '"' && Next(i) != '"') { i++; break; }
                    if (text[i] == '"') i++;          // "" is an escaped quote, not the end
                    Take();
                }
            }
            else if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c && text[i] != '\n'; i++)
                {
                    if (text[i] == '\\') { i++; continue; }
                    Take();
                }

                i++;
            }
            else
            {
                i++;
            }

            void Take()
            {
                if (i >= text.Length) return;
                if (text[i] == '\n') line++;
                else if (text[i] > 127) found.Add((line, text[i]));
            }
        }

        return found;

        char Next(int at) => at + 1 < text.Length ? text[at + 1] : '\0';
        bool Matches(int at, string s) => at + s.Length <= text.Length
                                          && string.CompareOrdinal(text, at, s, 0, s.Length) == 0;
    }

    private static string Relative(string file) => Path.GetRelativePath(FindRepoRoot(), file);

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
