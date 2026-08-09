using System.Reflection;
using System.Runtime.Loader;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The loader, against a real assembly in a real <see cref="AssemblyLoadContext"/>: the sample
/// plugin under <c>tests/Packet.SoundModem.SamplePlugin</c>, built alongside these tests and
/// reached by path, never referenced.
/// </summary>
/// <remarks>
/// <para>The one that matters is
/// <see cref="The_Contract_Crosses_The_Boundary_As_One_Type"/>. Everything else here is plumbing;
/// that test is the whole mechanism, and
/// <see cref="The_Plugin_Really_Does_Carry_Its_Own_Copy_Of_The_Contract"/> exists so it cannot pass
/// for the wrong reason.</para>
/// <para>Serialised with <see cref="ModemPluginRegistryTests"/> through one collection: the
/// registry is process-global static.</para>
/// </remarks>
[Collection("modem-plugin-registry")]
public class ModemPluginLoaderTests
{
    private static readonly Action<byte[]> Sink = _ => { };

    /// <summary>
    /// Where the sample plugin was built, injected by the test project as assembly metadata. Not
    /// derived from <see cref="AppContext.BaseDirectory"/> by walking upwards: that guesses at the
    /// configuration and the layout, and would quietly test nothing on the day either changed.
    /// </summary>
    private static string SamplePluginPath =>
        typeof(ModemPluginLoaderTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SamplePluginPath").Value!;

    [Fact]
    public void The_Sample_Plugin_Was_Built_Where_The_Tests_Expect_It()
    {
        // A missing artefact would otherwise show up as "no such file" in every test below, which
        // reads as a loader bug rather than as a build that did not run.
        File.Exists(SamplePluginPath).Should().BeTrue(
            "the test project has a ProjectReference to it with ReferenceOutputAssembly=false, so "
            + "it builds first - if this fails, that reference is gone: {0}",
            SamplePluginPath);
    }

    [Fact]
    public void The_Plugin_Really_Does_Carry_Its_Own_Copy_Of_The_Contract()
    {
        // The anti-vacuity check. A plugin's build copies its references next to its output, so
        // Packet.SoundModem.dll is sitting there, and the load context has to decline to load it.
        // If a future csproj change stops copying it, the shared-contract rule would go untested
        // and nothing else here would notice.
        string beside = Path.Combine(
            Path.GetDirectoryName(SamplePluginPath)!, "Packet.SoundModem.dll");

        File.Exists(beside).Should().BeTrue(
            "the identity test below is only worth anything if there is a second copy to get wrong");
    }

    [Fact]
    public void Loading_The_Sample_Plugin_Registers_Its_Modes()
    {
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);

        load.Failure.Should().BeNull();
        load.Loaded.Should().BeTrue();
        load.PluginId.Should().Be("sample");
        load.Path.Should().Be(SamplePluginPath);
        load.Modes.Should().Equal("sample:loopback", "sample:baseband", "sample:explodes");

        ModemCatalog.IsKnown("sample:loopback").Should().BeTrue();
        ModemCatalog.DspRateFor("sample:loopback").Should().Be(12000);
        ModemCatalog.DspRateFor("sample:baseband").Should().Be(48000);
        ModemCatalog.KnownModes.Should().NotContain("sample:loopback");
    }

    [Fact]
    public void The_Contract_Crosses_The_Boundary_As_One_Type()
    {
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        IModem modem = ModemCatalog.Create("sample:loopback", 12000, Sink);

        // The modem came from the plugin's assembly, in its own load context...
        Assembly modemAssembly = modem.GetType().Assembly;
        modemAssembly.GetName().Name.Should().Be("Packet.SoundModem.SamplePlugin");
        AssemblyLoadContext.GetLoadContext(modemAssembly).Should().NotBeSameAs(
            AssemblyLoadContext.Default, "a plugin loaded into the default context proves nothing");

        // ...and the IModem it implements is the host's, from the default context. That identity is
        // the whole mechanism: get it wrong and the cast above fails with a message saying IModem
        // cannot be converted to IModem, which is the failure this rule exists to prevent.
        Type contract = modem.GetType().GetInterfaces().Single(i => i.Name == nameof(IModem));
        contract.Should().BeSameAs(typeof(IModem));
        AssemblyLoadContext.GetLoadContext(contract.Assembly).Should().BeSameAs(
            AssemblyLoadContext.Default);
    }

    [Fact]
    public void A_Frame_Goes_Out_Through_Modulate_And_Comes_Back_Through_Process()
    {
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        var delivered = new List<byte[]>();
        var monitored = new List<FrameQuality>();
        IModem modem = ModemCatalog.Create("sample:loopback", 12000, delivered.Add);
        modem.FrameDecoded += (_, quality) => monitored.Add(quality);

        byte[] frame = [0x9C, 0x94, 0x6E, 0x40, 0x40, 0x40, 0x60, 0x03, 0xF0, .. "hello"u8];
        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 5);
        modem.Process(audio);

        delivered.Should().HaveCount(1);
        delivered[0].Should().Equal(frame);
        monitored.Should().HaveCount(1);
        monitored[0].Mode.Should().Be("sample:loopback");
        monitored[0].FrameBytes.Should().Be(frame.Length);
    }

    [Fact]
    public void Feeding_The_Same_Audio_In_Blocks_Decodes_The_Same_Frame()
    {
        // What the daemon does: 100 ms of audio at a time, arriving whenever the card says so. A
        // modem that only works when handed the whole burst works on the bench and nowhere else.
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        byte[] frame = [.. Enumerable.Range(0, 64).Select(i => (byte)(i * 7))];

        var whole = new List<byte[]>();
        IModem oneShot = ModemCatalog.Create("sample:loopback", 12000, whole.Add);
        float[] audio = oneShot.Modulate(frame, txDelayMilliseconds: 0);
        oneShot.Process(audio);

        var streamed = new List<byte[]>();
        IModem blocks = ModemCatalog.Create("sample:loopback", 12000, streamed.Add);
        const int Block = 1200; // 100 ms at 12 kHz
        for (int at = 0; at < audio.Length; at += Block)
        {
            blocks.Process(audio.AsSpan(at, Math.Min(Block, audio.Length - at)));
        }

        whole.Should().HaveCount(1);
        streamed.Should().BeEquivalentTo(whole);
    }

    [Fact]
    public void A_Mode_The_Plugin_Refuses_To_Build_Fails_By_Name()
    {
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        Action act = () => ModemCatalog.Create("sample:explodes", 12000, Sink);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*modem plugin 'sample' failed to build mode 'sample:explodes'*")
            .WithMessage("*this mode is meant to fail*");
    }

    [Fact]
    public void Disposing_A_Load_Takes_The_Plugin_Back_Out()
    {
        ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        load.Dispose();

        ModemCatalog.IsKnown("sample:loopback").Should().BeFalse();
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty();

        Action twice = load.Dispose;
        twice.Should().NotThrow();
    }

    [Fact]
    public void Loading_The_Same_Plugin_Twice_Is_Refused_Without_Disturbing_The_First()
    {
        using ModemPluginLoad first = ModemPluginLoader.Load(SamplePluginPath);
        first.Loaded.Should().BeTrue();

        using ModemPluginLoad second = ModemPluginLoader.Load(SamplePluginPath);

        second.Loaded.Should().BeFalse();
        second.Failure.Should().Contain("already registered");
        ModemCatalog.IsKnown("sample:loopback").Should().BeTrue("the first load still stands");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_Path_Is_A_Named_Failure_Rather_Than_A_Throw(string? path)
    {
        ModemPluginLoad load = ModemPluginLoader.Load(path);

        load.Loaded.Should().BeFalse();
        load.Failure.Should().Be("no path given");
        load.Modes.Should().BeEmpty();
        load.PluginId.Should().BeNull();
    }

    [Fact]
    public void A_Missing_File_Is_A_Named_Failure_Carrying_The_Absolute_Path()
    {
        // The whole point of non-fatal: a station whose plugin has not been installed yet starts
        // and says which file it wanted, rather than refusing to come up.
        ModemPluginLoad load = ModemPluginLoader.Load("does-not-exist.dll");

        load.Loaded.Should().BeFalse();
        load.Failure.Should().Be("no such file");
        load.Path.Should().Be(Path.GetFullPath("does-not-exist.dll"));
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
    }

    [Fact]
    public void An_Assembly_With_No_Plugin_In_It_Is_A_Named_Failure()
    {
        // A real managed assembly, in the test output, containing no IModemPlugin whatsoever.
        string notAPlugin = Path.Combine(AppContext.BaseDirectory, "M0LTE.Fec.dll");
        File.Exists(notAPlugin).Should().BeTrue();

        ModemPluginLoad load = ModemPluginLoader.Load(notAPlugin);

        load.Loaded.Should().BeFalse();
        load.Failure.Should().Contain("no IModemPlugin in it");
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
    }

    [Fact]
    public void A_Plugin_Whose_Dependency_Manifest_Is_Corrupt_Is_A_Named_Failure_Too()
    {
        // The load context reads and parses the plugin's .deps.json in its constructor and throws
        // if it cannot. Built before the try, that would break this method's whole contract - it
        // would throw for exactly the kind of half-installed plugin it exists to report calmly.
        string dir = Directory.CreateTempSubdirectory("pdnsm-plugin").FullName;
        try
        {
            string copied = Path.Combine(dir, Path.GetFileName(SamplePluginPath));
            File.Copy(SamplePluginPath, copied);
            File.WriteAllText(Path.ChangeExtension(copied, ".deps.json"), "{ not json");

            ModemPluginLoad load = ModemPluginLoader.Load(copied);

            load.Loaded.Should().BeFalse();
            load.Failure.Should().NotBeNullOrWhiteSpace();
            ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Something_That_Is_Not_An_Assembly_At_All_Is_A_Named_Failure()
    {
        string text = Path.Combine(Path.GetTempPath(), $"not-an-assembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(text, "this is not a PE file");
        try
        {
            ModemPluginLoad load = ModemPluginLoader.Load(text);

            load.Loaded.Should().BeFalse();
            load.Failure.Should().NotBeNullOrWhiteSpace();
            ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
        }
        finally
        {
            File.Delete(text);
        }
    }

    [Fact]
    public void The_Plugin_Modes_Answer_The_Same_Option_Rules_As_A_Built_In()
    {
        using ModemPluginLoad load = ModemPluginLoader.Load(SamplePluginPath);
        load.Failure.Should().BeNull();

        Action centreOnBaseband = () => ModemCatalog.Create(
            "sample:baseband", 48000, Sink, new ModemOptions(CentreFrequencyHz: 1500));
        centreOnBaseband.Should().Throw<ArgumentException>()
            .WithMessage("*no centre frequency to move*");

        Action plainIl2pOnPlainMode = () => ModemCatalog.Create(
            "sample:loopback", 12000, Sink, new ModemOptions(AcceptPlainIl2p: true));
        plainIl2pOnPlainMode.Should().Throw<ArgumentException>()
            .WithMessage("*does not run IL2P+CRC*");

        Action plainIl2pOnCrcMode = () => ModemCatalog.Create(
            "sample:baseband", 48000, Sink, new ModemOptions(AcceptPlainIl2p: true));
        plainIl2pOnCrcMode.Should().NotThrow();
    }
}
