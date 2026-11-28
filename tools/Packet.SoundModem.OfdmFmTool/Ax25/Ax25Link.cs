using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.OfdmFmTool.Ax25;

/// <summary>
/// Connected-mode AX.25 over the OFDM-FM station, in one process: the frame port, the adapter that gives it to the stack, and packet.net's <see cref="Ax25Listener"/> on top. It does no AX.25 of its own.
/// The commands are <see cref="AnswerAsync"/> (accept connections and run <see cref="AnswerBehaviour"/> on each) and <see cref="CallAsync"/> (connect, then talk or measure a transfer).
/// </summary>
internal sealed class Ax25Link : IAsyncDisposable
{
    private readonly OfdmFmFramePort _port;
    private readonly StampedLog _log;
    private readonly Callsign _call;
    private readonly string _callText;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, RepeatCounter> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _said = new();
    private Ax25Listener? _listener;
    private Action<Ax25Session, DataLinkSignal>? _onSignal;

    /// <param name="host">The station host, already given its identity.</param>
    /// <param name="channel">The channel the host listens on.</param>
    /// <param name="call">This station's callsign, as the stack and the modem both spell it.</param>
    /// <param name="time">The clock the stack's timers and the transfer are timed on.</param>
    /// <param name="log">Where lines go.</param>
    public Ax25Link(OfdmFmStationHost host, SoundModemChannel channel, string call, TimeProvider time, StampedLog log)
    {
        _log = log;
        _time = time;
        _callText = call;
        _call = Callsign.Parse(call);
        _port = OfdmFmFramePort.Attach(channel, 0, host);
        Transport = new OfdmFmAx25Transport(_port, host, channel, 0, time);
    }

    /// <summary>Every line this link has written, as it was written (the log's own queue is written out later).</summary>
    internal IReadOnlyCollection<string> Said => _said;

    private void Say(string line)
    {
        _said.Enqueue(line);
        _log.Write(line);
    }

    /// <summary>The adapter, for a bench that waits for the stack to be at rest.</summary>
    internal OfdmFmAx25Transport Transport { get; }

    /// <summary>Starts the stack.</summary>
    public async Task StartAsync()
    {
        var listener = new Ax25Listener(Transport, LinkSettings.Options(_call, Configure), _time);
        listener.FrameTraced += OnFrameTraced;
        _listener = listener;
        await listener.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Accepts connections until <paramref name="stop"/> is cancelled.</summary>
    public async Task<int> AnswerAsync(CancellationToken stop)
    {
        var behaviours = new ConcurrentDictionary<Ax25Session, AnswerBehaviour>();
        _onSignal = (session, signal) =>
        {
            string peer = session.Context.Remote.ToString();
            switch (signal)
            {
                case DataLinkConnectIndication:
                    _counters[peer] = new RepeatCounter();
                    var behaviour = new AnswerBehaviour(
                        _callText,
                        bytes => session.PostEvent(new DlDataRequest(bytes, Ax25Frame.PidNoLayer3)),
                        () => session.PostEvent(new DlDisconnectRequest()),
                        () => _counters.TryGetValue(peer, out RepeatCounter? counter) ? counter.Counts : (0, 0));
                    behaviours[session] = behaviour;
                    Say($"Connected from {peer}");
                    behaviour.Connected();
                    break;
                case DataLinkDataIndication data:
                    if (behaviours.TryGetValue(session, out AnswerBehaviour? known))
                    {
                        known.Data(data.Info.Span);
                    }

                    break;
                case DataLinkDisconnectIndication or DataLinkDisconnectConfirm:
                    behaviours.TryRemove(session, out _);
                    Say($"Disconnected from {peer}");
                    break;
                case DataLinkErrorIndication error:
                    Say($"Link error with {peer}: {error.Code}");
                    break;
            }
        };

        Say($"Answering as {_callText}");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>
    /// Connects to <paramref name="to"/>. With <paramref name="get"/>, asks for that many bytes, times them, prints the throughput and the frames sent again, and disconnects. Without it, sends each line of
    /// <paramref name="input"/> and prints each line that arrives, until the link goes or <paramref name="stop"/> is cancelled.
    /// </summary>
    /// <returns>0 when the link was made and what was asked for was done, else 1.</returns>
    public async Task<int> CallAsync(string to, int? get, TextReader? input, CancellationToken stop)
    {
        Callsign remote = Callsign.Parse(to);
        // Continuations run where these are completed, inside the stack's own dispatch: what the call does next is then done before the stack gives control back, which a bench can rely on.
        var connected = new TaskCompletionSource();
        var closed = new TaskCompletionSource<string?>();
        var measurement = get is { } wanted ? new GetMeasurement(wanted, _time) : null;
        var lines = new LineAssembler();
        int outcome = 1;
        bool askedStats = false;
        Ax25Session? link = null;

        void Send(string text) => link!.PostEvent(new DlDataRequest(Encoding.ASCII.GetBytes(text), Ax25Frame.PidNoLayer3));
        void Disconnect() => link!.PostEvent(new DlDisconnectRequest());

        _onSignal = (session, signal) =>
        {
            switch (signal)
            {
                case DataLinkConnectConfirm:
                    link = session;
                    _counters[remote.ToString()] = new RepeatCounter();
                    Say($"Connected to {to}");
                    connected.TrySetResult();
                    if (measurement is not null)
                    {
                        measurement.Requested();
                        Send($"SEND {get!.Value.ToString(CultureInfo.InvariantCulture)}\r");
                    }

                    break;
                case DataLinkDataIndication data when measurement is not null:
                    foreach (string line in measurement.Data(data.Info.Span))
                    {
                        if (line.StartsWith("STATS ", StringComparison.Ordinal))
                        {
                            string[] words = line.Split(' ');
                            long sent = words.Length > 1 && long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : 0;
                            long repeated = words.Length > 2 && long.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out long r) ? r : 0;
                            Say(measurement.Result(sent, repeated));
                            outcome = measurement.Damaged == 0 ? 0 : 1;
                            Disconnect();
                        }
                    }

                    if (measurement.GotAllBytes && !askedStats)
                    {
                        askedStats = true;
                        Send("STATS\r");
                    }

                    break;
                case DataLinkDataIndication data:
                    foreach (string line in lines.Add(data.Info.Span))
                    {
                        Say(line);
                    }

                    break;
                case DataLinkDisconnectIndication or DataLinkDisconnectConfirm:
                    Say($"Disconnected from {to}");
                    closed.TrySetResult(null);
                    break;
                case DataLinkErrorIndication error:
                    Say($"Link error with {to}: {error.Code}");
                    break;
            }
        };

        // The stack's own dial polls in real time, so its result is only read for a failure; the link itself is followed through its signals.
        Task dial = _listener!.ConnectAsync(remote, _call, extended: false, preConnectXidNegotiatesSrej: false, stop);
        _ = dial.ContinueWith(
            failed => closed.TrySetResult(failed.Exception!.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Task? typing = null;
        using var typingStop = CancellationTokenSource.CreateLinkedTokenSource(stop);

        async Task TypeAsync()
        {
            while (await input!.ReadLineAsync(typingStop.Token).ConfigureAwait(false) is { } line)
            {
                Send(line + "\r");
            }

            Disconnect();
        }

        try
        {
            await Task.WhenAny(connected.Task, closed.Task).ConfigureAwait(false);
            if (!connected.Task.IsCompleted)
            {
                Say($"Could not connect to {to}: {await closed.Task.ConfigureAwait(false) ?? "refused"}");
                return 1;
            }

            if (measurement is null && input is not null)
            {
                typing = TypeAsync();
            }

            using (stop.Register(() => { if (link is not null) { Disconnect(); } }))
            {
                string? reason = await closed.Task.ConfigureAwait(false);
                if (reason is not null)
                {
                    Say($"Link to {to} lost: {reason}");
                    return 1;
                }
            }

            return measurement is null ? 0 : outcome;
        }
        finally
        {
            // The input may be a terminal that is waiting for a key: it is told to stop and not waited for.
            await typingStop.CancelAsync().ConfigureAwait(false);
            _ = typing?.ContinueWith(ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            try
            {
                await dial.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already said.
            }
        }
    }

    /// <summary>The stack's counts of the I frames it sent to a peer and how many were repeats.</summary>
    internal (long Sent, long Repeated) CountsFor(string peer) => _counters.TryGetValue(peer, out RepeatCounter? counter) ? counter.Counts : (0, 0);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        await Transport.DisposeAsync().ConfigureAwait(false);
        _port.Dispose();
    }

    private void Configure(Ax25Session session) => session.DataLinkSignalEmitted += (_, signal) => _onSignal?.Invoke(session, signal);

    /// <summary>For a test or a detail trace: every frame the stack sends or receives, in a few words.</summary>
    internal Action<string>? FrameTrace { get; set; }

    private void OnFrameTraced(object? sender, Ax25FrameEventArgs e)
    {
        FrameTrace?.Invoke($"{(e.Direction == FrameDirection.Transmitted ? "tx" : "rx")} {e.Frame.FrameType} {e.Frame.Source.Callsign}>{e.Frame.Destination.Callsign} ns={e.Frame.Ns} nr={e.Frame.Nr} pf={e.Frame.PollFinal} cmd={e.Frame.IsCommand} info={e.Frame.Info.Length}");
        if (e.Direction == FrameDirection.Transmitted && e.Frame.FrameType == Ax25FrameType.I)
        {
            string peer = e.Frame.Destination.Callsign.ToString();
            if (_counters.TryGetValue(peer, out RepeatCounter? counter))
            {
                counter.Sent(Convert.ToInt32((object?)e.Frame.Ns ?? 0, CultureInfo.InvariantCulture));
            }
        }
    }
}
