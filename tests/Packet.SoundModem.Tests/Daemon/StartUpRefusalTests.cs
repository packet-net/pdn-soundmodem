using AwesomeAssertions;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The FM refusals and fallbacks that live in the daemon's top-level statements, asserted
/// against the source text.
/// </summary>
/// <remarks>
/// <para>The same trick as <see cref="StartUpOrderTests"/>, and for the same reason: these are
/// real properties of start-up that no unit test can reach, because <c>Program.cs</c> is a
/// script that runs once, opens sound cards and never returns. Reading the file is cheaper than
/// leaving them unguarded, and each of them was found by running the daemon rather than by
/// reading it - one of them after a review reproduced a station that would not start.</para>
/// <para>Ugly, and much better than nothing. Each assertion names what to do if the code it
/// anchors on is renamed: update the test, do not delete it.</para>
/// </remarks>
public class StartUpRefusalTests
{
    [Fact]
    public void An_Fm_Ident_Can_Default_To_The_Centre_The_Plan_Says_The_Modem_Is_On()
    {
        // On SSB the planner writes each modem's audio centre back into "frequency", so an
        // "identify" with no "toneHz" defaults to it. On FM it deliberately writes nothing back,
        // which left a perfectly ordinary FM station refusing to start with "mode 'afsk1200' has
        // no audio centre" printed directly under a report line saying 1700 Hz audio. The plan is
        // where the centre is written down on FM, so the fallback reads it from there.
        string source = Program();

        int fallback = source.IndexOf("plannedCentreHz", StringComparison.Ordinal);
        int refusal = source.IndexOf("has no audio centre", StringComparison.Ordinal);

        fallback.Should().BeGreaterThan(
            -1, "the ident tone falls back to the plan's AudioCentreHz through plannedCentreHz; "
              + "if that was renamed, update this test rather than deleting it");
        refusal.Should().BeGreaterThan(-1, "and a baseband mode is still refused by name");
        fallback.Should().BeLessThan(
            refusal,
            "the plan has to be consulted before the ident gives up, or an FM station with an "
            + "\"identify\" and no \"toneHz\" will not start at all");
        source.Should().Contain(
            "modemConfig.Frequency ?? plannedCentreHz",
            "the configured centre still wins where there is one, which is every SSB station: "
            + "the planner has already written it back there and nothing about that path moves");
    }

    [Fact]
    public void An_Ident_Placed_By_Rf_Is_Refused_On_Fm_Before_Any_Arithmetic_Runs()
    {
        // "identify"."rfFrequency" is a dial-minus-tone sum, and on FM there is no dial to
        // subtract from: the channel is the RF and the ident is a tone sent over it.
        string source = Program();

        int refusal = source.IndexOf("\"rfFrequency\\\" has no meaning on FM", StringComparison.Ordinal);
        int arithmetic = source.IndexOf("identRf - bandPlan.DialHz", StringComparison.Ordinal);

        refusal.Should().BeGreaterThan(-1, "the refusal says FM in those words");
        arithmetic.Should().BeGreaterThan(-1, "and the sum it guards is still there for SSB");
        refusal.Should().BeLessThan(
            arithmetic, "the refusal has to come first, or the nonsense tone is worked out anyway");
    }

    [Fact]
    public void Fm_Is_Refused_On_A_Web_Receiver_Before_It_Is_Tuned()
    {
        // A web receiver hands the daemon single-sideband IQ. Left to run, the tuning below takes
        // "not LSB" as USB and the station demodulates one sideband of an FM signal in silence.
        string source = Program();

        int refusal = source.IndexOf(
            "cannot be served by {uberSdrEndpoint}", StringComparison.Ordinal);
        int tuning = source.IndexOf("new UberSdrTuning", StringComparison.Ordinal);

        refusal.Should().BeGreaterThan(-1, "the refusal is in the station's UberSDR branch");
        tuning.Should().BeGreaterThan(-1, "and the tuning it guards is built just below it");
        refusal.Should().BeLessThan(tuning, "nothing may be tuned for a radio this cannot serve");
    }

    [Fact]
    public void A_Flex_Slice_Contradiction_Says_Which_Kind_Of_Disagreement_It_Is()
    {
        // "Every modem would land mirrored about the dial" is true of USB against LSB and is
        // nonsense when one of the two is FM, where the disagreement is not about which side of a
        // carrier the audio is on but about whether there is a carrier at all.
        string source = Program();

        source.Should().Contain(
            "Every modem would land mirrored about the dial",
            "the sideband-against-sideband sentence is unchanged, word for word");
        source.Should().Contain(
            "One of those is a channel radio and the other is not",
            "and FM against a sideband gets its own");
    }

    [Fact]
    public void Only_Fm_Falls_Through_From_The_Stations_Kind_To_The_Pages()
    {
        // The page's kind is the band plan's where there is one and the "waterfall" section's
        // own otherwise, so a station placed by audio centre says it twice. FM is the exception:
        // a page told nothing draws an RF scale that is a lie, which is the whole of #413. The
        // two sidebands must not join it, because an SSB page told nothing draws a USB scale and
        // has always drawn one - changing that would move a page nobody asked to have moved.
        string source = Program();

        source.Should().Contain(
            "string pageSideband =",
            "the page's kind is settled once and used by every reader of it");
        source.Should().Contain(
            "!waterfallSidebandWasStated && RfPlan.IsFmRadio(sideband) ? sideband : null",
            "and FM is the only kind that falls through from the station's own setting");
        source.Should().NotContain(
            "bandPlan?.Sideband ?? waterfallConfig",
            "every site that used to work the kind out for itself reads pageSideband now, or one "
            + "of them will quietly stop agreeing with the others");
    }

    private static string Program() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs"));

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
