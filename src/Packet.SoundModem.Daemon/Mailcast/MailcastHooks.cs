using System.Globalization;
using System.Text.Json;
using Packet.Mailcast;

namespace Packet.SoundModem.Daemon;

/// <summary>How a window's start went: whether whatever "before" was to stop is stopped.</summary>
internal enum MailcastBeforeOutcome
{
    /// <summary>"before" worked, or there is none.</summary>
    Ok,

    /// <summary>"before" failed: what it was to stop may still be running.</summary>
    Failed,

    /// <summary>
    /// Nothing was run: an earlier window, someone else's (one from before a restart, say), has
    /// not had its "after" yet.
    /// </summary>
    Busy,
}

/// <summary>A slot's listening window, which the hooks run around.</summary>
/// <param name="Opens">When listening starts.</param>
/// <param name="Closes">When it ends.</param>
/// <param name="Slot">The slot's start.</param>
internal readonly record struct MailcastHookWindow(DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot);

/// <summary>
/// Runs <c>mailcast.hooks</c> around GB7RDG's slots: "before" ahead of each window, timed to have
/// finished (or been killed at its timeout) by the window's opening, and "after" once the window
/// is over, always, if "before" was started: after a failure, as the station stops, and after a
/// restart in the middle of a window, from the note in the mailcast state directory. The same
/// rules as pdn-mailcast's standalone receiver (its <c>SlotHooks</c>, PR #31), with the
/// programs run by Packet.Mailcast's <see cref="HookRunner"/>.
/// </summary>
/// <remarks>
/// <para>A station that retunes its rig for the signal has <see cref="MailcastRetuner"/> run them,
/// in order with its own steps: "before" first, and only if it worked is the transmitter held
/// and the rig tuned; "after" once the rig is back and transmissions released. A station whose
/// passband already hears the signal has <see cref="RunAsync"/> run them around every slot, from
/// <see cref="PassbandBefore"/> before it to <see cref="MailcastOnAir.ListenAfter"/> after.</para>
/// <para>The note, <see cref="FileName"/>, is written before "before" starts and removed once
/// "after" has exited with 0. A command's arguments may hold anything, a password included, so
/// they are never logged: the journal names the program and how many arguments it has.</para>
/// </remarks>
internal sealed class MailcastHooks : IDisposable
{
    /// <summary>The note's name in the mailcast state directory.</summary>
    internal const string FileName = "hooks.json";

    /// <summary>The longest wait on the clock before looking at it again.</summary>
    internal static readonly TimeSpan Check = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long before a slot a station that hears the signal without retuning starts its
    /// window, as pdn-mailcast's receiver does for a sound card.
    /// </summary>
    internal static readonly TimeSpan PassbandBefore = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions NoteJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly MailcastConfig _config;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _gate = new();
    private readonly object _loop = new();
    private Owed? _owed;
    private bool _recovered;

    /// <summary>What the note in the state directory holds.</summary>
    /// <param name="Slot">The slot "before" was run for.</param>
    /// <param name="LastSlot">The last slot of the window, when slots follow on so closely that one window covers them.</param>
    /// <param name="BeforeOk">Whether "before" worked, once it has finished.</param>
    /// <param name="Written">When the note was written.</param>
    internal sealed record Note(DateTimeOffset Slot, DateTimeOffset LastSlot, bool? BeforeOk, DateTimeOffset Written);

    /// <summary>An "after" owed: for which slot, to whom (null: whoever runs next), and how "before" went.</summary>
    private sealed record Owed(DateTimeOffset Slot, object? Owner, bool BeforeOk);

    /// <summary>The hooks in <paramref name="config"/>, keeping their note in <paramref name="directory"/>.</summary>
    internal MailcastHooks(MailcastConfig config, string directory, TimeProvider time, Action<string> log)
    {
        _config = config;
        _time = time;
        _log = log;
        NotePath = Path.Combine(directory, FileName);
        Runner = new HookRunner(time, line => log("mailcast: " + MailcastOnAir.Ascii(line)));
    }

    /// <summary>What starts the programs.</summary>
    internal HookRunner Runner { get; }

    /// <summary>The note's path.</summary>
    internal string NotePath { get; }

    private MailcastHooksConfig? Settings => _config.Hooks;

    /// <summary>Whether the config has a hook at all.</summary>
    internal bool Configured => Settings is { } s && (s.Before is not null || s.After is not null);

    /// <summary>How much earlier than the window's opening "before" is started: its timeout, so it is done by then.</summary>
    internal TimeSpan BeforeLead => Settings?.Before?.TimeLimit ?? TimeSpan.Zero;

    /// <summary>The last slot of the window the note from last time was for, if there was one: it is not listened to again.</summary>
    internal DateTimeOffset? RecoveredSlot { get; private set; }

    /// <summary>The slot whose "after" is owed, if any.</summary>
    internal DateTimeOffset? OwedSlot
    {
        get
        {
            lock (_gate)
            {
                return _owed?.Slot;
            }
        }
    }

    /// <summary>For tests: when the passband loop's current timer fires.</summary>
    internal DateTimeOffset NextWake { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>A hook for the journal: the program, and how many arguments it has, never what they are.</summary>
    internal static string Describe(HookCommand command)
    {
        int count = command.Args?.Count ?? 0;
        return count == 0
            ? command.Command
            : string.Create(CultureInfo.InvariantCulture, $"{command.Command} (with {count} argument{(count == 1 ? "" : "s")}, not shown)");
    }

    /// <summary>The start-up line saying what runs when, or null with no hooks.</summary>
    internal string? DescribeConfig()
    {
        if (!Configured || Settings is not { } hooks)
        {
            return null;
        }

        string before = hooks.Before is { } b
            ? string.Create(CultureInfo.InvariantCulture, $"\"before\" each slot runs {Describe(b)}, started {b.TimeoutSeconds} s ahead so it is done in time")
            : "no \"before\"";
        string after = hooks.After is { } a ? $"\"after\" runs {Describe(a)}" : "no \"after\"";
        return $"mailcast: hooks: {before}; {after}";
    }

    /// <summary>
    /// At start-up, once: a note left by a station that stopped part way through a window means
    /// "after" is owed for it. Says so in the journal; whichever runs the hooks then runs it.
    /// </summary>
    internal void Recover()
    {
        lock (_gate)
        {
            if (_recovered)
            {
                return;
            }

            _recovered = true;
        }

        if (!File.Exists(NotePath))
        {
            return;
        }

        Note note;
        try
        {
            note = JsonSerializer.Deserialize<Note>(File.ReadAllText(NotePath), NoteJson) ?? new Note(default, default, null, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            note = new Note(default, default, null, default);
        }

        string which = note.Slot == default
            ? "a slot"
            : $"the {Hhmm(note.Slot)} UTC slot on {note.Slot.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        RecoveredSlot = note.LastSlot == default ? null : note.LastSlot;
        if (Settings?.After is null)
        {
            _log($"mailcast: hooks: found {NotePath}: the station stopped during {which} last time, but there is no \"after\" command in the config to run for it now");
            DeleteNote();
            return;
        }

        lock (_gate)
        {
            _owed = new Owed(note.Slot, null, note.BeforeOk ?? false);
        }

        _log($"mailcast: hooks: the station stopped during {which} last time, after starting its \"before\" command; running \"after\" now (once the rig is back, if it was retuned), and not listening to that slot again");
    }

    /// <summary>
    /// Runs "before" for <paramref name="slot"/>, having written the note first, and returns
    /// whether it worked; <see cref="MailcastBeforeOutcome.Ok"/> when there is no "before". Its
    /// failure is logged once, with <paramref name="consequence"/> on the end. From then on
    /// "after" is owed, to <paramref name="owner"/> alone. If <paramref name="owner"/>'s own window
    /// is still open, "before" is not run again and this says how it went; if anyone else's is
    /// (or one from last time), nothing is run and this returns <see cref="MailcastBeforeOutcome.Busy"/>.
    /// </summary>
    internal async Task<MailcastBeforeOutcome> BeforeAsync(DateTimeOffset slot, object owner, string consequence, CancellationToken cancellation)
    {
        if (!Configured)
        {
            return MailcastBeforeOutcome.Ok;
        }

        await _running.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_owed is { } owed)
                {
                    return owed.Owner != owner ? MailcastBeforeOutcome.Busy
                        : owed.BeforeOk ? MailcastBeforeOutcome.Ok : MailcastBeforeOutcome.Failed;
                }
            }

            HookCommand? before = Settings?.Before;
            WriteNote(new Note(slot, slot, null, _time.GetUtcNow()));
            bool ok = true;
            if (before is not null)
            {
                _log($"mailcast: hooks: running \"before\" for the {Hhmm(slot)} UTC slot: {Describe(before)}");
                HookResult result = await Runner.RunAsync(
                    before, Environment(SlotHookEnvironment.BeforeName, slot, null), "hooks: before", cancellation).ConfigureAwait(false);
                ok = result.Ok;
                _log(ok
                    ? $"mailcast: hooks: \"before\" {result.Describe()}"
                    : $"mailcast: hooks: WARNING - \"before\" for the {Hhmm(slot)} UTC slot {result.Describe()}{consequence}");
            }

            lock (_gate)
            {
                _owed = new Owed(slot, owner, ok);
            }

            WriteNote(new Note(slot, slot, ok, _time.GetUtcNow()));
            return ok ? MailcastBeforeOutcome.Ok : MailcastBeforeOutcome.Failed;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>When a following slot keeps the window open: the note says so, so a restart in it does not listen to that slot again.</summary>
    internal void Extend(DateTimeOffset lastSlot)
    {
        DateTimeOffset slot;
        bool ok;
        lock (_gate)
        {
            if (_owed is not { } owed)
            {
                return;
            }

            slot = owed.Slot;
            ok = owed.BeforeOk;
        }

        WriteNote(new Note(slot, lastSlot, ok, _time.GetUtcNow()));
    }

    /// <summary>
    /// Whether "after" is owed to <paramref name="owner"/>, or, with <paramref name="unowned"/>,
    /// owed to nobody in particular (a note from last time).
    /// </summary>
    internal bool AfterOwedTo(object owner, bool unowned)
    {
        lock (_gate)
        {
            return _owed is { } owed && (owed.Owner == owner || (unowned && owed.Owner is null));
        }
    }

    /// <summary>
    /// Runs "after", if it is owed to <paramref name="owner"/> (or to nobody, with
    /// <paramref name="unowned"/>; a null <paramref name="owner"/> runs it whoever it is owed
    /// to). It runs to its end or its timeout, whatever else is stopping. The note is removed only
    /// if it exited with 0: otherwise (it failed, timed out or was killed, by systemd stopping the
    /// station, say) the note stays, so the next start-up runs it again. <paramref name="why"/>
    /// is said in the journal line.
    /// </summary>
    internal async Task AfterAsync(object? owner, bool unowned, string why = "")
    {
        await _running.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Owed owed;
            lock (_gate)
            {
                if (_owed is not { } o || !(o.Owner == owner || (unowned && o.Owner is null) || owner is null))
                {
                    return;
                }

                owed = o;
            }

            bool done = true;
            if (Settings?.After is { } after)
            {
                string which = owed.Slot == default ? "a slot" : $"the {Hhmm(owed.Slot)} UTC slot";
                _log($"mailcast: hooks: running \"after\" for {which}{why}: {Describe(after)}");
                HookResult result = await Runner.RunAsync(
                    after, Environment(SlotHookEnvironment.AfterName, owed.Slot, owed.BeforeOk), "hooks: after", CancellationToken.None).ConfigureAwait(false);
                done = result.Ok;
                _log(done
                    ? $"mailcast: hooks: \"after\" {result.Describe()}"
                    : $"mailcast: hooks: WARNING - \"after\" for {which} {result.Describe()}; keeping {NotePath}, so it is run again when the station next starts");
            }

            lock (_gate)
            {
                _owed = null;
            }

            if (done)
            {
                DeleteNote();
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>As the station stops: "after", if it is still owed to anyone.</summary>
    internal Task FinishAsync() => AfterAsync(null, unowned: true, " as the station stops");

    /// <summary>
    /// For a station that hears the signal without retuning: runs the hooks around each window
    /// <paramref name="windowAt"/> gives (the window in progress at a time, or else the next; null
    /// for none), until <paramref name="cancellation"/> is cancelled. "after" still owed when it
    /// is cancelled is left for <see cref="FinishAsync"/>.
    /// </summary>
    internal async Task RunAsync(Func<DateTimeOffset, MailcastHookWindow?> windowAt, CancellationToken cancellation)
    {
        Recover();
        DateTimeOffset? finished = RecoveredSlot;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (AfterOwedTo(_loop, unowned: true))
                {
                    await AfterAsync(_loop, unowned: true).ConfigureAwait(false);
                    continue;
                }

                DateTimeOffset now = _time.GetUtcNow();
                if (!Configured || Next(windowAt, now, finished) is not { } window)
                {
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                    continue;
                }

                DateTimeOffset startAt = window.Opens - BeforeLead;
                if (now < startAt)
                {
                    await WaitAsync(Shorter(startAt - now, Check), cancellation).ConfigureAwait(false);
                    continue;
                }

                if (await BeforeAsync(window.Slot, _loop, "; listening anyway", cancellation).ConfigureAwait(false) == MailcastBeforeOutcome.Busy)
                {
                    await WaitAsync(Check, cancellation).ConfigureAwait(false);
                    continue;
                }

                DateTimeOffset closes = window.Closes;
                DateTimeOffset last = window.Slot;
                while (true)
                {
                    now = _time.GetUtcNow();
                    if (now >= closes)
                    {
                        // A following window whose "before" would start before this one closes keeps it open.
                        if (Next(windowAt, closes, null) is { } following && following.Opens - BeforeLead <= closes)
                        {
                            closes = following.Closes;
                            last = following.Slot;
                            Extend(last);
                            _log($"mailcast: hooks: the {Hhmm(last)} UTC slot follows straight on, so \"after\" waits until {Hhmm(closes)} UTC");
                            continue;
                        }

                        break;
                    }

                    await WaitAsync(Shorter(closes - now, Check), cancellation).ConfigureAwait(false);
                }

                finished = last;
                await AfterAsync(_loop, unowned: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The listening window in progress at <paramref name="now"/>, or else the next, for a
    /// station that hears the signal without retuning: <see cref="PassbandBefore"/> before each
    /// slot to <see cref="MailcastOnAir.ListenAfter"/> after.
    /// </summary>
    internal static MailcastHookWindow? PassbandWindowAt(DateTimeOffset now, SlotTimetable timetable) =>
        MailcastOnAir.WindowAt(now, timetable, PassbandBefore, MailcastOnAir.ListenAfter) is { } w
            ? new MailcastHookWindow(w.Opens, w.Closes, w.Slot)
            : null;

    /// <summary>The window in progress at <paramref name="at"/> or the next, skipping any slot up to <paramref name="finished"/>.</summary>
    internal static MailcastHookWindow? Next(Func<DateTimeOffset, MailcastHookWindow?> windowAt, DateTimeOffset at, DateTimeOffset? finished)
    {
        MailcastHookWindow? window = windowAt(at);
        for (int i = 0; i < 1000 && window is { } w && finished is { } f && w.Slot <= f; i++)
        {
            window = windowAt(w.Closes);
        }

        return window is { } found && finished is { } done && found.Slot <= done ? null : window;
    }

    private IReadOnlyDictionary<string, string> Environment(string hook, DateTimeOffset slot, bool? beforeOk) =>
        SlotHookEnvironment.For(hook, slot, _config.DialKHz, _config.CentreHz / 1000, beforeOk);

    private void WriteNote(Note note)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(NotePath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = NotePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(note, NoteJson));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, NotePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without it, a station that stops in the window does not run "after" when it starts
            // again: whatever "before" stopped stays stopped, which is the safe way round.
            _log($"mailcast: hooks: WARNING - cannot write {NotePath} ({MailcastOnAir.Ascii(e.Message)}); if the station stops during this window, \"after\" will not be run for it when it starts again");
        }
    }

    private void DeleteNote()
    {
        try
        {
            File.Delete(NotePath);
            File.Delete(NotePath + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"mailcast: hooks: WARNING - cannot remove {NotePath} ({MailcastOnAir.Ascii(e.Message)}); the next start-up runs \"after\" again");
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellation)
    {
        // The timer first, then the note of when it fires: a test that moves the clock on once it
        // sees the note never moves it before the timer exists.
        DateTimeOffset due = _time.GetUtcNow() + delay;
        Task tick = Task.Delay(delay, _time, cancellation);
        NextWake = due;
        await tick.ConfigureAwait(false);
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    private static string Hhmm(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Dispose() => _running.Dispose();
}
