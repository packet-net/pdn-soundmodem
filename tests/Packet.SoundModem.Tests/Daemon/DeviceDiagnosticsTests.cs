using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The message a fresh install produces. The seeded config names a CM108 on /dev/hidraw0, so on
/// most machines this is literally the first thing an admin ever reads about pdn-soundmodem -
/// it has to name the setting, name the file, and give the command that lists what is really
/// there. See CONFIG.md § What is rejected at start-up.
/// </summary>
public class DeviceDiagnosticsTests
{
    private const string ConfigPath = "/etc/pdn-soundmodem/soundmodem.json";

    [Fact]
    public void A_Missing_Cm108_Names_The_Setting_The_File_And_The_Udev_Trap()
    {
        var ptt = new PttConfig { Type = "cm108", Device = "/dev/hidraw0", Gpio = 3 };

        string message = DeviceDiagnostics.Ptt(
            ptt, ConfigPath, new FileNotFoundException("Could not find file '/dev/hidraw0'."));

        message.Should().Contain("/dev/hidraw0", "the operator must see which device failed");
        message.Should().Contain("\"ptt\"").And.Contain(ConfigPath);
        message.Should().Contain("ls -l /dev/hidraw*", "tell them how to see what is really there");
        message.Should().Contain("udev", "permission-denied on hidraw is the usual cause");
        message.Should().Contain("retrying", Exactly.Once(), "hardware may just be late");
    }

    [Fact]
    public void A_Missing_Serial_Port_Points_At_Stable_By_Id_Names_And_Dialout()
    {
        var ptt = new PttConfig { Type = "serial", Device = "/dev/ttyUSB0", Line = "rts" };

        string message = DeviceDiagnostics.Ptt(
            ptt, ConfigPath, new UnauthorizedAccessException("Access to the port is denied."));

        message.Should().Contain("/dev/serial/by-id/", "the stable name is the better answer");
        message.Should().Contain("dialout");
        message.Should().NotContain("udev", "the hidraw advice would be a red herring for serial");
    }

    [Fact]
    public void A_Missing_Sound_Card_Names_The_Setting_And_How_To_List_Cards()
    {
        string message = DeviceDiagnostics.Audio(
            "plughw:99,0", ConfigPath,
            new InvalidOperationException("snd_pcm_open(plughw:99,0): Invalid argument"));

        message.Should().Contain("plughw:99,0");
        message.Should().Contain("\"device\"").And.Contain(ConfigPath);
        message.Should().Contain("aplay -l", "tell them how to see what is really there");
        message.Should().Contain("plughw:CARD=", "steer them to a name that survives re-plugging");
    }

    [Fact]
    public void Without_A_Config_File_The_Message_Blames_The_Command_Line_Flag_Instead()
    {
        // Running the daemon by hand with flags and no --config: pointing at a file that does
        // not exist would send the operator somewhere useless.
        string message = DeviceDiagnostics.Audio(
            "bogus", configPath: null, new InvalidOperationException("nope"));

        message.Should().Contain("--device").And.Contain("command line");
        message.Should().NotContain("soundmodem.json");
    }

    [Fact]
    public void Device_Failures_Never_Read_As_Permanent_Because_The_Unit_Keeps_Retrying()
    {
        // These exit 1, not 2 - RestartPreventExitStatus=2 must not catch them, so the message
        // must not tell the operator the service has given up.
        string audio = DeviceDiagnostics.Audio("x", ConfigPath, new IOException("boom"));
        string ptt = DeviceDiagnostics.Ptt(
            new PttConfig { Type = "serial", Device = "x" }, ConfigPath, new IOException("boom"));

        foreach (string message in new[] { audio, ptt })
        {
            message.Should().Contain("keep retrying");
            message.Should().NotContain("will not start until",
                "that phrasing belongs to the config errors, which really do stay stopped");
        }
    }
}
