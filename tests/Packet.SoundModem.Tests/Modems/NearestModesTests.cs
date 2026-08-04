using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The "did you mean" behind the daemon's unknown-mode error. With 38 mode names differing by a
/// hyphen or a digit, naming the near miss is most of the help an operator needs.
/// </summary>
public class NearestModesTests
{
    [Theory]
    // A dropped hyphen - the single most likely way to mistype these.
    [InlineData("fsk9600il2p", "fsk9600-il2p")]
    [InlineData("afsk1200il2p", "afsk1200-il2p")]
    // A hyphen that should not be there.
    [InlineData("bpsk-300", "bpsk300")]
    // Wrong case: the catalogue is ordinal, so this is a genuine miss worth naming.
    [InlineData("AFSK1200", "afsk1200")]
    [InlineData("QPSK2400", "qpsk2400")]
    // Trailing whitespace, as left by a careless edit.
    [InlineData("qpsk2400 ", "qpsk2400")]
    // A transposition.
    [InlineData("afks1200", "afsk1200")]
    public void A_Near_Miss_Suggests_The_Mode_That_Was_Meant(string typo, string expected)
    {
        ModemCatalog.NearestModes(typo).Should().Contain(expected);
    }

    [Fact]
    public void A_Case_Only_Miss_Suggests_Exactly_One_Mode_And_Not_A_Distance_Scattering()
    {
        // The cased hit is the whole answer; offering neighbours alongside it would be noise.
        ModemCatalog.NearestModes("MS110D-WN13").Should().Equal("ms110d-wn13");
    }

    [Fact]
    public void Something_Unrelated_Suggests_Nothing_Rather_Than_A_Bad_Guess()
    {
        ModemCatalog.NearestModes("totalnonsense").Should().BeEmpty();
        ModemCatalog.NearestModes("").Should().BeEmpty();
        ModemCatalog.NearestModes("   ").Should().BeEmpty();
    }

    [Fact]
    public void Suggestions_Are_Capped_So_The_Journal_Does_Not_Fill_With_Guesses()
    {
        ModemCatalog.NearestModes("ms110d-wn14").Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public void Every_Known_Mode_Suggests_Itself()
    {
        // A real mode should never be reported as unknown, but if the daemon's IsKnown check and
        // this ever disagree, the suggestion must still be the mode itself.
        foreach (string mode in ModemCatalog.KnownModes)
        {
            ModemCatalog.NearestModes(mode).Should().Contain(mode, $"'{mode}' is a real mode");
        }
    }

    [Fact]
    public void The_Catalogue_Has_No_Duplicates()
    {
        ModemCatalog.KnownModes.Should().OnlyHaveUniqueItems();
    }
}
