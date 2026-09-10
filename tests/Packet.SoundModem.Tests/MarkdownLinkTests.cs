using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// Every relative link in the repository's markdown points at something that exists.
/// </summary>
/// <remarks>
/// The documentation is plain markdown rendered by GitHub, with relative links between pages,
/// and source files cite documents by path. A file that moves takes every link to it with it,
/// and nothing else notices until a reader lands on a 404. This walks every markdown file we
/// author and resolves every relative link and image against the tree, so a move that leaves a
/// dangling link fails here rather than on GitHub. External links (http, https, mailto) and
/// same-page anchors are not checked; nor is <c>docs/dev/archive</c>, whose files are records
/// and are never edited, so a link in one of them says where a thing was at the time.
/// </remarks>
public partial class MarkdownLinkTests
{
    /// <summary>Records: never edited, so their links are left as written.</summary>
    private static readonly string[] Frozen = [Path.Combine("docs", "dev", "archive")];

    [Fact]
    public void Every_Relative_Link_In_The_Repository_Resolves_To_A_File_Or_Folder()
    {
        string repo = FindRepoRoot();
        var broken = new List<string>();

        foreach (string file in MarkdownFiles(repo))
        {
            string folder = Path.GetDirectoryName(file)!;

            foreach (string target in RelativeLinks(File.ReadAllText(file)))
            {
                string path = Uri.UnescapeDataString(target);
                string resolved = path.StartsWith('/')
                    ? Path.GetFullPath(Path.Combine(repo, path.TrimStart('/')))
                    : Path.GetFullPath(Path.Combine(folder, path));

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{Path.GetRelativePath(repo, file)}: {target}");
                }
            }
        }

        // The list goes in the reason: BeEmpty on its own reports one item and stops.
        broken.Should().BeEmpty(
            "every relative link and image in a markdown file has to point at a file or folder "
            + "that exists; if the target moved, move the link with it. Broken (file: link):{0}",
            string.Concat(broken.Select(b => Environment.NewLine + "  " + b)));
    }

    /// <summary>
    /// The target of every <c>[text](target)</c> and <c>![alt](target)</c> in <paramref name="markdown"/>
    /// that is a relative path: not a URL, not a mail address and not a same-page anchor. Any
    /// <c>#fragment</c> or <c>?query</c> is dropped. Links inside fenced code blocks and inline
    /// code spans are ignored, because there they are text, not links.
    /// </summary>
    private static IEnumerable<string> RelativeLinks(string markdown)
    {
        var targets = new List<string>();
        bool fenced = false;

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (Fence().IsMatch(line))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced) continue;

            foreach (Match m in Link().Matches(CodeSpan().Replace(line, " ")))
            {
                string target = m.Groups["target"].Value.Trim();
                if (target.StartsWith('<') && target.EndsWith('>')) target = target[1..^1];

                if (target.Length == 0
                    || target.StartsWith('#')
                    || target.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int cut = target.IndexOfAny(['#', '?']);
                if (cut >= 0) target = target[..cut];
                if (target.Length > 0) targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>Every markdown file we author, skipping build output, tooling directories and records.</summary>
    private static IEnumerable<string> MarkdownFiles(string repo)
    {
        char sep = Path.DirectorySeparatorChar;

        foreach (string file in Directory.EnumerateFiles(repo, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repo, file);

            if (relative.Contains($"{sep}bin{sep}") || relative.Contains($"{sep}obj{sep}")
                || relative.Contains($"{sep}node_modules{sep}") || relative.StartsWith("node_modules", StringComparison.Ordinal)
                || (relative.StartsWith('.') && !relative.StartsWith(".github", StringComparison.Ordinal))
                || Frozen.Any(f => relative.StartsWith(f, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return file;
        }
    }

    // A fence line: ``` or ~~~, optionally indented up to three spaces, optionally with an info string.
    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex Fence();

    // An inline code span: one or more backticks, anything, the same number of backticks.
    [GeneratedRegex(@"(`+)(.+?)\1")]
    private static partial Regex CodeSpan();

    // The (target) part of a markdown link or image, with an optional "title" after the target.
    [GeneratedRegex(@"\]\((?<target>[^)\s]*)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

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
