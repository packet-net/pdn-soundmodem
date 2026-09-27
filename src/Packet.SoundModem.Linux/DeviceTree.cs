using System.Runtime.InteropServices;

namespace Packet.SoundModem.Linux;

/// <summary>
/// The parts of <c>/sys</c>, <c>/proc</c> and <c>/dev</c> that discovery reads, behind one seam so
/// that the grouping and the diagnoses can be tested against a tree written in a test rather than
/// the machine's own. Every path is absolute, as the kernel spells it.
/// </summary>
internal interface IDeviceTree
{
    /// <summary>The names of the entries in a directory, sorted; empty when it is not there.</summary>
    IReadOnlyList<string> List(string directory);

    /// <summary>
    /// The canonical path <paramref name="path"/> names, with every symbolic link followed as the
    /// kernel follows them (<c>realpath(3)</c>); null when it does not resolve.
    /// </summary>
    /// <remarks>
    /// Not a lexical join. Every entry under <c>/sys/class</c> is a link into <c>/sys/devices</c>,
    /// and its <c>device</c> link is relative to where that one points: resolving
    /// <c>/sys/class/sound/card0/device</c> by joining text would climb out of
    /// <c>/sys/class/sound</c> instead of out of the card's real directory.
    /// </remarks>
    string? Resolve(string path);

    /// <summary>A small text file's content, trimmed; null when it cannot be read.</summary>
    string? Read(string path);

    /// <summary>Whether the process may open <paramref name="path"/> for reading, writing or both.</summary>
    bool CanOpen(string path, bool read, bool write);
}

/// <summary>The machine's own device tree.</summary>
internal sealed class SystemDeviceTree : IDeviceTree
{
    /// <summary>The one instance there needs to be.</summary>
    public static SystemDeviceTree Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<string> List(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public string? Resolve(string path) => LinuxNative.RealPath(path);

    /// <inheritdoc />
    public string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool CanOpen(string path, bool read, bool write) =>
        LinuxNative.Access(path, (read ? LinuxNative.ReadOk : 0) | (write ? LinuxNative.WriteOk : 0));
}

/// <summary>The two libc calls discovery needs.</summary>
internal static partial class LinuxNative
{
    /// <summary><c>R_OK</c>.</summary>
    public const int ReadOk = 4;

    /// <summary><c>W_OK</c>.</summary>
    public const int WriteOk = 2;

    /// <summary><c>realpath(3)</c>, or null when the path does not resolve.</summary>
    public static string? RealPath(string path)
    {
        IntPtr resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    /// <summary><c>access(2)</c>: whether the real user may open the path in that mode. A group
    /// or ACL the session has not picked up yet (added to <c>plugdev</c> without logging in
    /// again) is answered as the kernel will answer the open, which is the point.</summary>
    public static bool Access(string path, int mode) => access(path, mode) == 0;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr realpath(string path, IntPtr resolved);

    [LibraryImport("libc")]
    private static partial void free(IntPtr pointer);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int access(string path, int mode);
}
