namespace Packet.SoundModem.Daemon;

/// <summary>
/// Bounds the daemon's orderly shutdown: once stopping has begun, the process ends within
/// <see cref="Limit"/> whatever is still running (issue #790).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A station that says it is stopping and then does not is the worst state it
/// can be in: off the air, but still listening on every port, so hosts attach, a head end takes
/// a transmit lease and nothing is ever sent. GB7RDG sat like that for 21 minutes on 2026-11-08,
/// after the Flex ended its session, until <c>systemctl restart</c> gave up and SIGKILLed it. The
/// orderly path closes things one after another and any one of them can wait for ever on
/// something outside this process; the cause found then is fixed where it was, and this is what
/// makes the next one cost 20 seconds rather than a slot.</para>
/// <para><b>What it does.</b> <see cref="Begin"/> starts the clock, from whichever door stopping
/// came through: a lost radio, a station fault, an applied configuration change, Ctrl-C or SIGTERM.
/// <see cref="Dispose"/> stops it, and is the last thing the orderly shutdown does. If the clock
/// runs out first, one journal line names what had not closed and the process exits with code 1,
/// which the shipped unit's <c>Restart=on-failure</c> restarts. A fixed value, no setting: there
/// is no station for which "hang for ever" is the right answer.</para>
/// <para><b>What it names.</b> The parts the daemon registered with
/// <see cref="Track(string?, IDisposable?)"/> and friends that had not closed, in the order the
/// shutdown closes them, so the first one named is the one it was stuck on. Cheap by design: names and flags, nothing that
/// could itself hang.</para>
/// </remarks>
internal sealed class ShutdownDeadline : IDisposable
{
    /// <summary>How long the orderly shutdown gets. Ordinarily it takes well under a second; the
    /// slowest legitimate step is a Flex slice removal or an ALSA read, each a few seconds at
    /// most.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>What a host, a lease holder or a test caller is told while the station stops.</summary>
    internal const string Refusal =
        "this station is stopping so the service can restart it; try again once it is back";

    private readonly Action<string> _journal;
    private readonly Action<int> _exit;
    private readonly TimeProvider _time;
    private readonly TimeSpan _limit;
    private readonly Lock _gate = new();
    private readonly List<Part> _parts = [];
    private ITimer? _clock;
    private bool _finished;
    private volatile bool _stopping;

    /// <param name="journal">Where the one line goes when the deadline is missed.</param>
    /// <param name="exit">Ends the process with the code given; <see cref="Environment.Exit"/> in
    /// the daemon, something a test can watch in a test.</param>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="limit">The deadline; <see cref="Limit"/> when null. Only tests change it.</param>
    public ShutdownDeadline(
        Action<string> journal, Action<int> exit, TimeProvider? time = null, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(exit);
        _journal = journal;
        _exit = exit;
        _time = time ?? TimeProvider.System;
        _limit = limit ?? Limit;
    }

    /// <summary>Whether stopping has begun. Work that would never be carried out is refused from
    /// then on.</summary>
    public bool IsStopping => _stopping;

    /// <summary><see cref="Refusal"/> while stopping, null otherwise: the shape every refusal
    /// check in the daemon already takes.</summary>
    public string? RefusalReason => _stopping ? Refusal : null;

    /// <summary>Stopping has begun: start the clock. Safe to call any number of times, from any
    /// thread, including from inside a cancellation callback; only the first starts it.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_stopping || _finished)
            {
                return;
            }

            _stopping = true;
            _clock = _time.CreateTimer(_ => Expire(), null, _limit, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Registers a part the shutdown closes by disposing it, and hands back the thing to
    /// dispose instead. A null name, or a null <paramref name="inner"/>, registers nothing (the
    /// part does not exist on this station); <paramref name="inner"/> is still disposed.</summary>
    public IDisposable Track(string? name, IDisposable? inner)
    {
        Part? part = inner is null ? null : Register(name);
        return new Disposer(() =>
        {
            try
            {
                inner?.Dispose();
            }
            finally
            {
                part?.Close();
            }
        });
    }

    /// <summary>As <see cref="Track(string?, IDisposable?)"/>, for a part closed asynchronously.
    /// A null <paramref name="inner"/> is a part this station does not have: nothing is
    /// registered, and the result disposes nothing.</summary>
    public IAsyncDisposable Track(string name, IAsyncDisposable? inner)
    {
        Part? part = inner is null ? null : Register(name);
        return new AsyncPart(inner, part);
    }

    /// <summary>Registers a part that closes when <paramref name="task"/> ends, however it ends.</summary>
    public void Watch(string name, Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Part part = Register(name)!;
        _ = task.ContinueWith(
            _ => part.Close(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Registers a part that is running now and closes when the result is disposed: a
    /// loop the shutdown waits for, such as the receive loop.</summary>
    public IDisposable Running(string name)
    {
        Part part = Register(name)!;
        return new Disposer(part.Close);
    }

    /// <summary>The orderly shutdown finished: stop the clock.</summary>
    public void Dispose()
    {
        ITimer? clock;
        lock (_gate)
        {
            _finished = true;
            clock = _clock;
            _clock = null;
        }

        clock?.Dispose();
    }

    /// <summary>The parts not yet closed, in the order the shutdown closes them (the reverse of
    /// the order they were registered in, as <c>using</c> declarations are disposed).</summary>
    internal IReadOnlyList<string> StillOpen()
    {
        lock (_gate)
        {
            var open = new List<string>();
            for (int i = _parts.Count - 1; i >= 0; i--)
            {
                if (!_parts[i].Closed)
                {
                    open.Add(_parts[i].Name);
                }
            }

            return open;
        }
    }

    private Part? Register(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var part = new Part(name);
        lock (_gate)
        {
            _parts.Add(part);
        }

        return part;
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }
        }

        IReadOnlyList<string> open = StillOpen();
        string what = open.Count == 0
            ? "every tracked part had closed, so it was stuck somewhere untracked"
            : $"still open, in closing order: {string.Join(", ", open)}";
        _journal(
            $"shutdown: not finished {_limit.TotalSeconds:0} s after it began ({what}); "
            + "exiting with code 1 so the service restarts");
        _exit(1);
    }

    private sealed class Part(string name)
    {
        private volatile bool _closed;

        public string Name { get; } = name;

        public bool Closed => _closed;

        public void Close() => _closed = true;
    }

    private sealed class AsyncPart(IAsyncDisposable? inner, Part? part) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (inner is not null)
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                part?.Close();
            }
        }
    }
}
