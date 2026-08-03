using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Reporting audio the sound device lost.
/// </summary>
/// <remarks>
/// ALSA recovers from an xrun by restarting the stream, silently: every one is a hole where audio
/// should have been. They were counted and never reported, so a station being starved of CPU
/// looked exactly like a station on a quiet band.
/// </remarks>
public class XrunWatchTests
{
    [Fact]
    public void Silence_While_Nothing_Is_Lost()
    {
        var watch = new XrunWatch();

        watch.Poll(0, 0).Should().BeNull();
        watch.Poll(0, 0).Should().BeNull("a healthy station must not chatter");
    }

    [Fact]
    public void Only_What_Was_Lost_Since_The_Last_Look_Is_Reported()
    {
        // Counters are cumulative; reporting the total every time would read as a fresh disaster
        // every ten seconds long after the machine settled down.
        var watch = new XrunWatch();
        watch.Poll(3, 0).Should().NotBeNull();

        watch.Poll(3, 0).Should().BeNull("nothing new was lost");

        string? more = watch.Poll(5, 0);
        more.Should().NotBeNull();
        more.Should().Contain("2 capture overruns").And.Contain("5 since start");
    }

    [Fact]
    public void Capture_And_Playback_Losses_Are_Named_Separately()
    {
        // They are different faults: a capture hole drops a frame that was on the air, a playback
        // hole puts a discontinuity into what we transmitted.
        var watch = new XrunWatch();

        string? line = watch.Poll(1, 2);

        line.Should().Contain("1 capture overrun").And.Contain("2 playback underruns");
        line.Should().NotContain("1 capture overruns", "one is not plural");
    }

    [Fact]
    public void The_Explanation_Is_Given_Once_And_Then_The_Counts_Speak_For_Themselves()
    {
        var watch = new XrunWatch();

        string first = watch.Poll(1, 0)!;
        string second = watch.Poll(2, 0)!;

        first.Should().Contain("not scheduling the modem", "the first loss must say what it means");
        first.Should().Contain("not a radio problem",
            "the operator's instinct will be to blame the band or the rig");
        second.Should().NotContain("not a radio problem", "repeating the advice is noise");
    }

    [Fact]
    public void A_Counter_That_Goes_Backwards_Does_Not_Report_A_Loss()
    {
        // A device reopened mid-run restarts its count. That is not audio arriving from nowhere.
        var watch = new XrunWatch();
        watch.Poll(10, 10).Should().NotBeNull();

        watch.Poll(0, 0).Should().BeNull("a reset counter is not a recovery to announce");
    }
}
