using M0LTE.OfdmFm;
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

    /// <summary>A test's hook, called on a worker thread when a job is waiting and before it runs it, so that a test can hold a job in flight.</summary>
    internal Action? BeforeJob { get; init; }
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
/// <b>Listening only.</b> The station has no identity, so it has no number and transmits nothing; the host does not subscribe to <c>TransmitRequest</c>. The transmit half comes next.
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
        _station = new OfdmStation(random, options.RunJobsInline);
        _book = new TransmitBook(new StationInput(_station));

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

    /// <summary>
    /// How many times the input reported lost audio. Each time, the station's clock has slipped by an unknown amount (the report carries no sample count),
    /// so its timers run late by that much. Nothing corrects it.
    /// </summary>
    public long AudioLostCount => Interlocked.Read(ref _audioLost);

    /// <summary>
    /// Bursts dropped from the decode queue since the host was made: decoding is behind the audio and the computer is too slow. Rising means loss.
    /// </summary>
    public long BurstsDropped => _station.BurstsDropped;

    /// <summary>The samples given to the station so far, heard and skipped together.</summary>
    public long SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _book.SampleCount;
            }
        }
    }

    /// <summary>Stops listening and ends the decode workers, waiting for each to finish the job it is on. Safe to call twice. No event is raised once it has returned.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
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
            if (!_disposed)
            {
                _book.Heard(samples);
            }
        }
    }

    private void OnKeyedBlock(int count)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _book.Keyed(count);
            }
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
    }
}
