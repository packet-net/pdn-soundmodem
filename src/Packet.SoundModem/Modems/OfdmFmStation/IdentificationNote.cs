using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Packet.SoundModem.Modems.OfdmFmStation;

/// <summary>
/// The note a program keeps of when its OFDM-FM station last identified, so that a program started again within ten minutes does not identify again (<see cref="OfdmFmStationHostOptions.LastIdentification"/>).
/// One file holding one line: the time in ISO 8601, UTC, to the millisecond, such as <c>2026-11-28T09:15:02.250Z</c>. Written through a temporary file and a rename, so a reader never sees half a line.
/// </summary>
/// <remarks>
/// Reading never throws: a file that is missing, unreadable, empty or garbled, or that holds a time later than the clock, is no record. Writing never throws either: a failure is said once, in one plain line,
/// to the callback, and again only after a write has worked in between. The path is the program's to choose; there is no setting.
/// </remarks>
public sealed class IdentificationNote
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Action<string> _say;
    private bool _failing;

    /// <param name="path">The file.</param>
    /// <param name="time">The clock a time read back is compared with.</param>
    /// <param name="say">Where the one line about a failed write goes.</param>
    public IdentificationNote(string path, TimeProvider time, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _time = time;
        _say = say;
    }

    /// <summary>The time written last, or null when there is no usable record.</summary>
    public DateTimeOffset? Read()
    {
        try
        {
            string[] lines = File.ReadAllLines(_path);
            if (lines.Length != 1 || !DateTimeOffset.TryParseExact(lines[0].Trim(), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset when))
            {
                return null;
            }

            return when > _time.GetUtcNow() ? null : when;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Records <paramref name="when"/>. Never throws.</summary>
    public void Write(DateTimeOffset when)
    {
        string temp = _path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temp, when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "\n");
            File.Move(temp, _path, overwrite: true);
            _failing = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_failing)
            {
                _failing = true;
                _say($"cannot write the identification time to {_path}: {e.Message}");
            }

            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Nothing more to say.
            }
        }
    }

    /// <summary>
    /// A file name for one callsign on one device: both made safe for any file system, with a short hash of the exact pair so that two pairs never share a name, however alike they look once made safe.
    /// One machine may run the same callsign on two radios.
    /// </summary>
    public static string FileName(string callsign, string device)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callsign + "\0" + device)).AsSpan(0, 4)).ToLowerInvariant();
        return $"{Safe(callsign, 20)}-{Safe(device, 40)}-{hash}.txt";
    }

    private static string Safe(string text, int longest)
    {
        var made = new StringBuilder();
        foreach (char c in text)
        {
            made.Append(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? c : '_');
        }

        string result = made.ToString().Trim('_');
        return result.Length == 0 ? "x" : result.Length > longest ? result[..longest] : result;
    }
}
