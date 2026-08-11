using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Every file in <c>examples/</c>, through the loader the daemon itself uses.
/// </summary>
/// <remarks>
/// <para>An example configuration that does not load is worse than no example at all: it is
/// something an operator copies, pastes and then has to debug, having reasonably assumed the
/// repository tested what it shipped. These are handed to people as working starting points, so
/// they are held to being exactly that.</para>
/// <para><b>The warnings check is the one that earns its keep.</b> A misspelt or invented key does
/// not fail deserialisation - it lands in the extension-data bucket and the daemon carries on with
/// the setting silently ignored. Asserting no warnings is what catches an example that documents a
/// setting this version does not have.</para>
/// <para>What this cannot check is whether a mode exists: mode names are resolved against the
/// plugin registry at start-up, after plugins load, so an <c>ofdm-fm:*</c> mode is only real on a
/// station that has the plugin and its geometry file installed. The mode-shape assertions below
/// cover what can be checked without them.</para>
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
        // Without this, moving or renaming the directory turns every test below into a silent
        // pass over an empty set, which is the failure mode a data-driven suite is prone to.
        ExampleFiles().Should().HaveCountGreaterThan(1);
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

    [Theory]
    [MemberData(nameof(Examples))]
    public void An_Example_Using_Ofdm_Fm_Obeys_What_That_Waveform_Requires(string name)
    {
        DaemonConfig config = DaemonConfig.TryLoad(
            Path.Combine(FindRepoRoot(), name), out _)!;

        List<ModemConfig> ofdmFm = [.. config.Modems.Where(
            m => m.Mode.StartsWith("ofdm-fm:", StringComparison.OrdinalIgnoreCase))];
        if (ofdmFm.Count == 0)
        {
            return;
        }

        config.CaptureRate.Should().Be(
            48000,
            "the plugin offers every profile at one host rate and that rate is 48 kHz, so a "
            + "12000 channel would not carry an ofdm-fm mode at all");

        config.ModemPlugins.Should().NotBeEmpty(
            "nothing is scanned for, so a mode from a plugin needs the path that loads it");

        foreach (ModemConfig modem in ofdmFm)
        {
            modem.Frequency.Should().BeNull(
                "ofdm-fm declares no settable centre, so modem {0} would be refused at start-up "
                + "rather than have the setting ignored",
                modem.SubChannel);
        }
    }

    /// <summary>
    /// Everything shipped as a starting point: the examples directory, and the flagship
    /// <c>soundmodem.example.json</c> at the root.
    /// </summary>
    /// <remarks>
    /// The root file is in here because leaving it out is what let it go wrong: it told operators
    /// to give a modem its own TCP port with <c>"kissPort"</c>, where the setting is <c>"port"</c>,
    /// so the instruction was silently ignored by the very mechanism these tests check for.
    /// </remarks>
    private static string[] ExampleFiles() =>
    [
        .. Directory.GetFiles(Path.Combine(FindRepoRoot(), "examples"), "*.json"),
        Path.Combine(FindRepoRoot(), "soundmodem.example.json"),
    ];

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
