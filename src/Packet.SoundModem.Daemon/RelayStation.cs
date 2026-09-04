using Microsoft.Data.Sqlite;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A private station listed on this site: its page, its frame log, its links, and the receive
/// loop that turns its relayed audio into a waterfall.
/// </summary>
/// <remarks>
/// <para><b>An ordinary station over a socket.</b> A <see cref="SoundModemChannel"/> at the rate
/// the station said it is relaying at, <b>with no modems added at all</b>, a
/// <see cref="Station"/> over an <see cref="UplinkAudioInput"/>, and a
/// <see cref="WaterfallWebServer"/> routed under <c>/r/&lt;slug&gt;/</c> exactly as a receiver's
/// is. Everything a visitor sees therefore comes out of code that already runs in production; the
/// only new thing is where the samples came from. See <c>docs/uplink-plan.md</c> 4.1.</para>
/// <para><b>The decodes stay the station's own.</b> This process runs no modem for a relayed
/// station and never will: what the page lists is what that operator's daemon decoded, with their
/// modes, their diversity settings and their dial, pushed in through
/// <see cref="WaterfallWebServer.PushFrame"/>. The links panel is folded here from the frames'
/// own bytes rather than sent as a summary, so there is one implementation of a link card and it
/// survives the station going off the air.</para>
/// <para><b>Built on the first hello and kept for the life of the process</b>, exactly as a
/// receiver's station is. That is what makes a page, a frame log and a links panel outlive a
/// station going off air, and it is why there is no teardown anywhere in this class except
/// <see cref="DisposeAsync"/>.</para>
/// </remarks>
internal sealed class RelayStation : IMonitorStation
{
    /// <summary>
    /// How many frames may be held waiting for their audio before the oldest is listed anyway.
    /// </summary>
    /// <remarks>
    /// The hold exists to tag a frame onto the burst that carried it, and it is bounded because
    /// everything on this path is: a station sending frames while its audio has stalled must cost
    /// a slightly wrong burst tag rather than a queue that grows. Twenty is about a second of a
    /// channel doing one frame every 50 ms, which nothing on HF does.
    /// </remarks>
    internal const int MaxHeldFrames = 20;

    /// <summary>
    /// The display line rate to draw a relayed station at: the site's own, where the station's
    /// audio rate allows it, and the next one down that does where it does not. 0 means this
    /// site cannot draw that rate at all.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Dsp.WaterfallSource"/> hops <c>sampleRate / linesPerSecond</c> samples
    /// per line, so the line rate has to divide the sample rate exactly, and the hop has to fit
    /// inside the transform. Thirty lines a second does not divide 8000 or 16000 - and those are
    /// two of the rates a 48 kHz station is offered by its own start-up, because <c>publish</c>
    /// validates <c>audioRate</c> as an integer divisor of its channel. So a station following
    /// its own daemon's advice was refused here, with a sentence naming nothing it could change
    /// and a .NET stack trace in this site's journal.</para>
    /// <para>Deriving the rate rather than refusing it is the answer that needs no agreement
    /// between the two ends: 8000 and 16000 are drawn at 25 lines a second instead of 30, which
    /// nobody can see, and every rate <c>publish</c> will accept is one this site can draw. A
    /// rate no line rate divides - a prime one, which only a hand-written client would send - is
    /// still refused, and now at the boundary with a sentence about rates rather than a stack
    /// trace.</para>
    /// </remarks>
    internal static int LineRateFor(int sampleRate, int wanted, int fftSize)
    {
        if (sampleRate <= 0 || wanted <= 0)
        {
            return 0;
        }

        // The transform WaterfallSource would pick for this rate, if the site has not pinned one.
        int transform = fftSize > 0 ? fftSize : sampleRate >= 24000 ? 8192 : 2048;
        for (int lines = Math.Min(wanted, sampleRate); lines >= 1; lines--)
        {
            if (sampleRate % lines == 0 && sampleRate / lines <= transform)
            {
                return lines;
            }
        }

        return 0;
    }

    private readonly MonitorHostOptions _options;
    private readonly WaterfallRouter _router;
    private readonly SoundModemChannel _channel;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping;
    private readonly FrameLog? _frameLog;
    private readonly UplinkAudioInput _input;

    private readonly Lock _gate = new();
    private readonly Lock _holdGate = new();
    private readonly Queue<(long Target, RelayedFrame Frame)> _held = new();

    private IUplinkLink? _link;
    private Station? _station;
    private Task? _running;
    private ITimer? _linger;
    private int _demand;
    private string? _stationStatus;
    private string? _fault;
    private bool _disposed;
    private bool _routed;

    /// <param name="options">The host's own settings: where the logs go, the page's dressing.</param>
    /// <param name="router">The front door this station's prefix is registered on.</param>
    /// <param name="entry">The configured station, which is where the slug comes from.</param>
    /// <param name="hello">What the station said about itself when it connected.</param>
    /// <param name="stopping">Cancelled when the site is shutting down.</param>
    internal RelayStation(
        MonitorHostOptions options, WaterfallRouter router, UplinkEntry entry, UplinkHello hello,
        CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(hello);

        _options = options;
        _router = router;
        _time = options.TimeProvider;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Slug = entry.Slug;
        Callsign = entry.Callsign;
        Hello = hello;
        Journal = options.Journal(Slug);

        // The station's own rate, and no modems: this process decodes nothing for it. The
        // receive-only reason is said here for the same reason MonitorStation says it - the
        // channel is the same class a transmitting station uses, so it is told once, here.
        _channel = new SoundModemChannel(hello.AudioRate)
        {
            ReceiveOnlyReason =
                "this station receives only: its audio is relayed from "
                + $"{UberSdrDirectory.Ascii(entry.Callsign)}'s own receiver over a one-way "
                + "uplink, and nothing on this site can reach its transmitter.",
        };

        if (options.FrameLogDirectory is { Length: > 0 } directory)
        {
            string path = Path.Combine(directory, $"frames-{Slug}.db");
            try
            {
                _frameLog = FrameLog.Open(path, _time);
                Journal.Write($"frame log: {_frameLog.Path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or SqliteException)
            {
                // The same answer a receiver's station gives: say so and carry on without a log.
                // A page with no history is worth a great deal more than no page.
                Journal.WriteError(
                    $"cannot open the frame log at {path}\n"
                    + $"  {UberSdrDirectory.Ascii(e.Message)}\n"
                    + "  Set by \"frameLog\".\"path\", which is a directory in a monitor.");
            }
        }

        try
        {
            Build();
        }
        catch
        {
            // Half a station is worse than none, and for the same reasons a receiver's is: an
            // open SQLite handle with a writer thread, a route registered, and no reference to
            // any of it anywhere.
            Unbuild();
            throw;
        }

        _input = new UplinkAudioInput(hello.AudioRate, transmit => Server.IncomingIsTransmit = transmit)
        {
            BeforeRead = ReleaseDueFrames,
        };
        StartLoop();
    }

    /// <inheritdoc />
    public string Slug { get; }

    /// <inheritdoc />
    public WaterfallWebServer Server { get; private set; } = null!;

    /// <summary>The callsign this station's token was issued to. Never comes off the wire.</summary>
    internal string Callsign { get; }

    /// <summary>What it said about itself when it connected.</summary>
    internal UplinkHello Hello { get; }

    /// <summary>This station's own journal, tagged with its slug.</summary>
    internal StationJournal Journal { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Null, always. A receiver's station has one sentence the receiver itself supplied once a
    /// session was open; a relayed station says who and what it is in its row's own fields, off
    /// its hello, and a second summary of the same words would only be another thing to keep in
    /// step.
    /// </remarks>
    public string? Description => null;

    /// <summary>Whether its uplink is up right now.</summary>
    internal bool Connected => _input.Connected;

    /// <summary>How this station is credited on its page and named in its status chip.</summary>
    internal string Credit => string.Join(
        ", ",
        new[] { Callsign, Hello.Operator, Hello.Location }.Where(p => !string.IsNullOrEmpty(p)));

    private void Build()
    {
        Server = WaterfallWebServer.Routed(_channel, new WaterfallOptions
        {
            DialFrequencyHz = Hello.DialHz,
            Sideband = Hello.Sideband,
            LinesPerSecond = LineRateFor(Hello.AudioRate, _options.LinesPerSecond, _options.FftSize),
            FftSize = _options.FftSize,
            Public = true,
            Title = _options.Title,
            About = _options.About,
            PickerUrl = "../",
            // What its modems occupy, off the wire. Nothing enumerable carries them because this
            // process runs none of them, which is exactly what DeclaredBands is for.
            DeclaredBands = Hello.Bands,
            ReceiverKind = "station",
            FrameHistory = _frameLog is null ? null : _frameLog.Recent,
            TimeProvider = _time,
        });

        Server.SetReceiver(Credit, Hello.Site);
        Server.SetRadioStatus(Chip("connected"));

        if (_frameLog is not null)
        {
            StationFactory.BackfillLinks(Server, _frameLog);
        }

        Server.ViewersChanged += OnViewersChanged;
        Server.Start();
        _router.Add($"/r/{Slug}/", Server);
        _routed = true;
        Journal.Write($"station: {Server.Url} for {UberSdrDirectory.Ascii(Callsign)}");
    }

    /// <summary>
    /// Starts the receive loop over the uplink's audio.
    /// </summary>
    /// <remarks>
    /// Its own thread, as every station's is: <c>Read</c> is synchronous and blocking, so a host
    /// running fifty of these cannot share a pool with them. The loop turns whether or not a
    /// station is connected - <c>Read</c> waits and returns 0, which is the shape every network
    /// input here has - so there is nothing to start and stop as stations come and go.
    /// </remarks>
    private void StartLoop()
    {
        Station station;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            station = new Station(
                new StationOptions
                {
                    Channel = _channel,
                    Input = _input,
                    DspRate = Hello.AudioRate,
                    Journal = Journal,
                    DeviceKind = DeadFeedDevice.Uplink,
                    DeadFeed = _options.DeadFeed,
                    // Quiet with nobody watching is the whole design, and quiet with nobody
                    // connected is a station that is off the air; neither is a starved feed. What
                    // is left, and what this watch is for, is a socket that is up and demanded
                    // and delivering nothing, which is a half-open connection.
                    SessionLive = () => _input.Connected && Server.Viewers > 0,
                    TimeProvider = _time,
                    HealthChecks = [FrameLogDropCheck(), JitterDropCheck()],
                },
                _stopping.Token);
            _station = station;
        }

        station.Faulted += OnFault;
        _running = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    station.Run();
                }
                finally
                {
                    Stopped(station);
                }
            },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>The status chip, which always names the station: the sentence says what its
    /// uplink is doing and not whose radio it is.</summary>
    private string Chip(string sentence) => $"{Credit} - {sentence}";

    // ------------------------------------------------------------------ the uplink's own side

    /// <summary>A station has connected: this is now its live socket.</summary>
    internal void Attach(IUplinkLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_gate)
        {
            _link = link;
            _fault = null;
        }

        // Anything still queued is from before the gap, and painting it now would put a burst on
        // the display long after it happened.
        _input.Flush();
        _input.Connected = true;
        Server.SetRadioStatus(Chip(_stationStatus ?? "connected"));
    }

    /// <summary>That socket has gone. The page, the log and the links stay exactly as they are.</summary>
    internal void Detach(IUplinkLink link)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_link, link))
            {
                return;   // a ghost that was superseded; the live one is somebody else's
            }

            _link = null;
            _stationStatus = null;
        }

        _input.Connected = false;
        _input.Flush();
        Server.SetRadioStatus(Chip("not connected just now"));
    }

    /// <summary>
    /// Tells the station how many people are watching it, on whichever socket is live.
    /// </summary>
    /// <remarks>
    /// Called on every change, and again on the heartbeat so a station that missed one still
    /// finds out. This is the entire downward protocol.
    /// </remarks>
    internal void Announce(IUplinkLink? link = null)
    {
        int viewers;
        IUplinkLink? live;
        lock (_gate)
        {
            viewers = _demand;
            live = link ?? _link;
        }

        live?.Demand(viewers);
    }

    /// <summary>Audio off the wire.</summary>
    internal void PushAudio(ReadOnlySpan<short> pcm, bool transmitted) =>
        _input.Push(pcm, transmitted);

    /// <summary>The station's own sentence about its radio, for the page's status chip.</summary>
    internal void PushRadio(string? status)
    {
        lock (_gate)
        {
            _stationStatus = status;
        }

        Server.SetRadioStatus(Chip(status is { Length: > 0 } ? status : "connected"));
    }

    /// <summary>
    /// A frame the station decoded, held until the audio that carried it has been painted.
    /// </summary>
    /// <remarks>
    /// A frame crosses the wire as soon as the station decodes it, but the audio that carried the
    /// burst is still in the jitter buffer here. <c>BroadcastFrame</c> tags a frame onto the line
    /// the display has reached, so listing it on arrival would tag it five to twelve lines above
    /// its own burst - visibly wrong on a display doing thirty lines a second. So it waits until
    /// as much audio has left the buffer as was in it when the frame arrived. Self-correcting
    /// across an overrun and across a reconnect, because dropped audio counts as consumed.
    /// </remarks>
    internal void PushFrame(RelayedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_holdGate)
        {
            _held.Enqueue((_input.Accepted, frame));
        }

        // A frame that arrived with the buffer already empty - which is every frame on a station
        // nobody is watching - has nothing to wait for and goes out now.
        ReleaseDueFrames();
    }

    /// <summary>Lists every held frame whose audio has now been read.</summary>
    private void ReleaseDueFrames()
    {
        // The whole drain under one lock, so that the audio thread and the socket thread cannot
        // interleave two releases and list them in the wrong order.
        lock (_holdGate)
        {
            long consumed = _input.Consumed;
            while (_held.Count > 0
                   && (_held.Peek().Target <= consumed || _held.Count > MaxHeldFrames))
            {
                RelayedFrame frame = _held.Dequeue().Frame;
                Log(frame);
                Server.PushFrame(frame);
            }
        }
    }

    /// <summary>
    /// Writes a relayed frame into this station's own log, with the station's own timestamp.
    /// </summary>
    /// <remarks>
    /// Only where the frame carried bytes. An ident ghost, and a decoder that hands none over,
    /// are listed on the page and make no row, exactly as they make no link: the log's columns
    /// are derived from the frame, and there is nothing here to derive them from.
    /// </remarks>
    private void Log(RelayedFrame frame)
    {
        if (_frameLog is null || frame.Raw is not { Length: > 0 } raw)
        {
            return;
        }

        if (frame.Transmitted)
        {
            _frameLog.RecordTransmitted(
                frame.SubChannel, raw, frame.Mode, audioHz: null, rfHz: null,
                frame.TransmitTrimHz, frame.At);
            return;
        }

        _frameLog.Record(
            frame.SubChannel, raw,
            new FrameQuality(
                frame.Mode, frame.LengthBytes, frame.CorrectedBytes, frame.CrcValid,
                FrequencyOffsetHz: frame.OffsetHz, PlainIl2p: frame.PlainIl2p,
                MonitorOnly: frame.MonitorOnly, SnrDb: frame.SnrDb),
            audioHz: null, rfHz: null, modeName: null, at: frame.At);
    }

    // ------------------------------------------------------------------ viewers, and the linger

    /// <summary>
    /// The page's viewer count, which is the whole mechanism: leaving zero asks the station for
    /// audio and returning there stops it a linger later, and it is the same count however many
    /// browsers are behind it.
    /// </summary>
    private void OnViewersChanged(int viewers)
    {
        bool announce = false;
        lock (_gate)
        {
            if (viewers > 0)
            {
                _linger?.Dispose();
                _linger = null;
                if (_demand != viewers)
                {
                    _demand = viewers;
                    announce = true;
                }
            }
            else if (_demand != 0 && _linger is null && !_disposed)
            {
                // The same linger a receiver's session gets, and for the same reason: a page
                // refresh or a tab switch must not stop and restart a home station's stream.
                _linger = _time.CreateTimer(
                    _ => LingerExpired(), null, _options.Linger, Timeout.InfiniteTimeSpan);
            }
        }

        if (announce)
        {
            Announce();
        }
    }

    private void LingerExpired()
    {
        lock (_gate)
        {
            _linger?.Dispose();
            _linger = null;
            if (Server.Viewers > 0 || _demand == 0)
            {
                return;   // somebody came back inside the window
            }

            _demand = 0;
        }

        Announce();
    }

    // ------------------------------------------------------------------ what the picker is told

    /// <inheritdoc />
    public (string State, string? Status) State()
    {
        lock (_gate)
        {
            if (_fault is { } fault)
            {
                return ("faulted", fault);
            }

            if (!_input.Connected)
            {
                return ("offline", "not connected just now");
            }

            string sentence = _stationStatus ?? "connected";
            return (Server.Viewers > 0 ? "live" : _demand > 0 ? "lingering" : "idle", sentence);
        }
    }

    /// <inheritdoc />
    public object Row(bool listed)
    {
        (string state, string? status) = State();
        bool connected = _input.Connected;
        return new
        {
            slug = Slug,
            kind = "station",
            callsign = Callsign,
            @operator = Hello.Operator,
            Hello.Location,
            Hello.Radio,
            // What it runs, off its own hello. The names are the modes' own, capped and escaped
            // like everything else it sends.
            modes = Hello.Bands.Select(b => ModeNames.Display(b.Mode)).Distinct().ToArray(),
            publicUrl = Hello.Site,
            offered = connected,
            why = connected ? null : "not connected just now",
            state,
            status,
            viewers = Server.Viewers,
            // The five figures a station has no honest answer for. Present and null rather than
            // absent, so a reader of this API sees one row shape: they are facts about a web
            // receiver, and inventing them for somebody's transceiver would be inventing them.
            host = (string?)null,
            name = (string?)null,
            snrDb = (double?)null,
            loadStatus = (string?)null,
            availableClients = (int?)null,
            maxClients = (int?)null,
            description = (string?)null,
        };
    }

    // ------------------------------------------------------------------ faults

    /// <summary>
    /// The receive loop has faulted. On a relayed station that means one thing - a socket that is
    /// up, demanded, and delivering nothing - so the answer is to drop it and let the station
    /// reconnect on its own ladder.
    /// </summary>
    private void OnFault(StationFault fault)
    {
        IUplinkLink? link;
        lock (_gate)
        {
            _fault = fault.Reason;
            link = _link;
        }

        Journal.WriteError(fault.Reason);
        Server.SetRadioStatus(Chip(fault.Reason));

        // Deliberately not Environment.Exit on fault.Stalled, which is what a receiver's station
        // does: this input's Read waits on an event with a timeout and cannot block for ever, so
        // there is nothing here that a process restart could unwedge that closing the socket
        // cannot.
        link?.Close("this site asked for audio and got none; reconnect when you can");
    }

    /// <summary>The loop has returned. Start another, because this station has no other way to
    /// hear anything, and building one costs no network at all.</summary>
    private void Stopped(Station station)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_station, station))
            {
                return;
            }

            _station = null;
        }

        station.Faulted -= OnFault;
        station.Dispose();

        if (!_stopping.IsCancellationRequested && !_disposed)
        {
            StartLoop();
        }
    }

    /// <summary>
    /// A full disk left a station keeping an empty frame log for weeks with nothing anywhere
    /// saying so. Polled from the receive loop, which turns whether or not audio is flowing.
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
                $"frame log: {drops - seen} frames dropped unwritten ({drops} total) - the disk "
                + "cannot keep up, is full, or is unwritable";
            seen = drops;
            return line;
        };
    }

    /// <summary>
    /// Audio thrown away because the jitter buffer overran. Worth one line rather than none: it
    /// means this site cannot keep up with the station or the station is sending faster than real
    /// time, and either way somebody watching is seeing a display with holes in it.
    /// </summary>
    private Func<string?> JitterDropCheck()
    {
        long seen = 0;
        return () =>
        {
            long drops = _input.Dropped;
            if (drops <= seen)
            {
                return null;
            }

            string line =
                $"uplink: dropped {(drops - seen) * 1000 / Hello.AudioRate} ms of relayed audio "
                + "the jitter buffer had no room for";
            seen = drops;
            return line;
        };
    }

    /// <summary>Takes down whatever <see cref="Build"/> managed to put up, on the way out of a
    /// constructor that is about to throw.</summary>
    private void Unbuild()
    {
        try
        {
            if (_routed)
            {
                _router.Remove($"/r/{Slug}/");
                _routed = false;
            }

            if (Server is { } server)
            {
                server.ViewersChanged -= OnViewersChanged;
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _frameLog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Journal.WriteError(
                "could not fully take down a station that would not build - "
                + UberSdrDirectory.Ascii(e.Message));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Station? station;
        Task? running;
        lock (_gate)
        {
            _disposed = true;
            _linger?.Dispose();
            _linger = null;
            station = _station;
            _station = null;
            running = _running;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        Server.ViewersChanged -= OnViewersChanged;

        // The input first: its Read is what the loop is sitting in, and disposing it is what lets
        // the loop notice it has been asked to stop.
        _input.Dispose();
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

        _stopping.Dispose();
    }
}
