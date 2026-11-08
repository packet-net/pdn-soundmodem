using Packet.SoundModem.Linux;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A machine written down in a file, for tests that run the real daemon and need
/// <c>/api/config/discover</c> to find a CM108 interface, two serial adapters and a Flex without
/// owning any. Read only when the <c>PDN_SOUNDMODEM_DISCOVERY_FIXTURE</c> environment variable
/// names a file; an operator never sets it, and with it set nothing real is looked at.
/// </summary>
/// <remarks>
/// One entry per line, fields separated by tabs, and in a <c>file</c> line the content has
/// <c>\n</c> for a newline and <c>\\</c> for a backslash:
/// <c>file PATH CONTENT</c>, <c>link PATH TARGET</c>, <c>refuse PATH</c>,
/// <c>mixer CARDID CAPTURE,CONTROLS PLAYBACK,CONTROLS</c>, <c>rigctld answering</c>,
/// <c>flex NAME MODEL SERIAL IP</c>, <c>ubersdr HOST DEVICE NAME LOCATION free</c>.
/// </remarks>
internal static class HardwareFixture
{
    /// <summary>The variable that names the fixture file.</summary>
    internal const string Variable = "PDN_SOUNDMODEM_DISCOVERY_FIXTURE";

    /// <summary>A discovery over the machine the file describes.</summary>
    internal static HardwareDiscovery Load(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException or FormatException)
        {
            // Every section says so, rather than every request being a 500.
            var broken = new FailingProbes($"the discovery fixture {path} cannot be read ({e.Message})");
            return new HardwareDiscovery(broken, broken, broken, broken, broken, broken);
        }
    }

    /// <summary>The line the daemon journals at start-up when a fixture is in force, else null.</summary>
    internal static string? StartUpNotice() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path
            ? $"api: discovery is showing the machine written in {path} ({Variable}), not this machine"
            : null;

    private static HardwareDiscovery Read(string path)
    {
        var tree = new WrittenDeviceTree();
        var mixers = new Dictionary<string, MixerNames>(StringComparer.Ordinal);
        bool answering = false;
        var flex = new List<FoundFlex>();
        var uber = new List<FoundUberSdr>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "file":
                    tree.Files[f[1]] = f.Length > 2 ? f[2].Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : "";
                    break;
                case "link":
                    tree.Links[f[1]] = f[2];
                    break;
                case "refuse":
                    tree.Refused.Add(f[1]);
                    break;
                case "mixer":
                    mixers[f[1]] = new MixerNames(Csv(f[2]), Csv(f[3]), null);
                    break;
                case "rigctld":
                    answering = f[1] == "answering";
                    break;
                case "flex":
                    flex.Add(new FoundFlex(f[1], f[2], f[3], f[4]));
                    break;
                case "ubersdr":
                    uber.Add(new FoundUberSdr(f[1], f[2], f[3], f[4], f[5] == "free"));
                    break;
            }
        }

        var sysfs = new SysfsProbes(tree, new WrittenMixers(mixers));
        return new HardwareDiscovery(sysfs, sysfs, sysfs, new FixedRigctld(answering), new FixedFlex(flex), new FixedUberSdr(uber));
    }

    private static string[] Csv(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class WrittenMixers(Dictionary<string, MixerNames> byCard) : IMixerReader
    {
        public MixerNames Read(string mixerCard) =>
            byCard.GetValueOrDefault(mixerCard["hw:CARD=".Length..]) ?? new MixerNames([], [], "no mixer was described");
    }

    private sealed class FixedRigctld(bool answering) : IRigctldProbe
    {
        public string Address => "localhost:4532";

        public Task<bool> AnswersAsync(CancellationToken cancellation) => Task.FromResult(answering);
    }

    private sealed class FixedFlex(List<FoundFlex> radios) : IFlexProbe
    {
        public Task<IReadOnlyList<FoundFlex>> FindAsync(TimeSpan listen, CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<FoundFlex>>(radios);
    }

    private sealed class FixedUberSdr(List<FoundUberSdr> receivers) : IUberSdrProbe
    {
        public Task<IReadOnlyList<FoundUberSdr>> FindAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<FoundUberSdr>>(receivers);
    }

    /// <summary>The device tree the file writes: files, links and refused nodes.</summary>
    private sealed class WrittenDeviceTree : IDeviceTree
    {
        public Dictionary<string, string> Files { get; } = [];

        public Dictionary<string, string> Links { get; } = [];

        public HashSet<string> Refused { get; } = [];

        public IReadOnlyList<string> List(string directory)
        {
            string prefix = directory.TrimEnd('/') + "/";
            return Files.Keys.Concat(Links.Keys)
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                .Select(p => p[prefix.Length..].Split('/')[0])
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        public string? Resolve(string path)
        {
            for (int hops = 0; hops < 16; hops++)
            {
                string? link = Links.Keys
                    .Where(l => path == l || path.StartsWith(l + "/", StringComparison.Ordinal))
                    .OrderByDescending(l => l.Length)
                    .FirstOrDefault();
                if (link is null)
                {
                    break;
                }

                path = Links[link] + path[link.Length..];
            }

            return Files.ContainsKey(path) || Files.Keys.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal)) || Links.ContainsKey(path)
                ? path
                : null;
        }

        public string? Read(string path) =>
            Resolve(path) is { } real && Files.TryGetValue(real, out string? content)
                ? content.Trim()
                : null;

        public bool CanOpen(string path, bool read, bool write) => Resolve(path) is not null && !Refused.Contains(path);
    }
}
