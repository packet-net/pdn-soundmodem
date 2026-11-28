using System.Threading.Channels;
using ThreadingChannel = System.Threading.Channels.Channel;
using M0LTE.OfdmFm;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Modems.OfdmFmStation;

/// <summary>
/// The AX.25 frame port of the adaptive OFDM-FM station: a whole AX.25 frame in, sent as a datagram of protocol 0x00 through an <see cref="OfdmFmStationHost"/>, and every received datagram
/// of that protocol handed up to the channel as a received frame. A program makes one with <see cref="Attach"/>.
/// </summary>
/// <remarks>
/// <para><b>Sending.</b> The link addresses come from the frame's header through <see cref="Ax25Header"/> (the packet.net library reads the frame; nothing here looks inside it): the datagram goes to the next hop,
/// as the station the frame is being transmitted as; a UI frame with no digipeater still to repeat it goes to everyone. Spelled as ASCII <c>CALL-SSID</c>, upper case, SSID 0 bare. An acknowledgement
/// or response (RR, RNR, REJ, SREJ, UA, DM) is expedited, the rest ordinary, except that one is sent as ordinary while an ordinary frame to the same station is still waiting: the link needs its frames in order. No report is asked for: AX.25 acknowledges for itself. One tag per frame, from a counter; the task completes when the host raises
/// <see cref="OfdmFmStationHost.Transmitted"/> for it and faults when it raises <see cref="OfdmFmStationHost.Dropped"/>. A frame the station refuses, or whose header does not read, faults the task and sends nothing.</para>
/// <para><b>Threads.</b> The host raises its events on the receive thread inside its lock. The handlers here only complete tasks (with continuations run asynchronously, so no caller's code runs there) or queue a copy of
/// a received frame; they never call the host. Received frames are handed to <see cref="SoundModemChannel.DeliverReceived"/> from a thread of this port's own, one at a time in arrival order, because the channel's
/// listeners (the in-process transport, a KISS server) may answer at once by sending, which calls the host, and the host refuses calls from inside its own events.</para>
/// </remarks>
public sealed class OfdmFmFramePort : ISubChannelPort, IDisposable
{
    /// <summary>The mode's name, as shown wherever a modem's would be.</summary>
    public const string ModeName = "ofdm-fm";

    /// <summary>How many received frames may wait for the channel's listeners before more are counted and dropped.</summary>
    private const int ReceiveQueueLimit = 1024;

    private readonly OfdmFmStationHost _host;
    private readonly SoundModemChannel _channel;
    private readonly int _subChannel;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, TaskCompletionSource> _outstanding = [];
    private readonly Dictionary<long, LinkAddress> _ordinaryTo = [];
    private readonly Dictionary<LinkAddress, int> _ordinaryAhead = [];
    private readonly Channel<(byte[] Frame, FrameQuality Quality)> _received =
        ThreadingChannel.CreateBounded<(byte[], FrameQuality)>(new BoundedChannelOptions(ReceiveQueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Thread _delivery;
    private readonly Action<ReceivedEvent> _onReceived;
    private readonly Action<TransmittedEvent> _onTransmitted;
    private readonly Action<DroppedEvent> _onDropped;
    private long _nextTag;
    private long _nonAx25;
    private long _lostOnReceive;
    private long _deliveryFaults;
    private bool _disposed;

    private OfdmFmFramePort(OfdmFmStationHost host, SoundModemChannel channel, int subChannel)
    {
        _host = host;
        _channel = channel;
        _subChannel = subChannel;
        _onReceived = OnReceived;
        _onTransmitted = OnTransmitted;
        _onDropped = OnDropped;
        _delivery = new Thread(DeliverLoop) { IsBackground = true, Name = "ofdm-fm frame port delivery" };
    }

    /// <summary>
    /// Makes a port over <paramref name="host"/> and puts it on <paramref name="subChannel"/> of <paramref name="channel"/>. The host and the port should belong to the same channel.
    /// The caller disposes the port before the host.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sub-channel already has a modem or a port.</exception>
    public static OfdmFmFramePort Attach(SoundModemChannel channel, int subChannel, OfdmFmStationHost host)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(host);
        var port = new OfdmFmFramePort(host, channel, subChannel);
        channel.AddPort(subChannel, port);
        port._delivery.Start();
        host.Received += port._onReceived;
        host.Transmitted += port._onTransmitted;
        host.Dropped += port._onDropped;
        return port;
    }

    /// <inheritdoc />
    public string Mode => ModeName;

    /// <summary>Datagrams heard whose protocol byte is not 0x00 (a whole AX.25 frame). They are not delivered as frames.</summary>
    public long NonAx25Datagrams => Interlocked.Read(ref _nonAx25);

    /// <summary>Raised after <see cref="NonAx25Datagrams"/> has moved, on the thread that heard it. For tests, which wait on it instead of looking again after a pause.</summary>
    internal event Action? NonAx25Counted;

    /// <summary>Received frames dropped because more than 1024 were waiting for the channel's listeners.</summary>
    public long ReceivedFramesLost => Interlocked.Read(ref _lostOnReceive);

    /// <summary>Received frames that a listener on the channel threw on.</summary>
    public long DeliveryFaults => Interlocked.Read(ref _deliveryFaults);

    /// <summary>How many frames accepted as ordinary, an acknowledgement held to the order of its station's frames among them, have not yet been reported transmitted or dropped.</summary>
    internal int OrdinaryWaiting
    {
        get
        {
            lock (_gate)
            {
                return _ordinaryTo.Count;
            }
        }
    }

    /// <summary>How many frames have been accepted by the host and not yet reported transmitted or dropped.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
            {
                return _outstanding.Count;
            }
        }
    }

    /// <summary>The choice of link addresses and urgency for a frame, or null when its header does not read. The first element is where the datagram goes, the second who it is from.</summary>
    internal static (LinkAddress To, LinkAddress From, SendPriority Priority)? Plan(byte[] frame)
    {
        if (Ax25Header.NextHop(frame) is not { } nextHop || Ax25Header.HeardStation(frame) is not { } from)
        {
            return null;
        }

        LinkAddress to = Ax25Header.IsUiWithNoDigipeaterPending(frame) ? LinkAddress.Everyone : LinkAddress.FromAscii(nextHop);
        return (to, LinkAddress.FromAscii(from), Ax25Header.IsExpedited(frame) ? SendPriority.Expedited : SendPriority.Ordinary);
    }

    /// <inheritdoc />
    public Task Transmit(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Plan(frame) is not { } plan)
        {
            return Task.FromException(new InvalidOperationException("frame addresses unreadable, not sent"));
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long tag = Interlocked.Increment(ref _nextTag);
        SendPriority priority = plan.Priority;

        // Registered before the call: the host may report the datagram's fate as soon as it has released its lock.
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromException(new InvalidOperationException("frame port closed"));
            }

            _outstanding.Add(tag, done);

            // AX.25 relies on frames arriving in the order they were sent. The modem sends an expedited datagram ahead of ordinary ones, so an acknowledgement sent after an I frame that is still queued
            // for the same station would arrive first, with an N(R) that the I frame then appears to take back: the receiver resets the link. An acknowledgement goes first only when nothing ordinary is ahead of it.
            if (priority == SendPriority.Expedited && _ordinaryAhead.TryGetValue(plan.To, out int ahead) && ahead > 0)
            {
                priority = SendPriority.Ordinary;
            }

            if (priority == SendPriority.Ordinary)
            {
                _ordinaryTo[tag] = plan.To;
                _ordinaryAhead[plan.To] = _ordinaryAhead.GetValueOrDefault(plan.To) + 1;
            }
        }

        SendResult result;
        try
        {
            result = _host.Send(frame, plan.To, plan.From, LinkProtocol.Ax25Frame, new HostTag(tag), reportWanted: false, priority);
        }
        catch
        {
            Forget(tag);
            throw;
        }

        if (result != SendResult.Accepted)
        {
            Forget(tag);
            return Task.FromException(new InvalidOperationException(Refusal(result, plan.To)));
        }

        return done.Task;
    }

    private string Refusal(SendResult result, LinkAddress to) => result switch
    {
        SendResult.NoIdentity => "no callsign set, not sent",
        SendResult.QueueFull => $"queue to {Name(to)} full, not sent",
        SendResult.TooLarge => $"frame too large for one burst to {Name(to)} now{RoomNote(to)}, not sent",
        _ => $"refused ({result}), not sent",
    };

    private string RoomNote(LinkAddress to)
    {
        foreach (LinkStateEvent state in _host.Snapshot())
        {
            if (state.Address.Equals(to))
            {
                return $" ({state.DatagramRoom} bytes room)";
            }
        }

        return string.Empty;
    }

    private static string Name(LinkAddress address) => address.IsEveryone ? "everyone" : System.Text.Encoding.ASCII.GetString(address.Bytes);

    private void Forget(long tag)
    {
        lock (_gate)
        {
            _outstanding.Remove(tag);
            ReleaseOrdinary(tag);
        }
    }

    private TaskCompletionSource? Take(long tag)
    {
        lock (_gate)
        {
            ReleaseOrdinary(tag);
            return _outstanding.Remove(tag, out TaskCompletionSource? done) ? done : null;
        }
    }

    /// <summary>Under the gate: an ordinary frame is no longer ahead of the acknowledgements that follow it.</summary>
    private void ReleaseOrdinary(long tag)
    {
        if (_ordinaryTo.Remove(tag, out LinkAddress to) && _ordinaryAhead.TryGetValue(to, out int count))
        {
            if (count <= 1)
            {
                _ordinaryAhead.Remove(to);
            }
            else
            {
                _ordinaryAhead[to] = count - 1;
            }
        }
    }

    // The three handlers run on the receive thread inside the host's lock: no host calls, no caller's code.
    private void OnTransmitted(TransmittedEvent e) => Take(e.Tag.Value)?.TrySetResult();

    private void OnDropped(DroppedEvent e) => Take(e.Tag.Value)?.TrySetException(new InvalidOperationException(e.Reason switch
    {
        DropReason.Cancelled => "frame cancelled before it was sent",
        DropReason.Expired => "frame expired in the queue",
        DropReason.NoLongerFits => "frame no longer fits one burst at the link's present format",
        _ => $"frame dropped ({e.Reason})",
    }));

    private void OnReceived(ReceivedEvent e)
    {
        if (e.Protocol != LinkProtocol.Ax25Frame)
        {
            Interlocked.Increment(ref _nonAx25);
            NonAx25Counted?.Invoke();
            return;
        }

        // CrcValid: the station delivers a datagram only after the burst's payload checks. Nothing else the modem reports maps onto a field here:
        // its quality figure is not an SNR on the 3 kHz reference, and there is no correction count, frequency offset or level for the frame.
        byte[] frame = e.Datagram.ToArray();
        var quality = new FrameQuality(ModeName, frame.Length, CorrectedBytes: null, CrcValid: true);
        if (!_received.Writer.TryWrite((frame, quality)))
        {
            Interlocked.Increment(ref _lostOnReceive);
        }
    }

    private void DeliverLoop()
    {
        ChannelReader<(byte[] Frame, FrameQuality Quality)> reader = _received.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out (byte[] Frame, FrameQuality Quality) item))
            {
                try
                {
                    _channel.DeliverReceived(_subChannel, item.Frame, item.Quality);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _deliveryFaults);
                }
            }
        }
    }

    /// <summary>Stops listening to the host, ends the delivery thread once the frames already queued are handed up, and faults every frame still waiting to go. Safe to call twice.</summary>
    public void Dispose()
    {
        TaskCompletionSource[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _outstanding.Values];
            _outstanding.Clear();
            _ordinaryTo.Clear();
            _ordinaryAhead.Clear();
        }

        _host.Received -= _onReceived;
        _host.Transmitted -= _onTransmitted;
        _host.Dropped -= _onDropped;
        _received.Writer.TryComplete();
        foreach (TaskCompletionSource done in pending)
        {
            done.TrySetException(new InvalidOperationException("frame port closed"));
        }

        if (_delivery.ManagedThreadId != Environment.CurrentManagedThreadId && _delivery.IsAlive)
        {
            _delivery.Join();
        }
    }
}
