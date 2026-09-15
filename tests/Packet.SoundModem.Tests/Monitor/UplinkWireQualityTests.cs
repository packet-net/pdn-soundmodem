using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// ARDOP's own 0-100 constellation quality on the uplink wire, read at the parser rather than
/// through a socket - the same reason <see cref="UplinkWireLevelTests"/> is its own class.
/// </summary>
public class UplinkWireQualityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static string Message(string extra = "") =>
        $$"""
        {"type":"frame","sub":2,"mode":"IDFrame","from":"GB7NOT",
         "lenBytes":0,"crc":true,{{extra}}
         "at":"2026-09-15T10:00:00.0000000+00:00"}
        """;

    private static RelayedFrame Read(string extra = "")
    {
        using JsonDocument message = JsonDocument.Parse(Message(extra));
        RelayedFrame? frame = UplinkWire.ReadFrame(message.RootElement, Now);
        frame.Should().NotBeNull("the message is well formed whatever quality it carries");
        return frame!;
    }

    [Fact]
    public void A_Stations_Own_Quality_Crosses_The_Wire()
    {
        Read("\"quality\":78,").Quality.Should().Be(78);
    }

    /// <summary>
    /// A station running a version that does not send the field - which is every non-ARDOP row,
    /// and any ARDOP row from before #479 - reads as null rather than as a claimed zero.
    /// </summary>
    [Fact]
    public void A_Station_That_Sends_No_Quality_Gets_No_Quality_Here()
    {
        Read().Quality.Should().BeNull();
    }
}
