namespace Packet.SoundModem.Tests.Modems;

using AwesomeAssertions;
using Packet.SoundModem.Modems;
using Xunit;

/// <summary>
/// The FM metadata is a single table now, and these hold it to what Nino actually published.
/// </summary>
public class FmModeProfileTests
{
    [Theory]
    [InlineData("afsk1200", 1248, 3000)]
    [InlineData("fsk9600", 999, 2400)]
    [InlineData("fsk4800", 500, 1200)]
    [InlineData("c4fsk9600", 1039, 2500)]
    [InlineData("c4fsk19200", 2079, 5000)]
    public void Every_Tuning_Tone_Nulls_At_Its_Own_Target_Deviation(
        string mode, double toneHz, double deviationHz)
    {
        // This is what identifies that column as tuning tones rather than carriers, and it is worth
        // a test because reading it the other way produced a false interop alarm: Nino's v44 table
        // groups c4fsk19200 and qpsk3600 on one row at 2079 Hz, which looks like a claim that
        // qpsk3600's carrier moved from 1650 Hz. It had not. A carrier modulated by a single tone
        // loses its own component at a modulation index of 2.405, so tone x 2.405 = deviation.
        FmModeProfile profile = FmModeProfiles.For(mode)!;

        profile.BesselNullToneHz.Should().Be(toneHz);
        FmModeProfile.DeviationFromTone(toneHz).Should().BeApproximately(
            deviationHz,
            deviationHz * 0.005,
            "{0}'s published tuning tone must null at its published target deviation, or one of "
            + "the two figures is wrong",
            mode);
        profile.TargetPeakDeviationHz.Should().Be(deviationHz);
    }

    [Theory]
    [InlineData("afsk1200-il2p")]
    [InlineData("afsk1200-fx25")]
    [InlineData("fsk9600-il2p")]
    [InlineData("fsk4800-il2p")]
    public void A_Framing_Variant_Inherits_Its_Modulator(string mode)
    {
        // Framing does not change what the modulator does, so a variant must resolve to its base
        // mode rather than needing to be listed. The old hand-maintained table listed each variant
        // by name, which is how one gets forgotten.
        FmModeProfile? variant = FmModeProfiles.For(mode);
        FmModeProfile? baseMode = FmModeProfiles.For(mode[..mode.IndexOf('-')]);

        variant.Should().NotBeNull();
        variant.Should().Be(baseMode);
    }

    [Fact]
    public void An_Ssb_Mode_Has_No_Fm_Profile()
    {
        // Asking what deviation a BPSK 300 SSB mode runs at is a question with no answer, and
        // returning a default would be a fiction that reads like a measurement.
        FmModeProfiles.For("bpsk300").Should().BeNull();
        FmModeProfiles.IsFmMode("bpsk300").Should().BeFalse();
        FmModeProfiles.IsFmMode("afsk300").Should().BeFalse();
    }

    [Fact]
    public void Minimum_Shift_Keying_Modes_Sit_At_A_Quarter_Of_Their_Baud()
    {
        // Nino: "The tones selected in 4800 and 9600 mode provide Minimum Shift Keying, but more
        // deviation may work well too." So these two sitting at 48 % of full deviation for their
        // channel is a deliberate modulation index, not wasted headroom, and anyone tempted to
        // raise them for the sake of level should know they are changing the signal's geometry.
        FmModeProfiles.For("fsk4800")!.TargetPeakDeviationHz.Should().Be(4800 / 4.0);
        FmModeProfiles.For("fsk9600")!.TargetPeakDeviationHz.Should().Be(9600 / 4.0);
    }

    [Fact]
    public void Full_Deviation_Follows_The_Channel_Spacing()
    {
        FmModeProfile.FullDeviationHz(12500).Should().Be(2500);
        FmModeProfile.FullDeviationHz(20000).Should().Be(4000);
        FmModeProfile.FullDeviationHz(25000).Should().Be(5000);

        // qpsk3600 is the outlier, and both halves of it are asserted: Nino publishes 5 kHz, which
        // is 200 % of full deviation for the 12.5 kHz channel he puts the mode on, and we transmit
        // 2500. Keeping both visible is the point - a divergence from someone else's published
        // figure must be legible as a divergence, not hidden by an edit.
        FmModeProfile qpsk = FmModeProfiles.For("qpsk3600")!;
        qpsk.PercentOfFullDeviation.Should().BeApproximately(
            200, 0.1, "that is what NinoTNC publishes and this field records it");
        qpsk.DivergesFromPublished.Should().BeTrue();
        qpsk.PeakDeviationHz.Should().Be(2500, "which is 100 % of full deviation for its channel");
        qpsk.RecommendationRemark.Should().NotBeNullOrWhiteSpace(
            "a divergence without a stated reason is just an unexplained difference");
    }

    [Fact]
    public void A_Divergence_Always_Carries_Its_Reason()
    {
        // The guard on the mechanism itself: nobody gets to quietly recommend something other than
        // the published figure without saying why.
        foreach ((string mode, FmModeProfile profile) in FmModeProfiles.Profiles)
        {
            if (profile.DivergesFromPublished)
            {
                profile.RecommendationRemark.Should().NotBeNullOrWhiteSpace(
                    "{0} recommends a deviation other than the published one", mode);
            }
        }
    }

    [Fact]
    public void Every_Fm_Catalogue_Mode_Has_A_Profile()
    {
        // The ladder takes its mode list from the catalogue now rather than from a hand-written
        // list, so a new FM mode joins by existing. This is what stops it joining silently without
        // a deviation.
        foreach (string mode in ModemCatalog.KnownModes.Where(FmModeProfiles.IsFmMode))
        {
            FmModeProfiles.For(mode).Should().NotBeNull(
                "{0} is an FM mode and must say what deviation it wants", mode);
        }
    }
}
