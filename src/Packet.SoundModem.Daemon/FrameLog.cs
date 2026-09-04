using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
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
/// so the wart is documented (CONFIG.md § frameLog) rather than fixed.</para>
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
                    trailer_near_bits INTEGER,
                    monitor_only      INTEGER,
                    erased_bytes      INTEGER,
                    chased_bits       INTEGER,
                    snr_db            REAL,
                    offset_hz         REAL,
                    audio_hz          REAL,
                    rf_hz             REAL,
                    payload           BLOB    NOT NULL
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
    /// <c>trailer_near_bits</c> and <c>monitor_only</c> stay null on old rows: whether a frame
    /// was corroborated or withheld was not written down at the time, and null says so.
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
    internal void Record(
        int subChannel, byte[] frame, FrameQuality quality, double? audioHz, double? rfHz,
        string? modeName = null)
    {
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            _time.GetUtcNow(),
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
            quality.ErasedBytes,
            quality.ChasedBits,
            // Band-tracker convention (in-band over a rolling minimum floor), NOT the sim
            // ladder's 3 kHz reference - see FrameQuality.SnrDb before comparing.
            quality.SnrDb,
            quality.FrequencyOffsetHz,
            audioHz,
            rfHz,
            frame));
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
    /// (Same reasoning as the waterfall's TX rows.) <c>trailer_near_bits</c> and
    /// <c>monitor_only</c> stay null too: corroborating a trailer and withholding a frame from
    /// the host are things that happen to a receive.</para>
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
    internal void RecordTransmitted(
        int subChannel, byte[] frame, string mode, double? audioHz, double? rfHz,
        double? txTrimHz = null)
    {
        if (Backlogged())
        {
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            _time.GetUtcNow(),
            Transmitted: true,
            subChannel,
            mode,
            ModeNames.Display(mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            frame.Length,
            Corrected: null,
            CrcValid: null,
            TrailerNearBits: null,
            MonitorOnly: null,
            ErasedBytes: null,
            ChasedBits: null,
            SnrDb: null,
            OffsetHz: null,
            audioHz,
            rfHz,
            frame,
            txTrimHz));
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
                       monitor_only
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
                    !row.IsDBNull(11) && row.GetInt32(11) != 0));
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
                       monitor_only, payload
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
                    !row.IsDBNull(11) && row.GetInt32(11) != 0),
                    (byte[])row.GetValue(12)));
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or FormatException or InvalidCastException)
        {
            return [];
        }

        frames.Reverse();
        return frames;
    }

    private void WriteLoop()
    {
        using SqliteCommand insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO frames
              (heard_at, direction, sub_channel, mode, mode_name, source, destination,
               length, corrected, crc_valid, trailer_near_bits, monitor_only, erased_bytes,
               chased_bits, snr_db, offset_hz, audio_hz, rf_hz, payload, tx_trim_hz)
            VALUES
              ($heard_at, $direction, $sub, $mode, $mode_name, $source, $destination,
               $length, $corrected, $crc, $trailer, $monitor, $erased, $chased, $snr,
               $offset, $audio, $rf, $payload, $tx_trim)
            """;
        foreach (string name in new[]
                 {
                     "$heard_at", "$direction", "$sub", "$mode", "$mode_name", "$source",
                     "$destination", "$length", "$corrected", "$crc", "$trailer", "$monitor",
                     "$erased", "$chased", "$snr", "$offset", "$audio", "$rf", "$payload", "$tx_trim",
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
                insert.Parameters["$erased"].Value = (object?)entry.ErasedBytes ?? DBNull.Value;
                insert.Parameters["$chased"].Value = (object?)entry.ChasedBits ?? DBNull.Value;
                insert.Parameters["$snr"].Value = (object?)entry.SnrDb ?? DBNull.Value;
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
        int? ErasedBytes,
        int? ChasedBits,
        double? SnrDb,
        double? OffsetHz,
        double? AudioHz,
        double? RfHz,
        byte[] Payload,
        double? TxTrimHz = null);
}
