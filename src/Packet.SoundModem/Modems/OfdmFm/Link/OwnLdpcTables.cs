using System.Globalization;
using System.Reflection;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// The tables of the adaptive payload code family, as designed in the prototype: the protograph
/// (which base-matrix entries exist) and, for every lifting size, the circulant shift of each entry.
/// </summary>
/// <remarks>
/// The two text files are embedded resources (<c>Tables/own-protograph.txt</c> and
/// <c>Tables/own-shifts.txt</c>, the prototype's files unchanged) and parsed once on first use,
/// the same way the repo embeds its other data files.
/// </remarks>
public static class OwnLdpcTables
{
    private static readonly Lazy<Parsed> Table = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Information columns of the mother code.</summary>
    public static int Kb => Table.Value.Kb;

    /// <summary>The first this many information columns are never sent (punctured).</summary>
    public static int Punctured => Table.Value.Punctured;

    /// <summary>Check rows of the high-rate core.</summary>
    public static int CoreRows => Table.Value.CoreRows;

    /// <summary>Check rows of the whole mother code.</summary>
    public static int Mb => Table.Value.Mb;

    /// <summary>Columns of the whole mother code.</summary>
    public static int Nb => Table.Value.Nb;

    /// <summary>The lifting sizes, ascending.</summary>
    public static IReadOnlyList<int> Liftings => Table.Value.Liftings;

    /// <summary>Most information bits one code block holds.</summary>
    public static int MaxInfoBitsPerBlock => Table.Value.Kb * Table.Value.Liftings[^1];

    /// <summary>The smallest lifting size whose block holds <paramref name="infoBits"/>.</summary>
    public static int SelectLifting(int infoBits)
    {
        Parsed t = Table.Value;
        foreach (int z in t.Liftings)
        {
            if (t.Kb * z >= infoBits)
            {
                return z;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(infoBits), infoBits, $"too many for one code block (max {MaxInfoBitsPerBlock})");
    }

    /// <summary>The shift of entry (row, column) at a lifting size, or -1 where the base matrix has no entry.</summary>
    public static int Shift(int lifting, int row, int column) => Table.Value.Shifts[lifting][row * Table.Value.Nb + column];

    /// <summary>
    /// Splits <paramref name="total"/> into
    /// <paramref name="parts"/> numbers that differ by at most one, smaller first.
    /// </summary>
    internal static int[] SplitEvenly(int total, int parts)
    {
        int q = total / parts;
        int rem = total % parts;
        var result = new int[parts];
        for (int i = 0; i < parts; i++)
        {
            result[i] = i < parts - rem ? q : q + 1;
        }

        return result;
    }

    private static Parsed Load()
    {
        Assembly asm = typeof(OwnLdpcTables).Assembly;
        var meta = new Dictionary<string, int>();
        var rows = new List<string>();
        foreach (string raw in ReadLines(asm, "own-protograph.txt"))
        {
            string line = StripComment(raw);
            if (line.Length == 0)
            {
                continue;
            }

            if (char.IsLetter(line[0]))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                meta[parts[0]] = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            else
            {
                rows.Add(line);
            }
        }

        int mb = rows.Count;
        int nb = rows[0].Length;
        var edge = new bool[mb * nb];
        int edges = 0;
        for (int r = 0; r < mb; r++)
        {
            if (rows[r].Length != nb)
            {
                throw new InvalidDataException("protograph rows differ in length");
            }

            for (int c = 0; c < nb; c++)
            {
                if (rows[r][c] != '0')
                {
                    edge[r * nb + c] = true;
                    edges++;
                }
            }
        }

        var shifts = new SortedDictionary<int, int[]>();
        foreach (string raw in ReadLines(asm, "own-shifts.txt"))
        {
            string line = StripComment(raw);
            if (line.Length == 0)
            {
                continue;
            }

            string[] v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int z = int.Parse(v[0], CultureInfo.InvariantCulture);
            if (v.Length - 1 != edges)
            {
                throw new InvalidDataException($"shift table for Z={z} has {v.Length - 1} entries, expected {edges}");
            }

            var s = new int[mb * nb];
            Array.Fill(s, -1);
            int e = 1;
            for (int i = 0; i < s.Length; i++)
            {
                if (edge[i])
                {
                    s[i] = int.Parse(v[e++], CultureInfo.InvariantCulture);
                }
            }

            shifts[z] = s;
        }

        return new Parsed(meta["kb"], meta["punctured"], meta["core_rows"], mb, nb, [.. shifts.Keys], shifts.ToDictionary(p => p.Key, p => p.Value));
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return (hash >= 0 ? line[..hash] : line).Trim();
    }

    private static IEnumerable<string> ReadLines(Assembly asm, string name)
    {
        using Stream stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"embedded table {name} is missing");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private sealed record Parsed(int Kb, int Punctured, int CoreRows, int Mb, int Nb, int[] Liftings, Dictionary<int, int[]> Shifts);
}
