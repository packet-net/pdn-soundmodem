using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class ModemCatalogTests
{
    private static readonly Action<byte[]> Sink = _ => { };

    public static IEnumerable<object[]> AllModes =>
        ModemCatalog.KnownModes.Select(m => new object[] { m });

    [Fact]
    public void KnownModes_Are_Unique_And_All_Recognised()
    {
        ModemCatalog.KnownModes.Should().OnlyHaveUniqueItems();
        ModemCatalog.KnownModes.Should().OnlyContain(m => ModemCatalog.IsKnown(m));
        // Guards against an arm being added to Create() but forgotten in KnownModes (or vice-versa).
        ModemCatalog.KnownModes.Should().HaveCount(38);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Create_Builds_Every_Known_Mode_At_Its_Native_Rate(string mode)
    {
        // Exercises every switch arm: a broken/removed factory or a wrong sample rate throws here.
        IModem modem = ModemCatalog.Create(mode, ModemCatalog.DspRateFor(mode), Sink);
        modem.Should().NotBeNull();
        modem.Mode.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("afsk1200", 12000)]
    [InlineData("bpsk300", 12000)]
    [InlineData("bpsk1200", 12000)]
    [InlineData("qpsk600", 12000)]
    [InlineData("fsk9600", 48000)]
    [InlineData("fsk9600-il2p", 48000)]
    [InlineData("fsk4800-il2p", 48000)]
    [InlineData("c4fsk9600", 48000)]
    [InlineData("c4fsk19200", 48000)]
    [InlineData("freedv-datac0", 48000)]
    [InlineData("ms110d-wn0", 48000)]
    public void DspRateFor_Classifies_Baseband_And_Ofdm_At_48k(string mode, int expected)
    {
        ModemCatalog.DspRateFor(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("afsk1200", true)]
    [InlineData("bpsk300", true)]
    [InlineData("qpsk3600", true)]
    [InlineData("fsk9600", false)]
    [InlineData("c4fsk9600", false)]
    // The spec-fixed families accept one since the FrequencyShiftedModem decorator: their DSP
    // stays on its native centre, the audio is translated either side.
    [InlineData("freedv-datac0", true)]
    [InlineData("ms110d-wn6", true)]
    public void AcceptsCentreFrequency_Matches_Family(string mode, bool expected)
    {
        ModemCatalog.AcceptsCentreFrequency(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("bpsk300", PskDetector.Differential)]
    [InlineData("bpsk1200", PskDetector.Differential)]
    [InlineData("qpsk600", PskDetector.Differential)]
    [InlineData("qpsk3600", PskDetector.Differential)]
    public void DefaultDetectorFor_Is_Differential_For_Every_Psk_Family(string mode, PskDetector expected)
    {
        // BPSK reversed 2026-07-18 (#40/#42); QPSK followed 2026-07-31 on the studybox
        // NinoTNC corpus - see DefaultDetectorFor's provenance comment.
        ModemCatalog.DefaultDetectorFor(mode).Should().Be(expected);
    }

    [Fact]
    public void Create_Throws_For_Unknown_Mode()
    {
        Action act = () => ModemCatalog.Create("nonsense-mode", 12000, Sink);
        act.Should().Throw<ArgumentException>().WithMessage("*unknown mode*");
    }

    [Theory]
    [InlineData("fsk9600")]
    [InlineData("c4fsk9600")]
    public void Create_Rejects_A_Centre_Frequency_On_A_Baseband_Mode(string mode)
    {
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(CentreFrequencyHz: 1500));
        act.Should().Throw<ArgumentException>().WithMessage("*no centre frequency to move*");
    }

    [Fact]
    public void Create_Accepts_A_Centre_Frequency_On_A_Carrier_Mode()
    {
        Action act = () => ModemCatalog.Create(
            "bpsk300", 12000, Sink, new ModemOptions(CentreFrequencyHz: 1600));
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ms110d-wn6", 3000)]
    [InlineData("freedv-datac3", 2500)]
    public void Create_Wraps_A_Spec_Fixed_Mode_Asked_Off_Its_Native_Centre(string mode, double centre)
    {
        IModem modem = ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(CentreFrequencyHz: centre));
        modem.Should().BeOfType<FrequencyShiftedModem>();
        modem.Mode.Should().Be(
            ModemCatalog.Create(mode, ModemCatalog.DspRateFor(mode), Sink).Mode,
            "moving a modem does not rename its mode");
    }

    [Fact]
    public void Create_Skips_The_Wrapper_When_The_Asked_For_Centre_Is_The_Native_One()
    {
        // A config that spells out the default must be bit-identical to one that omits it.
        IModem modem = ModemCatalog.Create(
            "ms110d-wn6", 48000, Sink, new ModemOptions(CentreFrequencyHz: 1800));
        modem.Should().NotBeOfType<FrequencyShiftedModem>();
    }

    [Theory]
    // Down: ms110d's band reaches 170 Hz at its native 1800; another 200 down folds into DC.
    [InlineData("ms110d-wn6", 1600)]
    // Up: past the 24 kHz Nyquist guard at a 48 kHz channel rate.
    [InlineData("ms110d-wn6", 23000)]
    public void Create_Refuses_A_Centre_That_Folds_The_Band_Over(string mode, double centre)
    {
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(CentreFrequencyHz: centre));
        act.Should().Throw<ArgumentException>().WithMessage("*not clear of*");
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void RunsIl2pCrc_Agrees_With_The_Modem_The_Catalogue_Actually_Builds(string mode)
    {
        // Cross-checked against the modem rather than restating the switch: every IL2P modem that
        // has both framings spells which one it is in its own Mode string, so that is an
        // independent answer. freedv-*/ms110d-* have one framing each (IL2P+CRC) and do not spell
        // it, and the AX.25/FX.25 modes carry no IL2P at all.
        IModem modem = ModemCatalog.Create(mode, ModemCatalog.DspRateFor(mode), Sink);
        bool expected = modem.Mode.Contains("-il2pc", StringComparison.Ordinal)
            || mode.StartsWith("freedv-", StringComparison.Ordinal)
            || mode.StartsWith("ms110d-", StringComparison.Ordinal);

        ModemCatalog.RunsIl2pCrc(mode).Should().Be(
            expected, "'{0}' builds a modem calling itself '{1}'", mode, modem.Mode);
    }

    [Theory]
    [InlineData("afsk1200")]        // no IL2P at all
    [InlineData("fsk9600")]         // classic HDLC despite being a NinoTNC mode
    [InlineData("bpsk300-nocrc")]   // already reads plain IL2P
    [InlineData("afsk300-il2p")]
    [InlineData("afsk1200-il2p-nocrc")]
    public void Create_Rejects_Plain_Il2p_Tolerance_On_A_Mode_With_No_Crc_To_Relax(string mode)
    {
        // Refused rather than ignored, on the same reasoning as the centre frequency above: the
        // operator who asked for it believes their modem got more tolerant.
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(AcceptPlainIl2p: true));
        act.Should().Throw<ArgumentException>().WithMessage("*does not run IL2P+CRC*");
    }

    [Theory]
    [InlineData("bpsk300")]
    [InlineData("afsk300-il2pc")]
    [InlineData("qpsk2400")]
    [InlineData("fsk4800-il2p")]
    [InlineData("c4fsk9600")]
    [InlineData("freedv-datac0")]
    [InlineData("ms110d-wn0")]
    public void Create_Accepts_Plain_Il2p_Tolerance_On_An_Il2p_Crc_Mode(string mode)
    {
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(AcceptPlainIl2p: true));
        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Create_Leaves_Every_Mode_Alone_When_Plain_Il2p_Tolerance_Is_Not_Asked_For(string mode)
    {
        // The option is opt-in, so an unset one has to be buildable everywhere - including on the
        // modes where asking for it would throw.
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(AcceptPlainIl2p: null));
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, "multi1")]  // 2·0+1 = 1 branch: a plain single modem
    [InlineData(2, "multi5")]  // 2·2+1 = 5 branches
    public void Create_Threads_OffsetPairs_Into_The_Bpsk_Bank(int offsetPairs, string expectedSuffix)
    {
        IModem modem = ModemCatalog.Create(
            "bpsk300", 12000, Sink, new ModemOptions(OffsetPairs: offsetPairs));
        modem.Mode.Should().EndWith(expectedSuffix);
    }
}
