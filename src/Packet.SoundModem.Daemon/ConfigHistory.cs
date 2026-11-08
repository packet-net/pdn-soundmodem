using System.Globalization;
using System.Text.RegularExpressions;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The last configurations the station was applied on, kept so the configuration page can offer
/// to go back to one.
/// </summary>
/// <remarks>
/// <para><b>Where.</b> A <c>config-history</c> directory beside the one-run file, so in the state
/// directory under systemd and beside the config file on a terminal run, as
/// <see cref="ConfigApi.EphemeralPathFor"/> decides. One file per version, named for its time and
/// for whether it was saved or run once: <c>20261108T101500123Z-saved.json</c>. The file is the
/// document exactly as it was applied, secrets included, as the one-run file is; it is served only
/// redacted.</para>
/// <para><b>Twenty.</b> Recording a new one removes the oldest beyond that. A failure to write
/// one never fails the apply it follows.</para>
/// </remarks>
internal sealed partial class ConfigHistory
{
    /// <summary>How many versions are kept.</summary>
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
    /// <param name="Time">When it was applied.</param>
    /// <param name="Persisted">True if it was written to the config file, false if run once.</param>
    internal sealed record Version(string Id, DateTimeOffset Time, bool Persisted);

    /// <summary>Keeps <paramref name="document"/> as the latest version.</summary>
    public void Record(string document, bool persisted)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            DateTimeOffset now = _time.GetUtcNow();
            string id = now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
                + (persisted ? "-saved" : "-one-run");
            string path = Path.Combine(_directory, id + ".json");
            File.WriteAllText(path, document);
            if (!OperatingSystem.IsWindows())
            {
                // It can hold the access key and the monitor token.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            foreach (Version old in List().Skip(Keep))
            {
                File.Delete(Path.Combine(_directory, old.Id + ".json"));
            }
        }
    }

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
                        m.Groups[1].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
                {
                    found.Add(new Version(id, at, m.Groups[2].Value == "saved"));
                }
            }

            return [.. found.OrderByDescending(v => v.Id, StringComparer.Ordinal)];
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

    [GeneratedRegex(@"^(\d{8}T\d{9}Z)-(saved|one-run)$")]
    private static partial Regex IdPattern();
}
