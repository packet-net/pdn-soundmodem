using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Flavour B: one process, one port, many UberSDR receivers, and a page that lists them.
/// </summary>
/// <remarks>
/// <para>The front door is a <see cref="WaterfallRouter"/> on the waterfall's port. It serves the
/// picker at <c>/</c>, the picker's own snapshot at <c>/api/instances</c>, and each receiver's
/// page under <c>/r/&lt;slug&gt;/</c>. Nothing else is reachable: no KISS, no configuration API,
/// no survey, no transmitter. A monitor is a display.</para>
/// <para><b>A station is built when its receiver is first asked for</b>, and then kept for the
/// life of the process, because the links panel and the frame log surviving a visitor leaving and
/// coming back is the whole reason a quiet band looks alive. Building it costs a few MB and no
/// network: the receiver is not touched until a browser actually attaches, which is what keeps
/// the promise that a receiver nobody has picked costs its operator nothing.</para>
/// <para><b>One session per receiver, however many viewers.</b> The fan-out is here: every
/// browser watching one receiver attaches to that receiver's one
/// <see cref="WaterfallWebServer"/>, whose viewer count is what opens and closes the single
/// <see cref="OnDemandUberSdrInput"/> session. This is the most important promise the design
/// makes to the people whose antennas these are.</para>
/// </remarks>
internal sealed class MonitorHost : IAsyncDisposable
{
    /// <summary>How long a station that faulted waits before it is built again.</summary>
    /// <remarks>
    /// A dead feed or a starved stream is the receiver's end of things being broken, and the
    /// answer flavour A takes - restart and reconnect afresh - is right here too, applied to the
    /// one station rather than to the process. The wait is so that a receiver which is broken
    /// rather than glitching is not rebuilt against every second, and it is only ever spent while
    /// somebody is watching: a faulted station nobody is looking at simply waits.
    /// </remarks>
    internal static readonly TimeSpan RebuildAfter = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MonitorHostOptions _options;
    private readonly StationJournal _journal;
    private readonly TimeProvider _time;
    private readonly UberSdrDirectory _directory;
    private readonly WaterfallRouter _router;
    private readonly CancellationTokenSource _stopping;

    private readonly Lock _stationsLock = new();
    private readonly Dictionary<string, MonitorStation> _stations = new(StringComparer.Ordinal);

    internal MonitorHost(MonitorHostOptions options, CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _journal = options.Journal("");
        _time = options.TimeProvider;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _directory = new UberSdrDirectory(
            options.Directory, _journal, options.TimeProvider, options.FetchDirectory);
        _router = new WaterfallRouter(options.Port, options.Bind) { FrontDoor = FrontDoorAsync };
    }

    /// <summary>The port the whole site answers on.</summary>
    internal int Port => _router.Port;

    /// <summary>The list of receivers as it stands, for a caller that wants to look.</summary>
    internal DirectorySnapshot Directory => _directory.Snapshot;

    /// <summary>Fetches the directory again now, rather than waiting for the next refresh.</summary>
    internal Task RefreshDirectoryAsync() => _directory.RefreshAsync(_stopping.Token);

    /// <summary>Fetches the directory, opens the port and answers requests until cancelled.</summary>
    /// <returns>The process exit code: 0 for a clean stop.</returns>
    internal async Task<int> RunAsync()
    {
        int started = await StartAsync().ConfigureAwait(false);
        if (started != 0)
        {
            return started;
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>
    /// Fetches the directory and opens the port, and returns as soon as the site is answering.
    /// </summary>
    /// <returns>0 when the site is up, or the exit code to leave with.</returns>
    internal async Task<int> StartAsync()
    {
        await _directory.StartAsync(_stopping.Token).ConfigureAwait(false);

        try
        {
            _router.Start();
        }
        catch (Exception e) when (e is HttpListenerException or SocketException)
        {
            _journal.WriteError(
                $"cannot serve the monitor on {_options.Bind}:{_options.Port}\n"
                + $"  {e.Message}\n"
                + "  Set by \"waterfall\".\"port\" and the top-level \"bind\". Another process may\n"
                + "  already hold the port; \"*\" or \"0.0.0.0\" serves every interface.");
            return 2;
        }

        _journal.Write($"monitor: {_router.Url}");
        _journal.Write(
            "monitor: receive only - no KISS, no transmitter, no configuration API on this port");
        return 0;
    }

    /// <summary>
    /// Everything no receiver's prefix claims: the picker, its snapshot, and the first request
    /// for a receiver nobody has picked yet.
    /// </summary>
    private async Task<bool> FrontDoorAsync(HttpListenerContext context)
    {
        string? path = context.Request.Url?.AbsolutePath;
        if (path is null)
        {
            return false;
        }

        bool readOnlyMethod = context.Request.HttpMethod is "GET" or "HEAD";

        if (path.StartsWith("/r/", StringComparison.Ordinal))
        {
            return await ServeUnpickedAsync(context, path, readOnlyMethod).ConfigureAwait(false);
        }

        if (readOnlyMethod && path is "/" or "/index.html")
        {
            await WriteAsync(context, "text/html; charset=utf-8", MonitorPage.Bytes)
                .ConfigureAwait(false);
            return true;
        }

        if (readOnlyMethod && path == "/api/instances")
        {
            await WriteAsync(context, "application/json; charset=utf-8", InstancesJson())
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first request for a receiver: builds its station if it is one this monitor offers, and
    /// answers this request from it. Anything else under <c>/r/</c> is a 404.
    /// </summary>
    private async Task<bool> ServeUnpickedAsync(
        HttpListenerContext context, string path, bool readOnlyMethod)
    {
        int end = path.IndexOf('/', 3);
        string slug = end < 0 ? path[3..] : path[3..end];
        if (slug.Length == 0)
        {
            return false;
        }

        if (Offered(slug) is not { } receiver)
        {
            return false;   // a slug this monitor does not serve: the router's 404
        }

        // The prefix without its trailing slash, before there is a route for the router's own
        // redirect to find. The page works out what its socket is relative to from its own path,
        // so /r/m9psy-1 would leave it reaching for /r/ws and connecting to nothing.
        if (end < 0)
        {
            if (!readOnlyMethod)
            {
                return false;
            }

            context.Response.Redirect(path + "/" + context.Request.Url?.Query);
            context.Response.Close();
            return true;
        }

        MonitorStation station;
        try
        {
            station = Ensure(slug, receiver);
        }
        catch (Exception e)
        {
            // A station that will not build is this monitor's bug, not the visitor's, and the one
            // thing that must not happen is for it to disappear into an aborted response with
            // nothing anywhere saying why. Say it, and answer with the 404 the router would give
            // for a receiver it does not serve.
            _journal.WriteError(
                $"{slug}: could not build a station - {UberSdrDirectory.Ascii(e.ToString())}");
            return false;
        }

        return await station.Server.TryServeAsync(context, $"/r/{slug}/").ConfigureAwait(false);
    }

    /// <summary>The listed receiver this slug names, if this monitor offers it.</summary>
    private DirectoryReceiver? Offered(string slug)
    {
        foreach (DirectoryReceiver receiver in _directory.Snapshot.Receivers)
        {
            if (receiver.Slug == slug && receiver.Offered)
            {
                return receiver;
            }
        }

        return null;
    }

    /// <summary>
    /// This receiver's station, built and registered if it did not exist. Under a lock because
    /// two browsers arriving together must not build two of it.
    /// </summary>
    private MonitorStation Ensure(string slug, DirectoryReceiver receiver)
    {
        lock (_stationsLock)
        {
            if (_stations.TryGetValue(slug, out MonitorStation? existing))
            {
                return existing;
            }

            var station = new MonitorStation(this, receiver);
            _stations[slug] = station;
            _directory.Bind(slug, receiver.Host);
            return station;
        }
    }

    private MonitorStation[] Stations()
    {
        lock (_stationsLock)
        {
            return [.. _stations.Values];
        }
    }

    /// <summary>
    /// The picker's whole world in one document: every receiver the directory lists, decorated
    /// with what this monitor knows about it, plus how old the list is.
    /// </summary>
    /// <remarks>
    /// A station whose receiver has since left the directory is listed too, marked as gone. It is
    /// still being watched by whoever is watching it, and a picker whose "being watched" total did
    /// not add up would be a picker nobody could trust.
    /// </remarks>
    private byte[] InstancesJson()
    {
        DirectorySnapshot snapshot = _directory.Snapshot;
        Dictionary<string, MonitorStation> stations = Stations().ToDictionary(
            s => s.Slug, StringComparer.Ordinal);

        var rows = new List<object>(snapshot.Receivers.Count);
        foreach (DirectoryReceiver receiver in snapshot.Receivers)
        {
            stations.Remove(receiver.Slug, out MonitorStation? station);
            rows.Add(Row(receiver, station, listed: true));
        }

        foreach (MonitorStation orphan in stations.Values)
        {
            rows.Add(Row(orphan.Receiver, orphan, listed: false));
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                page = MonitorPage.Version,
                title = _options.Title,
                about = _options.About,
                listFrom = snapshot.ListFrom,
                staleSince = snapshot.Stale ? snapshot.ListFrom : null,
                problem = snapshot.Problem,
                receivers = rows,
            },
            Json);
    }

    private static object Row(DirectoryReceiver receiver, MonitorStation? station, bool listed)
    {
        (string state, string? status) = station?.State() ?? ("unpicked", null);
        return new
        {
            slug = receiver.Slug,
            host = receiver.Host,
            receiver.Callsign,
            receiver.Name,
            receiver.Location,
            publicUrl = receiver.PublicUrl,
            snrDb = receiver.SnrDb,
            loadStatus = receiver.LoadStatus,
            availableClients = receiver.AvailableClients,
            maxClients = receiver.MaxClients,
            offered = listed && receiver.Offered,
            why = !listed ? "no longer listed by the directory" : receiver.Why,
            state,
            status,
            viewers = station?.Server.Viewers ?? 0,
            description = station?.Description,
        };
    }

    private static async Task WriteAsync(
        HttpListenerContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        // The same reasoning as the waterfall page: nothing here carries a version in its URL,
        // and a browser left to cache heuristically keeps a picker that no longer matches the
        // daemon serving it.
        context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        context.Response.ContentLength64 = body.Length;
        if (context.Request.HttpMethod != "HEAD")
        {
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        }

        context.Response.Close();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _router.DisposeAsync().ConfigureAwait(false);
        foreach (MonitorStation station in Stations())
        {
            await station.DisposeAsync().ConfigureAwait(false);
        }

        _directory.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// One receiver's station: its channel and modems, its frame log, its page, and - once a
    /// browser is actually watching - its session on the receiver and the receive loop that turns
    /// between them.
    /// </summary>
    /// <remarks>
    /// Built in two stages on purpose. Everything that needs no network is built when the first
    /// request for the page arrives, so the page loads at once and its status chip can say what is
    /// happening; the receiver itself is not touched until a browser attaches, and a pre-flight
    /// that fails leaves a page that is up and saying so rather than a request that hangs for
    /// fifteen seconds and then fails.
    /// </remarks>
    private sealed class MonitorStation : IAsyncDisposable
    {
        private readonly MonitorHost _host;
        private readonly StationJournal _journal;
        private readonly SoundModemChannel _channel;
        private readonly FrameLog? _frameLog;
        private readonly Lock _gate = new();

        private OnDemandUberSdrInput? _input;
        private Station? _station;
        private Task? _running;
        private ITimer? _rebuild;
        private bool _attaching;
        private bool _disposed;
        private string? _fault;
        private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

        internal MonitorStation(MonitorHost host, DirectoryReceiver receiver)
        {
            _host = host;
            Receiver = receiver;
            Slug = receiver.Slug;
            _journal = host._options.Journal(Slug);
            _channel = new SoundModemChannel(host._options.DspRate)
            {
                // Said once, here, so anything that could put something on the air gets the same
                // answer for the same reason. Nothing in this flavour can, but the channel is the
                // same class the transmitting one uses and this is where it is told.
                ReceiveOnlyReason =
                    $"this station receives only: its audio comes from the UberSDR instance at "
                    + $"{receiver.Endpoint}, which is a receiver and has no transmitter.",
            };

            // The modems, from the one loop both flavours use.
            if (!StationFactory.TryAddModems(
                    _channel, host._options.Modems, host._options.DspRate,
                    host._options.PskDetector, _journal))
            {
                // Unreachable: the same modem list was built once at start-up before any station
                // existed, so a modem that would not build has already stopped the daemon.
                throw new InvalidOperationException(
                    "the monitor's modems would not build, which start-up should have caught");
            }

            StationFactory.JournalReceivedFrames(_channel, _journal);

            if (host._options.FrameLogDirectory is { Length: > 0 } directory
                && StationFactory.TryOpenFrameLog(
                    Path.Combine(directory, $"frames-{Slug}.db"),
                    host._options.Modems, _channel, _journal, out FrameLog? log))
            {
                _frameLog = log;
            }

            Server = WaterfallWebServer.Routed(_channel, new WaterfallOptions
            {
                DialFrequencyHz = host._options.DialHz,
                Sideband = host._options.Sideband,
                LinesPerSecond = host._options.LinesPerSecond,
                FftSize = host._options.FftSize,
                Public = true,
                Title = host._options.Title,
                About = host._options.About,
                PickerUrl = "../",
                DeclaredBands = StationFactory.DeclaredBandsFor(host._options.Modems),
                FrameHistory = _frameLog is null ? null : _frameLog.Recent,
                TimeProvider = host._time,
            });

            // Whose receiver it is, from the directory, before any session has ever been opened:
            // a visitor should see the callsign and where it is while the page is still idle.
            Server.SetReceiver(receiver.Description, receiver.PublicUrl ?? receiver.Endpoint.PublicUrl);
            Server.SetRadioStatus($"{receiver.Description} - waiting for a viewer");

            if (host._options.IdBeacons)
            {
                StationFactory.WireIdBeacons(
                    _channel, host._options.Modems, host._options.DspRate, Server, _frameLog,
                    onDecode: null, _journal);
            }

            if (_frameLog is not null)
            {
                StationFactory.BackfillLinks(Server, _frameLog);
            }

            Server.ViewersChanged += OnViewersChanged;
            Server.Start();
            _host._router.Add($"/r/{Slug}/", Server);
            _journal.Write($"station: {Server.Url} for {receiver.Endpoint}");
        }

        internal string Slug { get; }

        /// <summary>What the directory last said about this receiver. Kept even after it leaves
        /// the directory, because the page it serves has to go on saying whose receiver it is.</summary>
        internal DirectoryReceiver Receiver { get; }

        internal WaterfallWebServer Server { get; }

        /// <summary>How the receiver describes itself, once a session has ever been opened.</summary>
        internal string? Description
        {
            get
            {
                lock (_gate)
                {
                    return _input?.ReceiverDescription;
                }
            }
        }

        /// <summary>This station's state and its own sentence, for the picker.</summary>
        internal (string State, string? Status) State()
        {
            lock (_gate)
            {
                if (_fault is { } fault)
                {
                    return ("faulted", fault);
                }

                if (_input is not { } input)
                {
                    return ("idle", null);
                }

                if (input.Refused)
                {
                    return ("refused", input.Status);
                }

                return (input.Phase switch
                {
                    OnDemandPhase.Idle => "idle",
                    OnDemandPhase.Connecting => "connecting",
                    OnDemandPhase.Live => "live",
                    OnDemandPhase.Lingering => "lingering",
                    // Not in the plan's list of states, and real: an open that failed for a
                    // transport reason is retried on a ladder while somebody waits, which is
                    // neither connecting nor faulted and has its own sentence.
                    _ => "retrying",
                }, input.Status);
            }
        }

        /// <summary>
        /// The page's viewer count, which is the whole mechanism: it opens the receiver when it
        /// leaves zero and closes it a little after it returns there, and it is the same count
        /// however many browsers are behind it.
        /// </summary>
        private void OnViewersChanged(int viewers)
        {
            OnDemandUberSdrInput? input;
            lock (_gate)
            {
                input = _input;
            }

            input?.SetViewers(viewers);

            if (viewers > 0 && input is null)
            {
                _ = AttachAsync();
            }
        }

        /// <summary>
        /// Opens the session machinery for this receiver and starts its receive loop. Runs once
        /// somebody is actually watching, and again after a fault once the rebuild wait is up.
        /// </summary>
        private async Task AttachAsync()
        {
            lock (_gate)
            {
                if (_disposed || _attaching || _input is not null
                    || _host._time.GetUtcNow() < _notBefore)
                {
                    return;
                }

                _attaching = true;
            }

            OnDemandUberSdrInput input;
            try
            {
                input = await _host._options
                    .OpenInput(Receiver, _journal.ErrorSink, _host._stopping.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                        or System.Net.Http.HttpRequestException or IOException
                                        or OperationCanceledException)
            {
                lock (_gate)
                {
                    _attaching = false;
                }

                if (!_host._stopping.IsCancellationRequested)
                {
                    Fault(
                        $"cannot reach {Receiver.Endpoint}: {UberSdrDirectory.Ascii(e.Message)}. "
                        + $"Trying again in {RebuildAfter.TotalSeconds:F0} s while anybody is watching.");
                }

                return;
            }

            Station station;
            lock (_gate)
            {
                if (_disposed)
                {
                    input.Dispose();
                    return;
                }

                _input = input;
                _fault = null;
                _attaching = false;

                input.PhaseChanged += (_, sentence) => Server.SetRadioStatus(Chip(sentence));
                if (input.ReceiverDescription is { Length: > 0 } described)
                {
                    Server.SetReceiver(described, Receiver.PublicUrl ?? Receiver.Endpoint.PublicUrl);
                }

                Server.SetRadioStatus(Chip(input.Status));

                station = new Station(
                    new StationOptions
                    {
                        Channel = _channel,
                        Input = input,
                        DspRate = _host._options.DspRate,
                        Journal = _journal,
                        DeviceKind = DeadFeedDevice.UberSdr,
                        DeadFeed = _host._options.DeadFeed,
                        SessionLive = () => input.SessionLive,
                        TimeProvider = _host._time,
                        HealthChecks = [FrameLogDropCheck()],
                    },
                    _host._stopping.Token);
                _station = station;

                // Under the lock, and after the input is in place, so a browser arriving or
                // leaving in the meantime is not lost: the handler and this see one order.
                input.SetViewers(Server.Viewers);
            }

            if (input.Connection.RefusedForNow)
            {
                _journal.WriteError(
                    "ubersdr: the receiver is refusing this address for now "
                    + $"({UberSdrDirectory.Ascii(input.Connection.Reason ?? "daily listening allowance exhausted")}). "
                    + "The page is up and will start hearing audio when the receiver lets us back in.");
            }

            station.Faulted += OnFault;

            // Its own thread: every input's Read is synchronous and blocking, so a host running
            // fifty of these cannot share a pool with them.
            _running = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        station.Run();
                    }
                    finally
                    {
                        Stopped();
                    }
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>The status chip, which always names the receiver: the input's own sentence
        /// says what the session is doing and not whose antenna it is.</summary>
        private string Chip(string sentence) => $"{Receiver.Description} - {sentence}";

        /// <summary>
        /// A full disk left a station keeping an empty frame log for weeks with nothing anywhere
        /// saying so. Polled from the receive loop, which only turns when audio is flowing, which
        /// is exactly when this counter means something.
        /// </summary>
        private Func<string?> FrameLogDropCheck()
        {
            long seen = 0;
            return () =>
            {
                if (_frameLog is null)
                {
                    return null;
                }

                long drops = _frameLog.Dropped;
                if (drops <= seen)
                {
                    return null;
                }

                string line =
                    $"frame log: {drops - seen} frames dropped unwritten ({drops} total) - the "
                    + "disk cannot keep up, is full, or is unwritable";
                seen = drops;
                return line;
            };
        }

        /// <summary>
        /// This station is down. One wedged receiver must not take the other forty-nine with it,
        /// so the sentence is journalled, shown on the page and in the picker, and the station is
        /// built again after a wait - which is flavour A's "restart and reconnect afresh" applied
        /// to one station instead of to the process.
        /// </summary>
        private void OnFault(StationFault fault)
        {
            if (fault.Stalled)
            {
                // The receive loop is wedged inside a blocked Read and no cancellation will ever
                // reach it, so this station can never be rebuilt and this process can never shut
                // down tidily. The one case that still ends the process, exactly as flavour A
                // does; systemd restarts and every receiver comes back.
                _journal.WriteError(fault.Reason);
                Environment.Exit(1);
            }

            Fault(fault.Reason);
        }

        private void Fault(string reason)
        {
            lock (_gate)
            {
                _fault = reason;
                _notBefore = _host._time.GetUtcNow() + RebuildAfter;
            }

            _journal.WriteError(reason);
            Server.SetRadioStatus(Chip(reason));
            ArmRebuild();
        }

        /// <summary>The receive loop has returned: tidy up what it was running on, and arm the
        /// rebuild if this station is meant to carry on.</summary>
        private void Stopped()
        {
            OnDemandUberSdrInput? input;
            Station? station;
            lock (_gate)
            {
                input = _input;
                station = _station;
                _input = null;
                _station = null;
            }

            // Off the loop's own thread would be tidier, but this IS off it: Run has returned by
            // the time this is called, so nothing here can pull the rug from under it.
            station?.Dispose();
            input?.Dispose();

            if (!_host._stopping.IsCancellationRequested && !_disposed)
            {
                ArmRebuild();
            }
        }

        /// <summary>Try again when the wait is up, if anybody is still watching by then.</summary>
        private void ArmRebuild()
        {
            lock (_gate)
            {
                if (_disposed || _rebuild is not null)
                {
                    return;
                }

                _rebuild = _host._time.CreateTimer(
                    _ =>
                    {
                        lock (_gate)
                        {
                            _rebuild?.Dispose();
                            _rebuild = null;
                        }

                        if (Server.Viewers > 0)
                        {
                            _ = AttachAsync();
                        }

                        // Nobody watching: nothing to do. The next viewer to arrive raises
                        // ViewersChanged, which attaches, which is the polite way round.
                    },
                    null, RebuildAfter, Timeout.InfiniteTimeSpan);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            OnDemandUberSdrInput? input;
            Station? station;
            Task? running;
            lock (_gate)
            {
                _disposed = true;
                _rebuild?.Dispose();
                _rebuild = null;
                input = _input;
                station = _station;
                running = _running;
                _input = null;
                _station = null;
            }

            Server.ViewersChanged -= OnViewersChanged;

            // The input first: its Read is what the loop is sitting in, and disposing it is what
            // lets the loop notice it has been asked to stop.
            input?.Dispose();
            if (running is not null)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            station?.Dispose();
            await Server.DisposeAsync().ConfigureAwait(false);
            if (_frameLog is not null)
            {
                await _frameLog.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Everything a <see cref="MonitorHost"/> needs to front a directory of receivers.</summary>
internal sealed record MonitorHostOptions
{
    /// <summary>The directory client's settings: where the list is, how often, and the filters.</summary>
    public required UberSdrDirectoryOptions Directory { get; init; }

    /// <summary>The one port the whole site answers on.</summary>
    public required int Port { get; init; }

    /// <summary>The address that port binds to; "*" is every interface.</summary>
    public required string Bind { get; init; }

    /// <summary>The modems every receiver is given, already placed by the band plan.</summary>
    public required IReadOnlyList<ModemConfig> Modems { get; init; }

    /// <summary>The rate every station's channel runs at.</summary>
    public required int DspRate { get; init; }

    /// <summary>The dial the band plan chose, for the pages' RF scale.</summary>
    public required double DialHz { get; init; }

    /// <summary>Which sideband the plan is on.</summary>
    public string Sideband { get; init; } = "usb";

    /// <summary>Where each station's frame log goes; empty keeps none.</summary>
    public string FrameLogDirectory { get; init; } = "";

    /// <summary>The picker's and every page's title.</summary>
    public string? Title { get; init; }

    /// <summary>One paragraph for the visitor, on the picker and on every page.</summary>
    public string? About { get; init; }

    /// <summary>Waterfall line rate.</summary>
    public int LinesPerSecond { get; init; } = 30;

    /// <summary>FFT length; 0 picks the rate default.</summary>
    public int FftSize { get; init; }

    /// <summary>Listen for the NinoTNC idents sent alongside the PSK SSB modes.</summary>
    public bool IdBeacons { get; init; } = true;

    /// <summary>The PSK detector override, where one was given.</summary>
    public PskDetector? PskDetector { get; init; }

    /// <summary>Dead-feed thresholds; null takes the UberSDR family's defaults.</summary>
    public DeadFeedConfig? DeadFeed { get; init; }

    /// <summary>Where a station's lines go, given its slug. The empty tag is the host's own.</summary>
    public Func<string, StationJournal> Journal { get; init; } = StationJournal.Console;

    /// <summary>The clock every station, timer and linger runs on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Opens the on-demand input for one receiver: the pre-flight, and the session factory behind
    /// it. Injected so that the host can be driven against fake receivers, which is how the
    /// one-session-per-receiver promise is tested rather than hoped for.
    /// </summary>
    public required Func<DirectoryReceiver, Action<string>, CancellationToken, Task<OnDemandUberSdrInput>>
        OpenInput { get; init; }

    /// <summary>How the directory is fetched; null uses the client's own HTTP.</summary>
    public Func<CancellationToken, Task<string>>? FetchDirectory { get; init; }
}
