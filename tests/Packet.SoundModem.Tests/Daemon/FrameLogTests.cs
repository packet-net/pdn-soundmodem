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
        var payload = new byte[16];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i + 16) * 11);
        }

        return Packet.SoundModem.Waterfall.Ax25UiFrame.Build(from, to, payload);
    }

    /// <summary>
    /// A log in the schema exactly as it shipped before the <c>direction</c> column, holding one
    /// heard frame - a database this code has never opened, which is what a deployed station has.
    /// </summary>
    private void WriteLogInTheOldSchema()
    {
        using (var old = new SqliteConnection($"Data Source={DbPath}"))
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE frames (
                    id          INTEGER PRIMARY KEY,
                    heard_at    TEXT    NOT NULL,
                    sub_channel INTEGER NOT NULL,
                    mode        TEXT    NOT NULL,
                    mode_name   TEXT    NOT NULL,
                    source      TEXT,
                    destination TEXT,
                    length      INTEGER NOT NULL,
                    corrected   INTEGER,
                    crc_valid   INTEGER,
                    offset_hz   REAL,
                    audio_hz    REAL,
                    rf_hz       REAL,
                    payload     BLOB    NOT NULL
                );
                CREATE INDEX frames_heard_at ON frames(heard_at);
                CREATE INDEX frames_source ON frames(source);
                INSERT INTO frames
                  (heard_at, sub_channel, mode, mode_name, source, destination,
                   length, corrected, crc_valid, offset_hz, audio_hz, rf_hz, payload)
                VALUES
                  ('2026-07-30T08:00:00.0000000+00:00', 0, 'bpsk300-il2pc', 'BPSK300 IL2Pc',
                   'G0OLD', 'GB7RDG', 4, 1, 1, -2.5, 1500.0, 7051600.0, X'01020304');
                """;
            create.ExecuteNonQuery();
        }

        // The handle above must be gone before the log opens the same file for writing.
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A log in the schema as it shipped WITH <c>direction</c> but before the corroboration
    /// columns (<c>trailer_near_bits</c>, <c>monitor_only</c>) - what a station deployed
    /// between the two migrations is holding right now.
    /// </summary>
    private void WriteLogInThePreCorroborationSchema()
    {
        using (var old = new SqliteConnection($"Data Source={DbPath}"))
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE frames (
                    id          INTEGER PRIMARY KEY,
                    heard_at    TEXT    NOT NULL,
                    direction   TEXT    NOT NULL DEFAULT 'rx',
                    sub_channel INTEGER NOT NULL,
                    mode        TEXT    NOT NULL,
                    mode_name   TEXT    NOT NULL,
                    source      TEXT,
                    destination TEXT,
                    length      INTEGER NOT NULL,
                    corrected   INTEGER,
                    crc_valid   INTEGER,
                    offset_hz   REAL,
                    audio_hz    REAL,
                    rf_hz       REAL,
                    payload     BLOB    NOT NULL
                );
                CREATE INDEX frames_heard_at ON frames(heard_at);
                CREATE INDEX frames_source ON frames(source);
                INSERT INTO frames
                  (heard_at, direction, sub_channel, mode, mode_name, source, destination,
                   length, corrected, crc_valid, offset_hz, audio_hz, rf_hz, payload)
                VALUES
                  ('2026-08-01T09:00:00.0000000+00:00', 'rx', 0, 'bpsk300-il2pc',
                   'BPSK300 IL2Pc', 'G0OLD', 'GB7RDG', 4, 0, NULL, -2.5, 1500.0, 7051600.0,
                   X'01020304');
                """;
            create.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
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
        row["direction"].Should().Be("rx", "the station heard this one");
        row["length"].Should().Be(32L);
        row["corrected"].Should().Be(2L);
        row["crc_valid"].Should().Be(1L);
        row["trailer_near_bits"].Should().BeNull("the CRC verified; no trailer was corroborated");
        row["monitor_only"].Should().Be(0L, "the host received this one");
        row["offset_hz"].Should().Be(-3.5);
        row["audio_hz"].Should().Be(2150.0);
        row["rf_hz"].Should().Be(7_051_600.0, "where it was heard on the band is the useful column");
        ((string)row["heard_at"]!).Should().StartWith("2026-08-02T14:30:00");
        ((byte[])row["payload"]!).Should().Equal(Frame(), "the frame itself must survive intact");
    }

    /// <summary>
    /// A frame this station sent is written down like one it heard, marked as ours, and carries
    /// no receive measurements - because there were none to take.
    /// </summary>
    /// <remarks>
    /// A journal that records every frame received and none sent is half a record. What it must
    /// not do is fill in the columns it cannot know: <c>corrected</c>, <c>crc_valid</c> and
    /// <c>offset_hz</c> are measurements of somebody else's signal, and plausible values there
    /// would be a measurement of ourselves.
    /// </remarks>
    [Fact]
    public async Task A_Transmitted_Frame_Is_Written_As_Ours_With_No_Receive_Measurements()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.RecordTransmitted(
                1, Frame(from: "M0LTE", to: "GB7RDG"), "bpsk300-il2pc",
                audioHz: 1500, rfHz: 7_051_600));

        Dictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        row["direction"].Should().Be("tx", "the station's own frames must be tellable apart");
        row["source"].Should().Be("M0LTE");
        row["destination"].Should().Be("GB7RDG");
        row["sub_channel"].Should().Be(1L);
        row["mode"].Should().Be("bpsk300-il2pc");
        row["mode_name"].Should().Be("BPSK300 IL2Pc");
        row["length"].Should().Be(32L);
        row["audio_hz"].Should().Be(1500.0);
        row["rf_hz"].Should().Be(7_051_600.0, "where it went out is as useful as where one arrived");
        ((byte[])row["payload"]!).Should().Equal(Frame(), "the frame sent is the evidence");

        // heard_at on a transmitted row is when it went out. The column keeps its name so that
        // every query already written against the log keeps working; the wart is documented.
        ((string)row["heard_at"]!).Should().StartWith("2026-08-02T14:30:00");

        row["corrected"].Should().BeNull("nothing corrected a frame we generated");
        row["crc_valid"].Should().BeNull("we did not check a CRC, we wrote one");
        row["trailer_near_bits"].Should().BeNull("corroborating a trailer is a receive event");
        row["monitor_only"].Should().BeNull("and so is withholding a frame from the host");
        row["offset_hz"].Should().BeNull("we did not measure our own carrier against itself");
    }

    /// <summary>
    /// A log written before transmitted frames were recorded opens, keeps its rows, and reads
    /// them back as receives.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> leaves an existing table alone, so a deployed station's
    /// log would have gone on without the <c>direction</c> column and every INSERT would have
    /// failed - a modem that silently stops logging. The old rows are all receives, so the
    /// column's default is the truth about them rather than a guess.
    /// </remarks>
    [Fact]
    public async Task A_Log_From_Before_The_Direction_Column_Is_Migrated_And_Keeps_Its_History()
    {
        WriteLogInTheOldSchema();

        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.RecordTransmitted(0, Frame(), "bpsk300-il2pc", 1500, 7_051_600));

        rows.Should().HaveCount(2, "a station's existing history must survive the upgrade");
        rows[0]["source"].Should().Be("G0OLD");
        rows[0]["direction"].Should().Be("rx", "everything logged before this was heard");
        rows[0]["trailer_near_bits"].Should().BeNull("nobody measured a trailer at the time");
        rows[0]["monitor_only"].Should().BeNull("whether it was withheld was never written down");
        ((byte[])rows[0]["payload"]!).Should().Equal([1, 2, 3, 4], "the old rows are untouched");
        rows[1]["direction"].Should().Be("tx", "and the log can now take what we send");
    }

    /// <summary>
    /// The other deployed schema: <c>direction</c> already present, the corroboration columns
    /// not. It gains them, keeps its history honest (null, not a guessed 0), and takes a
    /// corroborated frame's evidence from then on.
    /// </summary>
    [Fact]
    public async Task A_Log_From_Before_The_Corroboration_Columns_Gains_Them_And_Keeps_Its_History()
    {
        WriteLogInThePreCorroborationSchema();

        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 0, CrcValid: null,
                PlainIl2p: true, MonitorOnly: false, TrailerNearBits: 3), null, null));

        rows.Should().HaveCount(2, "the station's existing history must survive the upgrade");
        rows[0]["source"].Should().Be("G0OLD");
        rows[0]["trailer_near_bits"].Should().BeNull(
            "a pre-upgrade crc_valid-null row stays ambiguous rather than gaining invented facts");
        rows[0]["monitor_only"].Should().BeNull();
        rows[1]["trailer_near_bits"].Should().Be(3L, "and new rows carry the evidence");
        rows[1]["monitor_only"].Should().Be(0L);
        rows[1]["erased_bytes"].Should().BeNull("this frame needed no erasures");
    }

    /// <summary>A frame that leaned on confidence erasures writes the count down, so a log
    /// reader can see how hard-won a decode was.</summary>
    [Fact]
    public async Task A_Frame_Read_Through_Erasures_Records_How_Many()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 12, CrcValid: true,
                ErasedBytes: 14), null, null));

        rows.Should().ContainSingle().Which["erased_bytes"].Should().Be(14L,
            "fourteen bytes were erased on the receiver's own confidence flags");
    }

    /// <summary>The strength of the burst behind the frame goes in the record - the first
    /// question of nearly every receive investigation, answerable by SQL forever after.
    /// Band-tracker convention, per FrameQuality.SnrDb; the column must never be compared
    /// against the sim ladder's 3 kHz-referenced numbers without converting.</summary>
    [Fact]
    public async Task A_Frame_Records_The_Strength_Of_Its_Burst()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 0, CrcValid: true,
                SnrDb: 19.3), null, null));

        rows.Should().ContainSingle().Which["snr_db"].Should().Be(19.3);
    }

    /// <summary>A frame that leaned on chase decoding writes the flipped-bit count down, the
    /// same honesty as <c>erased_bytes</c>: a log reader can see the decode rested on receiver
    /// confidence, not parity alone.</summary>
    [Fact]
    public async Task A_Frame_Read_Through_Chase_Records_How_Many_Bits_Were_Flipped()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 7, CrcValid: true,
                ChasedBits: 3), null, null));

        rows.Should().ContainSingle().Which["chased_bits"].Should().Be(3L,
            "three bits were flipped on the receiver's own confidence ranking");
    }

    /// <summary>The migrated log reads back through the backlog query too - the waterfall panel
    /// is the other consumer of these rows, and it asks for the columns by name.</summary>
    [Fact]
    public async Task The_Backlog_Reads_A_Migrated_Logs_Old_Rows_As_Received()
    {
        WriteLogInTheOldSchema();

        await using FrameLog log = FrameLog.Open(DbPath, _time);
        Packet.SoundModem.Waterfall.LoggedFrame old0 =
            log.Recent(10).Should().ContainSingle().Subject;
        old0.From.Should().Be("G0OLD");
        old0.Transmitted.Should().BeFalse("a row written before the column existed was heard");
        old0.OffsetHz.Should().Be(-2.5, "and the rest of it is read back unchanged");
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
        // not be dropped from the log - the payload is the evidence and it is still there.
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
        // the interesting part - a connect request and a data frame are different events.
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
    public async Task A_Frame_The_Station_Read_And_Withheld_Is_Still_Written_Down()
    {
        // The log is fed from the monitor path, so a frame shown to the operator and not passed
        // to the host is recorded like any other. That is the point of it: it is the record of
        // what the station heard, not of what its host was given - and monitor_only writes down
        // which of the two this row was, where crc_valid null used to leave the question open.
        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(
                0, Frame(), new FrameQuality(
                    "bpsk300-il2pc-multi9", FrameBytes: 46, CorrectedBytes: 0, CrcValid: null,
                    PlainIl2p: true, MonitorOnly: true),
                audioHz: 2150, rfHz: 7_051_600));

        Dictionary<string, object?> row = rows.Should().ContainSingle(
            "a burst the station read is a burst the station read").Subject;
        row["crc_valid"].Should().BeNull("no CRC was checked");
        row["monitor_only"].Should().Be(1L, "the host never saw it, and the log says so");
        row["trailer_near_bits"].Should().BeNull("nothing corroborated this reading");
        row["mode"].Should().Be("bpsk300-il2pc-multi9");
        row["length"].Should().Be(46L);
    }

    /// <summary>
    /// The gap the 40 m capture campaign found by diffing this log against an offline re-decode:
    /// a delivered trailer-corroborated frame and a withheld RS-only reading both logged as
    /// crc_valid null, indistinguishable. Now the corroborated row carries its measured bit
    /// distance and monitor_only 0, and the withheld row monitor_only 1.
    /// </summary>
    [Fact]
    public async Task A_Corroborated_Frame_Is_Tellable_From_A_Withheld_One()
    {
        List<Dictionary<string, object?>> rows = await ReadBackAsync(log =>
        {
            // Delivered on the strength of a trailer 2 bits from the one its payload implies.
            log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 1, CrcValid: null,
                PlainIl2p: true, MonitorOnly: false, TrailerNearBits: 2), null, null);
            // Read, shown, withheld: the plain reading with nothing behind it but Reed-Solomon.
            log.Record(0, Frame(), new FrameQuality(
                "bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 0, CrcValid: null,
                PlainIl2p: true, MonitorOnly: true), null, null);
        });

        rows.Should().HaveCount(2);
        rows[0]["crc_valid"].Should().BeNull("both rows read identically before these columns");
        rows[1]["crc_valid"].Should().BeNull();
        rows[0]["trailer_near_bits"].Should().Be(2L, "the corroborating distance is the evidence");
        rows[0]["monitor_only"].Should().Be(0L, "corroboration delivered it to the host");
        rows[1]["trailer_near_bits"].Should().BeNull();
        rows[1]["monitor_only"].Should().Be(1L, "the host never received this one");
    }

    /// <summary>
    /// Off-air evidence from the live 40 m campaign: PD4R-12 beacons decoded and delivered CRC
    /// valid, and the log recorded source and destination both null. The destination field is
    /// all spaces - degenerate, but no reason to lose a perfectly readable sender.
    /// </summary>
    [Fact]
    public async Task A_Frame_With_A_Blank_Destination_Still_Logs_Its_Source()
    {
        byte[] frame = Convert.FromHexString(
            Packet.SoundModem.Tests.Waterfall.Ax25AddressParserTests.Pd4rBeaconHex);

        List<Dictionary<string, object?>> rows = await ReadBackAsync(
            log => log.Record(0, frame, Quality(), audioHz: 2150, rfHz: 7_051_600));

        Dictionary<string, object?> row = rows.Should().ContainSingle().Subject;
        row["source"].Should().Be("PD4R-12", "the sender is readable and is the column queried");
        row["destination"].Should().BeNull("an all-spaces destination field is not a callsign");
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

    /// <summary>
    /// The backlog carries both kinds, in the order they happened, each saying which it was.
    /// </summary>
    /// <remarks>
    /// The panel badges a transmission TX and styles it apart; if the direction were lost on the
    /// way out of the log, a browser reloading would see its own beacons listed as stations
    /// heard - which is worse than not showing them at all.
    /// </remarks>
    [Fact]
    public async Task The_Backlog_Says_Which_Frames_The_Station_Sent()
    {
        await using FrameLog log = FrameLog.Open(DbPath, _time);
        log.Record(0, Frame(from: "G0AAA"), Quality(), audioHz: 1500, rfHz: 7_051_600);
        log.RecordTransmitted(0, Frame(from: "M0LTE"), "bpsk300-il2pc", 1500, 7_051_600);
        log.Record(0, Frame(from: "G0BBB"), Quality(), audioHz: 1500, rfHz: 7_051_600);

        for (int i = 0; i < 100 && log.Recent(10).Count < 3; i++)
        {
            await Task.Delay(20);
        }

        IReadOnlyList<Packet.SoundModem.Waterfall.LoggedFrame> recent = log.Recent(10);
        recent.Select(f => f.From).Should().Equal("G0AAA", "M0LTE", "G0BBB");
        recent.Select(f => f.Transmitted).Should().Equal(false, true, false);
        recent[1].CrcValid.Should().BeNull("a transmission carries no receive measurements");
        recent[1].OffsetHz.Should().BeNull();
        recent[1].CorrectedBytes.Should().BeNull();
    }

    [Fact]
    public async Task Reading_The_Backlog_Does_Not_Disturb_The_Writer()
    {
        // Called from whichever connection thread a browser turned up on, while the writer
        // thread is mid-INSERT on its own connection - which is not thread-safe, hence the
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
