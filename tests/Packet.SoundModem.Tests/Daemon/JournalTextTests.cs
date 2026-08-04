using System.Text;
using AwesomeAssertions;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Everything the daemon can print is plain ASCII.
/// </summary>
/// <remarks>
/// The journal is read through a pager, and `less` under a C/POSIX locale — the default on a
/// minimal Debian install, and what systemd hands journalctl when LANG is unset — renders any
/// byte above 0x7F as `&lt;E2&gt;&lt;80&gt;&lt;94&gt;`. A line that reads "id beacons — listening"
/// on the developer's terminal reads "id beacons &lt;E2&gt;&lt;80&gt;&lt;94&gt; listening" on the
/// station's. We cannot fix every reader's locale, so the writer stays inside ASCII: `-` for a
/// dash, `-&gt;` for an arrow, `,` for a separator. Prose in comments is unaffected — this only
/// pins the strings that can reach a terminal.
/// </remarks>
public class JournalTextTests
{
    [Fact]
    public void No_String_The_Daemon_Can_Print_Carries_A_Byte_Above_Ascii()
    {
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach ((int line, char c) in NonAsciiInLiterals(File.ReadAllText(file)))
            {
                offenders.Add(
                    $"{Path.GetRelativePath(FindRepoRoot(), file)}:{line}: U+{(int)c:X4} '{c}'");
            }
        }

        offenders.Should().BeEmpty(
            "journalctl's pager renders non-ASCII as <XX> hex escapes under a C locale, so "
            + "printable output has to be ASCII (use - for a dash, -> for an arrow)");
    }

    /// <summary>
    /// Every non-ASCII character inside a string or char literal, with its line number. Comments
    /// are skipped, which is why this walks the source rather than grepping it: the codebase is
    /// full of em dashes and maths notation in prose, and none of that is ever printed.
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
