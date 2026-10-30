using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The journal line for a keyup whose DAX transmit audio fell behind the radio (issue #748).
/// </summary>
public class TransmitStallJournalTests
{
    private static readonly DateTimeOffset KeyedAt = new(2026, 10, 30, 6, 41, 58, 460, TimeSpan.Zero);

    [Fact]
    public void One_Line_Names_The_Keyup_Says_What_Happened_And_What_To_Do()
    {
        string line = TransmitStallJournal.Line(new TransmitStallReport(
            KeyedAt, TimeSpan.FromSeconds(7.08), Stalls: 1, Worst: TimeSpan.FromMilliseconds(38.2)));

        line.Should().Be(
            "tx: keyup from 06:41:58.4 UTC (7.1 s): DAX audio fell up to 38 ms behind 1 time, "
            + "so the radio sent a gap; give this host more CPU");
    }

    [Fact]
    public void Several_Stalls_Read_As_Several_And_The_Line_Is_Plain_Ascii()
    {
        string line = TransmitStallJournal.Line(new TransmitStallReport(
            KeyedAt, TimeSpan.FromSeconds(12), Stalls: 3, Worst: TimeSpan.FromMilliseconds(61)));

        line.Should().Contain("61 ms behind 3 times");
        line.Should().NotContain("\n");
        line.All(c => c < 0x80).Should().BeTrue("journalctl shows anything else as escapes");
    }
}
