using System.Collections.Concurrent;
using M0LTE.OfdmFm;
using Packet.SoundModem.Channel;
using Packet.SoundModem.CarrierSense;
using OfdmStation = M0LTE.OfdmFm.Station;

namespace Packet.SoundModem.Modems.OfdmFmStation;

/// <summary>Settings of an <see cref="OfdmFmStationHost"/> that only tests need.</summary>
public sealed class OfdmFmStationHostOptions
{
    /// <summary>
    /// True: the station runs every decode job itself, on the receive thread, inside the call that pushes the audio. No worker threads are made.
    /// For tests; on a slow computer it holds the receive thread for a good part of a burst's airtime.
    /// </summary>
    public bool RunJobsInline { get; init; }

    /// <summary>Decode worker threads. Zero or less: <c>Math.Clamp(Environment.ProcessorCount - 1, 2, 3)</c>. Ignored when <see cref="RunJobsInline"/> is set.</summary>
    public int WorkerCount { get; init; }

    /// <summary>
    /// The sub-channel the host's transmissions belong to for the transmit lease: while another sub-channel holds the lease, the host does not transmit. Not a modem setting.
    /// </summary>
    public int SubChannel { get; init; }

    /// <summary>A test's hook, called on a worker thread when a job is waiting and before it runs it, so that a test can hold a job in flight.</summary>
    internal Action? BeforeJob { get; init; }

    /// <summary>A test's hook, called on the receive thread after each time the host gave the station a carrier indication, with what it gave.</summary>
    internal Action<bool?>? AfterExternalBusy { get; init; }
}

/// <summary>
/// The adaptive OFDM-FM station's audio layer, receive side: it gives a <see cref="SoundModemChannel"/>'s audio to a <c>M0LTE.OfdmFm</c> station and re-raises what the station hears.
/// A program builds the channel itself and constructs the host around it; the daemon does not use it yet.
/// <para>
/// <b>The count.</b> The station's only clock is the number of samples it has been given. The host registers a receive tap and the channel's keyed-block event, and hands every block
/// to the station exactly once, in order, on the channel's receive thread: an unkeyed block as <c>Push</c>, a keyed block of n samples as <c>Skip(n)</c> (<see cref="TransmitBook"/> does the arithmetic).
/// </para>
/// <para>
/// <b>Threads.</b> One lock covers every call into the station from the host. The station raises its events inside that lock, on the receive thread, and the host re-raises them from there:
/// a handler must return promptly and must not call back into the host or block on another thread that does. Decode runs on worker threads the host owns (not the thread pool),
/// each woken by the station's <c>WorkAvailable</c> and calling <c>RunOne</c> until it has no more; workers never raise an event.
/// </para>
/// <para>
/// <b>Transmitting.</b> The station has no identity until the host gives it one (<see cref="SetIdentity"/>), and transmits nothing without one. It decides when to transmit and asks for it with a request:
/// the host hands the request, once the station's count has reached its earliest sample, to the channel as a transmission that owns its own timing (the station has already waited its turn; the channel's CSMA must not run),
/// queued under the host as its own keyup identity, and the channel gives back the key-up delay as silence in front of a copy of the station's samples. The key-up delay and tail are the channel's (<c>Csma</c>); persistence and slot time have no effect.
/// The run of keyed blocks IS the keyup: <see cref="TransmitBook"/> reports its first block to the station as the start and the first unkeyed block after it as the end.
/// Everything the transmitter says from its own thread (on air, finished, refused) is queued and applied on the receive thread, under the lock, at the start of the next block.
/// </para>
/// <para>
/// <b>Refusals.</b> A request the channel will not key (receive-only, a transmit lease held by another sub-channel, a PTT or device failure) is reported to the station as refused, and the station offers it again by its own access rules.
/// The host has no retry timer. A request is also refused to the station, without troubling the channel, while the lease excludes this host; a lease that is taken after hand-over is the channel's refusal.
/// A channel that is receive-only says so once, through <see cref="Notice"/>.
/// </para>
/// <para>
/// <b>Carrier.</b> If the channel has a radio-derived carrier source (not the audio guess), the host reads it once per heard block and tells the station when the answer changes, including when the source stops knowing.
/// Nothing else is given to the station as carrier: not other modems' detectors, not this radio's own keying (the source is not read while keyed), not the audio guess.
/// The station's first indication switches it from slow to short slots for as long as it has one.
/// </para>
/// </summary>
public sealed class OfdmFmStationHost : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SoundModemChannel _channel;
    private readonly OfdmStation _station;
    private readonly TransmitBook _book;
    private readonly ReceiveTap _tap;
    private readonly Action<int> _keyed;
    private readonly Action _lost;
    private readonly SemaphoreSlim _work = new(0);
    private readonly Thread[] _workers;
    private readonly Action? _beforeJob;
    private readonly Action<bool?>? _afterExternalBusy;
    private readonly IChannelBusySource? _radio;
    private readonly ConcurrentQueue<Signal> _inbox = new();
    private readonly Dictionary<long, Outstanding> _requests = [];
    private bool? _indicated;
    private bool _noticed;
    private volatile bool _stopping;
    private volatile bool _disposed;
    private long _audioLost;

    /// <summary>Makes the host and starts listening.</summary>
    /// <param name="channel">The channel to listen on. It must run at 48000 Hz.</param>
    /// <param name="random">The station's source of random choices. The caller seeds it.</param>
    /// <param name="options">Test settings; null for the defaults.</param>
    /// <exception cref="ArgumentException">The channel does not run at 48000 Hz.</exception>
    public OfdmFmStationHost(SoundModemChannel channel, Random random, OfdmFmStationHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(random);
        if (channel.SampleRate != OfdmStation.SampleRate)
        {
            throw new ArgumentException($"The adaptive OFDM-FM station needs a {OfdmStation.SampleRate} Hz channel; this one runs at {channel.SampleRate} Hz.");
        }

        options ??= new OfdmFmStationHostOptions();
        _channel = channel;
        _beforeJob = options.BeforeJob;
        _afterExternalBusy = options.AfterExternalBusy;
        _station = new OfdmStation(random, options.RunJobsInline);
        _book = new TransmitBook(new StationInput(_station));

        // Only a source that reads the radio: the audio guess would put the station on short slots on a shaky basis, and it is not given other modems' detectors either.
        _radio = channel.BusySource is FmShapeBusyDetector ? null : channel.BusySource;
        channel.TransmitLease.Attribute(this, options.SubChannel);

        _station.TransmitRequest += OnTransmitRequest;
        _station.WithdrawRequest += OnWithdrawRequest;
        _station.Transmitted += e => { if (!_disposed) { Transmitted?.Invoke(e); } };
        _station.Dropped += e => { if (!_disposed) { Dropped?.Invoke(e); } };
        _station.ModemSent += e => { if (!_disposed) { ModemSent?.Invoke(e); } };

        // Raised inside the lock, on the receive thread. The flag stops the rest of a call that was disposing the host from inside a handler.
        _station.Received += e => { if (!_disposed) { Received?.Invoke(e); } };
        _station.LinkState += e => { if (!_disposed) { LinkState?.Invoke(e); } };
        _station.PathReport += e => { if (!_disposed) { PathReport?.Invoke(e); } };
        _station.Identified += e => { if (!_disposed) { Identified?.Invoke(e); } };
        _station.ChannelBusy += e => { if (!_disposed) { ChannelBusy?.Invoke(e); } };

        int workers = options.RunJobsInline ? 0 : options.WorkerCount > 0 ? options.WorkerCount : Math.Clamp(Environment.ProcessorCount - 1, 2, 3);
        _workers = new Thread[workers];
        if (workers > 0)
        {
            // The handler only signals: it runs on the receive thread, inside the lock.
            _station.WorkAvailable += () => _work.Release();
            for (int i = 0; i < workers; i++)
            {
                _workers[i] = new Thread(RunWorker) { IsBackground = true, Name = $"ofdm-fm-decode-{i + 1}" };
                _workers[i].Start();
            }
        }

        _tap = OnReceiveBlock;
        _keyed = OnKeyedBlock;
        _lost = OnReceiveAudioLost;
        channel.AddReceiveTap(_tap);
        channel.KeyedReceiveBlock += _keyed;
        channel.ReceiveAudioLost += _lost;
    }

    /// <summary>A datagram arrived. Raised on the receive thread inside the host's lock: return promptly and do not call the host. The datagram's memory is valid only during the call.</summary>
    public event Action<ReceivedEvent>? Received;

    /// <summary>The state of the link to a station changed. Same rules as <see cref="Received"/>.</summary>
    public event Action<LinkStateEvent>? LinkState;

    /// <summary>A report came back about the path to a station. Same rules as <see cref="Received"/>.</summary>
    public event Action<PathReportEvent>? PathReport;

    /// <summary>A station was heard identifying itself. Same rules as <see cref="Received"/>.</summary>
    public event Action<IdentifiedEvent>? Identified;

    /// <summary>The channel became busy or clear. Same rules as <see cref="Received"/>.</summary>
    public event Action<ChannelBusyEvent>? ChannelBusy;

    /// <summary>A datagram's burst has left the audio device. Same rules as <see cref="Received"/>.</summary>
    public event Action<TransmittedEvent>? Transmitted;

    /// <summary>A datagram will not go out. Same rules as <see cref="Received"/>.</summary>
    public event Action<DroppedEvent>? Dropped;

    /// <summary>The station sent a burst of its own (not a host's datagram). Same rules as <see cref="Received"/>.</summary>
    public event Action<ModemSentEvent>? ModemSent;

    /// <summary>A line for the operator, raised once: the channel cannot transmit and every request will be refused. Same rules as <see cref="Received"/>.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// Gives the station the callsign it transmits as. Until it is accepted the station only listens. Takes the host's lock; call it from outside the host's events.
    /// </summary>
    public IdentityResult SetIdentity(string callsign)
    {
        lock (_gate)
        {
            return _station.SetIdentity(callsign);
        }
    }

    /// <summary>
    /// Queues a datagram for a link address. The station's own rules decide when it goes. Takes the host's lock; call it from outside the host's events.
    /// </summary>
    public SendResult Send(ReadOnlyMemory<byte> datagram, LinkAddress to, LinkAddress from, byte protocol, HostTag tag, bool reportWanted = false, SendPriority priority = SendPriority.Ordinary)
    {
        lock (_gate)
        {
            return _station.Send(datagram, to, from, protocol, tag, reportWanted, priority);
        }
    }

    /// <summary>The station, for tests that look at what it was asked.</summary>
    internal OfdmStation StationForTests => _station;

    /// <summary>
    /// How many times the input reported lost audio. Each time, the station's clock has slipped by an unknown amount (the report carries no sample count),
    /// so its timers run late by that much. Nothing corrects it.
    /// </summary>
    public long AudioLostCount => Interlocked.Read(ref _audioLost);

    /// <summary>
    /// Bursts dropped from the decode queue since the host was made: decoding is behind the audio and the computer is too slow. Rising means loss.
    /// </summary>
    public long BurstsDropped => _station.BurstsDropped;

    /// <summary>The station's own clock: the samples it has been given so far, heard and skipped together.</summary>
    public long SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _station.SampleCount;
            }
        }
    }

    /// <summary>Stops listening and ends the decode workers, waiting for each to finish the job it is on. Safe to call twice. No event is raised once it has returned.</summary>
    public void Dispose()
    {
        CancellationTokenSource[] handedOver;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
            handedOver = [.. _requests.Values.Select(r => r.Cancellation).OfType<CancellationTokenSource>()];
        }

        // A request still waiting in the channel must not key the radio after its host has gone. One the transmitter has taken goes on regardless.
        foreach (CancellationTokenSource cancellation in handedOver)
        {
            cancellation.Cancel();
        }

        // The channel has no way to remove a tap: the tap stays and answers nothing once disposed.
        _channel.KeyedReceiveBlock -= _keyed;
        _channel.ReceiveAudioLost -= _lost;
        if (_workers.Length > 0)
        {
            _work.Release(_workers.Length);
        }

        foreach (Thread worker in _workers)
        {
            if (worker != Thread.CurrentThread)
            {
                worker.Join();
            }
        }
    }

    private void OnReceiveBlock(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ApplyInbox();
            PollCarrier();
            _book.Heard(samples);
            HandOver();
        }
    }

    private void OnKeyedBlock(int count)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ApplyInbox();
            _book.Keyed(count);
            HandOver();
        }
    }

    // ---- the transmit handshake. Handlers run on the receive thread inside the lock, and must not call the station. ----

    private void OnTransmitRequest(TransmitRequestEvent e)
    {
        if (_disposed)
        {
            return;
        }

        _requests[e.Id] = new Outstanding(e.Samples, e.EarliestSample);
        _book.Requested(e.Id, e.EarliestSample);
    }

    private void OnWithdrawRequest(WithdrawRequestEvent e)
    {
        if (_disposed)
        {
            return;
        }

        _book.Withdrawn(e.Id);
        if (_requests.TryGetValue(e.Id, out Outstanding? request))
        {
            if (request.Cancellation is { } cancellation)
            {
                // Silent if the transmitter has the request already: it goes on air, and the station is told it started.
                cancellation.Cancel();
            }
            else
            {
                _requests.Remove(e.Id);
            }
        }
    }

    /// <summary>Hands the outstanding request to the channel once the count has reached its earliest sample, or refuses it to the station while the transmit lease excludes this host.</summary>
    private void HandOver()
    {
        while (_book.TakeDue(out long id))
        {
            if (!_requests.TryGetValue(id, out Outstanding? request))
            {
                continue;
            }

            if (!_channel.TransmitLease.Admits(this))
            {
                // Refused here, so the station keeps offering by its own rules and no queue builds up in the channel; the lease is nobody's news.
                _requests.Remove(id);
                _station.TransmitRefused(id);
                continue;
            }

            Enqueue(id, request);
        }
    }

    // Apart from HandOver so that the closures are made only when a request really goes: a closure over a variable of the loop's scope is allocated each time the scope is entered, and this runs on every block.
    private void Enqueue(long id, Outstanding request)
    {
        request.Cancellation = new CancellationTokenSource();
        Task sent = _channel.EnqueueTransmit(
            txDelay => request.Render(txDelay, _channel.SampleRate),
            rejected: _ => _inbox.Enqueue(new Signal(SignalKind.Refused, id)),
            ownsChannelTiming: true,
            source: this,
            withdraw: request.Cancellation.Token,
            started: _ => _inbox.Enqueue(new Signal(SignalKind.OnAir, id)));
        _ = sent.ContinueWith(
            finished =>
            {
                _ = finished.Exception;
                _inbox.Enqueue(new Signal(SignalKind.Finished, id));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>What the transmitter's thread left since the last block, in the order it happened.</summary>
    private void ApplyInbox()
    {
        while (_inbox.TryDequeue(out Signal signal))
        {
            switch (signal.Kind)
            {
                case SignalKind.OnAir:
                    _book.OnAir(signal.Id, _requests.TryGetValue(signal.Id, out Outstanding? onAir) ? onAir.Earliest : 0);
                    break;
                case SignalKind.Refused:
                    if (_book.Refused(signal.Id))
                    {
                        if (_channel.ReceiveOnlyReason is { } reason && !_noticed)
                        {
                            _noticed = true;
                            Notice?.Invoke(reason);
                        }

                        _station.TransmitRefused(signal.Id);
                    }

                    break;
                default:
                    _book.Finished(signal.Id);
                    if (_requests.Remove(signal.Id, out Outstanding? done))
                    {
                        done.Cancellation?.Dispose();
                    }

                    break;
            }
        }
    }

    /// <summary>Tells the station what the radio's own carrier indication says, when it changes. Never for anything but a source that reads the radio.</summary>
    private void PollCarrier()
    {
        if (_radio is null)
        {
            return;
        }

        bool? now = _radio.Busy;
        if (now == _indicated)
        {
            return;
        }

        _indicated = now;
        _station.SetExternalBusy(now);
        _afterExternalBusy?.Invoke(now);
    }

    private enum SignalKind
    {
        OnAir,
        Refused,
        Finished,
    }

    private readonly record struct Signal(SignalKind Kind, long Id);

    private sealed class Outstanding(ReadOnlyMemory<float> samples, long earliest)
    {
        public long Earliest { get; } = earliest;

        /// <summary>Set when the request is handed to the channel.</summary>
        public CancellationTokenSource? Cancellation { get; set; }

        /// <summary>The key-up delay as silence, then a copy of the station's samples. Runs on the transmitter's thread; the station keeps the samples until it is told the transmission ended.</summary>
        public float[] Render(int txDelayMilliseconds, int sampleRate)
        {
            int lead = (int)Math.Round(txDelayMilliseconds / 1000.0 * sampleRate);
            var audio = new float[lead + samples.Length];
            samples.Span.CopyTo(audio.AsSpan(lead));
            return audio;
        }
    }

    private void OnReceiveAudioLost() => Interlocked.Increment(ref _audioLost);

    private void RunWorker()
    {
        while (true)
        {
            _work.Wait();
            while (!_stopping && _station.HasWork)
            {
                _beforeJob?.Invoke();
                if (!_station.RunOne())
                {
                    break;
                }
            }

            if (_stopping)
            {
                return;
            }
        }
    }

    private sealed class StationInput(OfdmStation station) : IStationClockInput
    {
        public void Push(ReadOnlySpan<float> samples) => station.Push(samples);

        public void Skip(int count) => station.Skip(count);

        public void TransmitStarted(long id, long sampleIndex) => station.TransmitStarted(id, sampleIndex);

        public void TransmitEnded(long id, long sampleIndex) => station.TransmitEnded(id, sampleIndex);
    }
}
