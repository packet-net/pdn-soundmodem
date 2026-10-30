using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// A <c>M0LTE_FLEX_TX_LEAD_MS</c> that M0LTE.Flex did not take is said at start-up, so a rollback
/// with a typo in it cannot quietly leave the 80 ms lead running.
/// </summary>
public class FlexTransmitLeadVariableTests
{
    private static readonly TimeSpan Default = TimeSpan.FromMilliseconds(80);

    [Theory]
    [InlineData(null, 80)]
    [InlineData("", 80)]
    [InlineData("0", 0)]
    [InlineData("off", 0)]
    [InlineData(" OFF ", 0)]
    [InlineData("40", 40)]
    [InlineData("80", 80)]
    public void A_Value_That_Was_Taken_Says_Nothing(string? raw, int inForceMs)
    {
        FlexStationDevice.TransmitLeadProblem(raw, TimeSpan.FromMilliseconds(inForceMs)).Should().BeNull();
    }

    [Theory]
    [InlineData("of")]
    [InlineData("40ms")]
    [InlineData("-1")]
    [InlineData("600")]
    [InlineData("false")]
    public void A_Value_That_Was_Not_Taken_Says_What_Is_In_Force_And_How_To_Turn_It_Off(string raw)
    {
        string? line = FlexStationDevice.TransmitLeadProblem(raw, Default);

        line.Should().Be(
            $"M0LTE_FLEX_TX_LEAD_MS={raw} not understood, so the DAX transmit lead is 80 ms; use 0 or off to turn it off, or 1 to 500 for ms");
        line.Should().MatchRegex("^[\\x20-\\x7e]+$", "printed lines are plain ASCII");
    }
}
