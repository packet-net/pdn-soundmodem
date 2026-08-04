using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The station's record of everything it heard. Written on a background thread so the receive
/// path never waits on a disk, and readable while the modem is still running.
/// </summary>
public class FrameLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-log").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 2, 14, 30, 0, TimeSpan.Zero));

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

    /// <summary>An AX.25 UI frame from M0LTE to GB7RDG, so the addresses are real ones to find.</summary>
    private static byte[] Frame(string from = "M0LTE", string to = "GB7RDG")
    {
        var frame = new byte[32];
        WriteAddress(frame, 0, to, last: false);
        WriteAddress(frame, 7, from, last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        for (int i = 16; i < frame.Length; i++)
        {
            frame[i] = (byte)(i * 11);
        }

        return frame;

        static void WriteAddress(byte[] frame, int at, string call, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }

    private static FrameQuality Quality(string mode = "bpsk300-il2pc") =>
        new(mode, FrameBytes: 32, CorrectedBytes: 2, CrcValid: true,
            FrequencyOffsetHz: -3.5, EmphasisDb: null);

    private async Task<List<Dictionary<string, object?>>> ReadBackAsync(Action<FrameLog> write)
    {
        await using (FrameLog log = FrameLog.Open(DbPath, _time))
        {
            write(log);
        }

        var rows = new List<Dictionary<string, object?>>();
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT * FROM frames ORDER BY id";
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task A_Heard_Frame_Is_Written_With_Who_When_And_How_Well()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(2, Frame(), Quality(), audioHz: 2150, rfHz: 7_051_600));

        Dictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        row["source"].Should().Be("M0LTE");
        row["destination"].Should().Be("GB7RDG");
        row["sub_channel"].Should().Be(2L);
        row["mode"].Should().Be("bpsk300-il2pc");
        row["mode_name"].Should().Be("BPSK300 IL2Pc", "a log is read by people too");
        row["length"].Should().Be(32L);
        row["corrected"].Should().Be(2L);
        row["crc_valid"].Should().Be(1L);
        row["offset_hz"].Should().Be(-3.5);
        row["audio_hz"].Should().Be(2150.0);
        row["rf_hz"].Should().Be(7_051_600.0, "where it was heard on the band is the useful column");
        ((string)row["heard_at"]!).Should().StartWith("2026-08-02T14:30:00");
        ((byte[])row["payload"]!).Should().Equal(Frame(), "the frame itself must survive intact");
    }

    [Fact]
    public async Task Every_Frame_Is_Kept_Not_Just_The_Last()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(log =>
        {
            for (int i = 0; i < 250; i++)
            {
                log.Record(0, Frame(), Quality(), null, null);
            }
        });

        rows.Should().HaveCount(250);
    }

    [Fact]
    public async Task A_Frame_With_No_Ax25_Addresses_Is_Still_Recorded()
    {
        // IL2P and the OFDM waveforms carry AX.25 inside, but a frame that will not parse must
        // not be dropped from the log — the payload is the evidence and it is still there.
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, [1, 2, 3, 4], Quality("freedv-datac1"), null, null));

        Dictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        row["source"].Should().BeNull();
        row["mode_name"].Should().Be("FreeDV datac1");
        ((byte[])row["payload"]!).Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public async Task An_Ardop_Frame_Is_Logged_Under_Its_Frame_Type()
    {
        // Every ARDOP entry would otherwise read "ARDOP", which says nothing: the frame type is
        // the interesting part — a connect request and a data frame are different events.
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(
                2, [7, 7, 7], new FrameQuality(
                    "ardop", FrameBytes: 3, CorrectedBytes: null, CrcValid: true,
                    FrequencyOffsetHz: null, EmphasisDb: null),
                audioHz: 1500, rfHz: 7_050_950, modeName: "ARDOP ConReq500M"));

        Dictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        row["mode"].Should().Be("ardop", "the mode column stays queryable");
        row["mode_name"].Should().Be("ARDOP ConReq500M");
    }

    [Fact]
    public async Task Reopening_An_Existing_Log_Appends_Rather_Than_Starting_Again()
    {
        await using (FrameLog first = FrameLog.Open(DbPath, _time))
        {
            first.Record(0, Frame(), Quality(), null, null);
        }

        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, Frame("G8BPQ"), Quality(), null, null));

        rows.Should().HaveCount(2, "a restart must not lose the station's history");
        rows[1]["source"].Should().Be("G8BPQ");
    }

    [Fact]
    public async Task The_Log_Is_Readable_While_The_Modem_Still_Holds_It_Open()
    {
        // WAL, so a logbook or a dashboard can read the file without stopping the modem.
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        log.Record(0, Frame(), Quality(), null, null);

        for (int i = 0; i < 100; i++)
        {
            using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM frames";
            if (Convert.ToInt64(count.ExecuteScalar()) == 1)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("the frame never became visible to a concurrent reader");
    }

    [Fact]
    public async Task The_Newest_Frames_Come_Back_Oldest_First_For_The_Waterfall_Panel()
    {
        // The decoded-frames panel's opening backlog: the last N heard, in the order they
        // happened, so the page's newest-on-top prepend lands the right way up.
        await using (FrameLog writing = FrameLog.Open(DbPath, _time))
        {
            foreach (string call in new[] { "G0AAA", "G0BBB", "G0CCC", "G0DDD", "G0EEE" })
            {
                writing.Record(1, Frame(from: call), Quality(), audioHz: 1500, rfHz: 7_051_600);
            }
        }

        await using FrameLog log = FrameLog.Open(DbPath, _time);
        IReadOnlyList<Packet.SoundModem.Waterfall.LoggedFrame> recent = log.Recent(3);

        // The last three heard, oldest first.
        recent.Select(f => f.From).Should().Equal("G0CCC", "G0DDD", "G0EEE");
        Packet.SoundModem.Waterfall.LoggedFrame first = recent[0];
        first.SubChannel.Should().Be(1);
        first.Mode.Should().Be("bpsk300-il2pc");
        first.To.Should().Be("GB7RDG");
        first.LengthBytes.Should().Be(32);
        first.CorrectedBytes.Should().Be(2);
        first.CrcValid.Should().BeTrue();
        first.OffsetHz.Should().Be(-3.5);
        first.HeardAt.Should().Be(new DateTimeOffset(2026, 8, 2, 14, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Reading_The_Backlog_Does_Not_Disturb_The_Writer()
    {
        // Called from whichever connection thread a browser turned up on, while the writer
        // thread is mid-INSERT on its own connection — which is not thread-safe, hence the
        // separate short-lived reader. Frames recorded after the read must still land.
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        log.Record(0, Frame(from: "G0AAA"), Quality(), null, null);

        for (int i = 0; i < 100 && log.Recent(10).Count == 0; i++)
        {
            await Task.Delay(20);
        }

        log.Recent(10).Should().ContainSingle().Which.From.Should().Be("G0AAA");

        log.Record(0, Frame(from: "G0BBB"), Quality(), null, null);
        for (int i = 0; i < 100 && log.Recent(10).Count < 2; i++)
        {
            await Task.Delay(20);
        }

        log.Recent(10).Select(f => f.From).Should().Equal("G0AAA", "G0BBB");
        log.Dropped.Should().Be(0, "nothing was lost to the concurrent read");
    }

    [Fact]
    public async Task An_Empty_Log_Has_No_Backlog_To_Offer()
    {
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        log.Recent(50).Should().BeEmpty();
        log.Recent(0).Should().BeEmpty("asking for none is not a query worth running");
    }

    [Fact]
    public async Task Recording_Does_Not_Wait_On_The_Disk()
    {
        // The receive path calls this between bursts; it has to return at memory speed.
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            log.Record(0, Frame(), Quality(), null, null);
        }

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "queueing a thousand frames must not cost a thousand disk writes");
    }
}
