using System.Reflection;
using System.Text.RegularExpressions;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The configuration reference page, read for the one sentence it has on each key.
/// </summary>
/// <remarks>
/// <para>The configuration page shows help beside every field, and the reference page already
/// has that help: one row per key, a test making sure no key is missing from it. So the page is
/// embedded in the daemon and read here rather than the help being written a second time, where
/// it would drift from the first.</para>
/// <para>A row lives in a scope: the key path its section heading names (<c>## `waterfall`</c>,
/// <c>### `modems[].identify`</c>), with a table introduced as <c>Each `uplinks` entry:</c>
/// one level further in. The top-level table's scope is empty.</para>
/// </remarks>
internal static partial class ConfigHelp
{
    /// <summary>One row of a key table: the key as the page spells it, its type, default and
    /// description, as markdown.</summary>
    internal sealed record Row(string Key, string Type, string Default, string Text);

    /// <summary>The scope of a table under a heading that names no key; never looked up.</summary>
    private const string NoScope = "#";

    /// <summary>Scope, then key, to the row; both matched without regard to case, as the file is.</summary>
    private static readonly Lazy<Dictionary<string, Dictionary<string, Row>>> Scopes =
        new(() => Parse(LoadPage()));

    /// <summary>The rows of <paramref name="scope"/>, or null when the page has no table there.</summary>
    internal static IReadOnlyDictionary<string, Row>? In(string scope) =>
        Scopes.Value.TryGetValue(scope, out Dictionary<string, Row>? rows) ? rows : null;

    /// <summary>Reads the reference page's key tables into scopes.</summary>
    internal static Dictionary<string, Dictionary<string, Row>> Parse(string markdown)
    {
        var scopes = new Dictionary<string, Dictionary<string, Row>>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        string scope = "";
        bool inTable = false;

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith('#'))
            {
                inTable = false;
                // A heading that is nothing but a key path starts that key's scope. Any other
                // "##" heading leaves the keys behind ("The file", "Band placement: ..."), and
                // any other "###" is a subsection of the key above ("The channel probe").
                Match heading = HeadingKey().Match(line);
                if (heading.Success)
                {
                    section = Normalise(heading.Groups[1].Value);
                }
                else if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    section = line.Contains("Top-level keys", StringComparison.Ordinal) ? "" : NoScope;
                }

                scope = section;
                continue;
            }

            Match each = EachEntry().Match(line);
            if (each.Success)
            {
                scope = section.Length == 0 ? each.Groups[1].Value : $"{section}.{each.Groups[1].Value}";
                continue;
            }

            if (line.StartsWith("| Key |", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!line.StartsWith('|'))
            {
                if (line.Length > 0)
                {
                    inTable = false;
                }

                continue;
            }

            Match row = KeyRow().Match(line);
            if (!inTable || !row.Success || scope.StartsWith(NoScope, StringComparison.Ordinal))
            {
                continue;
            }

            if (!scopes.TryGetValue(scope, out Dictionary<string, Row>? rows))
            {
                rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
                scopes[scope] = rows;
            }

            string[] cells = row.Groups[2].Value.Split(" | ");
            string type = cells.Length > 0 ? cells[0].Trim() : "";
            string fallback = cells.Length > 1 ? cells[1].Trim() : "";
            string text = cells.Length > 2 ? string.Join(" | ", cells[2..]).Trim().TrimEnd('|').Trim() : "";
            rows.TryAdd(row.Groups[1].Value, new Row(row.Groups[1].Value, type, fallback, text));
        }

        return scopes;
    }

    /// <summary><c>modems[].identify</c> is scope <c>modems.identify</c>.</summary>
    internal static string Normalise(string path) => path.Replace("[]", "", StringComparison.Ordinal);

    private static string LoadPage()
    {
        Assembly assembly = typeof(ConfigHelp).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("config-reference.md");
        if (stream is null)
        {
            return "";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A heading whose last code span is a key path: <c>## `modems`</c>, <c>### `modems[].identify`</c>.</summary>
    [GeneratedRegex(@"^##+ `([A-Za-z][A-Za-z0-9]*(?:\[\])?(?:\.[A-Za-z][A-Za-z0-9]*(?:\[\])?)*)`\s*$")]
    private static partial Regex HeadingKey();

    [GeneratedRegex(@"^Each `([A-Za-z][A-Za-z0-9]*)` entry")]
    private static partial Regex EachEntry();

    [GeneratedRegex(@"^\| `([A-Za-z][A-Za-z0-9]*)` \| (.*)$")]
    private static partial Regex KeyRow();
}
