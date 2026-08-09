using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The registry and the catalogue paths that read it: what a plugin has to get right to be
/// registered at all, and what the catalogue then answers about its modes.
/// </summary>
/// <remarks>
/// <para>No assembly loading here - that is <see cref="ModemPluginLoaderTests"/>. These use an
/// in-assembly fake so the registry's own rules can be exercised without a build artefact.</para>
/// <para>Serialised with the loader tests through one collection, and every registration is
/// disposed. Registration is process-global static: a leak from here is visible to every
/// whole-catalogue sweep that runs afterwards, and two classes registering the same id at once
/// would fail each other rather than the code.</para>
/// </remarks>
[Collection("modem-plugin-registry")]
public class ModemPluginRegistryTests
{
    private static readonly Action<byte[]> Sink = _ => { };

    private static ModemDescriptor Carrier(string name = "carrier") =>
        new(name, DspRate: 12000, AcceptsCentre: true, DefaultCentreHz: 1500, RunsIl2pCrc: false);

    private static ModemDescriptor Baseband(string name = "baseband") =>
        new(name, DspRate: 48000, AcceptsCentre: false, DefaultCentreHz: null, RunsIl2pCrc: true);

    [Fact]
    public void A_Registered_Plugin_Puts_Its_Modes_In_AllModes_And_Nowhere_Near_KnownModes()
    {
        int builtIns = ModemCatalog.KnownModes.Count;

        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier(), Baseband()]));

        // The whole point of the split: KnownModes is what this repository is answerable for, and
        // five whole-catalogue sweeps iterate it. A plugin joining it breaks all of them.
        ModemCatalog.KnownModes.Should().HaveCount(builtIns);
        ModemCatalog.KnownModes.Should().NotContain("fake:carrier");

        ModemCatalog.AllModes.Should().HaveCount(builtIns + 2);
        ModemCatalog.AllModes.Should().Contain(["fake:carrier", "fake:baseband"]);
        ModemPluginRegistry.RegisteredModes.Should().Equal("fake:carrier", "fake:baseband");
        ModemPluginRegistry.RegisteredPlugins.Should().Equal("fake");
    }

    [Fact]
    public void AllModes_Is_KnownModes_When_Nothing_Is_Registered()
    {
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty("no test may leak a registration");
        ModemCatalog.AllModes.Should().BeSameAs(ModemCatalog.KnownModes);
    }

    [Fact]
    public void Disposing_A_Registration_Takes_Its_Modes_Back_Out()
    {
        IDisposable registration = ModemPluginRegistry.Register(new FakePlugin("fake", [Carrier()]));
        ModemCatalog.IsKnown("fake:carrier").Should().BeTrue();

        registration.Dispose();

        ModemCatalog.IsKnown("fake:carrier").Should().BeFalse();
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
        ModemPluginRegistry.IsPluginRegistered("fake").Should().BeFalse();

        // Idempotent, so a using-block and an explicit dispose cannot fight over it.
        Action twice = registration.Dispose;
        twice.Should().NotThrow();
    }

    [Fact]
    public void The_Catalogue_Answers_A_Plugin_Mode_From_Its_Descriptor()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier(), Baseband()]));

        ModemCatalog.IsKnown("fake:carrier").Should().BeTrue();
        ModemCatalog.DspRateFor("fake:carrier").Should().Be(12000);
        ModemCatalog.DspRateFor("fake:baseband").Should().Be(48000);
        ModemCatalog.AcceptsCentreFrequency("fake:carrier").Should().BeTrue();
        ModemCatalog.AcceptsCentreFrequency("fake:baseband").Should().BeFalse();
        ModemCatalog.DefaultCentreFrequencyFor("fake:carrier").Should().Be(1500);
        ModemCatalog.DefaultCentreFrequencyFor("fake:baseband").Should().BeNull();
        ModemCatalog.RunsIl2pCrc("fake:carrier").Should().BeFalse();
        ModemCatalog.RunsIl2pCrc("fake:baseband").Should().BeTrue();

        // Not a NinoTNC mode, and nothing lets a plugin claim to be one.
        ModemCatalog.HasNinoPskIdBeacon("fake:carrier").Should().BeFalse();
    }

    [Fact]
    public void A_Built_In_Mode_Is_Still_Answered_By_The_Built_Ins()
    {
        // The registry is consulted second, so a plugin cannot change an existing answer even if
        // it somehow named a mode the same. It cannot, because no built-in contains a colon.
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier()]));

        ModemCatalog.DspRateFor("fsk9600").Should().Be(48000);
        ModemCatalog.AcceptsCentreFrequency("fsk9600").Should().BeFalse();
        ModemPluginRegistry.RegisteredModes.Should().AllSatisfy(
            m => m.Should().Contain(":", "a plugin mode is always qualified"));
    }

    [Fact]
    public void Create_Builds_A_Plugin_Mode_Through_The_Catalogue()
    {
        var plugin = new FakePlugin("fake", [Carrier()]);
        using IDisposable registration = ModemPluginRegistry.Register(plugin);

        IModem modem = ModemCatalog.Create("fake:carrier", 12000, Sink);

        modem.Mode.Should().Be("fake:carrier");
        // The plugin is asked for the bare name it declared, not the qualified one.
        plugin.LastModeAsked.Should().Be("carrier");
        plugin.LastDspRate.Should().Be(12000);
    }

    [Fact]
    public void Create_Hands_A_Plugin_The_Resolved_Centre_Frequency()
    {
        var plugin = new FakePlugin("fake", [Carrier()]);
        using IDisposable registration = ModemPluginRegistry.Register(plugin);

        ModemCatalog.Create("fake:carrier", 12000, Sink);
        plugin.LastOptions.CentreFrequencyHz.Should().Be(
            1500, "an unstated centre resolves to the descriptor's default");

        ModemCatalog.Create("fake:carrier", 12000, Sink, new ModemOptions(CentreFrequencyHz: 1234));
        plugin.LastOptions.CentreFrequencyHz.Should().Be(1234);
    }

    [Fact]
    public void Create_Refuses_A_Centre_Frequency_On_A_Baseband_Plugin_Mode()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Baseband()]));

        Action act = () => ModemCatalog.Create(
            "fake:baseband", 48000, Sink, new ModemOptions(CentreFrequencyHz: 1500));

        // Word for word the built-in refusal: it is the same rule, and a different wording would
        // read as a different one.
        act.Should().Throw<ArgumentException>().WithMessage("*no centre frequency to move*");
    }

    [Fact]
    public void Create_Applies_The_Il2p_Crc_Rule_To_Plugin_Modes_Both_Ways()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier(), Baseband()]));

        Action refused = () => ModemCatalog.Create(
            "fake:carrier", 12000, Sink, new ModemOptions(AcceptPlainIl2p: true));
        refused.Should().Throw<ArgumentException>().WithMessage("*does not run IL2P+CRC*");

        Action allowed = () => ModemCatalog.Create(
            "fake:baseband", 48000, Sink, new ModemOptions(AcceptPlainIl2p: true));
        allowed.Should().NotThrow();
    }

    [Fact]
    public void Create_Refuses_An_Ensemble_Second_Detector_On_Any_Plugin_Mode()
    {
        // Refused unconditionally rather than by the built-in path's "starts with bpsk" test: a
        // plugin id of "bpsk" would sneak past that and be handed a detector it cannot use.
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("bpsk", [Carrier()]));

        Action act = () => ModemCatalog.Create(
            "bpsk:carrier", 12000, Sink, new ModemOptions(SecondDetector: PskDetector.Coherent));

        act.Should().Throw<ArgumentException>().WithMessage("*ensemble second detector*");
    }

    [Fact]
    public void An_Unloaded_Plugin_Says_So_Rather_Than_Being_A_Missing_Mode()
    {
        Action act = () => ModemCatalog.Create("ofdm-fm:nb", 48000, Sink);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*unknown mode 'ofdm-fm:nb'*")
            .WithMessage("*no modem plugin registered for 'ofdm-fm'*");
    }

    [Fact]
    public void A_Loaded_Plugin_Asked_For_A_Mode_It_Does_Not_Have_Says_That_Instead()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier()]));

        Action act = () => ModemCatalog.Create("fake:nonesuch", 12000, Sink);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*modem plugin 'fake' is loaded and does not provide it*");
    }

    [Fact]
    public void An_Unknown_Mode_With_No_Colon_Is_Still_Just_An_Unknown_Mode()
    {
        Action act = () => ModemCatalog.Create("nonsense-mode", 12000, Sink);

        act.Should().Throw<ArgumentException>().WithMessage("*unknown mode*");
        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("plugin");
    }

    [Fact]
    public void A_Typo_On_A_Plugin_Mode_Is_Offered_The_Plugin_Mode()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier()]));

        ModemCatalog.NearestModes("fake:carier").Should().Contain("fake:carrier");
    }

    [Fact]
    public void A_Plugin_That_Throws_While_Building_Is_Named_In_The_Failure()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier()], _ => throw new InvalidOperationException("nope")));

        Action act = () => ModemCatalog.Create("fake:carrier", 12000, Sink);

        // Named because the bare exception comes from an assembly the operator has no source for.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*modem plugin 'fake' failed to build mode 'fake:carrier'*")
            .WithMessage("*nope*");
    }

    [Fact]
    public void A_Plugin_Handing_Back_Nothing_Is_Named_Too()
    {
        using IDisposable registration = ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier()], _ => null));

        Action act = () => ModemCatalog.Create("fake:carrier", 12000, Sink);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*modem plugin 'fake' returned no modem*");
    }

    [Fact]
    public void Two_Plugins_Cannot_Share_An_Id()
    {
        using IDisposable first = ModemPluginRegistry.Register(new FakePlugin("fake", [Carrier()]));

        Action act = () => ModemPluginRegistry.Register(new FakePlugin("fake", [Baseband()]));

        // Refused rather than replaced: one of the two is not the modem the operator wrote down.
        act.Should().Throw<ArgumentException>().WithMessage("*already registered*");
        ModemPluginRegistry.RegisteredModes.Should().Equal("fake:carrier");
    }

    [Theory]
    [InlineData("", "needs an id")]
    [InlineData("   ", "needs an id")]
    [InlineData("has:colon", "contains ':'")]
    public void An_Id_That_Cannot_Prefix_A_Mode_Name_Is_Refused(string id, string because)
    {
        Action act = () => ModemPluginRegistry.Register(new FakePlugin(id, [Carrier()]));

        act.Should().Throw<ArgumentException>().WithMessage($"*{because}*");
    }

    [Fact]
    public void A_Plugin_With_No_Modes_Is_Refused()
    {
        Action act = () => ModemPluginRegistry.Register(new FakePlugin("fake", []));

        act.Should().Throw<ArgumentException>().WithMessage("*declares no modes*");
    }

    [Fact]
    public void A_Plugin_Declaring_One_Mode_Twice_Is_Refused()
    {
        Action act = () => ModemPluginRegistry.Register(
            new FakePlugin("fake", [Carrier("same"), Baseband("same")]));

        act.Should().Throw<ArgumentException>().WithMessage("*declares mode 'same' twice*");
    }

    [Fact]
    public void A_Refused_Registration_Leaves_Nothing_Behind()
    {
        Action act = () => ModemPluginRegistry.Register(new FakePlugin("fake", []));
        act.Should().Throw<ArgumentException>();

        ModemPluginRegistry.IsPluginRegistered("fake").Should().BeFalse();
        ModemPluginRegistry.RegisteredModes.Should().BeEmpty();
    }

    [Fact]
    public void Register_Refuses_Nothing_At_All()
    {
        Action act = () => ModemPluginRegistry.Register(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("", 12000, false, null, "needs a name")]
    [InlineData("has:colon", 12000, false, null, "contains ':'")]
    [InlineData("zero", 0, false, null, "DSP rate of 0")]
    [InlineData("negative", -48000, false, null, "DSP rate of -48000")]
    [InlineData("no-default", 12000, true, null, "declares no default one")]
    [InlineData("contradiction", 12000, false, 1500d, "accepts no centre frequency")]
    public void A_Descriptor_That_Contradicts_Itself_Is_Refused(
        string name, int dspRate, bool acceptsCentre, double? defaultCentreHz, string because)
    {
        var descriptor = new ModemDescriptor(
            name, dspRate, acceptsCentre, defaultCentreHz, RunsIl2pCrc: false);

        Action direct = descriptor.Validate;
        direct.Should().Throw<ArgumentException>().WithMessage($"*{because}*");

        // And the registry applies the same check, so a bad descriptor never reaches the catalogue.
        Action registered = () => ModemPluginRegistry.Register(new FakePlugin("fake", [descriptor]));
        registered.Should().Throw<ArgumentException>().WithMessage($"*{because}*");
    }

    [Fact]
    public void DescriptorFor_Answers_Nothing_For_A_Mode_It_Does_Not_Have()
    {
        ModemPluginRegistry.DescriptorFor("fake:carrier").Should().BeNull();
        ModemPluginRegistry.IsRegistered("fake:carrier").Should().BeFalse();
        ModemPluginRegistry.IsPluginRegistered("fake").Should().BeFalse();
    }

    private sealed class FakePlugin(
        string id, IReadOnlyList<ModemDescriptor> modes, Func<string, IModem?>? create = null)
        : IModemPlugin
    {
        public string Id => id;

        public IReadOnlyList<ModemDescriptor> Modes => modes;

        public string? LastModeAsked { get; private set; }

        public int LastDspRate { get; private set; }

        public ModemOptions LastOptions { get; private set; }

        public IModem Create(
            string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options)
        {
            LastModeAsked = mode;
            LastDspRate = dspRate;
            LastOptions = options;
            return create is null ? new StubModem($"{id}:{mode}") : create(mode)!;
        }
    }

    private sealed class StubModem(string mode) : IModem
    {
        public string Mode => mode;

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default);
    }
}
