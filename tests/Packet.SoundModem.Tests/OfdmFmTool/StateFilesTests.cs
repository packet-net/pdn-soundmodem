using AwesomeAssertions;
using Packet.SoundModem.OfdmFmTool;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class StateFilesTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) => name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void The_file_is_under_xdg_state_home_when_it_is_set()
    {
        string? path = StateFiles.NotePath("G4ABC-1", "pipe:a,b", Env(("XDG_STATE_HOME", "/var/state"), ("HOME", "/home/pi")));

        path.Should().StartWith("/var/state/pdn-ofdm/").And.EndWith(".txt");
    }

    [Fact]
    public void The_file_is_under_local_state_in_the_home_otherwise()
    {
        StateFiles.NotePath("G4ABC-1", "pipe:a,b", Env(("HOME", "/home/pi"))).Should().StartWith("/home/pi/.local/state/pdn-ofdm/");
        StateFiles.NotePath("G4ABC-1", "pipe:a,b", Env(("XDG_STATE_HOME", ""), ("HOME", "/home/pi"))).Should().StartWith("/home/pi/.local/state/pdn-ofdm/");
        StateFiles.NotePath("G4ABC-1", "pipe:a,b", Env(("XDG_STATE_HOME", "relative"), ("HOME", "/home/pi"))).Should().StartWith("/home/pi/.local/state/pdn-ofdm/");
    }

    [Fact]
    public void With_no_home_there_is_no_file()
    {
        StateFiles.NotePath("G4ABC-1", "pipe:a,b", Env()).Should().BeNull();
    }

    [Fact]
    public void The_same_callsign_on_two_devices_has_two_files()
    {
        Func<string, string?> env = Env(("HOME", "/home/pi"));

        StateFiles.NotePath("G4ABC-1", "plughw:CARD=A,DEV=0", env).Should().NotBe(StateFiles.NotePath("G4ABC-1", "plughw:CARD=B,DEV=0", env));
    }
}
