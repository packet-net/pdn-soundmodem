using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// What an operator reads on the waterfall. The modem's own mode string says more than they are
/// choosing between - the framing it settled on and how many branches its diversity bank has -
/// so the label is a rendering of it, not the string itself.
/// </summary>
public class ModeNamesTests
{
    [Theory]
    // The one that prompted this: the bank's branch count is a receiver detail.
    [InlineData("bpsk300-il2pc-multi9", "BPSK300 IL2Pc")]
    [InlineData("afsk1200-multi3", "AFSK1200")]
    [InlineData("bpsk1200-il2p-multi", "BPSK1200 IL2P")]
    // Plain families.
    [InlineData("afsk1200", "AFSK1200")]
    [InlineData("bpsk300-il2pc", "BPSK300 IL2Pc")]
    [InlineData("qpsk2400-il2pc", "QPSK2400 IL2Pc")]
    [InlineData("fsk9600-il2p", "FSK9600 IL2P")]
    [InlineData("c4fsk19200-il2pc", "C4FSK19200 IL2Pc")]
    // Framings that are not IL2P.
    [InlineData("afsk1200-fx25", "AFSK1200 FX.25")]
    [InlineData("afsk1200-fx25rx", "AFSK1200 FX.25 (RX only)")]
    [InlineData("bpsk300-nocrc", "BPSK300 (no CRC)")]
    // Waveform families whose names are not just a modulation.
    [InlineData("freedv-datac1", "FreeDV datac1")]
    [InlineData("freedv-datac13", "FreeDV datac13")]
    [InlineData("ms110d-wn3", "MS110D WN3")]
    [InlineData("ms110d-wn13", "MS110D WN13")]
    [InlineData("ardop", "ARDOP")]
    public void A_Mode_Reads_As_An_Operator_Would_Say_It(string mode, string expected)
    {
        ModeNames.Display(mode).Should().Be(expected);
    }

    [Fact]
    public void Every_Catalogue_Mode_Gets_A_Name()
    {
        foreach (string mode in ModemCatalog.KnownModes)
        {
            string display = ModeNames.Display(mode);

            display.Should().NotBeNullOrWhiteSpace($"'{mode}' must be presentable");
            display.Should().NotContain("-", $"'{mode}' should read as words, not as an id");
        }
    }

    [Fact]
    public void An_Unrecognised_Mode_Still_Reads_As_Itself()
    {
        // A mode this has never seen must not vanish from the display; passing the parts
        // through is worse-looking than a curated name and far better than a blank label.
        ModeNames.Display("newmode1200-someframing").Should().Be("NEWMODE1200 someframing");
    }

    [Fact]
    public void Nothing_In_Gives_Nothing_Out_Rather_Than_Throwing()
    {
        ModeNames.Display(null).Should().BeEmpty();
        ModeNames.Display("").Should().BeEmpty();
        ModeNames.Display("   ").Should().BeEmpty();
    }

    [Fact]
    public void A_Bank_Suffix_Is_Only_Dropped_When_It_Is_One()
    {
        // "multi" as a family, not a suffix, must survive - the rule is positional.
        ModeNames.Display("multi1200").Should().Be("MULTI1200");
    }
}
