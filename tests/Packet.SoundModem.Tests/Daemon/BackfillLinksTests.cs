using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The links pane as a station comes back up: warmed from the frame log so a link that was up a
/// moment ago is up on the first page load, and warmed from the same frames the live path would
/// have shown the observer - which does not include the ones Reed-Solomon alone stood behind.
/// </summary>
/// <remarks>
/// Two feeds, one rule. Skipping a withheld frame as it is heard and replaying it out of the log
/// on the next restart would put back exactly the cards the live path refused, and the operator
/// would see them appear on a restart and never again.
/// </remarks>
public class BackfillLinksTests : IDisposable
{
    private const int SampleRate = 12000;

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-backfill").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 4, 9, 15, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DbPath => Path.Combine(_dir, "frames.db");

    /// <summary>A UI frame, so the observer has a pair of callsigns to make a link of.</summary>
    private static byte[] Frame(string from, string to) =>
        Ax25UiFrame.Build(from, to, "hi there"u8);

    /// <summary>A verified reading: the trailing CRC checked out and the host was given it.</summary>
    private static FrameQuality Verified() =>
        new("bpsk300-il2pc", FrameBytes: 24, CorrectedBytes: 0, CrcValid: true);

    /// <summary>The plain reading of an IL2P+CRC link: no trailer behind it, withheld.</summary>
    private static FrameQuality Withheld() =>
        new("bpsk300-il2pc", FrameBytes: 24, CorrectedBytes: 0, CrcValid: null,
            PlainIl2p: true, MonitorOnly: true);

    [Fact]
    public async Task A_Withheld_Row_Is_Replayed_Into_No_Link_And_A_Verified_One_Into_Its_Own()
    {
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        log.Record(0, Frame("G0AAA", "GB7RDG"), Verified(), audioHz: 1500, rfHz: 7_051_600);
        log.Record(0, Frame("G0BBB", "GB7RDG"), Withheld(), audioHz: 1500, rfHz: 7_051_600);

        for (int i = 0; i < 100 && log.Recent(10).Count < 2; i++)
        {
            await Task.Delay(20);
        }

        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 11), FreePorts.Next());

        StationFactory.BackfillLinks(server, log);

        server.Links.Snapshot().Should().ContainSingle(
            "the verified frame is history worth reopening and the withheld one never was a link")
            .Which.Id.Should().Be("0|G0AAA<>GB7RDG");
    }

    /// <summary>
    /// A row with no CRC verdict is replayed like any other, because most of them are not
    /// withheld frames.
    /// </summary>
    /// <remarks>
    /// The obvious test for "did anything check this frame" is a null <c>crc_valid</c>, and it is
    /// the wrong one: that column is null on HDLC, on FX.25, on our own transmissions and on every
    /// row a station wrote before the withheld flag existed. Reading null as "withheld" would
    /// empty the pane on every port that does not run IL2P+CRC. The transmitted row here is the
    /// stronger half of the case: its <c>monitor_only</c> is null on the way in, exactly as an old
    /// log's rows are after the column is migrated onto them, and it still makes its link.
    /// </remarks>
    [Fact]
    public async Task A_Row_With_No_Crc_Verdict_Is_Still_A_Link()
    {
        await using FrameLog log = FrameLog.Open(DbPath, _time);

        // Two framings with nothing to say about a CRC, neither of them withheld.
        log.Record(0, Frame("G0CCC", "GB7RDG"), new FrameQuality(
            "afsk1200", FrameBytes: 24, CorrectedBytes: null, CrcValid: null), null, null);
        log.RecordTransmitted(0, Frame("M0LTE", "G0CCC"), "afsk1200", 1500, 7_051_600);

        for (int i = 0; i < 100 && log.Recent(10).Count < 2; i++)
        {
            await Task.Delay(20);
        }

        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 12), FreePorts.Next());

        StationFactory.BackfillLinks(server, log);

        server.Links.Snapshot().Select(l => l.Id).Should().BeEquivalentTo(
            ["0|G0CCC<>GB7RDG", "0|G0CCC<>M0LTE"],
            "an HDLC frame and our own transmission are both links, and neither has a CRC verdict");
    }
}
