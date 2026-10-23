using System.Globalization;
using System.Text;
using M0LTE.Dsp;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// Keeps a stretch of receive audio on request, with the carrier sense's reasons for every second
/// of it and a waterfall (packet-net/pdn-soundmodem#723, <c>POST /api/keepaudio</c>).
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> A client told "channel busy" by the transmit lease had no way to
/// find out afterwards whether the channel really was busy: the raw capture is optional and keeps
/// whatever its budget allows, and the frame log only notes frames. This keeps what the channel
/// sounded like and what each detector measured, for exactly the stretch a client asks about.
/// It knows nothing about who is asking or why; any client with the API key can ask.</para>
/// <para><b>Always listening, so a window can start in the past.</b> Receive audio goes into an
/// in-memory ring (<see cref="KeepAudioRing"/>) at <see cref="StoredRate"/> Hz, decimated through
/// a proper anti-alias filter, and the carrier sense is looked at every
/// <see cref="SampleInterval"/>. Neither depends on <c>rawCapture</c>. Once a window's end has
/// passed, its audio and samples are copied out and written off the timer thread by
/// <see cref="KeptWindowWriter"/>.</para>
/// <para><b>Fixed limits, no settings.</b> A window is at most <see cref="MaxWindow"/> long, may
/// start at most <see cref="MaxPast"/> back and end at most <see cref="MaxFuture"/> ahead, and at
/// most <see cref="MaxPending"/> wait at once. The ring holds <see cref="RingLength"/>, which is
/// the longest window plus time to spare, so a window is still whole when its end comes round.
/// Kept windows are deleted after <see cref="Retention"/>.</para>
/// </remarks>
internal sealed class KeepAudioRecorder : IDisposable
{
    /// <summary>The rate audio is kept at: the whole of an SSB or FM voice passband.</summary>
    internal const int StoredRate = 8000;

    /// <summary>How much audio the ring holds.</summary>
    internal static readonly TimeSpan RingLength = TimeSpan.FromMinutes(12);

    /// <summary>The longest window that can be kept.</summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(10);

    /// <summary>How far back a window may start.</summary>
    internal static readonly TimeSpan MaxPast = TimeSpan.FromMinutes(5);

    /// <summary>How far ahead a window may end.</summary>
    internal static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(15);

    /// <summary>How long a kept window stays on disk.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>The most windows waiting to be written at once.</summary>
    internal const int MaxPending = 16;

    /// <summary>How often the carrier sense is looked at.</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after a window's end it is written, for the last audio to arrive.</summary>
    internal static readonly TimeSpan SettleAfterEnd = TimeSpan.FromSeconds(2);

    /// <summary>How often old windows are looked for.</summary>
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    /// <summary>The longest label kept, before sanitising.</summary>
    internal const int MaxLabelLength = 64;

    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    private readonly Lock _gate = new();
    private readonly KeepAudioRing _ring;
    private readonly RationalResampler? _resampler;
    private readonly Queue<DetectorSample> _samples = new();
    private readonly List<KeptWindow> _pending = [];
    private readonly List<Task> _writing = [];
    private readonly Func<DateTimeOffset, DetectorSample> _sample;
    private readonly TimeProvider _time;
    private readonly Action<string> _say;
    private readonly Action<string> _complain;
    private float[] _decimated = new float[4096];
    private ITimer? _timer;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private bool _tickFailed;

    /// <param name="inputRate">The rate of the receive tap, the channel's DSP rate.</param>
    /// <param name="directory">Where kept windows go, one directory each.</param>
    /// <param name="sample">Takes one carrier-sense sample at the given time.</param>
    /// <param name="time">The clock.</param>
    /// <param name="say">Where an ordinary journal line goes.</param>
    /// <param name="complain">Where a problem goes.</param>
    public KeepAudioRecorder(
        int inputRate, string directory, Func<DateTimeOffset, DetectorSample> sample,
        TimeProvider time, Action<string> say, Action<string> complain)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inputRate, 0);
        Directory = directory;
        _sample = sample;
        _time = time;
        _say = say;
        _complain = complain;
        int rate = Math.Min(inputRate, StoredRate);
        if (inputRate != rate)
        {
            // A little short of the 4 kHz Nyquist, so the filter has room to stop everything that
            // would fold back; 3.4 kHz is past the top of any SSB or FM voice filter.
            _resampler = new RationalResampler(new RationalResamplerOptions
            {
                InputRate = inputRate,
                OutputRate = rate,
                PassbandHz = rate * 0.425,
                StopbandHz = rate * 0.5,
            });
        }

        _ring = new KeepAudioRing(rate, RingLength);
    }

    /// <summary>Where kept windows go.</summary>
    public string Directory { get; }

    /// <summary>The rate audio is kept at.</summary>
    public int SampleRate => _ring.SampleRate;

    /// <summary>Starts sampling the carrier sense. The first look also prunes old windows.</summary>
    public void Start()
    {
        _timer = _time.CreateTimer(_ => SafeTick(), null, SampleInterval, SampleInterval);
    }

    /// <summary>The receive tap: one block at the channel's DSP rate.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        ReadOnlySpan<float> kept = samples;
        if (_resampler is not null)
        {
            int most = _resampler.MaxOutput(samples.Length);
            if (_decimated.Length < most)
            {
                _decimated = new float[most];
            }

            int made = _resampler.Process(samples, _decimated);
            kept = _decimated.AsSpan(0, made);
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            _ring.Append(kept, now);
        }
    }

    /// <summary>
    /// Asks for a window to be kept. Returns it, or the HTTP status and the reason it was refused.
    /// </summary>
    public (KeptWindow? Window, int Status, string? Refusal) Keep(string? label, DateTimeOffset from, DateTimeOffset to)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return (null, 400, "\"label\" is required: a few words saying what the window is for");
        }

        if (label.Length > MaxLabelLength)
        {
            return (null, 400, $"\"label\" is at most {MaxLabelLength} characters");
        }

        string safe = SanitiseLabel(label);
        if (safe.Length == 0)
        {
            return (null, 400, "\"label\" needs at least one letter or digit");
        }

        // Whole seconds outwards, so the per-second log lines up with the window exactly.
        from = new DateTimeOffset(from.UtcTicks - from.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        long toRemainder = to.UtcTicks % TimeSpan.TicksPerSecond;
        to = new DateTimeOffset(to.UtcTicks - toRemainder + (toRemainder > 0 ? TimeSpan.TicksPerSecond : 0), TimeSpan.Zero);

        if (to <= from)
        {
            return (null, 400, "\"to\" must be later than \"from\"");
        }

        if (to - from > MaxWindow)
        {
            return (null, 400,
                $"a window is at most {MaxWindow.TotalMinutes:0} minutes long; this one is {Minutes(to - from)}");
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (from < now - MaxPast)
        {
            return (null, 400,
                $"\"from\" is {Minutes(now - from)} ago, and this station only keeps the last "
                + $"{MaxPast.TotalMinutes:0} minutes of receive audio to start a window from");
        }

        if (to > now + MaxFuture)
        {
            return (null, 400,
                $"\"to\" is {Minutes(to - now)} away; a window must end within {MaxFuture.TotalMinutes:0} minutes");
        }

        lock (_gate)
        {
            if (_pending.Count >= MaxPending)
            {
                return (null, 409, $"{MaxPending} windows are already waiting to be written; try again after one ends");
            }

            // Only ever true just after start-up: the ring holds more than MaxPast otherwise.
            DateTimeOffset heldFrom = _ring.HeldFrom ?? now;
            string? note = null;
            if (from < heldFrom)
            {
                if (to <= heldFrom)
                {
                    return (null, 400,
                        $"this station has only been listening since {TxLeaseApi.Utc(heldFrom)}, after this window ends");
                }

                long ticks = heldFrom.UtcTicks % TimeSpan.TicksPerSecond;
                from = new DateTimeOffset(heldFrom.UtcTicks - ticks + (ticks > 0 ? TimeSpan.TicksPerSecond : 0), TimeSpan.Zero);
                note = $"this station has only been listening since {TxLeaseApi.Utc(heldFrom)}, so the window starts then";
            }

            var window = new KeptWindow(UniqueId(from, safe), label.Trim(), from, to, now, note);
            _pending.Add(window);
            _say($"keep audio: keeping {TxLeaseApi.Utc(from)} to {TxLeaseApi.Utc(to)} as {window.Id}");
            return (window, 200, null);
        }
    }

    /// <summary>What is held, what is waiting and what has been kept.</summary>
    public (DateTimeOffset? HeldFrom, DateTimeOffset? HeldTo, IReadOnlyList<KeptWindow> Pending, IReadOnlyList<string> Kept) Status()
    {
        DateTimeOffset? from;
        DateTimeOffset? to;
        KeptWindow[] pending;
        lock (_gate)
        {
            from = _ring.HeldFrom;
            to = _ring.HeldTo;
            pending = [.. _pending];
        }

        return (from, to, pending, KeptNames());
    }

    /// <summary>One look at the carrier sense, and anything that has fallen due.</summary>
    internal void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        DetectorSample sample = _sample(now);
        List<(KeptWindow Window, float[] Audio, int Held, DetectorSample[] Samples)> due = [];
        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > 0 && _samples.Peek().At < now - RingLength)
            {
                _samples.Dequeue();
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                KeptWindow window = _pending[i];
                if (window.To + SettleAfterEnd > now)
                {
                    continue;
                }

                _pending.RemoveAt(i);
                (float[] audio, int held) = _ring.Extract(window.From, window.To);
                DetectorSample[] samples = [.. _samples.Where(s => s.At >= window.From && s.At < window.To)];
                due.Add((window, audio, held, samples));
            }
        }

        foreach ((KeptWindow window, float[] audio, int held, DetectorSample[] samples) in due)
        {
            Task writing = Task.Run(() => WriteOne(window, audio, held, samples));
            lock (_writing)
            {
                _writing.RemoveAll(t => t.IsCompleted);
                _writing.Add(writing);
            }
        }

        if (now - _lastPrune >= PruneInterval)
        {
            _lastPrune = now;
            Prune(Directory, now, _complain);
        }
    }

    /// <summary>Completes when every window written so far is on disk. For tests and shut-down.</summary>
    internal Task WhenWrittenAsync()
    {
        lock (_writing)
        {
            return Task.WhenAll([.. _writing]);
        }
    }

    /// <summary>
    /// Deletes every kept window that started more than <see cref="Retention"/> before
    /// <paramref name="now"/>, judged by the time in its name. Anything whose name does not start
    /// with one is not ours and is left alone.
    /// </summary>
    internal static int Prune(string directory, DateTimeOffset now, Action<string> complain)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string path in System.IO.Directory.GetDirectories(directory))
        {
            string name = Path.GetFileName(path).TrimStart('.');
            if (name.Length < 16
                || !DateTimeOffset.TryParseExact(
                    name[..16], TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset started)
                || started > now - Retention)
            {
                continue;
            }

            try
            {
                System.IO.Directory.Delete(path, recursive: true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complain($"keep audio: could not delete {path}: {e.Message}");
            }
        }

        return deleted;
    }

    /// <summary>
    /// A label made safe for a directory name: letters, digits, dot, hyphen and underscore kept,
    /// anything else a hyphen, runs of hyphens folded, and no dot or hyphen at either end.
    /// </summary>
    internal static string SanitiseLabel(string label)
    {
        var safe = new StringBuilder(label.Length);
        foreach (char c in label.Trim())
        {
            char kept = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-';
            if (kept == '-' && safe.Length > 0 && safe[^1] == '-')
            {
                continue;
            }

            safe.Append(kept);
        }

        return safe.ToString().Trim('-', '.');
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            // A diagnostic must never be what takes a station down, and an exception out of a
            // timer callback ends the process. Said once, not ten times a second.
            if (!_tickFailed)
            {
                _tickFailed = true;
                _complain($"keep audio: sampling failed, carrying on: {e.Message}");
            }
        }
    }

    private void WriteOne(KeptWindow window, float[] audio, int held, DetectorSample[] samples)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string written = KeptWindowWriter.Write(Directory, window, audio, SampleRate, held, samples);
            int busy = samples.Count(s => s.Busy && !s.Transmitting);
            _say($"keep audio: wrote {written} ({(window.To - window.From).TotalSeconds:0} s, "
                + $"busy in {busy} of {samples.Length} looks)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _complain($"keep audio: could not write {window.Id} under {Directory}: {e.Message}");
        }
    }

    private string UniqueId(DateTimeOffset from, string label)
    {
        string stem = $"{from.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{label}";
        string id = stem;
        for (int n = 2; Taken(id); n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }

    private bool Taken(string id) =>
        _pending.Exists(w => w.Id == id) || System.IO.Directory.Exists(Path.Combine(Directory, id));

    private string[] KeptNames()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        string[] names = [.. System.IO.Directory.GetDirectories(Directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith('.'))];
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)span.TotalSeconds} s";
}

/// <summary>A window someone asked to keep.</summary>
/// <param name="Id">Its directory name: the start time and the sanitised label.</param>
/// <param name="Label">The label as asked for.</param>
/// <param name="From">Its start, to the second.</param>
/// <param name="To">Its end, to the second.</param>
/// <param name="RequestedAt">When it was asked for.</param>
/// <param name="Note">Anything changed from the request, or null.</param>
internal sealed record KeptWindow(
    string Id, string Label, DateTimeOffset From, DateTimeOffset To, DateTimeOffset RequestedAt, string? Note);
