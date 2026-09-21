using System.Collections.Concurrent;
using Packet.SoundModem.Channel;
using Microsoft.Data.Sqlite;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Records every frame the station hears <i>and every frame it sends</i> to a SQLite file - what
/// it was, when, on which modem, from and to whom, and (for a receive) how well it decoded.
/// </summary>
/// <remarks>
/// <para>Writes happen on a background thread fed by an unbounded queue, because the receive
/// path must never wait on a disk. If the disk goes away the queue is drained and dropped
/// rather than allowed to grow without limit: a station that cannot log should keep decoding,
/// and a station that cannot log should not eventually run out of memory either.</para>
/// <para>The database is opened WAL, so a copy can be read - by a logbook, a dashboard, a
/// `sqlite3` prompt - while the modem is still writing to it.</para>
/// <para>The timestamp column is called <c>heard_at</c> for a transmitted row too, where it
/// means "when it went out". Renaming it would be more honest about one row in ten and would
/// silently break every query, dashboard and documented example written against the log so far,
/// so the wart is documented (docs/reference/config.md § frameLog) rather than fixed.</para>
/// </remarks>
internal sealed class FrameLog : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BlockingCollection<Entry> _pending = new(new ConcurrentQueue<Entry>());
    private readonly TimeProvider _time;
    private readonly Task _writer;
    private long _dropped;

    private FrameLog(SqliteConnection connection, TimeProvider time)
    {
        _connection = connection;
        _time = time;
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>
    /// Folds the write-ahead log into the database file, so that what is left on disk after a
    /// clean shutdown is one self-contained file.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing here is about durability.</b> A committed row is durable in the WAL the
    /// moment it is written and every SQLite reader sees it, because opening the database
    /// recovers the WAL - which is exactly why the log has always been readable with
    /// <c>sqlite3</c> while the modem holds it. This is about what an operator finds when they
    /// look. Without it a station that has logged all day leaves a 4 KB <c>frames.db</c> beside a
    /// <c>frames.db-wal</c> holding the traffic, and anything that reads the file rather than the
    /// database - a copy to another machine that takes the one file, a <c>grep</c> for a callsign,
    /// a size check - finds an empty-looking log and concludes the station is not recording.
    /// Measured: it is what made a test transmission look unlogged when the row was there all
    /// along.</para>
    /// <para>Best effort, and last: a checkpoint that cannot get its lock leaves the WAL exactly
    /// where it was, which is the behaviour this replaces, and a shutdown is no place to start
    /// throwing over tidiness.</para>
    /// </remarks>
    private void Checkpoint()
    {
        try
        {
            using SqliteCommand checkpoint = _connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            Console.Error.WriteLine(
                $"frame log: could not fold the write-ahead log into {Path} ({e.Message}); the "
                + "rows are safe in the .db-wal beside it and any reader will see them");
        }
    }

    /// <summary>How many frames were dropped rather than written; 0 on a healthy station.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The file being written to.</summary>
    internal string Path { get; private init; } = "";

    /// <summary>
    /// Opens (creating if needed) the log at <paramref name="path"/>. Throws with an
    /// operator-facing message if the file cannot be opened - a station configured to keep a
    /// log and silently not keeping one is worse than one that says so.
    /// </summary>
    internal static FrameLog Open(string path, TimeProvider? time = null)
    {
        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        using (SqliteCommand schema = connection.CreateCommand())
        {
            // WAL so the file stays readable while we hold it open; NORMAL because losing the
            // last few frames to a power cut costs nothing worth a synchronous write per frame.
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS frames (
                    id                INTEGER PRIMARY KEY,
                    heard_at          TEXT    NOT NULL,
                    direction         TEXT    NOT NULL DEFAULT 'rx',
                    sub_channel       INTEGER NOT NULL,
                    mode              TEXT    NOT NULL,
                    mode_name         TEXT    NOT NULL,
                    source            TEXT,
                    destination       TEXT,
                    length            INTEGER NOT NULL,
                    corrected         INTEGER,
                    crc_valid         INTEGER,
                    plain_il2p        INTEGER,
                    trailer_near_bits INTEGER,
                    monitor_only      INTEGER,
                    erased_bytes      INTEGER,
                    chased_bits       INTEGER,
                    snr_db            REAL,
                    peak_dbfs         REAL,
                    clipped           INTEGER,
                    level             TEXT,
                    peak_shown        INTEGER,
                    offset_hz         REAL,
                    audio_hz          REAL,
                    rf_hz             REAL,
                    payload           BLOB    NOT NULL,
                    quality           INTEGER,
                    ardop_sn_db       REAL
                );
                CREATE INDEX IF NOT EXISTS frames_heard_at ON frames(heard_at);
                CREATE INDEX IF NOT EXISTS frames_source ON frames(source);
                """;
            schema.ExecuteNonQuery();
        }

        Migrate(connection);
        return new FrameLog(connection, time ?? TimeProvider.System) { Path = path };
    }

    /// <summary>
    /// Brings a log written by an earlier version up to the current schema.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> does nothing to a table that already exists, and there
    /// are deployed stations whose <c>frames</c> table predates one or more of these columns - on
    /// those, every INSERT would fail and every frame would be silently dropped. <c>direction</c>
    /// can be added NOT NULL with a default and no backfill because the old rows are all
    /// receives, so <c>'rx'</c> is the truth about them rather than a guess.
    /// <c>trailer_near_bits</c>, <c>monitor_only</c> and <c>plain_il2p</c> stay null on old rows:
    /// whether a frame was corroborated, withheld, or read without a CRC behind it was not
    /// written down at the time, and null says so. So do <c>peak_dbfs</c>, <c>clipped</c> and
    /// <c>level</c>: how loud a frame was, whether the card railed under it, and what its own
    /// modem made of that were not written down then, and a backlog row without them draws
    /// exactly as it did before the columns existed. <c>peak_shown</c> is the same: whether the
    /// deciding modem thought <c>peak_dbfs</c> was worth a place on a row, null on a row from
    /// before it, which is listed with its figure as that row always was.
    /// <c>quality</c> and <c>ardop_sn_db</c> are the same story again: neither was written down
    /// before #479, so both stay null on every row logged before the columns existed, which is
    /// every non-ARDOP row there has ever been and every ARDOP row from an older build.
    /// </remarks>
    private static void Migrate(SqliteConnection connection)
    {
        foreach ((string column, string definition) in new[]
                 {
                     ("direction", "TEXT NOT NULL DEFAULT 'rx'"),
                     ("trailer_near_bits", "INTEGER"),
                     ("monitor_only", "INTEGER"),
                     ("erased_bytes", "INTEGER"),
                     ("chased_bits", "INTEGER"),
                     ("snr_db", "REAL"),
                     ("tx_trim_hz", "REAL"),
                     ("plain_il2p", "INTEGER"),
                     ("peak_dbfs", "REAL"),
                     ("clipped", "INTEGER"),
                     ("level", "TEXT"),
                     ("peak_shown", "INTEGER"),
                     ("quality", "INTEGER"),
                     ("ardop_sn_db", "REAL"),
                     ("held_ms", "INTEGER"),
                     ("held_cause", "TEXT"),
                     ("held_busy_ms", "INTEGER"),
                     ("held_slot_ms", "INTEGER"),
                     ("held_turnaround_ms", "INTEGER"),
                     ("held_inhibit_ms", "INTEGER"),
                     ("held_ourtx_ms", "INTEGER"),
                     ("held_queue_ms", "INTEGER"),
                     ("held_busy_ch", "INTEGER"),
                     ("held_busy_by", "TEXT"),
                 })
        {
            using SqliteCommand columns = connection.CreateCommand();
            // PRAGMA table_info(frames) in its table-valued form, so the answer is one scalar.
            columns.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = $column";
            columns.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                continue;
            }

            using SqliteCommand add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE frames ADD COLUMN {column} {definition}";
            add.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Queues a heard frame. Returns immediately: called from the receive path, which is
    /// decoding the next burst while this is being written.
    /// </summary>
    /// <param name="modeName">
    /// Overrides the human-readable name derived from <see cref="FrameQuality.Mode"/>. ARDOP
    /// uses it to name the frame type - "ARDOP ConReq500M" rather than a column of identical
    /// "ARDOP" rows, since with ARDOP the frame type is most of what the entry says.
    /// </param>
    /// <param name="at">
    /// When it was decoded, for a frame that was decoded somewhere else: a relayed station's own
    /// clock, so the monitor's copy of its log carries the station's times rather than the times
    /// they happened to cross the wire. Null is this process's own clock, which is every frame
    /// this process decoded itself.
    /// </param>
    internal void Record(
        int subChannel, byte[] frame, FrameQuality quality, double? audioHz, double? rfHz,
        string? modeName = null, DateTimeOffset? at = null)
    {
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            at ?? _time.GetUtcNow(),
            Transmitted: false,
            subChannel,
            quality.Mode,
            modeName ?? ModeNames.Display(quality.Mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            quality.FrameBytes,
            quality.CorrectedBytes,
            quality.CrcValid,
            // Without these two, a crc_valid-null row is ambiguous: a corroborated frame the
            // host received and a withheld RS-only reading logged identically. The 40 m capture
            // campaign found the gap by diffing this log against an offline re-decode.
            quality.TrailerNearBits,
            quality.MonitorOnly,
            // And this third one is what the reading WAS rather than what became of it: crc_valid
            // is null on HDLC, on FX.25 and on ARDOP too, so a row cannot be read back as RS-only
            // unless the log says so outright. Without it a browser opening on the backlog loses
            // the RS ONLY badge every one of these frames carried live.
            quality.PlainIl2p,
            quality.ErasedBytes,
            quality.ChasedBits,
            // Band-tracker convention (in-band over a rolling minimum floor), NOT the sim
            // ladder's 3 kHz reference - see FrameQuality.SnrDb before comparing.
            quality.SnrDb,
            // How loud this frame's own audio was and whether the card railed under it, so the
            // panel's opening backlog says the same about a frame as the live row did - and so a
            // question about a capture level from last Tuesday can be answered from the log
            // rather than from memory.
            quality.PeakDbFs,
            quality.Clipped,
            // The verdict the deciding modem's own limits reached at the moment of the decode,
            // stored rather than re-derived: a backlog row badges as the live row did, and
            // nothing reading this log has to know what a threshold is.
            quality.Level,
            quality.FrequencyOffsetHz,
            audioHz,
            rfHz,
            frame,
            // And whether that modem thought the figure worth a row, so the backlog shows what
            // the live row showed. The measurement above is written whatever this says.
            PeakWorthShowing: quality.PeakWorthShowing,
            // ARDOP's own two figures, null on every frame from a mode that does not report them
            // (which is every mode but ARDOP) and null on an ARDOP frame neither was computed
            // for - a Ping and a PingAck measure ArdopSnDb, everything else leaves it null rather
            // than claiming a measurement that was never made (#479).
            Quality: quality.Quality,
            ArdopSnDb: quality.ArdopSnDb));
    }

    /// <summary>
    /// Queues a frame this station sent. Returns immediately, like <see cref="Record"/>: called
    /// from the transmit path once the audio has gone to the device, so a logged row is a frame
    /// that actually went on air.
    /// </summary>
    /// <remarks>
    /// <para>A journal that records every frame received and none sent is half a record. There
    /// is no <see cref="FrameQuality"/> to take the mode from - nothing measured a transmission -
    /// so the caller passes it, and <c>corrected</c>, <c>crc_valid</c> and <c>offset_hz</c> are
    /// left null rather than filled with plausible values: they are receive measurements, and
    /// inventing them for our own transmission would be inventing a measurement of ourselves.
    /// (Same reasoning as the waterfall's TX rows.) <c>trailer_near_bits</c>, <c>monitor_only</c>
    /// and <c>plain_il2p</c> stay null too: corroborating a trailer, withholding a frame from the
    /// host and reading one without a CRC behind it are all things that happen to a receive.</para>
    /// <para><c>heard_at</c> holds when it went out - see the note on the class.</para>
    /// </remarks>
    /// <param name="mode">
    /// The mode string of the modem that sent it, as that modem reports itself - so the column
    /// reads the same for a frame we sent as for one the same modem heard.
    /// </param>
    /// <param name="txTrimHz">
    /// How far the burst was shifted off the nominal centre to suit the station it was addressed
    /// to; null when it went out straight. Deliberately not <c>offset_hz</c>: that column holds a
    /// measurement of somebody else's transmitter, and averaging the two together would mix what
    /// a station did with what we did about it.
    /// </param>
    /// <param name="waits">
    /// Where that wait went, split by cause, and which sub-channels asserted carrier sense during
    /// it. Null on a transmission this process did not make and on one made by a consumer that
    /// does not measure it; a station's own frames always carry it.
    /// </param>
    /// <param name="heldMs">
    /// How long this frame waited for the channel before it went out, in milliseconds. Written for
    /// every transmission, including the ones that went straight out: the column is what a later
    /// question about how much airtime this station is losing to deferral gets asked of, and a
    /// column that only exists above a threshold cannot answer "how often" - only "how bad".
    /// Null on a relayed transmission, where the figure belongs to the other station's channel.
    /// </param>
    /// <param name="at">
    /// When it went out, for a transmission made somewhere else: a relayed station's own clock.
    /// Null is this process's own, which is every frame this process sent itself.
    /// </param>
    /// <param name="modeName">
    /// Overrides the human-readable name derived from <paramref name="mode"/>, as on
    /// <see cref="Record"/> and for the same reason: ARDOP names the frame type, so a station's
    /// own "ARDOP ConReq500M" reads as the far end's row for the same burst does rather than as
    /// one more identical "ARDOP".
    /// </param>
    internal void RecordTransmitted(
        int subChannel, byte[] frame, string mode, double? audioHz, double? rfHz,
        double? txTrimHz = null, long? heldMs = null, DateTimeOffset? at = null,
        string? modeName = null, TransmitWaits? waits = null)
    {
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            at ?? _time.GetUtcNow(),
            Transmitted: true,
            subChannel,
            mode,
            modeName ?? ModeNames.Display(mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            frame.Length,
            Corrected: null,
            CrcValid: null,
            TrailerNearBits: null,
            MonitorOnly: null,
            PlainIl2p: null,
            ErasedBytes: null,
            ChasedBits: null,
            SnrDb: null,
            PeakDbFs: null,
            Clipped: null,
            Level: null,
            OffsetHz: null,
            audioHz,
            rfHz,
            frame,
            txTrimHz,
            HeldMs: heldMs,
            Waits: waits));
    }

    /// <summary>
    /// Whether the queue has run away from the disk, counting the frame as dropped if it has.
    /// </summary>
    /// <remarks>
    /// A backlog means the disk cannot keep up with the air. Dropping the newest keeps the memory
    /// bounded and the loss visible, which is better than either alternative.
    /// </remarks>
    private bool Backlogged()
    {
        if (_pending.Count <= 10_000)
        {
            return false;
        }

        Interlocked.Increment(ref _dropped);
        return true;
    }

    /// <summary>
    /// The most recent <paramref name="count"/> frames - heard and sent alike, <b>oldest
    /// first</b> - as the waterfall's decoded-frames panel opens with, so a browser arriving
    /// mid-afternoon sees what the channel has been doing rather than an empty list.
    /// </summary>
    /// <remarks>
    /// <para>Its own short-lived read-only connection, not the writer's: <see cref="SqliteConnection"/>
    /// is not thread-safe and this is called from whichever connection thread a browser turned
    /// up on, while the writer thread is mid-INSERT. The database is WAL - which is why the
    /// class docs promise it stays readable while the modem writes - so a reader takes no lock
    /// the writer cares about. A connection per page visit costs nothing at that rate.</para>
    /// <para>Returns empty rather than throwing if the file has gone: a browser losing its
    /// backlog is not a reason to fault a station that is still decoding.</para>
    /// </remarks>
    internal IReadOnlyList<LoggedFrame> Recent(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var frames = new List<LoggedFrame>(count);
        try
        {
            using var reader = new SqliteConnection(ReaderConnectionString(Path));
            reader.Open();
            using SqliteCommand query = reader.CreateCommand();
            // Newest first out of the index, then reversed: "the last N" is a descending
            // query, and the panel wants them in the order they happened.
            query.CommandText = """
                SELECT heard_at, sub_channel, mode, source, destination,
                       length, corrected, crc_valid, offset_hz, direction, tx_trim_hz,
                       monitor_only, plain_il2p, peak_dbfs, clipped, level, peak_shown,
                       trailer_near_bits, chased_bits, quality, ardop_sn_db, held_ms,
                       held_cause, held_busy_ch
                FROM frames ORDER BY id DESC LIMIT $count
                """;
            query.Parameters.AddWithValue("$count", count);
            using SqliteDataReader row = query.ExecuteReader();
            while (row.Read())
            {
                frames.Add(new LoggedFrame(
                    DateTimeOffset.Parse(row.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                    row.GetInt32(1),
                    row.GetString(2),
                    row.IsDBNull(3) ? null : row.GetString(3),
                    row.IsDBNull(4) ? null : row.GetString(4),
                    row.GetInt32(5),
                    row.IsDBNull(6) ? null : row.GetInt32(6),
                    row.IsDBNull(7) ? null : row.GetInt32(7) != 0,
                    row.IsDBNull(8) ? null : row.GetDouble(8),
                    // Anything that is not 'tx' is a receive, including a row from a log written
                    // before the column existed: those were all heard.
                    string.Equals(row.GetString(9), "tx", StringComparison.Ordinal),
                    row.IsDBNull(10) ? null : row.GetDouble(10),
                    // Null on a row written before the column existed, and on every transmission:
                    // not withheld, because nothing said it was.
                    !row.IsDBNull(11) && row.GetInt32(11) != 0,
                    // Same reading of null, and the same reason: an old row never said whether
                    // Reed-Solomon stood alone behind it, so the panel does not badge it as if
                    // it had. What the panel draws the RS ONLY badge from on a backlog row.
                    !row.IsDBNull(12) && row.GetInt32(12) != 0,
                    // These two keep their nulls rather than being read as zero and false: no
                    // level was written down for a transmission or for a row older than the
                    // columns, and 0 dBFS with a clear clip flag would be a claim that the frame
                    // arrived at full scale.
                    row.IsDBNull(13) ? null : row.GetDouble(13),
                    row.IsDBNull(14) ? null : row.GetInt32(14) != 0,
                    // And the verdict its own modem reached, so the backlog badges what the live
                    // row badged. An unrecognised word reads as null, which is how a log written
                    // by a later build stays readable here.
                    FrameLevelText.Parse(row.IsDBNull(15) ? null : row.GetString(15)),
                    // Null on a row from before the column, which is listed with its figure: the
                    // build that wrote it drew one, and nothing here can say it should not have.
                    row.IsDBNull(16) ? null : row.GetInt32(16) != 0,
                    // And the pair that says what stood behind the reading, so a replayed row
                    // makes the same claim about a station as the live row did: a corroborating
                    // trailer, and how many bits the chase had to move. Null on a row from before
                    // the columns, which reads as "nothing said" and keeps its callsign.
                    row.IsDBNull(17) ? null : row.GetInt32(17),
                    row.IsDBNull(18) ? null : row.GetInt32(18),
                    // ARDOP's own two figures. Null on every row that is not ARDOP, and on an
                    // ARDOP row from before the columns existed (#479).
                    row.IsDBNull(19) ? null : row.GetInt32(19),
                    row.IsDBNull(20) ? null : row.GetDouble(20),
                    // How long the channel held it. Only ever set on a transmission, and null on
                    // a transmission from before the column: a backlog row that says nothing is
                    // drawn without the note rather than as an instant one.
                    row.IsDBNull(21) ? null : row.GetInt64(21))
                {
                    // And which cause accounted for most of that wait, so a page that has just
                    // been reloaded tells a busy channel from a frame behind our own window
                    // exactly as the live row did. Null on a row from before the columns, which
                    // draws the held note with no cause on it, as it always did.
                    HeldCause = row.IsDBNull(22) ? null : row.GetString(22),
                    HeldBusySubChannel = row.IsDBNull(23) ? null : row.GetInt32(23),
                });
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or FormatException)
        {
            return [];
        }

        frames.Reverse();
        return frames;
    }

    /// <summary>
    /// The last <paramref name="count"/> frames with their bytes, <b>oldest first</b>, for
    /// replaying through something that reads frames rather than rows: the waterfall's link
    /// observer, which a restart would otherwise leave knowing nothing about links that were
    /// up a moment ago. Same reader, same shrug on a log that cannot be read.
    /// </summary>
    /// <remarks>
    /// Every row, withheld ones included, each saying which it was on
    /// <see cref="LoggedFrame.MonitorOnly"/>: what to do with an RS-only reading is the caller's
    /// question, and the answer differs between the links pane (which skips them) and anything
    /// re-reading the record of what the station heard.
    /// </remarks>
    internal IReadOnlyList<(LoggedFrame Frame, byte[] Payload)> RecentWithPayload(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var frames = new List<(LoggedFrame, byte[])>(count);
        try
        {
            using var reader = new SqliteConnection(ReaderConnectionString(Path));
            reader.Open();
            using SqliteCommand query = reader.CreateCommand();
            query.CommandText = """
                SELECT heard_at, sub_channel, mode, source, destination,
                       length, corrected, crc_valid, offset_hz, direction, tx_trim_hz,
                       monitor_only, plain_il2p, payload, peak_dbfs, clipped, level, peak_shown,
                       trailer_near_bits, chased_bits, quality, ardop_sn_db
                FROM frames ORDER BY id DESC LIMIT $count
                """;
            query.Parameters.AddWithValue("$count", count);
            using SqliteDataReader row = query.ExecuteReader();
            while (row.Read())
            {
                frames.Add((new LoggedFrame(
                    DateTimeOffset.Parse(row.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                    row.GetInt32(1),
                    row.GetString(2),
                    row.IsDBNull(3) ? null : row.GetString(3),
                    row.IsDBNull(4) ? null : row.GetString(4),
                    row.GetInt32(5),
                    row.IsDBNull(6) ? null : row.GetInt32(6),
                    row.IsDBNull(7) ? null : row.GetInt32(7) != 0,
                    row.IsDBNull(8) ? null : row.GetDouble(8),
                    string.Equals(row.GetString(9), "tx", StringComparison.Ordinal),
                    row.IsDBNull(10) ? null : row.GetDouble(10),
                    !row.IsDBNull(11) && row.GetInt32(11) != 0,
                    !row.IsDBNull(12) && row.GetInt32(12) != 0,
                    row.IsDBNull(14) ? null : row.GetDouble(14),
                    row.IsDBNull(15) ? null : row.GetInt32(15) != 0,
                    FrameLevelText.Parse(row.IsDBNull(16) ? null : row.GetString(16)),
                    row.IsDBNull(17) ? null : row.GetInt32(17) != 0,
                    // Read here as well as in Recent, so that one of these rows says the same
                    // thing about a station whichever query produced it.
                    row.IsDBNull(18) ? null : row.GetInt32(18),
                    row.IsDBNull(19) ? null : row.GetInt32(19),
                    // ARDOP's own two figures, read here as well as in Recent (#479).
                    row.IsDBNull(20) ? null : row.GetInt32(20),
                    row.IsDBNull(21) ? null : row.GetDouble(21)),
                    (byte[])row.GetValue(13)));
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or FormatException or InvalidCastException)
        {
            return [];
        }

        frames.Reverse();
        return frames;
    }

    /// <summary>
    /// Fills in where a transmission's wait went, a column per cause plus the one that dominated.
    /// </summary>
    /// <remarks>
    /// <para><b>Columns rather than one packed field</b>, because the question this exists to
    /// answer is asked of thousands of rows at once and not of one row at a time: "how much of
    /// this station's deferral is other people's traffic" is
    /// <c>SELECT held_cause, COUNT(*), SUM(held_ms)/1000 FROM frames WHERE direction='tx' GROUP BY
    /// held_cause</c>, and "which sub-channel is holding us up" is the same query over
    /// <c>held_busy_ch</c>. Neither is possible against a packed string without teaching every
    /// reader to unpack it.</para>
    /// <para><c>held_busy_by</c> is the one text field, and it is there because the answer can be
    /// "the radio", which is not a sub-channel and must not be filed as one. A station whose
    /// carrier sense comes from a control cable reports the whole station's answer; one reading
    /// the audio reports the sub-channels that overlapped the frame's own passband, which is the
    /// diagnostic for packet-net/pdn-soundmodem#526.</para>
    /// <para>Null on every row from a build before this existed, and on every row for a
    /// transmission made somewhere else, which is what null in those columns has always meant.</para>
    /// </remarks>
    private static void BindWaits(SqliteCommand insert, TransmitWaits? waits)
    {
        if (waits is not TransmitWaits spent || spent.Total <= TimeSpan.Zero)
        {
            foreach (string name in WaitColumns)
            {
                insert.Parameters[name].Value = DBNull.Value;
            }

            return;
        }

        // "mixed" rather than null where no one cause took half the wait, so that null in this
        // column means exactly one thing: nothing measured it. A row with a held time and no cause
        // at all would otherwise be indistinguishable from a row written before the columns
        // existed, and the query this is here for would quietly count the two together.
        insert.Parameters["$held_cause"].Value = CauseText(spent.Dominant);
        insert.Parameters["$held_busy_ms"].Value = Milliseconds(spent.ChannelBusy);
        insert.Parameters["$held_slot_ms"].Value = Milliseconds(spent.Backoff);
        insert.Parameters["$held_turnaround_ms"].Value = Milliseconds(spent.TurnaroundHold);
        insert.Parameters["$held_inhibit_ms"].Value = Milliseconds(spent.TransmitInhibit);
        insert.Parameters["$held_ourtx_ms"].Value = Milliseconds(spent.OurTransmission);
        insert.Parameters["$held_queue_ms"].Value = Milliseconds(spent.OurTurn);
        insert.Parameters["$held_busy_ch"].Value = (object?)spent.BusiestSubChannel ?? DBNull.Value;

        string subs = spent.SubChannelList();
        string by = (subs, spent.RadioSaidBusy) switch
        {
            ("", true) => "radio",
            ("", false) => "",
            (_, true) => $"radio,{subs}",
            _ => subs,
        };
        insert.Parameters["$held_busy_by"].Value = by.Length == 0 ? DBNull.Value : by;
    }

    private static readonly string[] WaitColumns =
    [
        "$held_cause", "$held_busy_ms", "$held_slot_ms", "$held_turnaround_ms", "$held_inhibit_ms",
        "$held_ourtx_ms", "$held_queue_ms", "$held_busy_ch", "$held_busy_by",
    ];

    private static long Milliseconds(TimeSpan span) => (long)span.TotalMilliseconds;

    /// <summary>
    /// The cause as it is written into the log: one lower-case word per cause, stable, so a query
    /// written today against a station's log still runs next year.
    /// </summary>
    internal static string CauseText(TransmitWaitCause cause) => cause switch
    {
        TransmitWaitCause.ChannelBusy => "busy",
        TransmitWaitCause.Backoff => "slot",
        TransmitWaitCause.TurnaroundHold => "turnaround",
        TransmitWaitCause.TransmitInhibit => "inhibit",
        TransmitWaitCause.OurTransmission => "ourtx",
        TransmitWaitCause.OurTurn => "queue",
        _ => "mixed",
    };

    private void WriteLoop()
    {
        using SqliteCommand insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO frames
              (heard_at, direction, sub_channel, mode, mode_name, source, destination,
               length, corrected, crc_valid, trailer_near_bits, monitor_only, plain_il2p,
               erased_bytes, chased_bits, snr_db, offset_hz, audio_hz, rf_hz, payload,
               tx_trim_hz, peak_dbfs, clipped, level, peak_shown, quality, ardop_sn_db,
               held_ms, held_cause, held_busy_ms, held_slot_ms, held_turnaround_ms,
               held_inhibit_ms, held_ourtx_ms, held_queue_ms, held_busy_ch, held_busy_by)
            VALUES
              ($heard_at, $direction, $sub, $mode, $mode_name, $source, $destination,
               $length, $corrected, $crc, $trailer, $monitor, $plain, $erased, $chased, $snr,
               $offset, $audio, $rf, $payload, $tx_trim, $peak, $clipped, $level, $peak_shown,
               $quality, $ardop_sn_db, $held_ms, $held_cause, $held_busy_ms, $held_slot_ms,
               $held_turnaround_ms, $held_inhibit_ms, $held_ourtx_ms, $held_queue_ms,
               $held_busy_ch, $held_busy_by)
            """;
        foreach (string name in new[]
                 {
                     "$heard_at", "$direction", "$sub", "$mode", "$mode_name", "$source",
                     "$destination", "$length", "$corrected", "$crc", "$trailer", "$monitor",
                     "$plain", "$erased", "$chased", "$snr", "$offset", "$audio", "$rf", "$payload",
                     "$tx_trim", "$peak", "$clipped", "$level", "$peak_shown", "$quality",
                     "$ardop_sn_db", "$held_ms", "$held_cause", "$held_busy_ms", "$held_slot_ms",
                     "$held_turnaround_ms", "$held_inhibit_ms", "$held_ourtx_ms", "$held_queue_ms",
                     "$held_busy_ch", "$held_busy_by",
                 })
        {
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        foreach (Entry entry in _pending.GetConsumingEnumerable())
        {
            try
            {
                insert.Parameters["$heard_at"].Value = entry.HeardAt.ToString("O");
                insert.Parameters["$direction"].Value = entry.Transmitted ? "tx" : "rx";
                insert.Parameters["$sub"].Value = entry.SubChannel;
                insert.Parameters["$mode"].Value = entry.Mode;
                insert.Parameters["$mode_name"].Value = entry.ModeName;
                insert.Parameters["$source"].Value = (object?)entry.Source ?? DBNull.Value;
                insert.Parameters["$destination"].Value = (object?)entry.Destination ?? DBNull.Value;
                insert.Parameters["$length"].Value = entry.Length;
                insert.Parameters["$corrected"].Value = (object?)entry.Corrected ?? DBNull.Value;
                insert.Parameters["$crc"].Value = entry.CrcValid is bool crc ? crc ? 1 : 0 : DBNull.Value;
                insert.Parameters["$trailer"].Value = (object?)entry.TrailerNearBits ?? DBNull.Value;
                insert.Parameters["$monitor"].Value = entry.MonitorOnly is bool monitor ? monitor ? 1 : 0 : DBNull.Value;
                insert.Parameters["$plain"].Value = entry.PlainIl2p is bool plain ? plain ? 1 : 0 : DBNull.Value;
                insert.Parameters["$erased"].Value = (object?)entry.ErasedBytes ?? DBNull.Value;
                insert.Parameters["$chased"].Value = (object?)entry.ChasedBits ?? DBNull.Value;
                insert.Parameters["$snr"].Value = (object?)entry.SnrDb ?? DBNull.Value;
                insert.Parameters["$peak"].Value = (object?)entry.PeakDbFs ?? DBNull.Value;
                insert.Parameters["$level"].Value =
                    (object?)FrameLevelText.From(entry.Level) ?? DBNull.Value;
                insert.Parameters["$clipped"].Value =
                    entry.Clipped is bool clipped ? clipped ? 1 : 0 : DBNull.Value;
                insert.Parameters["$peak_shown"].Value =
                    entry.PeakWorthShowing is bool shown ? shown ? 1 : 0 : DBNull.Value;
                insert.Parameters["$quality"].Value = (object?)entry.Quality ?? DBNull.Value;
                insert.Parameters["$ardop_sn_db"].Value = (object?)entry.ArdopSnDb ?? DBNull.Value;
                insert.Parameters["$held_ms"].Value = (object?)entry.HeldMs ?? DBNull.Value;
                BindWaits(insert, entry.Waits);
                insert.Parameters["$offset"].Value = (object?)entry.OffsetHz ?? DBNull.Value;
            insert.Parameters["$tx_trim"].Value = (object?)entry.TxTrimHz ?? DBNull.Value;
                insert.Parameters["$audio"].Value = (object?)entry.AudioHz ?? DBNull.Value;
                insert.Parameters["$rf"].Value = (object?)entry.RfHz ?? DBNull.Value;
                insert.Parameters["$payload"].Value = entry.Payload;
                insert.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // A disk that has filled or gone away must not take the modem down with it.
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _pending.CompleteAdding();
        await _writer.ConfigureAwait(false);
        _pending.Dispose();
        Checkpoint();
        _connection.Dispose();

        // This log's pools, not every log's. Disposing a connection returns it to a pool keyed
        // on the connection string, which keeps the file handle open; clearing that pool is what
        // finally releases it. There are TWO of them per log, because Recent and RecentWithPayload
        // open read-only connections and that is a different string and so a different pool -
        // clearing only the writer's leaves the file open on any station whose backlog was ever
        // read, which on a monitor is every station a visitor has looked at. ClearAllPools did
        // the job while a process held exactly one frame log, and would have had each closing
        // station reach across and shut every other station's handles the moment it held more.
        SqliteConnection.ClearPool(_connection);
        using (var reader = new SqliteConnection(ReaderConnectionString(Path)))
        {
            SqliteConnection.ClearPool(reader);
        }
    }

    /// <summary>
    /// The connection string the backlog readers use. Read-only, and therefore a different
    /// connection string - and a different pool - from the writer's ReadWriteCreate one, which
    /// is the whole reason <see cref="DisposeAsync"/> has to clear both.
    /// </summary>
    private static string ReaderConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();

    private sealed record Entry(
        DateTimeOffset HeardAt,
        bool Transmitted,
        int SubChannel,
        string Mode,
        string ModeName,
        string? Source,
        string? Destination,
        int Length,
        int? Corrected,
        bool? CrcValid,
        int? TrailerNearBits,
        bool? MonitorOnly,
        bool? PlainIl2p,
        int? ErasedBytes,
        int? ChasedBits,
        double? SnrDb,
        double? PeakDbFs,
        bool? Clipped,
        FrameLevel? Level,
        double? OffsetHz,
        double? AudioHz,
        double? RfHz,
        byte[] Payload,
        double? TxTrimHz = null,
        bool? PeakWorthShowing = null,
        int? Quality = null,
        double? ArdopSnDb = null,
        long? HeldMs = null,
        TransmitWaits? Waits = null);
}
