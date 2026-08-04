using System.Collections.Concurrent;
using System.Text.Json;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Survey;

/// <summary>What one capture records, written beside its WAV as JSON.</summary>
/// <param name="CapturedAt">UTC, when the burst ended.</param>
/// <param name="Verdict">Why it was kept.</param>
/// <param name="AudioCentreHz">Measured centre, in audio frequency.</param>
/// <param name="AudioLowHz">Measured low edge.</param>
/// <param name="AudioHighHz">Measured high edge.</param>
/// <param name="WidthHz">Measured occupied width — the first thing a classifier wants.</param>
/// <param name="DurationSeconds">How long it lasted.</param>
/// <param name="PeakSnrDb">Best single-line SNR over the noise floor.</param>
/// <param name="MeanSnrDb">Mean SNR over the burst.</param>
/// <param name="RfCentreHz">Where it sat on the band, when the station knows its dial —
/// null on a station running in audio frequencies only.</param>
/// <param name="DialHz">The dial the RF figure was derived from.</param>
/// <param name="Sideband">Which sideband, for the same reason.</param>
/// <param name="SampleRate">Sample rate of the WAV beside this file.</param>
/// <param name="Modems">What the station was configured to listen to, so a capture read months
/// later says what "unclaimed" meant at the time.</param>
/// <param name="SubChannel">The modem this concerns, where the verdict names one.</param>
/// <param name="Mode">Its mode, where the verdict names one.</param>
/// <param name="FrameHex">The decoded bytes, on an <see cref="SurveyVerdict.Unattributed"/>
/// capture — the frame is readable, it is only its addresses that are not, so the payload is
/// the evidence and belongs next to the audio.</param>
/// <param name="Il2pHeaderType">Which IL2P encapsulation it arrived in, where the framing is
/// IL2P. The first question worth asking about a frame that decoded cleanly and then would not
/// yield callsigns: Type 1 translated and Type 0 transparent put the address field in different
/// places.</param>
/// <param name="AttributionNote">Why the addresses would not read, in a line — so the sidecar
/// answers the question instead of posing it.</param>
public sealed record BurstCapture(
    DateTimeOffset CapturedAt,
    SurveyVerdict Verdict,
    double AudioCentreHz,
    double AudioLowHz,
    double AudioHighHz,
    double WidthHz,
    double DurationSeconds,
    double PeakSnrDb,
    double MeanSnrDb,
    double? RfCentreHz,
    double? DialHz,
    string? Sideband,
    int SampleRate,
    IReadOnlyList<string> Modems,
    int? SubChannel = null,
    string? Mode = null,
    string? FrameHex = null,
    string? Il2pHeaderType = null,
    string? AttributionNote = null);

/// <summary>
/// Writes captured bursts to disk — a WAV of the audio and a JSON sidecar of everything measured
/// about it — on a background thread, under a budget.
/// </summary>
/// <remarks>
/// <para><b>Background, like the frame log.</b> The receive path copies a burst out of the ring
/// and returns; it must never wait on a disk to keep decoding. A backlog is dropped rather than
/// queued without limit, and counted so the loss is visible.</para>
/// <para><b>Budgeted, because this runs unattended for weeks.</b> Two limits: how many captures
/// an hour (a busy unclaimed channel must not drown the interesting ones), and how many bytes
/// the directory may hold. On reaching the byte limit the <em>oldest</em> captures are deleted to
/// make room. That is a real choice and the alternative is worse: stopping instead would mean a
/// station left collecting for a week quietly stops on day one and the operator finds an empty
/// tail. A flight recorder keeps the recent past; so does this.</para>
/// </remarks>
public sealed class BurstCaptureWriter : IDisposable
{
    private readonly BlockingCollection<Pending> _pending = new(new ConcurrentQueue<Pending>());
    private readonly Task _writer;
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private long _dropped;
    private long _written;
    private long _bytes;

    /// <summary>Creates a writer over <paramref name="directory"/>, creating it if needed.</summary>
    /// <param name="directory">Where captures land.</param>
    /// <param name="maxBytes">Byte budget for the directory; oldest captures are pruned to fit.</param>
    /// <param name="time">Clock, injected for tests.</param>
    public BurstCaptureWriter(string directory, long maxBytes, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);
        _directory = directory;
        _maxBytes = maxBytes;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>Captures written to disk.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Bytes the capture directory holds, as of the last write. What an operator wants
    /// against the budget, and the only reading of it that costs nothing to keep.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Raised on the writer thread once a capture is on disk, with the path of its
    /// audio. The station is the only thing that knows a capture happened at the moment it
    /// happens; a display refreshing a directory listing would always be behind it.</summary>
    public event Action<BurstCapture, string>? Captured;

    /// <summary>Captures dropped because the disk could not keep up, or would not take them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues a capture. Returns immediately.</summary>
    public void Write(BurstCapture capture, float[] audio)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(audio);
        if (_pending.Count > 32)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            _pending.Add(new Pending(capture, audio));
        }
        catch (InvalidOperationException)
        {
            // Disposed mid-flight; a shutting-down station owes nobody a capture.
        }
    }

    private void WriteLoop()
    {
        foreach (Pending item in _pending.GetConsumingEnumerable())
        {
            try
            {
                // Sortable, unique, and says what it is at a glance in an `ls`.
                string stem = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{item.Capture.CapturedAt:yyyyMMdd-HHmmss}-{item.Capture.AudioCentreHz:F0}hz-{item.Capture.Verdict.ToString().ToLowerInvariant()}");
                string wav = Path.Combine(_directory, stem + ".wav");
                string json = Path.Combine(_directory, stem + ".json");
                WavFile.WriteMono(wav, item.Audio, item.Capture.SampleRate);
                File.WriteAllText(
                    json,
                    JsonSerializer.Serialize(item.Capture, JsonOptions));
                Interlocked.Increment(ref _written);
                Prune();
                Captured?.Invoke(item.Capture, wav);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A disk that has filled or gone away must not take the modem down with it.
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    /// <summary>Deletes oldest-first until the directory fits its budget.</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.*");
        long total = 0;
        foreach (FileInfo file in files)
        {
            total += file.Length;
        }

        Interlocked.Exchange(ref _bytes, total);
        if (total <= _maxBytes)
        {
            return;
        }

        // By name, which is the capture timestamp, so a WAV and its sidecar go together.
        Array.Sort(files, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (FileInfo file in files)
        {
            if (total <= _maxBytes)
            {
                return;
            }

            try
            {
                long size = file.Length;
                file.Delete();
                total -= size;
                Interlocked.Exchange(ref _bytes, total);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;   // cannot prune, so stop trying; the next capture will find it full too
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pending.CompleteAdding();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _pending.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly record struct Pending(BurstCapture Capture, float[] Audio);
}
