using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.Rig;

/// <summary>
/// Where the rig goes back to after a tuning window, written down while the window is open so a
/// station killed part way (SIGKILL, a power cut) puts the rig back at its next start-up.
/// </summary>
/// <remarks>
/// <para>Written when a window first moves the rig, flushed to the disk before the rig is told
/// anything, and deleted only once the restore is confirmed. A file found at start-up is a
/// restore owed: the station does not transmit until it has been done.</para>
/// <para>It names the rigctld it belongs to, so a station moved to another rig does not retune
/// that one to where the old one was.</para>
/// </remarks>
public static class RigRestoreFile
{
    /// <summary>
    /// The file's name for one rigctld, <c>rig-restore-HOST-PORT.json</c>, so two stations that
    /// share a state directory but not a rig never touch each other's.
    /// </summary>
    /// <param name="endpoint">The rigctld.</param>
    public static string NameFor(RigctldEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var host = new System.Text.StringBuilder(endpoint.Host.Length);
        foreach (char c in endpoint.Host)
        {
            host.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' ? c : '_');
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"rig-restore-{host}-{endpoint.Port}.json");
    }

    /// <summary>Writes the target and flushes it to the disk. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it cannot.</summary>
    /// <param name="path">The file.</param>
    /// <param name="endpoint">The rigctld the target belongs to.</param>
    /// <param name="restoreTo">Where the rig goes back to.</param>
    public static void Write(string path, RigctldEndpoint endpoint, RigTuning restoreTo)
    {
        var json = new JsonObject
        {
            ["rigctld"] = endpoint.ToString(),
            ["dialHz"] = restoreTo.DialHz,
            ["mode"] = restoreTo.Mode,
            ["passbandHz"] = restoreTo.PassbandHz,
        };

        string temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, json);
            file.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The target written for <paramref name="endpoint"/>, or null when there is none. A file that
    /// cannot be read, or names another rigctld, is reported through <paramref name="why"/> and
    /// returned as null.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="endpoint">The rigctld this station uses now.</param>
    /// <param name="why">Why a file that is there was not used, or null.</param>
    /// <param name="foreign">True when the file is readable but names another rigctld: it is
    /// another station's, and must be left where it is.</param>
    public static RigTuning? Read(string path, RigctldEndpoint endpoint, out string? why, out bool foreign)
    {
        why = null;
        foreign = false;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            string? rigctld = root?["rigctld"]?.GetValue<string>();
            long dial = root?["dialHz"]?.GetValue<long>() ?? 0;
            string? mode = root?["mode"]?.GetValue<string>();
            int passband = root?["passbandHz"]?.GetValue<int>() ?? 0;
            if (dial <= 0 || mode is null || !RigModes.IsKnown(mode))
            {
                why = $"{path} does not hold a dial and mode this station can use";
                return null;
            }

            if (rigctld != endpoint.ToString())
            {
                why = $"{path} is for the rigctld at {rigctld}, not {endpoint}";
                foreign = true;
                return null;
            }

            return new RigTuning(dial, mode.ToUpperInvariant(), passband);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException
                                            or JsonException or InvalidOperationException or FormatException)
        {
            why = $"{path} could not be read ({unreadable.Message})";
            return null;
        }
    }

    /// <summary>Removes the file, if it is there.</summary>
    /// <param name="path">The file.</param>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }
}
