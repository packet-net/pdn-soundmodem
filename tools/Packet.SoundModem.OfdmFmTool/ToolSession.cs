using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using M0LTE.OfdmFm;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>What happened to a datagram handed to the modem.</summary>
/// <param name="Sent">True: its burst left. False: it was dropped.</param>
/// <param name="Reason">Why it was dropped, else null.</param>
/// <param name="Burst">The burst that carried it, when it left.</param>
internal sealed record Fate(bool Sent, string? Reason, TransmittedEvent? Burst);

/// <summary>What the tool does with a station host, apart from the device I/O around it: the identity, the commands and every line they print. Time comes from the injected clock.</summary>
/// <remarks>
/// The host's events run on the receive thread inside the host's lock and must not call the host. The handlers here only log (a queue write), complete tasks and queue an echo to answer;
/// a task of this class's own sends the echoes. The commands run on the caller's thread and may call the host.
/// </remarks>
internal sealed class ToolSession : IAsyncDisposable
{
    /// <summary>How long a ping waits for its echo.</summary>
    internal static readonly TimeSpan PingWait = TimeSpan.FromSeconds(60);

    private const long ReplyTagBase = 1L << 40;

    private readonly OfdmFmStationHost _host;
    private readonly StampedLog _log;
    private readonly TimeProvider _time;
    private readonly bool _canTransmit;
    private readonly bool _detail;
    private readonly string _call;
    private readonly LinkAddress _from;
    private readonly LinkAddress? _to;
    private readonly Dictionary<uint, string> _names = [];
    private readonly ConcurrentDictionary<long, Action<Fate>> _fates = [];
    private readonly ConcurrentDictionary<ushort, Action<long>> _answers = [];
    private readonly Lock _restGate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _repliesOwed;
    private int _wakesOwed;
    private int _notesOwed;
    private readonly Channel<(byte[] Reply, uint ToKey, ushort Sequence)> _replies = System.Threading.Channels.Channel.CreateUnbounded<(byte[], uint, ushort)>();
    private readonly System.Threading.Channels.Channel<DateTimeOffset> _identifications = System.Threading.Channels.Channel.CreateUnbounded<DateTimeOffset>();
    private readonly IdentificationNote? _note;
    private Task _replier = Task.CompletedTask;
    private Task _noteWriter = Task.CompletedTask;
    private long _replyTag;

    /// <param name="host">The station host.</param>
    /// <param name="call">This station's callsign, and the link address it sends from and answers to.</param>
    /// <param name="to">The other station's link address, or null for none.</param>
    /// <param name="detail">True: trace at the Detail level.</param>
    /// <param name="canTransmit">False: the station only listens, and never sends an echo.</param>
    /// <param name="log">Where every line goes.</param>
    /// <param name="time">The clock the lines and the round trips are read from.</param>
    /// <param name="note">Where the time of each identification is written, or null for nowhere.</param>
    public ToolSession(OfdmFmStationHost host, string call, string? to, bool detail, bool canTransmit, StampedLog log, TimeProvider time, IdentificationNote? note = null)
    {
        _note = note;
        _host = host;
        _call = call;
        _detail = detail;
        _canTransmit = canTransmit;
        _log = log;
        _time = time;
        _from = LinkAddress.FromAscii(call);
        _names[_from.Key] = call;
        if (to is not null)
        {
            _to = LinkAddress.FromAscii(to);
            _names[_to.Value.Key] = to;
        }
    }

    /// <summary>A test's hook: called on whichever thread is about to act on something the host reported (send an echo, or carry on with a command that was waiting), before it does. A test holds it to make the session slow.</summary>
    internal Action? BeforeAct { get; init; }

    /// <summary>
    /// True when the session has nothing in hand: no echo is queued or being sent, no command has been woken and not yet carried on to its next wait or its end, and no identification time is waiting to be written.
    /// The session acts on the host's reports from tasks of its own, so a bench that moves a clock or feeds audio must wait for this, with <see cref="Changed"/> taken before the look.
    /// </summary>
    public bool AtRest
    {
        get
        {
            lock (_restGate)
            {
                return _repliesOwed == 0 && _wakesOwed == 0 && _notesOwed == 0;
            }
        }
    }

    /// <summary>The signal for the next change of <see cref="AtRest"/>'s inputs. Take it before looking, never after.</summary>
    public Task Changed()
    {
        lock (_restGate)
        {
            return _changed.Task;
        }
    }

    private void Owe(ref int counter, int delta)
    {
        TaskCompletionSource old;
        lock (_restGate)
        {
            counter += delta;
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    /// <summary>
    /// Completes what a command waits on and counts the command as woken until it has carried on. The count goes up before the task completes: the continuation runs on another thread, and a look
    /// between the completion and the count would find the session at rest with its next move still to make.
    /// </summary>
    private bool Wake<T>(TaskCompletionSource<T> source, T value)
    {
        Owe(ref _wakesOwed, 1);
        if (source.TrySetResult(value))
        {
            return true;
        }

        Owe(ref _wakesOwed, -1);
        return false;
    }

    private void Carried() => Owe(ref _wakesOwed, -1);

    /// <summary>
    /// Gives the station its callsign and starts listening to what it reports. Call it once, before the first command.
    /// </summary>
    /// <returns>Null when it is ready, else one line saying why not.</returns>
    public string? Start()
    {
        _host.Trace += e => _log.Write(e.Text);
        _host.TraceLevel = _detail ? TraceLevel.Detail : TraceLevel.Flow;
        _host.Notice += _log.Write;
        _host.Received += OnReceived;
        _host.Transmitted += e => Settle(e.Tag, new Fate(true, null, e));
        _host.Dropped += e => Settle(e.Tag, new Fate(false, DropText(e.Reason), null));
        _host.MorseIdentSent += text =>
        {
            _log.Write($"Identified in Morse as {text}");
        };

        // The host raises this inside its lock on the audio thread: the file is written from a task of the tool's own.
        _host.IdentificationStarted += when =>
        {
            Owe(ref _notesOwed, 1);
            if (!_identifications.Writer.TryWrite(when))
            {
                Owe(ref _notesOwed, -1);
            }
        };

        IdentityResult accepted = _host.SetIdentity(_call);
        if (accepted != IdentityResult.Accepted)
        {
            return $"the modem refused the callsign {_call}";
        }

        _replier = Task.Run(AnswerEchoesAsync);
        _noteWriter = Task.Run(WriteNotesAsync);
        return null;
    }

    /// <summary>Listens until <paramref name="stop"/> is cancelled. Everything is printed as it happens.</summary>
    public async Task ListenAsync(CancellationToken stop)
    {
        _log.Write(_canTransmit ? $"Listening as {_call}, answering pings" : $"Listening as {_call}, receive only");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends one datagram of text to the other station and waits until it has been transmitted or dropped.</summary>
    /// <returns>True when it was transmitted.</returns>
    public async Task<bool> SendTextAsync(byte[] text, CancellationToken stop)
    {
        LinkAddress to = _to ?? throw new InvalidOperationException("no address to send to");
        var tag = new HostTag(1);
        var fate = new TaskCompletionSource<Fate>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fates[tag.Value] = f => Wake(fate, f);
        SendResult result = _host.Send(text, to, _from, LinkProtocol.NoLayer3, tag);
        if (result != SendResult.Accepted)
        {
            _fates.TryRemove(tag.Value, out _);
            _log.Write(Refusal(result, text.Length, to));
            return false;
        }

        try
        {
            Fate outcome;
            try
            {
                outcome = await fate.Task.WaitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                _log.Write("Stopped before it was sent");
                return false;
            }

            BeforeAct?.Invoke();
            _log.Write(outcome.Sent
                ? $"Transmitted {Bytes(text.Length)} to {_names[to.Key]}"
                : $"Dropped {Bytes(text.Length)} to {_names[to.Key]}: {outcome.Reason}");
            return outcome.Sent;
        }
        finally
        {
            if (fate.Task.IsCompletedSuccessfully)
            {
                Carried();
            }
        }
    }

    /// <summary>Sends <paramref name="count"/> echo requests one at a time and prints what came back. Ends early when <paramref name="stop"/> is cancelled.</summary>
    public async Task<PingSummary> PingAsync(int count, int size, CancellationToken stop)
    {
        LinkAddress to = _to ?? throw new InvalidOperationException("no address to ping");
        int sent = 0;
        int answered = 0;
        double? outRate = null;
        double? backRate = null;
        bool unsettled = false;
        try
        {
            for (int number = 1; number <= count && !stop.IsCancellationRequested; number++)
            {
                ushort sequence = (ushort)number;
                byte[] request = Echo.MakeRequest(sequence, size);
                var tag = new HostTag(number);

                // One outcome for the ping, whichever comes first: the echo, the datagram dropped, or the wait running out on the session's clock. Each is completed by Wake, which counts it until this loop has carried on.
                var outcome = new TaskCompletionSource<PingWake>(TaskCreationOptions.RunContinuationsAsynchronously);
                _fates[tag.Value] = fate =>
                {
                    if (!fate.Sent)
                    {
                        Wake(outcome, new PingWake(PingWakeKind.Dropped, fate.Reason, 0));
                    }
                };
                _answers[sequence] = timestamp => Wake(outcome, new PingWake(PingWakeKind.Answered, null, timestamp));
                string label = $"ping {number} of {count}";
                long start = _time.GetTimestamp();
                using ITimer timer = _time.CreateTimer(_ => Wake(outcome, new PingWake(PingWakeKind.TimedOut, null, 0)), null, PingWait, Timeout.InfiniteTimeSpan);
                SendResult result = _host.Send(request, to, _from, Echo.Protocol, tag, reportWanted: true);
                if (result != SendResult.Accepted)
                {
                    _fates.TryRemove(tag.Value, out _);
                    _answers.TryRemove(sequence, out _);
                    _log.Write($"{label}: {Refusal(result, request.Length, to)}");
                    break;
                }

                sent++;

                // The previous wake has been carried on from, up to this wait.
                if (unsettled)
                {
                    unsettled = false;
                    Carried();
                }

                PingWake woken;
                try
                {
                    woken = await outcome.Task.WaitAsync(stop);
                }
                catch (OperationCanceledException)
                {
                    unsettled = outcome.Task.IsCompletedSuccessfully;
                    _fates.TryRemove(tag.Value, out _);
                    _answers.TryRemove(sequence, out _);
                    _log.Write($"{label}: stopped");
                    break;
                }

                unsettled = true;
                BeforeAct?.Invoke();
                _fates.TryRemove(tag.Value, out _);
                _answers.TryRemove(sequence, out _);
                switch (woken.Kind)
                {
                    case PingWakeKind.Answered:
                        TimeSpan trip = _time.GetElapsedTime(start, woken.Timestamp);
                        answered++;
                        LinkStateEvent? link = LinkTo(to);
                        outRate = link?.RateBitsPerSecond ?? outRate;
                        backRate = link?.RecommendedRateBitsPerSecond ?? backRate;
                        _log.Write($"{label}: {Bytes(request.Length)}, {Seconds(trip)} round trip, out {PingSummary.RateOrUnknown(outRate)}, back {PingSummary.RateOrUnknown(backRate)}");
                        break;
                    case PingWakeKind.Dropped:
                        _log.Write($"{label}: dropped, {woken.Reason}");
                        break;
                    default:
                        _log.Write($"{label}: no answer in {PingWait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
                        break;
                }
            }

            var summary = new PingSummary(sent, answered, outRate, backRate);
            _log.Write(summary.ToString());
            return summary;
        }
        finally
        {
            if (unsettled)
            {
                Carried();
            }
        }
    }

    private enum PingWakeKind
    {
        Answered,
        Dropped,
        TimedOut,
    }

    private readonly record struct PingWake(PingWakeKind Kind, string? Reason, long Timestamp);

    /// <summary>Stops answering and waits for the queue of lines to be written.</summary>
    public async ValueTask DisposeAsync()
    {
        _replies.Writer.TryComplete();
        _identifications.Writer.TryComplete();
        await _replier;
        await _noteWriter;
    }

    /// <summary>The time on the clock, hundredths of a second, as a number of seconds: 4.21 s.</summary>
    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static string Bytes(int count) => count == 1 ? "1 byte" : $"{count} bytes";

    private static string DropText(DropReason reason) => reason switch
    {
        DropReason.Cancelled => "cancelled",
        DropReason.Expired => "waited too long",
        _ => "no longer fits one burst at the new rate",
    };

    private string Refusal(SendResult result, int length, LinkAddress to)
    {
        switch (result)
        {
            case SendResult.TooLarge:
                LinkStateEvent? link = LinkTo(to);
                return link is { } known
                    ? $"refused: {Bytes(length)} does not fit one datagram, the room is {known.DatagramRoom}"
                    : $"refused: {Bytes(length)} does not fit one datagram";
            case SendResult.QueueFull:
                return "refused: too much waiting to go";
            default:
                return "refused: no callsign set";
        }
    }

    /// <summary>What the station knows of the link to an address it has been asked to send to, or null.</summary>
    private LinkStateEvent? LinkTo(LinkAddress address)
    {
        foreach (LinkStateEvent state in _host.Snapshot())
        {
            if (state.Address.Key == address.Key)
            {
                return state;
            }
        }

        return null;
    }

    private void Settle(HostTag tag, Fate fate)
    {
        if (_fates.TryRemove(tag.Value, out Action<Fate>? waiting))
        {
            waiting(fate);
        }
    }

    private async Task WriteNotesAsync()
    {
        await foreach (DateTimeOffset when in _identifications.Reader.ReadAllAsync())
        {
            try
            {
                _note?.Write(when);
            }
            finally
            {
                Owe(ref _notesOwed, -1);
            }
        }
    }

    private void OnReceived(ReceivedEvent e)
    {
        ReadOnlySpan<byte> datagram = e.Datagram.Span;
        string who = Name(e.FromKey);
        if (e.Protocol == Echo.Protocol && Echo.TryRead(datagram, out byte kind, out ushort sequence))
        {
            if (kind == Echo.Reply)
            {
                if (_to is { } to && e.FromKey == to.Key && _answers.TryRemove(sequence, out Action<long>? deliver))
                {
                    deliver(_time.GetTimestamp());
                }

                return;
            }

            bool forUs = e.Addressing == Addressing.ToThisStation || (e.Addressing == Addressing.ForAddressNotYetLocated && e.ForKey == _from.Key);
            if (!forUs)
            {
                _log.Write($"Ping {sequence} from {who}: not for this station");
            }
            else if (!_canTransmit)
            {
                _log.Write($"Ping {sequence} from {who}, {Bytes(datagram.Length)}: not answered, receive only");
            }
            else
            {
                _log.Write($"Ping {sequence} from {who}, {Bytes(datagram.Length)}: answering");
                Owe(ref _repliesOwed, 1);
                if (!_replies.Writer.TryWrite((Echo.MakeReply(datagram), e.FromKey, sequence)))
                {
                    Owe(ref _repliesOwed, -1);
                }
            }

            return;
        }

        _log.Write($"Received {Bytes(datagram.Length)} from {who}{PrintableText(datagram)}");
    }

    private string Name(uint key) => _names.TryGetValue(key, out string? name) ? name : $"address {key.ToString("x8", CultureInfo.InvariantCulture)}";

    private static string PrintableText(ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty)
        {
            return "";
        }

        foreach (byte b in datagram)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return "";
            }
        }

        const int Longest = 60;
        string text = System.Text.Encoding.ASCII.GetString(datagram[..Math.Min(datagram.Length, Longest)]);
        return $": \"{text}\"" + (datagram.Length > Longest ? "..." : "");
    }

    private async Task AnswerEchoesAsync()
    {
        await foreach ((byte[] reply, uint toKey, ushort sequence) in _replies.Reader.ReadAllAsync())
        {
            try
            {
                BeforeAct?.Invoke();
                SendResult result = _host.Send(reply, LinkAddress.FromKey(toKey), _from, Echo.Protocol, new HostTag(ReplyTagBase + Interlocked.Increment(ref _replyTag)), reportWanted: false, SendPriority.Expedited);
                if (result != SendResult.Accepted)
                {
                    _log.Write($"Ping {sequence} from {Name(toKey)}: not answered, {Refusal(result, reply.Length, LinkAddress.FromKey(toKey))}");
                }
            }
            finally
            {
                Owe(ref _repliesOwed, -1);
            }
        }
    }
}
