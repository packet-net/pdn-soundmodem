using System.Collections.Concurrent;
using M0LTE.OfdmFm;
using Packet.SoundModem.Channel;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Ident;
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

    /// <summary>The audio frequency of the Morse identification, in Hz: where it sits in the passband, which the hosting program may need to place. Default 1500.</summary>
    public double MorseToneHz { get; init; } = 1500;

    /// <summary>The Morse identification's speed in words a minute. Default 20.</summary>
    public double MorseWordsPerMinute { get; init; } = 20;

    /// <summary>The Morse identification's key-down peak amplitude. Default 0.8, what the existing identification and the modulators use.</summary>
    public double MorseAmplitude { get; init; } = 0.8;

    /// <summary>
    /// When the station last identified, as the host program kept it (see <see cref="IdentificationNote"/>), or null for no record. Told to the station at construction, so a program started again within ten minutes
    /// does not identify again on its first transmission. A time in the future, or more than ten minutes before the host's clock, is ignored: the station identifies.
    /// </summary>
    public DateTimeOffset? LastIdentification { get; init; }

    /// <summary>The clock the host reads the time of an identification from, and the age of <see cref="LastIdentification"/> against. Null: the system's.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>A test's hook, called on a worker thread when a job is waiting and before it runs it, so that a test can hold a job in flight.</summary>
    internal Action? BeforeJob { get; init; }

    /// <summary>A test's hook: wraps what the host gives the station's clock, so that a test can see every push, skip and transmit start and end the station was told.</summary>
    internal Func<IStationClockInput, IStationClockInput>? ClockInputWrapper { get; init; }

    /// <summary>A test's hook, called on the receive thread after each time the host gave the station a carrier indication, with what it gave.</summary>
    internal Action<bool?>? AfterExternalBusy { get; init; }

    /// <summary>A test's hook: where the continuation that posts a finished transmission's signal runs, so that a test can choose when it runs. Null: the thread pool.</summary>
    internal TaskScheduler? SignalScheduler { get; init; }
}

/// <summary>
/// The adaptive OFDM-FM station's host: it gives a <see cref="SoundModemChannel"/>'s audio to a <c>M0LTE.OfdmFm</c> station, keys what the station asks to send, identifies in Morse, and re-raises what the station reports.
/// A program builds the channel itself and constructs the host around it; the daemon does not use it yet.
/// <para>
/// <b>The count.</b> The station's only clock is the number of samples it has been given. The host registers a receive tap and the channel's keyed-block event, and hands every block
/// to the station exactly once, in order, on the channel's receive thread: an unkeyed block as <c>Push</c>, a keyed block of n samples as <c>Skip(n)</c> (<see cref="TransmitBook"/> does the arithmetic).
/// </para>
/// <para>
/// <b>Threads, the rule for callers.</b> One lock covers every call into the station from the host. The operations (<see cref="SetIdentity"/>, <see cref="Send"/>, <see cref="Cancel(HostTag)"/>, <see cref="Cancel(LinkAddress)"/>, <see cref="Snapshot"/>)
/// may be called from any thread except from inside one of the host's event handlers: the lock is held there, on the channel's receive thread, and the station refuses calls from inside its own events
/// (<see cref="InvalidOperationException"/>). A program that wants to act on an event queues the work and returns. Every event is raised on the receive thread inside the lock, so a handler must return promptly
/// and must not block on another thread that calls the host. A tool's command loop or a network handler calls an operation from its own thread and gets the station's result directly. Decode runs on worker threads the host owns (not the thread pool),
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
/// <b>Identification and the Morse.</b> The one rule for when is the station's: before a transmission of its own accord, when it has never identified or ten minutes of its own clock have passed since its last identification burst started
/// (an idle station never identifies), the station asks to transmit its identification first, and the transmission that made it due follows. The host recognises that request (the first one after <c>IdentificationDue</c>) and keys it as
/// one transmission: the key-up delay as silence, the identification burst, a short gap, then the callsign in Morse (tone, speed and level from the options). The station is told the Morse's length beforehand
/// (<c>SetIdentificationTail</c>, whenever the identity is set) so that every station that hears the burst stays off the channel until it is over, and is told the transmission ended when the Morse has gone.
/// The host runs no clock of its own for this and does not use <see cref="StationIdentifier"/>'s rule. <see cref="MorseIdentSent"/> is raised when the Morse has gone, and <see cref="IdentificationStarted"/>, with the time on the host's clock,
/// as the burst goes on air: that is the moment for a program to write down (<see cref="IdentificationNote"/>). The longest such key-up is the key-up delay, the identification burst (at most 1.5 s), the gap and the Morse of a 16 character callsign (12.9 s at 20 words a minute), under 17 s.
/// A refusal by the channel is the station's to answer, as for any request.
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
    private readonly TaskScheduler _signalScheduler;
    private int _signalsOwed;
    private readonly Dictionary<long, Outstanding> _requests = [];
    private Action<TraceEvent>? _trace;
    private bool? _indicated;
    private bool _noticed;
    private volatile bool _stopping;
    private volatile bool _disposed;
    private long _audioLost;

    // The identification's Morse. Touched under the lock. The station's request for its identification is the first one after it raises IdentificationDue.
    private const double MorseGapSeconds = 0.25;
    private readonly double _morseTone;
    private readonly double _morseSpeed;
    private readonly double _morseAmplitude;
    private readonly TimeProvider _time;
    private float[] _morse = [];
    private string _morseText = "";
    private bool _identPending;
    private long _identRequest;

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MorseWordsPerMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MorseAmplitude);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MorseToneHz);
        if (options.MorseToneHz >= channel.SampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MorseToneHz, "The Morse tone must be below half the channel's sample rate.");
        }

        _morseTone = options.MorseToneHz;
        _morseSpeed = options.MorseWordsPerMinute;
        _morseAmplitude = options.MorseAmplitude;
        _channel = channel;
        _beforeJob = options.BeforeJob;
        _afterExternalBusy = options.AfterExternalBusy;
        _signalScheduler = options.SignalScheduler ?? TaskScheduler.Default;
        _time = options.TimeProvider ?? TimeProvider.System;
        _station = new OfdmStation(random, options.RunJobsInline);
        IStationClockInput clock = new StationInput(_station);
        _book = new TransmitBook(options.ClockInputWrapper?.Invoke(clock) ?? clock);

        // Only a source that reads the radio: the audio guess would put the station on short slots on a shaky basis, and it is not given other modems' detectors either.
        _radio = channel.BusySource is FmShapeBusyDetector ? null : channel.BusySource;
        channel.TransmitLease.Attribute(this, options.SubChannel);

        _station.TransmitRequest += OnTransmitRequest;
        _station.WithdrawRequest += OnWithdrawRequest;
        _station.Transmitted += e => { if (!_disposed) { Transmitted?.Invoke(e); } };
        _station.Dropped += e => { if (!_disposed) { Dropped?.Invoke(e); } };
        _station.ModemSent += OnModemSent;

        // Raised inside the lock, on the receive thread. The flag stops the rest of a call that was disposing the host from inside a handler.
        _station.Received += e => { if (!_disposed) { Received?.Invoke(e); } };
        _station.LinkState += e => { if (!_disposed) { LinkState?.Invoke(e); } };
        _station.PathReport += e => { if (!_disposed) { PathReport?.Invoke(e); } };
        _station.Identified += e => { if (!_disposed) { Identified?.Invoke(e); } };
        _station.ChannelBusy += e => { if (!_disposed) { ChannelBusy?.Invoke(e); } };
        _station.BurstNotDecoded += e => { if (!_disposed) { BurstNotDecoded?.Invoke(e); } };
        _station.Congestion += e => { if (!_disposed) { Congestion?.Invoke(e); } };

        // The station's one rule for when. The host only notes that the next request is the identification, whose key-up carries the Morse.
        _station.IdentificationDue += _ => _identPending = true;

        if (options.LastIdentification is { } last)
        {
            TimeSpan age = _time.GetUtcNow() - last;
            if (age >= TimeSpan.Zero && age <= IdentificationInterval)
            {
                _station.SetLastIdentification(age.TotalSeconds);
            }
        }

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

    /// <summary>A burst addressed to this station was heard and not decoded. Same rules as <see cref="Received"/>.</summary>
    public event Action<BurstNotDecodedEvent>? BurstNotDecoded;

    /// <summary>The queue to a station became full or has drained enough to take datagrams again. Same rules as <see cref="Received"/>.</summary>
    public event Action<CongestionEvent>? Congestion;

    /// <summary>The identification's Morse has gone (the text is the callsign). Raised on the receive thread at the next block, inside the host's lock; same rules as <see cref="Received"/>.</summary>
    public event Action<string>? MorseIdentSent;

    /// <summary>
    /// An identification burst has gone on air, at this time on the host's clock (<see cref="OfdmFmStationHostOptions.TimeProvider"/>). Raised on the receive thread inside the host's lock; same rules as <see cref="Received"/>: queue
    /// anything slow, such as writing the time to disk.
    /// </summary>
    public event Action<DateTimeOffset>? IdentificationStarted;

    /// <summary>A line for the operator, raised once: the channel cannot transmit and every request will be refused. Same rules as <see cref="Received"/>.</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// One line of the station's readable trace (docs/trace.md in the M0LTE.OfdmFm package): what it heard, decided and sent, and why.
    /// Raised inside the host's lock like every other event, so a handler must return promptly and must not call the host: queue the line and print it from another thread.
    /// It comes on the receive thread for what the station notices with the audio, and on the calling thread, inside the same lock, for a line about a host call (<see cref="Send"/>, <see cref="Cancel(HostTag)"/>, <see cref="SetIdentity"/>).
    /// Decode workers never raise it. Subscribe before the first <see cref="Send"/>: an address sent from earlier is shown by its key. The station builds no text while nobody is subscribed.
    /// </summary>
    public event Action<TraceEvent>? Trace
    {
        add
        {
            lock (_gate)
            {
                if (_trace is null)
                {
                    _station.Trace += OnTrace;
                }

                _trace += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _trace -= value;
                if (_trace is null)
                {
                    _station.Trace -= OnTrace;
                }
            }
        }
    }

    /// <summary>The level of trace lines <see cref="Trace"/> is given: <see cref="TraceLevel.Flow"/> until set. Takes the host's lock; call it from outside the host's events.</summary>
    public TraceLevel TraceLevel
    {
        get
        {
            lock (_gate)
            {
                return _station.TraceLevel;
            }
        }

        set
        {
            lock (_gate)
            {
                _station.TraceLevel = value;
            }
        }
    }

    private void OnTrace(TraceEvent line)
    {
        if (!_disposed)
        {
            _trace?.Invoke(line);
        }
    }

    /// <summary>
    /// Gives the station the callsign it transmits as. Until it is accepted the station only listens. Takes the host's lock; call it from outside the host's events.
    /// </summary>
    public IdentityResult SetIdentity(string callsign)
    {
        lock (_gate)
        {
            IdentityResult result = _station.SetIdentity(callsign);
            if (result == IdentityResult.Accepted && _station.Callsign is { } own && _morseText.Length == 0)
            {
                _morseText = MorseGenerator.IdText(own, null);
                float[] tone = MorseGenerator.Real(_morseText, _morseTone, _morseAmplitude, _morseSpeed, _channel.SampleRate);
                double tail = MorseGapSeconds + (tone.Length / (double)_channel.SampleRate);
                if (tail > OfdmStation.MaxIdentificationTail)
                {
                    throw new InvalidOperationException($"The Morse of {_morseText} at {_morseSpeed} words a minute lasts {tail:0.0} s, more than the {OfdmStation.MaxIdentificationTail} s a station can announce: use a faster speed.");
                }

                _morse = tone;
                _station.SetIdentificationTail(tail);
            }

            return result;
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

    /// <summary>
    /// Drops every queued datagram with this tag (each raises <see cref="Dropped"/>) and returns how many. Takes the host's lock; call it from outside the host's events.
    /// </summary>
    public int Cancel(HostTag tag)
    {
        lock (_gate)
        {
            return _station.Cancel(tag);
        }
    }

    /// <summary>
    /// Drops every datagram queued for a link address and returns how many. Takes the host's lock; call it from outside the host's events.
    /// </summary>
    public int Cancel(LinkAddress address)
    {
        lock (_gate)
        {
            return _station.Cancel(address);
        }
    }

    /// <summary>What the station knows of every link address it has sent to. Takes the host's lock; call it from outside the host's events.</summary>
    public IReadOnlyList<LinkStateEvent> Snapshot()
    {
        lock (_gate)
        {
            return _station.Snapshot();
        }
    }

    /// <summary>
    /// True while a transmission handed to the channel has not yet posted the signal that says it is over. Its task completes on the transmitter's thread and the signal is posted from a continuation
    /// the thread pool runs, so once the channel is idle this is the one thing still to happen. For a test that feeds the channel by hand: the next block must not go in before the signal has.
    /// </summary>
    internal bool SignalsOwed => Volatile.Read(ref _signalsOwed) > 0;

    /// <summary>Raised, on whatever thread posted it, after a transmission's end signal has been posted. For the same tests as <see cref="SignalsOwed"/>.</summary>
    internal event Action? SignalPosted;

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

    /// <summary>The station's identification interval, which the host's note of the last identification is read against.</summary>
    internal static readonly TimeSpan IdentificationInterval = TimeSpan.FromMinutes(10);

    private void OnModemSent(ModemSentEvent e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Kind == ModemSentKind.Identification)
        {
            _identPending = false;
            IdentificationStarted?.Invoke(_time.GetUtcNow());
        }

        ModemSent?.Invoke(e);
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

        var request = new Outstanding(e.Samples, e.EarliestSample);
        if (_identPending)
        {
            // The identification: its key-up carries the Morse after the burst.
            _identPending = false;
            _identRequest = e.Id;
            request.Morse = _morse;
            request.MorseText = _morseText;
        }

        _requests[e.Id] = request;
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
            if (request.Morse.Length > 0 && e.Id == _identRequest)
            {
                // The station offers its identification again: the next request is it.
                _identPending = true;
            }

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
                _identPending |= request.Morse.Length > 0;
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
        Interlocked.Increment(ref _signalsOwed);
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
                Post(new Signal(SignalKind.Finished, id));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            _signalScheduler);
    }

    /// <summary>Posts the signal that a transmission handed to the channel is over: its last word, so it is no longer owed.</summary>
    private void Post(Signal signal)
    {
        _inbox.Enqueue(signal);
        Interlocked.Decrement(ref _signalsOwed);
        SignalPosted?.Invoke();
    }

    /// <summary>What the transmitter's thread left since the last block, in the order it happened.</summary>
    private void ApplyInbox()
    {
        while (_inbox.TryDequeue(out Signal signal))
        {
            switch (signal.Kind)
            {
                case SignalKind.OnAir:
                    if (_requests.TryGetValue(signal.Id, out Outstanding? onAir))
                    {
                        onAir.OnAir = true;
                    }

                    _book.OnAir(signal.Id, onAir?.Earliest ?? 0);
                    break;
                case SignalKind.Refused:
                    if (_book.Refused(signal.Id))
                    {
                        // The station offers its identification again, and the next request is it.
                        _identPending |= _requests.TryGetValue(signal.Id, out Outstanding? refused) && refused.Morse.Length > 0;
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
                        if (done.OnAir && done.Morse.Length > 0)
                        {
                            MorseIdentSent?.Invoke(done.MorseText);
                        }
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

        /// <summary>The Morse that follows the burst, for the station's identification; empty for any other request.</summary>
        public float[] Morse { get; set; } = [];

        public string MorseText { get; set; } = "";

        /// <summary>True once the transmitter has started it.</summary>
        public bool OnAir { get; set; }

        /// <summary>The key-up delay as silence, then a copy of the station's samples, then for an identification the gap and the Morse. Runs on the transmitter's thread; the station keeps the samples until it is told the transmission ended.</summary>
        public float[] Render(int txDelayMilliseconds, int sampleRate)
        {
            int lead = (int)Math.Round(txDelayMilliseconds / 1000.0 * sampleRate);
            int gap = Morse.Length > 0 ? (int)Math.Round(MorseGapSeconds * sampleRate) : 0;
            var audio = new float[lead + samples.Length + gap + Morse.Length];
            samples.Span.CopyTo(audio.AsSpan(lead));
            Morse.CopyTo(audio, lead + samples.Length + gap);
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
