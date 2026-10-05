using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>How a <see cref="RigControl"/> is set up.</summary>
internal sealed class RigControlOptions
{
    /// <summary>Where rigctld listens.</summary>
    internal required RigctldEndpoint Endpoint { get; init; }

    /// <summary>The clock every wait and window is timed on.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Where an ordinary journal line goes.</summary>
    internal Action<string> Say { get; init; } = _ => { };

    /// <summary>Where a warning goes.</summary>
    internal Action<string> Warn { get; init; } = _ => { };

    /// <summary>True when the radio is keyed with rigctld's <c>T</c> command
    /// (<c>"ptt": {"type": "rigctld"}</c>) rather than by a line of its own.</summary>
    internal bool KeysThroughRig { get; init; }

    /// <summary>Where the band plan puts the dial, or null for a station with no plan, whose
    /// dial is the operator's and is never touched.</summary>
    internal RigTuning? Plan { get; init; }

    /// <summary>How often a connected rig is read, which is also how a dead rigctld that left
    /// its socket open is found.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The first wait before reconnecting after a failed attempt; it doubles each time.</summary>
    internal TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between reconnect attempts.</summary>
    internal TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one connect attempt may take.</summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>What became of a request to tune the rig for a window.</summary>
internal enum RigTuneOutcome
{
    /// <summary>The rig is tuned and a new window is open.</summary>
    Tuned,

    /// <summary>The caller's own window was extended, and retuned if it asked for somewhere new.</summary>
    Renewed,

    /// <summary>Nothing was wrong with the request; the station was in no state to do it.</summary>
    Refused,

    /// <summary>The request itself was wrong.</summary>
    Invalid,

    /// <summary>The rig or rigctld failed part way. The rig has been put back where it can be.</summary>
    Failed,
}

/// <summary>The answer to <see cref="RigControl.Tune"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Why">The reason, for anything but a tune or a renewal.</param>
/// <param name="Window">The window, when there is one.</param>
/// <param name="Capped">True when the length asked for was more than <see cref="RigControl.MaxWindow"/>.</param>
internal sealed record RigTuneResult(RigTuneOutcome Outcome, string? Why, RigWindow? Window, bool Capped = false)
{
    /// <summary>Whether the caller now holds a window.</summary>
    internal bool Granted => Outcome is RigTuneOutcome.Tuned or RigTuneOutcome.Renewed;
}

/// <summary>
/// One stretch of time the rig is tuned somewhere other than where the station keeps it, and the
/// promise to put it back. Disposing it ends the window now.
/// </summary>
internal sealed class RigWindow : IDisposable
{
    private readonly RigControl _rig;

    internal RigWindow(RigControl rig, string owner, RigTuning tuning, RigTuning restoreTo)
    {
        _rig = rig;
        Owner = owner;
        Tuning = tuning;
        RestoreTo = restoreTo;
    }

    /// <summary>Who asked for it, in a journal line: <c>the API</c>, a feature's name.</summary>
    internal string Owner { get; }

    /// <summary>Where the rig is tuned for the window.</summary>
    internal RigTuning Tuning { get; set; }

    /// <summary>Where the rig was before, and goes back to.</summary>
    internal RigTuning RestoreTo { get; }

    /// <summary>When the window ends unless it is renewed.</summary>
    internal DateTimeOffset Expires { get; set; }

    internal ITimer? Timer { get; set; }

    internal CancellationTokenRegistration Lifetime { get; set; }

    /// <summary>Ends the window and puts the rig back.</summary>
    public void Dispose() => _rig.EndWindow(this, $"{Owner} released it");
}

/// <summary>What <see cref="RigControl.Snapshot"/> reads.</summary>
internal sealed record RigState(
    RigctldEndpoint Endpoint,
    bool Connected,
    RigTuning? Tuning,
    bool Keyed,
    bool KeysThroughRig,
    RigWindow? Window,
    RigTuning? RestoreOwed,
    string? LastProblem);

/// <summary>
/// A radio controlled through Hamlib's rigctld: the dial set from the band plan, PTT through
/// <c>T 1</c>/<c>T 0</c> when the station keys that way, and short tuning windows that are always
/// put back.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> An ordinary SSB rig hears 2.4 to 3 kHz. A station whose packet
/// frequencies and pdn-mailcast's bulletin frequency are further apart than that cannot hear both,
/// so something has to retune the rig for the bulletin's window and put it back afterwards. This is
/// that something, and the band plan's dial and the rigctld PTT come with it because they need the
/// same connection.</para>
/// <para><b>A missing rigctld is not fatal.</b> The station runs without rig control and this keeps
/// trying, with a backoff from <see cref="RigControlOptions.FirstRetry"/> to
/// <see cref="RigControlOptions.MaxRetry"/>. Whatever was owed while it was away (an unkey, a
/// restore, the band plan's dial) is done the moment it answers again.</para>
/// <para><b>Never retuned while keyed.</b> Tuning and keying are mutually exclusive under one lock:
/// a tune asked for while the transmitter is keyed is refused, and a keyup asked for while a window
/// is open (or while the rig is still owed its restore) is refused. Ordinary frames are held rather
/// than refused, through the channel's transmit inhibit, which <see cref="HoldsTransmitter"/>
/// feeds; the refusal in <see cref="Key"/> is the backstop for anything that gets past it.</para>
/// <para><b>Always put back.</b> A window ends when its owner releases it, when its time runs out,
/// when the owner's lifetime token is cancelled, and when the station shuts down. A window cannot
/// outlive <see cref="MaxWindow"/> without being renewed, so an owner that dies without a word
/// costs at most that long. If the rig cannot be reached at the end, the restore is owed, the
/// station stays off the air, and it is done as soon as rigctld answers.</para>
/// <para><b>Fail-safe PTT.</b> The station's own idea of "keyed" is cleared before the unkey is
/// sent, so a failed unkey never leaves the station believing it is still transmitting. A failed
/// one is owed: the next connection to rigctld starts with <c>T 0</c>, as does shutdown.</para>
/// </remarks>
internal sealed class RigControl : IAsyncDisposable
{
    /// <summary>The longest one tune or renewal may ask for.</summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(5);

    private readonly RigControlOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;

    private RigctldConnection? _connection;
    private RigTuning? _known;
    private bool _everConnected;
    private bool _planApplied;
    private bool _keyed;
    private bool _unkeyOwed;
    private bool _unreachableSaid;
    private RigWindow? _window;
    private RigTuning? _restoreOwed;
    private string? _lastProblem;
    private bool _disposed;
    private int _disposing;

    internal RigControl(RigControlOptions options)
    {
        _options = options;
        _time = options.Time;
    }

    /// <summary>Where rigctld is.</summary>
    internal RigctldEndpoint Endpoint => _options.Endpoint;

    /// <summary>Whether the radio is keyed through rigctld.</summary>
    internal bool KeysThroughRig => _options.KeysThroughRig;

    /// <summary>Whether rigctld is connected right now.</summary>
    internal bool Connected
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null;
            }
        }
    }

    /// <summary>
    /// True while the station must not transmit because the rig is somewhere else: a window is
    /// open, or the rig is still owed its restore. The channel's transmit inhibit reads this.
    /// </summary>
    internal bool HoldsTransmitter
    {
        get
        {
            lock (_gate)
            {
                return _window is not null || _restoreOwed is not null;
            }
        }
    }

    /// <summary>The state as it stands, for the API and the tests.</summary>
    internal RigState Snapshot()
    {
        lock (_gate)
        {
            return new RigState(
                Endpoint, _connection is not null, _known, _keyed, KeysThroughRig, _window,
                _restoreOwed, _lastProblem);
        }
    }

    /// <summary>
    /// Connects once, journals what the rig is on, sets the band plan's dial if there is one, and
    /// starts the background watch that keeps the connection up.
    /// </summary>
    /// <returns>Whether rigctld answered. False is a warning, already journalled, and the watch
    /// keeps trying.</returns>
    internal async Task<bool> StartAsync(CancellationToken cancellation)
    {
        bool connected = await TryConnectAsync(cancellation).ConfigureAwait(false);
        _loop = Task.Run(() => RunAsync(connected, _stop.Token), CancellationToken.None);
        return connected;
    }

    /// <summary>A PTT that keys the radio with rigctld's <c>T 1</c> and <c>T 0</c>.</summary>
    internal IPttControl KeyingPtt() => new RigPtt(this, null);

    /// <summary>
    /// <paramref name="inner"/> with this rig's tuning windows guarding it: a keyup is refused
    /// while the rig is retuned, and the rig is never retuned while it is keyed. A PTT that
    /// already goes through this rig is returned as it is.
    /// </summary>
    internal IPttControl Guard(IPttControl inner) =>
        inner is RigPtt mine && ReferenceEquals(mine.Rig, this) ? inner : new RigPtt(this, inner);

    // ------------------------------------------------------------------ keying

    /// <summary>
    /// Starts a keyup: refused while the rig is retuned, and sends <c>T 1</c> when
    /// <paramref name="sendT"/>.
    /// </summary>
    internal void Key(bool sendT)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new InvalidOperationException("rig control has shut down, so the radio is not keyed");
            }

            if (HoldReasonLocked() is string why)
            {
                throw new InvalidOperationException(why);
            }

            if (sendT)
            {
                RigctldConnection connection = _connection ?? throw new IOException(
                    $"rigctld at {Endpoint} is not connected, so the radio cannot be keyed");
                try
                {
                    connection.SetPtt(true);
                }
                catch (IOException lost)
                {
                    // It may have reached the rig before the connection went, so an unkey is owed.
                    _unkeyOwed = true;
                    DropLocked(lost.Message);
                    throw;
                }
            }

            _keyed = true;
        }
    }

    /// <summary>
    /// Ends a keyup, sending <c>T 0</c> when <paramref name="sendT"/>. The keyup is over as far as
    /// this station is concerned whatever happens to the command.
    /// </summary>
    internal void Unkey(bool sendT)
    {
        lock (_gate)
        {
            _keyed = false;
            if (!sendT)
            {
                return;
            }

            if (_connection is not { } connection)
            {
                _unkeyOwed = true;
                throw new IOException(
                    $"rigctld at {Endpoint} is not connected, so the unkey could not be sent; the "
                    + "radio may still be keyed, and is unkeyed the moment rigctld answers");
            }

            try
            {
                connection.SetPtt(false);
                _unkeyOwed = false;
            }
            catch (IOException lost)
            {
                _unkeyOwed = true;
                DropLocked(lost.Message);
                throw;
            }
            catch (RigctldException)
            {
                // Tried again at the next poll.
                _unkeyOwed = true;
                throw;
            }
        }
    }

    // ------------------------------------------------------------------ tuning windows

    /// <summary>
    /// Tunes the rig to <paramref name="tuning"/> for <paramref name="duration"/> on behalf of
    /// <paramref name="owner"/>, and puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// The same owner asking again while its window is open renews it, retuning first if it asks
    /// for somewhere new; the rig still goes back to where it was before the first request.
    /// Another owner is refused while a window is open.
    /// </remarks>
    /// <param name="tuning">Where to tune.</param>
    /// <param name="duration">How long for; capped at <see cref="MaxWindow"/>.</param>
    /// <param name="owner">Who is asking, as a journal line should name them.</param>
    /// <param name="ownerLifetime">Cancelled when the owner goes away; the window then ends at
    /// once. A token that cannot be cancelled leaves it to the window's own time.</param>
    internal RigTuneResult Tune(
        RigTuning tuning, TimeSpan duration, string owner, CancellationToken ownerLifetime = default)
    {
        if (tuning.DialHz is <= 0 or > 100_000_000_000)
        {
            return new(RigTuneOutcome.Invalid, $"{tuning.DialHz} Hz is not a dial frequency", null);
        }

        if (!RigModes.IsKnown(tuning.Mode))
        {
            return new(RigTuneOutcome.Invalid,
                $"\"{tuning.Mode}\" is not a mode this station asks a rig for. Use one of {RigModes.List}", null);
        }

        if (tuning.PassbandHz is < 0 or > 100_000)
        {
            return new(RigTuneOutcome.Invalid,
                $"a passband of {tuning.PassbandHz} Hz is not one; use 0 for the rig's normal width", null);
        }

        if (duration <= TimeSpan.Zero)
        {
            return new(RigTuneOutcome.Invalid, "a window has to last more than 0 seconds", null);
        }

        if (ownerLifetime.IsCancellationRequested)
        {
            return new(RigTuneOutcome.Refused, "the owner had already gone away", null);
        }

        tuning = tuning with { Mode = tuning.Mode.ToUpperInvariant() };
        bool capped = duration > MaxWindow;
        TimeSpan length = capped ? MaxWindow : duration;

        RigWindow window;
        bool renewed;
        lock (_gate)
        {
            if (_disposed)
            {
                return new(RigTuneOutcome.Refused, "the station is shutting down", null);
            }

            if (_keyed)
            {
                return new(RigTuneOutcome.Refused,
                    "the transmitter is keyed, and the rig is never retuned in the middle of a "
                    + "transmission; ask again once it has unkeyed", null);
            }

            if (_window is { } open && open.Owner != owner)
            {
                return new(RigTuneOutcome.Refused,
                    $"the rig is already tuned to {open.Tuning} for {open.Owner} until "
                    + $"{TxLeaseApi.Utc(open.Expires)}", open);
            }

            if (_connection is not { } connection)
            {
                return new(RigTuneOutcome.Refused,
                    $"rigctld at {Endpoint} is not connected"
                    + (_lastProblem is null ? "" : $" ({_lastProblem})"), null);
            }

            if (_restoreOwed is not null)
            {
                return new(RigTuneOutcome.Refused,
                    "the rig is still owed its restore from the last window; ask again in a moment", null);
            }

            renewed = _window is not null;
            try
            {
                RigTuning restoreTo = _window?.RestoreTo ?? ReadLocked(connection);
                if (!renewed || _window!.Tuning != tuning)
                {
                    try
                    {
                        ApplyLocked(connection, tuning, $"for {owner}");
                    }
                    catch (RigctldException refused)
                    {
                        // Put back whatever half of it took, and keep any window that was open.
                        TryApplyLocked(connection, _window?.Tuning ?? restoreTo);
                        return new(RigTuneOutcome.Failed, refused.Message, _window);
                    }
                }

                window = _window ?? new RigWindow(this, owner, tuning, restoreTo);
                window.Tuning = tuning;
                window.Expires = _time.GetUtcNow() + length;
                if (window.Timer is null)
                {
                    RigWindow ending = window;
                    window.Timer = _time.CreateTimer(
                        _ => EndWindow(ending, "its time ran out"), null, length, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    window.Timer.Change(length, Timeout.InfiniteTimeSpan);
                }

                _window = window;
            }
            catch (IOException lost)
            {
                DropLocked(lost.Message);
                return new(RigTuneOutcome.Failed, $"rigctld at {Endpoint} went away: {lost.Message}", _window);
            }
        }

        // Registered outside the lock: a token cancelled between the check above and here runs
        // its callback inline, and that callback takes the lock.
        if (ownerLifetime.CanBeCanceled && !renewed)
        {
            window.Lifetime = ownerLifetime.Register(() => EndWindow(window, $"{owner} went away"));
        }

        _options.Say(
            renewed
                ? $"rig: {owner} renewed its window on {window.Tuning} until {TxLeaseApi.Utc(window.Expires)}"
                : $"rig: tuned to {window.Tuning} for {owner} until {TxLeaseApi.Utc(window.Expires)}; "
                  + $"transmissions are held until it is put back to {window.RestoreTo}");
        return new(renewed ? RigTuneOutcome.Renewed : RigTuneOutcome.Tuned, null, window, capped);
    }

    /// <summary>Ends <paramref name="owner"/>'s window now, if it has one.</summary>
    /// <returns>Whether there was one to end.</returns>
    internal bool Release(string owner)
    {
        RigWindow? window;
        lock (_gate)
        {
            window = _window is { } open && open.Owner == owner ? open : null;
        }

        if (window is null)
        {
            return false;
        }

        EndWindow(window, $"{owner} released it");
        return true;
    }

    /// <summary>Ends a window and puts the rig back, or owes the restore if it cannot.</summary>
    internal void EndWindow(RigWindow window, string why)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_window, window))
            {
                return;
            }

            _window = null;
            window.Timer?.Dispose();
            window.Lifetime.Unregister();
            _restoreOwed = window.RestoreTo;
            _options.Say($"rig: the window on {window.Tuning} for {window.Owner} has ended ({why})");
            RestoreOwedLocked();
        }
    }

    // ------------------------------------------------------------------ the connection

    private async Task RunAsync(bool connectedAtStart, CancellationToken cancellation)
    {
        TimeSpan retry = _options.FirstRetry;
        bool failed = !connectedAtStart;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (Connected)
                {
                    retry = _options.FirstRetry;
                    await NapAsync(_options.PollInterval, whileConnected: true, cancellation)
                        .ConfigureAwait(false);
                    Poll();
                    continue;
                }

                if (failed)
                {
                    await NapAsync(retry, whileConnected: false, cancellation).ConfigureAwait(false);
                    retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, _options.MaxRetry.Ticks));
                }

                failed = !await TryConnectAsync(cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits for <paramref name="span"/> on the station's clock, or until something
    /// asks for the connection to be looked at sooner (a drop).</summary>
    /// <param name="whileConnected">True for the wait between polls, which a connection that has
    /// already gone ends at once: the drop's wake may have come before this wait began.</param>
    private async Task NapAsync(TimeSpan span, bool whileConnected, CancellationToken cancellation)
    {
        Task wake;
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (whileConnected && _connection is null)
            {
                return;
            }

            wake = _wake.Task;
        }

        using var nap = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task delay = Task.Delay(span, _time, nap.Token);
        await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await nap.CancelAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellation)
    {
        RigctldConnection opened;
        try
        {
            opened = await RigctldConnection.OpenAsync(Endpoint, _options.ConnectTimeout, cancellation)
                .ConfigureAwait(false);
        }
        catch (IOException unreachable)
        {
            lock (_gate)
            {
                _lastProblem = unreachable.Message;
                if (!_unreachableSaid)
                {
                    _unreachableSaid = true;
                    _options.Warn(
                        $"rig: WARNING - cannot reach rigctld at {Endpoint} ({unreachable.Message}). "
                        + "The station carries on without rig control and keeps trying. Is rigctld "
                        + "running? For example: rigctld -m <model> -r /dev/ttyUSB0");
                }
            }

            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                opened.Dispose();
                return false;
            }

            _connection = opened;
            try
            {
                OnConnectedLocked(opened);
                _unreachableSaid = false;
                _lastProblem = null;
                return true;
            }
            catch (IOException lost)
            {
                // A rigctld that accepts and then hangs up (still starting, or wedged) is said once,
                // like one that refuses, rather than at every retry.
                DropLocked(lost.Message, quietly: _unreachableSaid);
                return false;
            }
        }
    }

    /// <summary>
    /// A new connection: settle anything owed, read the rig, and put it where the station wants
    /// it. Throws <see cref="IOException"/> if the connection fails part way.
    /// </summary>
    private void OnConnectedLocked(RigctldConnection connection)
    {
        // First, before anything else: a radio left keyed by a connection that died is the one
        // thing here that is on the air.
        if (KeysThroughRig && !_keyed)
        {
            try
            {
                connection.SetPtt(false);
                if (_unkeyOwed)
                {
                    _options.Say("rig: unkeyed the radio, which had been keyed when rigctld went away");
                }

                _unkeyOwed = false;
            }
            catch (RigctldException refused)
            {
                _options.Warn($"rig: WARNING - {refused.Message}");
            }
        }

        RigTuning state = ReadLocked(connection);
        _options.Say(_everConnected
            ? $"rig: rigctld at {Endpoint} is back: {state}"
            : $"rig: rigctld at {Endpoint}: {state}");
        _everConnected = true;

        if (_keyed)
        {
            return;
        }

        if (_restoreOwed is not null)
        {
            RestoreOwedLocked();
        }
        else if (_window is { } open)
        {
            TryApplyLocked(connection, open.Tuning);
        }
        else if (_options.Plan is { } plan && (!_planApplied || !IsOn(state, plan)))
        {
            // At start-up, and again after a reconnect that finds the rig somewhere else (a rig
            // that was power-cycled, a rigctld restarted against a rig that forgot): the band plan
            // is where this station's modems are, so it is where the dial has to be.
            _options.Say(_planApplied
                ? $"rig: the rig is not where the band plan puts it; setting it back to {plan}"
                : $"rig: setting the rig to {plan} from the band plan");
            try
            {
                ApplyLocked(connection, plan, "from the band plan");
            }
            catch (RigctldException refused)
            {
                _options.Warn(
                    $"rig: WARNING - {refused.Message}. The band plan's dial is not set; set the rig "
                    + $"to {plan} by hand");
            }

            _planApplied = true;
        }
    }

    /// <summary>One look at a connected rig: anything owed, then its dial and mode.</summary>
    private void Poll()
    {
        lock (_gate)
        {
            if (_connection is not { } connection || _keyed || _disposed)
            {
                return;
            }

            try
            {
                if (_unkeyOwed && KeysThroughRig)
                {
                    connection.SetPtt(false);
                    _unkeyOwed = false;
                    _options.Say("rig: unkeyed the radio, which had been left keyed");
                }

                if (_restoreOwed is not null)
                {
                    RestoreOwedLocked();
                    return;
                }

                RigTuning? before = _known;
                RigTuning now = ReadLocked(connection);
                if (before is not null && now != before)
                {
                    _options.Say($"rig: the rig is now on {now}, changed outside this station");
                }
            }
            catch (IOException lost)
            {
                DropLocked(lost.Message);
            }
            catch (RigctldException refused)
            {
                _lastProblem = refused.Message;
            }
        }
    }

    /// <summary>Puts the rig back after a window, or leaves the restore owed.</summary>
    private void RestoreOwedLocked()
    {
        if (_restoreOwed is not { } restore)
        {
            return;
        }

        if (_keyed)
        {
            // Unreachable while Key refuses during a window, and kept that way: the restore
            // waits for the unkey rather than retuning a transmitter.
            return;
        }

        if (_connection is not { } connection)
        {
            _options.Warn(
                $"rig: WARNING - rigctld at {Endpoint} is not connected, so the rig cannot be put "
                + $"back to {restore} yet. The station does not transmit until it has been");
            return;
        }

        try
        {
            ApplyLocked(connection, restore, "after the window");
            _restoreOwed = null;
            _options.Say($"rig: put back to {restore}; transmissions resume");
        }
        catch (IOException lost)
        {
            DropLocked(lost.Message);
            _options.Warn(
                $"rig: WARNING - the rig could not be put back to {restore} yet. The station does "
                + "not transmit until it has been");
        }
        catch (RigctldException refused)
        {
            _lastProblem = refused.Message;
            _options.Warn(
                $"rig: WARNING - the rig could not be put back to {restore} ({refused.Message}). The "
                + "station does not transmit until it has been; trying again at the next poll");
        }
    }

    /// <summary>
    /// Sets the mode and passband, then the dial, and reads them back. The mode goes first because
    /// some rigs move the dial when the mode changes.
    /// </summary>
    /// <exception cref="IOException">The connection failed; drop it.</exception>
    /// <exception cref="RigctldException">The rig refused the mode or the dial.</exception>
    private void ApplyLocked(RigctldConnection connection, RigTuning tuning, string purpose)
    {
        try
        {
            connection.SetMode(tuning.Mode, tuning.PassbandHz);
        }
        catch (RigctldException narrow) when (tuning.PassbandHz > 0)
        {
            _options.Warn(
                $"rig: WARNING - the rig would not take a {tuning.PassbandHz} Hz passband in "
                + $"{tuning.Mode} ({RigctldException.Describe(narrow.Code)}); using its normal width "
                + "for the mode instead");
            connection.SetMode(tuning.Mode, 0);
        }

        connection.SetFrequency(tuning.DialHz);
        RigTuning now = ReadLocked(connection);
        if (now.DialHz != tuning.DialHz || !string.Equals(now.Mode, tuning.Mode, StringComparison.OrdinalIgnoreCase))
        {
            _options.Warn(
                $"rig: WARNING - asked the rig for {tuning} {purpose}, and it reports {now}");
        }
        else if (tuning.PassbandHz > 0 && now.PassbandHz != tuning.PassbandHz)
        {
            _options.Warn(
                $"rig: WARNING - asked the rig for a {tuning.PassbandHz} Hz passband {purpose}, and "
                + $"it reports {now.PassbandHz} Hz. A modem outside the rig's filter is clipped or "
                + "deaf; \"rig\".\"passbandHz\" sets the width asked for, 0 the rig's normal one");
        }
    }

    /// <summary><see cref="ApplyLocked"/> where a refusal is only worth a warning.</summary>
    private void TryApplyLocked(RigctldConnection connection, RigTuning tuning)
    {
        try
        {
            ApplyLocked(connection, tuning, "");
        }
        catch (RigctldException refused)
        {
            _options.Warn($"rig: WARNING - {refused.Message}");
        }
    }

    /// <summary>Whether the rig is on <paramref name="wanted"/>: the dial and mode, and the
    /// passband when one was asked for.</summary>
    private static bool IsOn(RigTuning state, RigTuning wanted) =>
        state.DialHz == wanted.DialHz
        && string.Equals(state.Mode, wanted.Mode, StringComparison.OrdinalIgnoreCase)
        && (wanted.PassbandHz == 0 || state.PassbandHz == wanted.PassbandHz);

    private RigTuning ReadLocked(RigctldConnection connection)
    {
        long dial = connection.GetFrequency();
        (string mode, int passband) = connection.GetMode();
        _known = new RigTuning(dial, mode.ToUpperInvariant(), passband);
        return _known;
    }

    private void DropLocked(string why, bool quietly = false)
    {
        if (_connection is null)
        {
            return;
        }

        _connection.Dispose();
        _connection = null;
        _lastProblem = why;
        _unreachableSaid = true;
        _wake.TrySetResult();
        if (quietly)
        {
            return;
        }

        _options.Warn(
            $"rig: WARNING - lost rigctld at {Endpoint} ({why}); reconnecting"
            + (KeysThroughRig && (_keyed || _unkeyOwed)
                ? ". The radio may still be keyed: it is unkeyed the moment rigctld answers"
                : ""));
    }

    private string? HoldReasonLocked() =>
        _window is { } open
            ? $"the rig is tuned to {open.Tuning} for {open.Owner} until {TxLeaseApi.Utc(open.Expires)}, "
              + "so this station does not transmit until it is put back"
        : _restoreOwed is { } owed
            ? $"the rig has not yet been put back to {owed} after a tuning window, so this station "
              + "does not transmit until it has been"
        : null;

    // ------------------------------------------------------------------ shutdown

    /// <summary>Stops the watch, ends any window (putting the rig back), unkeys a rigctld PTT and
    /// closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        bool owed;
        lock (_gate)
        {
            owed = _window is not null || _restoreOwed is not null || _unkeyOwed || _keyed;
        }

        // One last try at a connection when something is owed, so a rigctld that was away is not
        // left with a radio on the wrong frequency, or keyed.
        if (owed && !Connected)
        {
            await TryConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _disposed = true;
            if (KeysThroughRig && _connection is { } connection)
            {
                try
                {
                    connection.SetPtt(false);
                    _unkeyOwed = false;
                }
                catch (Exception failed) when (failed is IOException or RigctldException)
                {
                    _options.Warn($"rig: WARNING - the unkey at shutdown failed ({failed.Message})");
                }
            }

            _keyed = false;
            if (_window is { } open)
            {
                _window = null;
                open.Timer?.Dispose();
                open.Lifetime.Unregister();
                _restoreOwed = open.RestoreTo;
                _options.Say($"rig: the window on {open.Tuning} for {open.Owner} has ended (the station is stopping)");
            }

            if (_restoreOwed is not null)
            {
                RestoreOwedLocked();
            }

            _connection?.Dispose();
            _connection = null;
        }

        _stop.Dispose();
    }
}

/// <summary>
/// The station's PTT with a rig in the way: keyed through rigctld (<see cref="Inner"/> null), or
/// through the station's own line with the rig's tuning windows guarding it.
/// </summary>
internal sealed class RigPtt(RigControl rig, IPttControl? inner) : IPttControl
{
    /// <summary>The rig.</summary>
    internal RigControl Rig => rig;

    /// <summary>The line that really keys the radio, or null when rigctld does.</summary>
    internal IPttControl? Inner => inner;

    /// <inheritdoc />
    public void Key()
    {
        rig.Key(sendT: inner is null);
        if (inner is null)
        {
            return;
        }

        try
        {
            inner.Key();
        }
        catch
        {
            rig.Unkey(sendT: false);
            throw;
        }
    }

    /// <inheritdoc />
    public void Unkey()
    {
        if (inner is null)
        {
            rig.Unkey(sendT: true);
            return;
        }

        try
        {
            inner.Unkey();
        }
        finally
        {
            rig.Unkey(sendT: false);
        }
    }
}
