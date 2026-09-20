using System.Text.Json;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The station file that opts a standalone station in to radio carrier sense.
/// </summary>
/// <remarks>The deciding lives in <see cref="RadioCarrierSenseTests"/>. This is only about whether
/// the file is read correctly and, above all, whether a station without one, or with a broken one,
/// carries on working.</remarks>
public class StationRadioFileTests
{
    [Fact]
    public void The_Station_File_Round_Trips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "carrierSense": {
                "taitPort": "/dev/ttyUSB0",
                "taitBaud": 28800,
                "busyAboveDbm": -110,
                "pollMilliseconds": 200
              }
            }
            """);
        try
        {
            StationRadio? read = StationRadio.Load(path);
            Assert.NotNull(read);
            Assert.True(read.WantsCarrierSense);
            Assert.Equal("/dev/ttyUSB0", read.TaitPort);
            Assert.Equal(28800, read.TaitBaud);
            Assert.Equal(-110, read.BusyAboveDbm);
            Assert.Equal(200, read.PollMilliseconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_File_With_No_Carrier_Sense_Section_Leaves_The_Feature_Off()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "somethingElse": 1 }""");
        try
        {
            Assert.Null(StationRadio.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Broken_File_Does_Not_Take_The_Daemon_Down()
    {
        // A modem constructor that throws takes daemon start-up with it, which is how a bad
        // profile once cost a station its whole service. A typo in an optional file must not.
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json at all");
        try
        {
            Assert.Null(StationRadio.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Missing_File_Is_Not_An_Error()
    {
        Assert.Null(StationRadio.Load(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Only_The_Baud_Defaults_To_This_Benchs_Value()
    {
        // Every other default is off. 28800 is only a default because a port that is not named is
        // never opened, so the number costs nothing until someone opts in.
        var off = new StationRadio();
        Assert.Null(off.TaitPort);
        Assert.Null(off.BusyAboveDbm);
        Assert.False(off.WantsCarrierSense);
        Assert.Equal(28800, off.TaitBaud);
    }

    [Fact]
    public void The_Shipped_Example_Parses()
    {
        // The example carries a "_comment" block, which only works because unknown properties are
        // ignored. An example that does not load is worse than no example.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !Directory.Exists(Path.Combine(dir.FullName, "docs", "dev", "ofdm-fm")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string path = Path.Combine(
            dir.FullName, "docs", "dev", "ofdm-fm", "ofdm-fm.station.json");
        Assert.True(File.Exists(path), $"missing {path}");

        StationRadio? read = StationRadio.Load(path);

        Assert.NotNull(read);
        Assert.True(read.WantsCarrierSense);
        Assert.Equal("/dev/ttyUSB0", read.TaitPort);
        Assert.Equal(-75, read.BusyAboveDbm);
    }
}
