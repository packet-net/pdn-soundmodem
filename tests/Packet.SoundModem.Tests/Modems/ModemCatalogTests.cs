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
        ModemCatalog.KnownModes.Should().HaveCount(36);
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
    [InlineData("freedv-datac0", false)]
    [InlineData("ms110d-wn6", false)]
    public void AcceptsCentreFrequency_Matches_Family(string mode, bool expected)
    {
        ModemCatalog.AcceptsCentreFrequency(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("bpsk300", PskDetector.Differential)]
    [InlineData("bpsk1200", PskDetector.Differential)]
    [InlineData("qpsk600", PskDetector.Coherent)]
    [InlineData("qpsk3600", PskDetector.Coherent)]
    public void DefaultDetectorFor_Is_Differential_For_Bpsk_Coherent_For_Qpsk(string mode, PskDetector expected)
    {
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
    [InlineData("freedv-datac0")]
    [InlineData("ms110d-wn0")]
    public void Create_Rejects_A_Centre_Frequency_On_A_Fixed_Centre_Mode(string mode)
    {
        Action act = () => ModemCatalog.Create(
            mode, ModemCatalog.DspRateFor(mode), Sink, new ModemOptions(CentreFrequencyHz: 1500));
        act.Should().Throw<ArgumentException>().WithMessage("*fixed centre frequency*");
    }

    [Fact]
    public void Create_Accepts_A_Centre_Frequency_On_A_Carrier_Mode()
    {
        Action act = () => ModemCatalog.Create(
            "bpsk300", 12000, Sink, new ModemOptions(CentreFrequencyHz: 1600));
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
