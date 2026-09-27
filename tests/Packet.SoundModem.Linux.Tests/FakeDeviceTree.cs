namespace Packet.SoundModem.Linux.Tests;

/// <summary>
/// A device tree written in a test: files with contents, links with where they resolve to, and
/// which nodes the process may open. Directories are implied by the paths under them.
/// </summary>
internal sealed class FakeDeviceTree : IDeviceTree
{
    private readonly Dictionary<string, string> _files = [];
    private readonly Dictionary<string, string> _links = [];
    private readonly HashSet<string> _refused = [];

    /// <summary>A file and its content.</summary>
    public FakeDeviceTree File(string path, string content)
    {
        _files[path] = content;
        return this;
    }

    /// <summary>A path that resolves somewhere else (a sysfs class entry, a device link).</summary>
    public FakeDeviceTree Link(string path, string target)
    {
        _links[path] = target;
        return this;
    }

    /// <summary>A node that exists but this process may not open.</summary>
    public FakeDeviceTree Refuse(string path)
    {
        _refused.Add(path);
        return this;
    }

    /// <summary>A node that exists and may be opened.</summary>
    public FakeDeviceTree Node(string path) => File(path, string.Empty);

    public IReadOnlyList<string> List(string directory)
    {
        string prefix = directory.TrimEnd('/') + "/";
        return _files.Keys.Concat(_links.Keys)
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..].Split('/')[0])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public string? Resolve(string path)
    {
        // Longest linked prefix first, then keep going: a class entry resolves into
        // /sys/devices, and the device link under it resolves again.
        for (int hops = 0; hops < 16; hops++)
        {
            string? link = _links.Keys
                .Where(l => path == l || path.StartsWith(l + "/", StringComparison.Ordinal))
                .OrderByDescending(l => l.Length)
                .FirstOrDefault();
            if (link is null)
            {
                break;
            }

            path = _links[link] + path[link.Length..];
        }

        return _files.ContainsKey(path) || _files.Keys.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal)) || _links.ContainsKey(path)
            ? path
            : null;
    }

    public string? Read(string path) =>
        Resolve(path) is { } real && _files.TryGetValue(real, out string? content) ? content.Trim() : null;

    public bool CanOpen(string path, bool read, bool write) => Resolve(path) is not null && !_refused.Contains(path);
}
