using System.Text.Json;
using System.Text.Json.Serialization;
using Mailcast.Core;

namespace Packet.SoundModem.Daemon;

/// <summary>One line of the delivery record.</summary>
internal sealed record MailcastDeliveryRecord(DateTimeOffset Time, string Bid, string Title, MailcastVerdict Verdict, string? Detail);

/// <summary>
/// What the BBS said about each bulletin, kept as <c>deliveries.jsonl</c> beside the store, one
/// JSON object per line, newest answer per BID kept when it is opened.
/// </summary>
internal sealed class MailcastLedger
{
    private const int RecentKept = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MailcastDeliveryRecord> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MailcastDeliveryRecord> _recent = [];

    /// <summary>Opens the record in <paramref name="directory"/>, reading what is there.</summary>
    internal MailcastLedger(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "deliveries.jsonl");
        if (!File.Exists(_path))
        {
            return;
        }

        var read = new List<MailcastDeliveryRecord>();
        foreach (string line in File.ReadLines(_path))
        {
            try
            {
                if (JsonSerializer.Deserialize<MailcastDeliveryRecord>(line, Json) is { } record)
                {
                    read.Add(record);
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash.
            }
        }

        var newest = read.GroupBy(r => r.Bid, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).OrderBy(r => r.Time).ToList();
        foreach (MailcastDeliveryRecord record in newest)
        {
            Remember(record);
        }

        if (newest.Count < read.Count)
        {
            string temporary = _path + ".tmp";
            File.WriteAllLines(temporary, newest.Select(r => JsonSerializer.Serialize(r, Json)));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    /// <summary>The newest records, newest first.</summary>
    internal IReadOnlyList<MailcastDeliveryRecord> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. Enumerable.Reverse(_recent)];
            }
        }
    }

    /// <summary>How many bulletins the BBS has taken (or confirmed it already had from us).</summary>
    internal int Delivered
    {
        get
        {
            lock (_gate)
            {
                return _latest.Values.Count(r => r.Verdict is MailcastVerdict.Accepted or MailcastVerdict.AlreadyHad);
            }
        }
    }

    /// <summary>
    /// Records an answer. An FS - for a bulletin whose last transfer was unconfirmed is the BBS
    /// saying it kept that transfer, so it is recorded as accepted.
    /// </summary>
    internal MailcastDeliveryRecord Record(Bulletin bulletin, MailcastOutcome outcome, DateTimeOffset now)
    {
        lock (_gate)
        {
            MailcastVerdict verdict = outcome.Verdict;
            string? detail = outcome.Detail;
            if (verdict == MailcastVerdict.AlreadyHad
                && _latest.TryGetValue(bulletin.Bid, out MailcastDeliveryRecord? before)
                && before.Verdict == MailcastVerdict.Unconfirmed)
            {
                verdict = MailcastVerdict.Accepted;
                detail = "confirmed on the next session: the BBS had kept the transfer";
            }

            var record = new MailcastDeliveryRecord(now, bulletin.Bid, bulletin.Title, verdict, detail);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + "\n");
            Remember(record);
            return record;
        }
    }

    private void Remember(MailcastDeliveryRecord record)
    {
        _latest[record.Bid] = record;
        _recent.Add(record);
        if (_recent.Count > RecentKept)
        {
            _recent.RemoveAt(0);
        }
    }
}

/// <summary>
/// Hands every rebuilt bulletin to the BBS: as soon as one is complete, again after a failure
/// with a growing wait, and again later for anything the BBS asked to have later.
/// </summary>
/// <remarks>
/// A bulletin leaves the store's outbox, which is on disk, only once the BBS has answered for it
/// for good: taken, already had, or refused. Anything else stays and is offered again, after a
/// restart too. Nothing is delivered twice: the BBS's own BID check answers FS - to one it took.
/// </remarks>
internal sealed class MailcastDelivery
{
    /// <summary>The waits after successive failures to reach the BBS; the last repeats.</summary>
    internal static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
    ];

    /// <summary>How long before bulletins the BBS asked to have later are offered again.</summary>
    internal static readonly TimeSpan LaterRetry = TimeSpan.FromMinutes(10);

    /// <summary>The most bulletins offered in one session.</summary>
    internal const int MaxPerSession = 50;

    private readonly MailcastIntake _intake;
    private readonly IMailcastBbs _bbs;
    private readonly MailcastLedger _ledger;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _failures;

    internal MailcastDelivery(MailcastIntake intake, IMailcastBbs bbs, MailcastLedger ledger, TimeProvider time, Action<string> log)
    {
        _intake = intake;
        _bbs = bbs;
        _ledger = ledger;
        _time = time;
        _log = log;
        intake.BulletinCompleted += _ => Nudge();
    }

    /// <summary>The last session's failure, or null if it worked (or none has run).</summary>
    internal string? LastFailure { get; private set; }

    /// <summary>When the last session ended.</summary>
    internal DateTimeOffset? LastSession { get; private set; }

    /// <summary>When the next attempt is due, while one is waiting on a timer.</summary>
    internal DateTimeOffset? NextAttempt { get; private set; }

    /// <summary>Raised after each session (for tests and the page).</summary>
    internal event Action<MailcastSession>? SessionFinished;

    /// <summary>For tests: raised once the loop is waiting, with its timer's task if on one.</summary>
    internal event Action<Task?>? Waiting;

    /// <summary>Asks for a session now, unless the loop is backing off a failure.</summary>
    internal void Nudge()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>Delivers until <paramref name="cancellation"/> is cancelled.</summary>
    internal async Task RunAsync(CancellationToken cancellation)
    {
        int pending = _intake.Pending().Count;
        if (pending > 0)
        {
            _log($"mailcast: {pending} rebuilt bulletin{(pending == 1 ? "" : "s")} waiting for the BBS from before");
        }

        while (!cancellation.IsCancellationRequested)
        {
            Task woken;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                woken = _wake.Task;
            }

            TimeSpan? wait;
            try
            {
                List<Bulletin> unique = Unique(_intake.Pending());
                if (unique.Count == 0)
                {
                    NextAttempt = null;
                    await WaitAsync(woken, null, cancellation).ConfigureAwait(false);
                    continue;
                }

                wait = await AttemptAsync([.. unique.Take(MaxPerSession)], cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                // Whatever it was, the loop carries on: a dead delivery loop delivers nothing.
                LastFailure = "unexpected failure: " + MailcastOnAir.Ascii(e.Message);
                _log($"mailcast: bbs: {LastFailure}. Trying again in {Words(Backoff[0])}.");
                wait = Backoff[0];
            }

            if (wait is TimeSpan delay)
            {
                NextAttempt = _time.GetUtcNow() + delay;
                // Waiting out a failure: a new bulletin does not make an unreachable BBS reachable.
                await WaitAsync(_failures > 0 ? null : woken, delay, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One session; returns how long to wait before the next, or null to go straight on.</summary>
    private async Task<TimeSpan?> AttemptAsync(IReadOnlyList<Bulletin> bulletins, CancellationToken cancellation)
    {
        MailcastSession session;
        try
        {
            session = await _bbs.DeliverAsync(bulletins, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            session = new MailcastSession(
                "the session failed: " + MailcastOnAir.Ascii(e.Message),
                [.. bulletins.Select(b => new MailcastOutcome(b.Bid, MailcastVerdict.NotOffered))], 0);
        }

        var byBid = bulletins.ToDictionary(b => b.Bid, StringComparer.OrdinalIgnoreCase);
        bool later = false;
        foreach (MailcastOutcome outcome in session.Outcomes)
        {
            Bulletin bulletin = byBid[outcome.Bid];
            switch (outcome.Verdict)
            {
                case MailcastVerdict.Accepted or MailcastVerdict.AlreadyHad or MailcastVerdict.Refused:
                    MailcastDeliveryRecord record = Record(bulletin, outcome);
                    try
                    {
                        _intake.Acknowledge(bulletin.Bid);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // It stays in the outbox and is offered again; the BBS answers FS -.
                        _log($"mailcast: WARNING - cannot take {MailcastOnAir.Ascii(bulletin.Bid)} out of the outbox: {MailcastOnAir.Ascii(e.Message)}");
                    }

                    _log($"mailcast: bbs: {MailcastOnAir.Ascii(bulletin.Bid)} {Words(record.Verdict)}{(record.Detail is null ? "" : ": " + MailcastOnAir.Ascii(record.Detail))}");
                    break;
                case MailcastVerdict.Deferred or MailcastVerdict.Unconfirmed:
                    Record(bulletin, outcome);
                    _log($"mailcast: bbs: {MailcastOnAir.Ascii(bulletin.Bid)} {Words(outcome.Verdict)}{(outcome.Detail is null ? "" : ": " + MailcastOnAir.Ascii(outcome.Detail))}");
                    later = true;
                    break;
                default:
                    later = true;
                    break;
            }
        }

        if (session.OfferedBack > 0)
        {
            _log($"mailcast: bbs: WARNING - the BBS tried to send the receiver's login {session.OfferedBack} message(s) and was told to keep them. "
                + "The login should have no forwarding routes (TO, AT or HR) on the BBS.");
        }

        LastSession = _time.GetUtcNow();
        LastFailure = session.Failure;
        SessionFinished?.Invoke(session);
        if (session.Failure is not null)
        {
            TimeSpan delay = Backoff[Math.Min(_failures, Backoff.Count - 1)];
            _failures++;
            _log($"mailcast: bbs: {MailcastOnAir.Ascii(session.Failure)}. Trying again in {Words(delay)}.");
            return delay;
        }

        _failures = 0;
        return later ? LaterRetry : null;
    }

    /// <summary>One bulletin per BID: two objects could in principle say the same BID.</summary>
    private static List<Bulletin> Unique(IReadOnlyList<Bulletin> pending) =>
        [.. pending.GroupBy(b => b.Bid, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];

    private MailcastDeliveryRecord Record(Bulletin bulletin, MailcastOutcome outcome)
    {
        try
        {
            return _ledger.Record(bulletin, outcome, _time.GetUtcNow());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"mailcast: WARNING - cannot write the delivery record for {MailcastOnAir.Ascii(bulletin.Bid)}: {MailcastOnAir.Ascii(e.Message)}");
            return new MailcastDeliveryRecord(_time.GetUtcNow(), bulletin.Bid, bulletin.Title, outcome.Verdict, outcome.Detail);
        }
    }

    private async Task WaitAsync(Task? woken, TimeSpan? delay, CancellationToken cancellation)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var waits = new List<Task> { Task.Delay(Timeout.InfiniteTimeSpan, done.Token) };
        Task? timer = null;
        if (delay is TimeSpan d)
        {
            timer = Task.Delay(d, _time, done.Token);
            waits.Add(timer);
        }

        if (woken is not null)
        {
            waits.Add(woken);
        }

        Waiting?.Invoke(timer);
        await Task.WhenAny(waits).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
    }

    internal static string Words(MailcastVerdict verdict) => verdict switch
    {
        MailcastVerdict.Accepted => "accepted by the BBS",
        MailcastVerdict.AlreadyHad => "rejected by the BBS: it already has this BID",
        MailcastVerdict.Refused => "refused",
        MailcastVerdict.Deferred => "deferred by the BBS: offering it again later",
        MailcastVerdict.Unconfirmed => "sent but not confirmed: offering it again later",
        _ => "not offered",
    };

    private static string Words(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:F0} min" : $"{delay.TotalSeconds:F0} s";
}
