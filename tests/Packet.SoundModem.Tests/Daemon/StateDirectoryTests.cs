using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The default paths the daemon keeps files at follow systemd's state directory, so a template
/// instance (<c>pdn-soundmodem@NAME</c>, state under <c>/var/lib/pdn-soundmodem/NAME</c>) never
/// shares a frame log, a survey directory or a raw-capture directory with the plain unit or with
/// another instance. Tested against an explicit value rather than the process environment: the
/// suite runs in parallel and other tests construct <see cref="DaemonConfig"/>.
/// </summary>
public class StateDirectoryTests
{
    [Fact]
    public void A_Default_Path_Sits_Under_The_State_Directory_Systemd_Sets()
    {
        StateDirectory.PathFor("frames.db", "/var/lib/pdn-soundmodem/tm8100")
            .Should().Be("/var/lib/pdn-soundmodem/tm8100/frames.db");
    }

    [Fact]
    public void Without_A_State_Directory_A_Default_Path_Is_The_Packaged_Location()
    {
        StateDirectory.PathFor("frames.db", null).Should().Be("/var/lib/pdn-soundmodem/frames.db");
        StateDirectory.PathFor("survey", null).Should().Be("/var/lib/pdn-soundmodem/survey");
        StateDirectory.PathFor("raw", null).Should().Be("/var/lib/pdn-soundmodem/raw");
    }

    [Fact]
    public void The_First_Of_Several_State_Directories_Is_This_Units_Own()
    {
        StateDirectory.FirstOf("/var/lib/a:/var/lib/b").Should().Be("/var/lib/a");
        StateDirectory.FirstOf("/var/lib/a").Should().Be("/var/lib/a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":/var/lib/b")]
    public void An_Unset_Or_Empty_Variable_Means_No_State_Directory(string? value)
    {
        StateDirectory.FirstOf(value).Should().BeNull();
    }
}
