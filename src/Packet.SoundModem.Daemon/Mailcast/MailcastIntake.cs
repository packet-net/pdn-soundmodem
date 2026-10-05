using System.Threading.Channels;
using Packet.Mailcast;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Takes mailcast frames from the modem and keeps their pieces in Packet.Mailcast's
/// <see cref="ReceiverStore"/>, which puts every piece on disk so bulletins add up across slots
/// and restarts.
/// </summary>
/// <remarks>
/// Frames arrive on the receive thread, and keeping a piece writes and flushes a file, which is no
/// business of that thread's: they go through a bounded queue to a worker of their own. A full
/// queue means the store has stopped, so frames are dropped and counted rather than waited for.
/// Every call into the store holds one lock, because the store is not thread-safe and delivery
/// and the page read it too.
/// </remarks>
internal sealed class MailcastIntake : IAsyncDisposable
{
    /// <summary>Frames that may wait for the store at once; they come a few a minute.</summary>
    internal const int QueueLength = 4096;

    private readonly ReceiverStore _store;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _queue;
    private readonly Task _worker;
    private readonly Lock _settledGate = new();
    private TaskCompletionSource _settledChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _offered;
    private long _settled;
    private long _heard;
    private long _dropped;
    private SlotTimetable? _timetable;

    /// <summary>Opens the store in <paramref name="directory"/>, creating it if need be.</summary>
    internal MailcastIntake(string directory, TimeProvider time, Action<string> log)
    {
        _log = log;
        _store = new ReceiverStore(directory, Compression.Default, new ReceiverStoreOptions
        {
            Time = time,
            Log = line => log("mailcast: store: " + MailcastOnAir.Ascii(line)),
        });
        _timetable = _store.HeardSchedule;
        _queue = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(QueueLength) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite },
            _ => Dropped());
        _worker = Task.Run(RunAsync);
    }

    /// <summary>A bulletin was rebuilt and is in the outbox (raised on the worker).</summary>
    internal event Action<Bulletin>? BulletinCompleted;

    /// <summary>A mailcast frame was heard (raised on the worker).</summary>
    internal event Action? FrameHeard;

    /// <summary>A directory gave GB7RDG's timetable, different from the one held (raised on the worker).</summary>
    internal event Action<SlotTimetable>? TimetableHeard;

    /// <summary>GB7RDG's timetable from the newest directory that gave one, kept across restarts.</summary>
    internal SlotTimetable? HeardTimetable
    {
        get
        {
            lock (_gate)
            {
                return _timetable;
            }
        }
    }

    /// <summary>Mailcast frames heard since start.</summary>
    internal long FramesHeard => Interlocked.Read(ref _heard);

    /// <summary>Frames dropped because the store was not keeping up.</summary>
    internal long FramesDropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Offers a decoded AX.25 frame; anything but a mailcast frame is ignored. Any thread, and
    /// returns at once.
    /// </summary>
    internal bool Offer(byte[] frame)
    {
        if (!MailcastOnAir.TryGetPayload(frame, out ReadOnlyMemory<byte> payload))
        {
            return false;
        }

        Interlocked.Increment(ref _heard);
        Interlocked.Increment(ref _offered);
        if (!_queue.Writer.TryWrite(payload))
        {
            Settle();
        }

        return true;
    }

    /// <summary>Waits until every frame offered so far has been through the store or dropped.</summary>
    internal async Task DrainAsync(CancellationToken cancellation)
    {
        long target = Interlocked.Read(ref _offered);
        while (true)
        {
            Task changed;
            lock (_settledGate)
            {
                if (_settled >= target)
                {
                    return;
                }

                changed = _settledChanged.Task;
            }

            await changed.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Rebuilt bulletins not yet answered for by the BBS, oldest first.</summary>
    internal IReadOnlyList<Bulletin> Pending()
    {
        lock (_gate)
        {
            return _store.Pending();
        }
    }

    /// <summary>Takes a bulletin out of the outbox once the BBS has answered for it for good.</summary>
    internal void Acknowledge(string bid)
    {
        lock (_gate)
        {
            _store.Acknowledge(bid);
        }
    }

    /// <summary>The newest directory, how far each of its objects has got, and how many objects
    /// have pieces but are not rebuilt.</summary>
    internal (BroadcastDirectory? Directory, IReadOnlyList<ObjectProgress> Progress, int Partial) Progress()
    {
        lock (_gate)
        {
            return (_store.Directory, _store.Progress(), _store.PartialObjects);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private void Dropped()
    {
        if (Interlocked.Increment(ref _dropped) % 100 == 1)
        {
            _log($"mailcast: WARNING - the store is not keeping up; {Interlocked.Read(ref _dropped)} frame(s) dropped");
        }

        Settle();
    }

    private void Settle()
    {
        TaskCompletionSource changed;
        lock (_settledGate)
        {
            _settled++;
            changed = _settledChanged;
            _settledChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private async Task RunAsync()
    {
        await foreach (ReadOnlyMemory<byte> payload in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Accept(payload);
            }
            finally
            {
                Settle();
            }
        }
    }

    private void Accept(ReadOnlyMemory<byte> payload)
    {
        AcceptResult result;
        SlotTimetable? changed = null;
        try
        {
            lock (_gate)
            {
                result = _store.Accept(payload.Span);
                if (result.Outcome == FrameOutcome.CompletedDirectory
                    && _store.HeardSchedule is { } heard && heard != _timetable)
                {
                    _timetable = changed = heard;
                }
            }
        }
        catch (Exception e)
        {
            // The worker must outlive whatever one frame does to the store, or every frame after
            // it is lost without a word.
            _log($"mailcast: WARNING - cannot keep a piece: {MailcastOnAir.Ascii(e.Message)}");
            return;
        }

        try
        {
            FrameHeard?.Invoke();
            Report(result);
            if (changed is not null)
            {
                TimetableHeard?.Invoke(changed);
            }
        }
        catch (Exception e)
        {
            _log($"mailcast: WARNING - a listener failed: {MailcastOnAir.Ascii(e.Message)}");
        }
    }

    private void Report(AcceptResult result)
    {
        switch (result.Outcome)
        {
            case FrameOutcome.CompletedBulletin when result.Bulletin is { } bulletin:
                _log($"mailcast: bulletin complete: {MailcastOnAir.Ascii(bulletin.Bid)} from {MailcastOnAir.Ascii(bulletin.From)} "
                    + $"to {MailcastOnAir.Ascii(bulletin.To)}{(bulletin.At.Length > 0 ? "@" + MailcastOnAir.Ascii(bulletin.At) : "")}, "
                    + $"\"{MailcastOnAir.Ascii(bulletin.Title)}\"");
                BulletinCompleted?.Invoke(bulletin);
                break;
            case FrameOutcome.CompletedDirectory when result.Directory is { } directory:
                _log($"mailcast: directory for {directory.Date:yyyy-MM-dd}: {directory.Entries.Count} bulletins in rotation");
                break;
            case FrameOutcome.CompletedUnhandled when result.ContentType is { } type:
                // Once per object: it is marked done, so its later frames are not rebuilt again.
                _log($"mailcast: object {ObjectId.Format(result.ObjectId ?? 0)} is a {ContentType.Describe(type)}, "
                    + "which this receiver does not deliver; kept out of the BBS");
                break;
            case FrameOutcome.UnknownDictionary:
                _log("mailcast: a frame uses a compression dictionary this version does not have; a newer pdn-soundmodem may be needed");
                break;
            case FrameOutcome.Rejected:
                _log($"mailcast: a frame or a rebuilt object failed its check: {MailcastOnAir.Ascii(result.Detail)}");
                break;
            default:
                break;
        }
    }
}
