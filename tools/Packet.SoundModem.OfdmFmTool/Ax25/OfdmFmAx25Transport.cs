using System.Runtime.CompilerServices;
using M0LTE.OfdmFm;
using Packet.Ax25.Transport;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.OfdmFmTool.Ax25;

/// <summary>
/// The AX.25 stack's view of the OFDM-FM frame port: <see cref="IAx25Transport"/> to send and receive whole frames, and <see cref="ITxCompletionTransport"/> so that the stack starts its acknowledgement timer from
/// <c>Transmitted</c> (a frame can wait a long time for a busy channel) and not from the moment it handed the frame over. No carrier sense is offered: the station does its own.
/// </summary>
/// <remarks>
/// <para><b>One outstanding copy.</b> The stack can retransmit a frame while its first copy is still queued in the modem. A frame whose bytes equal one still outstanding is not sent again: its task completes
/// when the original's does, with the same outcome, and no second datagram is queued.</para>
/// <para><b>At rest.</b> A bench has to know when the stack has no work in hand, so that it moves the clock and feeds audio only then. <see cref="AtRest"/> says so from what this class sees of the three places the stack hands
/// work across a thread: frames in (the port's delivery thread, then this class's queue, then the stack's pump), and completions out (the modem's report, then a continuation here, which completes the task the stack awaits
/// inline). Everything else the stack does is synchronous on the thread that gave it the event.</para>
/// </remarks>
internal sealed class OfdmFmAx25Transport : ITxCompletionTransport
{
    private readonly OfdmFmFramePort _port;
    private readonly OfdmFmStationHost _host;
    private readonly SoundModemChannel _channel;
    private readonly int _subChannel;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<Ax25InboundFrame> _queue = [];
    private readonly Dictionary<string, Outstanding> _outstanding = [];
    private readonly Action<int, byte[]> _onFrame;
    private readonly Action<ReceivedEvent> _onHostReceived;
    private readonly Action<TransmittedEvent> _onTransmitted;
    private readonly Action<DroppedEvent> _onDropped;
    private TaskCompletionSource? _waiter;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _heardByHost;
    private long _consumed;
    private long _reportedByModem;
    private long _completed;
    private bool _parked;
    private bool _started;
    private bool _disposed;

    /// <param name="port">The frame port, attached to <paramref name="channel"/> at <paramref name="subChannel"/>.</param>
    /// <param name="host">The host the port was made over.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="subChannel">The port's sub-channel.</param>
    /// <param name="time">The clock a frame's receive and completion times are read from.</param>
    public OfdmFmAx25Transport(OfdmFmFramePort port, OfdmFmStationHost host, SoundModemChannel channel, int subChannel, TimeProvider time)
    {
        _port = port;
        _host = host;
        _channel = channel;
        _subChannel = subChannel;
        _time = time;
        _onFrame = OnFrame;
        _onHostReceived = OnHostReceived;
        _onTransmitted = _ => Count(ref _reportedByModem);
        _onDropped = _ => Count(ref _reportedByModem);
        channel.FrameReceived += _onFrame;
        host.Received += _onHostReceived;
        host.Transmitted += _onTransmitted;
        host.Dropped += _onDropped;
    }

    /// <summary>
    /// True when the stack has nothing in hand that this class can see: every frame the modem handed up has been through the stack's pump, the pump is waiting for the next, and every report of a transmission has
    /// been turned into the completion the stack was waiting for. Frames still waiting in the modem for the air are not the stack's work.
    /// </summary>
    public bool AtRest
    {
        get
        {
            lock (_gate)
            {
                return _started && _parked && _queue.Count == 0
                    && _heardByHost == _consumed + _port.ReceivedFramesLost
                    && _reportedByModem == _completed;
            }
        }
    }

    /// <summary>For a failing test: the counts <see cref="AtRest"/> reads.</summary>
    internal string Describe()
    {
        lock (_gate)
        {
            return $"started={_started} parked={_parked} queued={_queue.Count} heardByHost={_heardByHost} consumed={_consumed} lost={_port.ReceivedFramesLost} reported={_reportedByModem} completed={_completed} outstanding={_outstanding.Count}";
        }
    }

    /// <summary>The signal for the next change of anything <see cref="AtRest"/> reads. Take it before looking, never after.</summary>
    public Task Changed()
    {
        lock (_gate)
        {
            return _changed.Task;
        }
    }

    /// <summary>Frames handed to the stack and finished with.</summary>
    public long FramesConsumed
    {
        get
        {
            lock (_gate)
            {
                return _consumed;
            }
        }
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        Submit(ax25.ToArray(), wait: null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TxCompletion> SendAwaitingCompletionAsync(ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // A task from a source whose continuations run where it is completed: the stack's re-arm of its acknowledgement timer then runs before this class counts the completion.
        var wait = new Waiter(_time.GetUtcNow());
        Submit(ax25.ToArray(), wait);
        return wait.Source.Task;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Ax25InboundFrame frame;
            TaskCompletionSource? wait = null;
            lock (_gate)
            {
                _started = true;
                if (_queue.Count > 0)
                {
                    frame = _queue.Dequeue();
                    _parked = false;
                }
                else
                {
                    frame = default;
                    wait = _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _parked = true;
                }
            }

            if (wait is not null)
            {
                Signal();
                bool stopped = false;
                try
                {
                    await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                }

                if (stopped)
                {
                    yield break;
                }

                continue;
            }

            yield return frame;

            // The stack has come back for the next: it has finished with this one, its whole reaction included.
            lock (_gate)
            {
                _consumed++;
            }

            Signal();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            waiter = _waiter;
        }

        _channel.FrameReceived -= _onFrame;
        _host.Received -= _onHostReceived;
        _host.Transmitted -= _onTransmitted;
        _host.Dropped -= _onDropped;
        waiter?.TrySetException(new OperationCanceledException());
        return ValueTask.CompletedTask;
    }

    private void OnHostReceived(ReceivedEvent e)
    {
        // Raised on the receive thread, inside the host's lock, before the audio call that carried the datagram returns: the count is up before anything can look.
        if (e.Protocol == LinkProtocol.Ax25Frame)
        {
            Count(ref _heardByHost);
        }
    }

    private void OnFrame(int subChannel, byte[] frame)
    {
        if (subChannel != _subChannel)
        {
            return;
        }

        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _queue.Enqueue(new Ax25InboundFrame(frame, 0, _time.GetUtcNow()));
            waiter = _waiter;
            _waiter = null;
            _parked = false;
        }

        waiter?.TrySetResult();
        Signal();
    }

    private void Count(ref long counter)
    {
        lock (_gate)
        {
            counter++;
        }

        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    private void Submit(byte[] frame, Waiter? wait)
    {
        string key = Convert.ToBase64String(frame);
        lock (_gate)
        {
            if (_outstanding.TryGetValue(key, out Outstanding? original))
            {
                if (wait is not null)
                {
                    original.Waiters.Add(wait);
                }

                return;
            }
        }

        // The port is called outside the gate: it takes the host's lock, which holds the gate in its handlers.
        Task sent = _port.Transmit(frame);
        bool refusedAtOnce = sent.IsCompleted;
        var entry = new Outstanding();
        if (wait is not null)
        {
            entry.Waiters.Add(wait);
        }

        if (!refusedAtOnce)
        {
            lock (_gate)
            {
                _outstanding[key] = entry;
            }
        }

        _ = sent.ContinueWith(
            finished =>
            {
                Waiter[] waiters;
                lock (_gate)
                {
                    _outstanding.Remove(key);
                    waiters = [.. entry.Waiters];
                }

                DateTimeOffset now = _time.GetUtcNow();
                Exception? failure = finished.Exception?.GetBaseException();
                foreach (Waiter waiter in waiters)
                {
                    if (failure is null)
                    {
                        waiter.Source.TrySetResult(new TxCompletion(waiter.Queued, now));
                    }
                    else
                    {
                        waiter.Source.TrySetException(failure);
                    }
                }

                if (!refusedAtOnce)
                {
                    Count(ref _completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class Waiter(DateTimeOffset queued)
    {
        public DateTimeOffset Queued { get; } = queued;

        public TaskCompletionSource<TxCompletion> Source { get; } = new();
    }

    private sealed class Outstanding
    {
        public List<Waiter> Waiters { get; } = [];
    }
}
