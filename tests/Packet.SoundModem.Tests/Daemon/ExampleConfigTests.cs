using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Every configuration file this repository hands somebody as a starting point, through the loader
/// the daemon itself uses.
/// </summary>
/// <remarks>
/// <para>An example configuration that does not load is worse than no example at all: it is
/// something an operator copies, pastes and then has to debug, having reasonably assumed the
/// repository tested what it shipped.</para>
/// <para><b>The warnings check is the one that earns its keep.</b> A misspelt or invented key does
/// not fail deserialisation - it lands in the extension-data bucket and the daemon carries on with
/// the setting silently ignored. That is not hypothetical: this test was written because
/// <c>soundmodem.example.json</c> had been telling operators to give a modem its own TCP port with
/// <c>"kissPort"</c>, where the setting is <c>"port"</c>, so anyone who followed it got no port and
/// no complaint.</para>
/// <para>What this cannot check is whether a mode exists. Mode names resolve against the plugin
/// registry at start-up, after plugins load, so an example naming a plugin mode is only real on a
/// station with that plugin installed. A plugin's own repository is where its example
/// configurations and their checks belong.</para>
/// </remarks>
public class ExampleConfigTests
{
    public static TheoryData<string> Examples
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string file in ExampleFiles())
            {
                data.Add(Path.GetRelativePath(FindRepoRoot(), file));
            }

            return data;
        }
    }

    [Fact]
    public void There_Are_Examples_To_Check()
    {
        // Without this, moving or renaming a file turns every test below into a silent pass over
        // an empty set, which is the failure mode a data-driven suite is prone to.
        ExampleFiles().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void An_Example_Loads_The_Way_The_Daemon_Loads_It(string name)
    {
        string path = Path.Combine(FindRepoRoot(), name);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.Warnings.Should().BeEmpty(
            "a key this version does not know is ignored rather than refused, so an example "
            + "carrying one would document a setting that does nothing");
    }

    /// <summary>Every configuration file shipped as a starting point.</summary>
    private static string[] ExampleFiles()
    {
        string root = FindRepoRoot();
        string examples = Path.Combine(root, "examples");
        return
        [
            Path.Combine(root, "soundmodem.example.json"),
            .. Directory.Exists(examples) ? Directory.GetFiles(examples, "*.json") : [],
        ];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
