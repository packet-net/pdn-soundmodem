using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Records every frame the station hears to a SQLite file — what was heard, when, on which
/// modem, from and to whom, and how well it decoded.
/// </summary>
/// <remarks>
/// <para>Writes happen on a background thread fed by an unbounded queue, because the receive
/// path must never wait on a disk. If the disk goes away the queue is drained and dropped
/// rather than allowed to grow without limit: a station that cannot log should keep decoding,
/// and a station that cannot log should not eventually run out of memory either.</para>
/// <para>The database is opened WAL, so a copy can be read — by a logbook, a dashboard, a
/// `sqlite3` prompt — while the modem is still writing to it.</para>
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
    /// operator-facing message if the file cannot be opened — a station configured to keep a
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
                CREATE INDEX IF NOT EXISTS frames_heard_at ON frames(heard_at);
                CREATE INDEX IF NOT EXISTS frames_source ON frames(source);
                """;
            schema.ExecuteNonQuery();
        }

        return new FrameLog(connection, time ?? TimeProvider.System) { Path = path };
    }

    /// <summary>
    /// Queues a heard frame. Returns immediately: called from the receive path, which is
    /// decoding the next burst while this is being written.
    /// </summary>
    /// <param name="modeName">
    /// Overrides the human-readable name derived from <see cref="FrameQuality.Mode"/>. ARDOP
    /// uses it to name the frame type — "ARDOP ConReq500M" rather than a column of identical
    /// "ARDOP" rows, since with ARDOP the frame type is most of what the entry says.
    /// </param>
    internal void Record(
        int subChannel, byte[] frame, FrameQuality quality, double? audioHz, double? rfHz,
        string? modeName = null)
    {
        // A backlog means the disk cannot keep up with the air. Dropping the newest keeps the
        // memory bounded and the loss visible, which is better than either alternative.
        if (_pending.Count > 10_000)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        Ax25AddressParser.TryParse(frame, out string source, out string destination);
        _pending.Add(new Entry(
            _time.GetUtcNow(),
            subChannel,
            quality.Mode,
            modeName ?? ModeNames.Display(quality.Mode),
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(destination) ? null : destination,
            quality.FrameBytes,
            quality.CorrectedBytes,
            quality.CrcValid,
            quality.FrequencyOffsetHz,
            audioHz,
            rfHz,
            frame));
    }

    /// <summary>
    /// The most recent <paramref name="count"/> frames, <b>oldest first</b> — what the
    /// waterfall's decoded-frames panel opens with, so a browser arriving mid-afternoon sees
    /// what the channel has been doing rather than an empty list.
    /// </summary>
    /// <remarks>
    /// <para>Its own short-lived read-only connection, not the writer's: <see cref="SqliteConnection"/>
    /// is not thread-safe and this is called from whichever connection thread a browser turned
    /// up on, while the writer thread is mid-INSERT. The database is WAL — which is why the
    /// class docs promise it stays readable while the modem writes — so a reader takes no lock
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
            using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            reader.Open();
            using SqliteCommand query = reader.CreateCommand();
            // Newest first out of the index, then reversed: "the last N" is a descending
            // query, and the panel wants them in the order they happened.
            query.CommandText = """
                SELECT heard_at, sub_channel, mode, source, destination,
                       length, corrected, crc_valid, offset_hz
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
                    row.IsDBNull(8) ? null : row.GetDouble(8)));
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or FormatException)
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
              (heard_at, sub_channel, mode, mode_name, source, destination,
               length, corrected, crc_valid, offset_hz, audio_hz, rf_hz, payload)
            VALUES
              ($heard_at, $sub, $mode, $mode_name, $source, $destination,
               $length, $corrected, $crc, $offset, $audio, $rf, $payload)
            """;
        foreach (string name in new[]
                 {
                     "$heard_at", "$sub", "$mode", "$mode_name", "$source", "$destination",
                     "$length", "$corrected", "$crc", "$offset", "$audio", "$rf", "$payload",
                 })
        {
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        foreach (Entry entry in _pending.GetConsumingEnumerable())
        {
            try
            {
                insert.Parameters["$heard_at"].Value = entry.HeardAt.ToString("O");
                insert.Parameters["$sub"].Value = entry.SubChannel;
                insert.Parameters["$mode"].Value = entry.Mode;
                insert.Parameters["$mode_name"].Value = entry.ModeName;
                insert.Parameters["$source"].Value = (object?)entry.Source ?? DBNull.Value;
                insert.Parameters["$destination"].Value = (object?)entry.Destination ?? DBNull.Value;
                insert.Parameters["$length"].Value = entry.Length;
                insert.Parameters["$corrected"].Value = (object?)entry.Corrected ?? DBNull.Value;
                insert.Parameters["$crc"].Value = entry.CrcValid is bool crc ? crc ? 1 : 0 : DBNull.Value;
                insert.Parameters["$offset"].Value = (object?)entry.OffsetHz ?? DBNull.Value;
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
        SqliteConnection.ClearAllPools();
    }

    private sealed record Entry(
        DateTimeOffset HeardAt,
        int SubChannel,
        string Mode,
        string ModeName,
        string? Source,
        string? Destination,
        int Length,
        int? Corrected,
        bool? CrcValid,
        double? OffsetHz,
        double? AudioHz,
        double? RfHz,
        byte[] Payload);
}
