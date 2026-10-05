using M0LTE.Radio.Audio;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The station's PTT with a rig in the way: keyed through rigctld (<see cref="Inner"/> null), or
/// through the station's own line with the rig's tuning windows guarding it.
/// </summary>
internal sealed class RigPtt(RigControl rig, IPttControl? inner) : IPttControl
{
    /// <summary>The rig.</summary>
    internal RigControl Rig => rig;

    /// <summary>The line that really keys the radio, or null when rigctld does.</summary>
    internal IPttControl? Inner => inner;

    /// <inheritdoc />
    public void Key()
    {
        rig.Key(sendT: inner is null);
        if (inner is null)
        {
            return;
        }

        try
        {
            inner.Key();
        }
        catch
        {
            rig.Unkey(sendT: false);
            throw;
        }
    }

    /// <inheritdoc />
    public void Unkey()
    {
        if (inner is null)
        {
            rig.Unkey(sendT: true);
            return;
        }

        try
        {
            inner.Unkey();
        }
        finally
        {
            rig.Unkey(sendT: false);
        }
    }
}

/// <summary>The station's side of a <see cref="RigControl"/>: its PTT and where its files go.</summary>
internal static class RigStation
{
    /// <summary>A PTT that keys the radio with rigctld's <c>T 1</c> and <c>T 0</c>.</summary>
    internal static IPttControl KeyingPtt(this RigControl rig) => new RigPtt(rig, null);

    /// <summary>
    /// <paramref name="inner"/> with this rig's tuning windows guarding it: a keyup is refused
    /// while the rig is retuned, and the rig is never retuned while it is keyed. A PTT that
    /// already goes through this rig is returned as it is.
    /// </summary>
    internal static IPttControl Guard(this RigControl rig, IPttControl inner) =>
        inner is RigPtt mine && ReferenceEquals(mine.Rig, rig) ? inner : new RigPtt(rig, inner);

    /// <summary>Where a window's restore target is written down: the state directory under
    /// systemd, else beside the config file, the same as the mixer state file.</summary>
    internal static string RestoreFilePath(string? configPath) =>
        Path.Combine(
            StateDirectory.Current
                ?? (Path.GetDirectoryName(configPath ?? "") is { Length: > 0 } beside ? beside : "."),
            RigRestoreFile.Name);
}
