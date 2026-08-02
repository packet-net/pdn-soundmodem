using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The operator-facing half of configuration loading. These messages are what an admin reads in
/// `journalctl` after the service refuses to start, so they are worth pinning: every failure must
/// name the file, say what is wrong in words, and say what to do about it — never surface a raw
/// exception. See CONFIG.md § What is rejected at start-up.
/// </summary>
public class DaemonConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Every failure message must be actionable, not just accurate.</summary>
    private static void ShouldGuideTheOperator(string error, string path)
    {
        error.Should().Contain(path, "the operator has to know which file to edit");
        error.Should().Contain("systemctl restart pdn-soundmodem",
            "the message must say how to apply the fix");
        error.Should().Contain("CONFIG.md", "the message must point at the reference");
        error.Should().NotContain("Exception", "a stack trace is not an explanation");
        error.Should().NotContain("   at ", "a stack trace is not an explanation");
    }

    [Fact]
    public void A_Valid_File_Loads_And_Reports_No_Error()
    {
        string path = WriteConfig("""{"device": "null", "kissPort": 8105}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull();
        error.Should().BeEmpty();
        config!.Device.Should().Be("null");
    }

    [Fact]
    public void An_Empty_Object_Is_Valid_And_Yields_One_Default_Modem()
    {
        string path = WriteConfig("{}");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config.Should().NotBeNull();
        config!.Modems.Should().ContainSingle();
        config.Modems[0].SubChannel.Should().Be(0);
        config.Modems[0].Mode.Should().Be("afsk1200");
    }

    [Fact]
    public void Comments_And_Trailing_Commas_Are_Accepted_Because_The_Shipped_Example_Uses_Them()
    {
        string path = WriteConfig("""
            {
              // the annotated example is full of these
              /* and these */
              "device": "null",
              "modems": [ { "subChannel": 0, "mode": "afsk1200", }, ],
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Modems.Should().ContainSingle();
    }

    [Fact]
    public void Malformed_Json_Names_The_Line_And_Position()
    {
        string path = WriteConfig("""
            {
              "device": "null",
              "modems": [ { "subChannel": 0, "mode": } ]
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("not valid JSON");
        // Counted from 1, the way an editor does — System.Text.Json counts from 0.
        error.Should().Contain("line 3", "the operator needs to be told where to look");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Duplicate_Sub_Channel_Says_Which_One_And_What_To_Do()
    {
        string path = WriteConfig("""
            {"device": "null", "modems": [
              {"subChannel": 1, "mode": "afsk1200"},
              {"subChannel": 1, "mode": "bpsk300"}
            ]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("subChannel").And.Contain("1");
        error.Should().Contain("renumber");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void Ardop_Beside_Modems_Explains_The_Exclusivity_And_Both_Ways_Out()
    {
        string path = WriteConfig("""
            {"device": "null", "ardop": {"port": 8515},
             "modems": [{"subChannel": 0, "mode": "afsk1200"}]}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("ardop").And.Contain("dedicated");
        error.Should().Contain("delete", "the operator needs to be told which way out to take");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void An_Empty_File_Says_So_Rather_Than_Talking_About_Json_Tokens()
    {
        string path = WriteConfig("   \n  ");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("the file is empty");
        error.Should().NotContain("JSON tokens");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_File_Of_Literal_Null_Offers_A_Minimal_Working_Config()
    {
        string path = WriteConfig("null");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("only `null`");
        error.Should().Contain("afsk1200", "showing a working file beats describing one");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void The_Suggested_Commands_Never_Assume_Sudo_Exists()
    {
        // Debian installs sudo only when the root password is left blank at setup, so a good
        // number of the machines this runs on do not have it. The message says "as root" and
        // gives bare commands, which is right on those and on sudo systems alike.
        string path = WriteConfig("{ not json");

        DaemonConfig.TryLoad(path, out string error);

        error.Should().NotContain("sudo", "the command would not exist on a sudo-less Debian");
        error.Should().Contain("As root", "the privilege needed has to be stated some other way");
    }

    [Fact]
    public void A_Missing_File_Is_Reported_As_Configuration_Not_As_A_Crash()
    {
        string path = Path.Combine(_dir, "does-not-exist.json");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("no such file");
        ShouldGuideTheOperator(error, path);
    }

    [Fact]
    public void A_Missing_Directory_Names_The_Directory()
    {
        string path = Path.Combine(_dir, "nope", "soundmodem.json");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull();
        error.Should().Contain("no such directory");
        ShouldGuideTheOperator(error, path);
    }
}
