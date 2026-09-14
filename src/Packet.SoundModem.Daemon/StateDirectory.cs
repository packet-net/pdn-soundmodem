namespace Packet.SoundModem.Daemon;

/// <summary>
/// The directory systemd hands the service for its own files, and the default paths that live
/// under it.
/// </summary>
/// <remarks>
/// <para>The shipped unit sets <c>StateDirectory=pdn-soundmodem</c>, so <c>$STATE_DIRECTORY</c> is
/// <c>/var/lib/pdn-soundmodem</c>. A template instance (<c>pdn-soundmodem@NAME</c>) gets
/// <c>/var/lib/pdn-soundmodem/NAME</c> instead, and everything the daemon keeps by default has to
/// follow it there: two instances whose frame logs both defaulted to one absolute path would be
/// two writers on one SQLite file.</para>
/// <para>Run from a terminal with no <c>$STATE_DIRECTORY</c>, the defaults fall back to the packaged
/// location, which is where they always pointed. The mixer state file and the pending-config file
/// have their own fallback, beside the config file, because they are written on the operator's
/// instruction and a terminal run has no business writing under <c>/var/lib</c>.</para>
/// </remarks>
public static class StateDirectory
{
    /// <summary>Where the packaged service keeps its state when systemd has not said otherwise.</summary>
    public const string Packaged = "/var/lib/pdn-soundmodem";

    /// <summary>The first directory in <c>$STATE_DIRECTORY</c>, or null when it is not set.</summary>
    /// <remarks>systemd hands over a colon-separated list when the unit names more than one; the
    /// first is this unit's own.</remarks>
    public static string? Current => FirstOf(Environment.GetEnvironmentVariable("STATE_DIRECTORY"));

    /// <summary>The default location of a file the daemon keeps: under the state directory when
    /// systemd sets one, else under <see cref="Packaged"/>.</summary>
    public static string PathFor(string name) => PathFor(name, Current);

    /// <summary>The first directory in a <c>$STATE_DIRECTORY</c> value, or null when it has none.</summary>
    public static string? FirstOf(string? stateDirectoryVariable)
    {
        if (string.IsNullOrEmpty(stateDirectoryVariable))
        {
            return null;
        }

        string first = stateDirectoryVariable.Split(':')[0];
        return first.Length > 0 ? first : null;
    }

    /// <summary><see cref="PathFor(string)"/> against a given first directory, for tests that
    /// must not touch the process environment.</summary>
    public static string PathFor(string name, string? current) =>
        Path.Combine(current ?? Packaged, name);
}
