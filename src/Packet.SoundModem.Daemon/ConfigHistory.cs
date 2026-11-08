using System.Globalization;
using System.Text.RegularExpressions;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The configurations the station has been applied on, kept so the configuration page can offer
/// to go back to one.
/// </summary>
/// <remarks>
/// <para><b>Where.</b> A <c>config-history</c> directory beside the one-run file, so in the state
/// directory under systemd and beside the config file on a terminal run, as
/// <see cref="ConfigApi.EphemeralPathFor"/> decides. One file per version, named
/// <c>000007-saved-20261108T101500123Z.json</c>: a sequence number, whether it was saved, run once
/// or is the original, and when. The file is the document exactly as it was applied, comments and
/// secrets included, as the one-run file is; it is served only redacted. Created 0600 in a 0700
/// directory.</para>
/// <para><b>Ordered by the sequence number, not the clock.</b> A clock that steps back (a Pi with
/// no battery clock, an NTP correction) must not make a new version look old and be pruned, and two
/// applies in one millisecond must not share a name.</para>
/// <para><b>The original.</b> The first time a change is applied the running document is kept first,
/// as the file's own text, so a roll-back can reach where the station started. It is kept outside
/// the cap; the last <see cref="Keep"/> applied versions are kept besides.</para>
/// </remarks>
internal sealed partial class ConfigHistory
{
    /// <summary>How many applied versions are kept, besides the original.</summary>
    internal const int Keep = 20;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <param name="ephemeralPath">The one-run file; the history lives beside it.</param>
    /// <param name="time">The clock.</param>
    public ConfigHistory(string ephemeralPath, TimeProvider time)
    {
        string beside = Path.GetDirectoryName(ephemeralPath) is { Length: > 0 } d ? d : ".";
        _directory = Path.Combine(beside, "config-history");
        _time = time;
    }

    /// <summary>One kept version.</summary>
    /// <param name="Id">Its name, safe to put in a URL.</param>
    /// <param name="Sequence">Its place in the order, which is the order it was kept in.</param>
    /// <param name="Time">When it was kept.</param>
    /// <param name="Kind"><c>saved</c>, <c>one-run</c> or <c>original</c>.</param>
    internal sealed record Version(string Id, int Sequence, DateTimeOffset Time, string Kind)
    {
        /// <summary>Whether it was written to the config file.</summary>
        public bool Persisted => Kind == "saved";
    }

    /// <summary>Keeps <paramref name="document"/> as the latest version.</summary>
    /// <param name="document">The document as applied.</param>
    /// <param name="persisted">Whether it went to the config file.</param>
    /// <param name="running">The running configuration's own text; kept first as the original when
    /// nothing has been kept yet.</param>
    public void Record(string document, bool persisted, string? running = null)
    {
        lock (_gate)
        {
            MakeDirectory();
            if (running is not null && List().Count == 0)
            {
                Write("original", running);
            }

            Write(persisted ? "saved" : "one-run", document);
            foreach (Version old in List().Where(v => v.Kind != "original").Skip(Keep))
            {
                File.Delete(Path.Combine(_directory, FileName(old) + ".json"));
            }
        }
    }

    private void Write(string kind, string text)
    {
        int next = List().Select(v => v.Sequence).DefaultIfEmpty(0).Max() + 1;
        DateTimeOffset now = _time.GetUtcNow();
        string name = $"{next:D6}-{kind}-{now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}";
        WritePrivate(Path.Combine(_directory, name + ".json"), text);
    }

    private void MakeDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(_directory, Private);
        // Made earlier with the umask's mode, or by an older version: tightened now.
        File.SetUnixFileMode(_directory, Private);
    }

    /// <summary>Writes a file only its owner can read, from the moment it exists: it can hold the
    /// access key and the monitor token, so there must be no window in which it is wider.</summary>
    internal static void WritePrivate(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    private static string FileName(Version v) =>
        $"{v.Sequence:D6}-{v.Kind}-{v.Time.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}";

    /// <summary>The kept versions, newest first.</summary>
    public IReadOnlyList<Version> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var found = new List<Version>();
            foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (IdPattern().Match(id) is { Success: true } m
                    && DateTimeOffset.TryParseExact(
                        m.Groups[3].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
                {
                    found.Add(new Version(id, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), at, m.Groups[2].Value));
                }
            }

            return [.. found.OrderByDescending(v => v.Sequence)];
        }
    }

    /// <summary>The document of version <paramref name="id"/>, or null if there is no such version.</summary>
    public string? Read(string id)
    {
        // Only a name this class made: nothing else reaches the file system.
        if (!IdPattern().IsMatch(id))
        {
            return null;
        }

        lock (_gate)
        {
            string path = Path.Combine(_directory, id + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    [GeneratedRegex(@"^(\d{6})-(saved|one-run|original)-(\d{8}T\d{9}Z)\z")]
    private static partial Regex IdPattern();
}
