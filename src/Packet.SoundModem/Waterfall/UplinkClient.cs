using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// What a station's <c>publish</c> block says: where to publish, what credential to present, and
/// who is publishing. Section 4.3 of <c>docs/uplink-plan.md</c>; the daemon validates every one of
/// these before one of these records is built, so nothing here re-checks an operator's typing.
/// </summary>
/// <remarks>
/// The three rate fields are not the operator's: <see cref="ChannelRate"/> is the DSP rate the
/// station's own channel runs at, and is what the relay's samples arrive at, while
/// <see cref="DialHz"/> and <see cref="Sideband"/> are the band plan's, so that a monitor draws
/// the same RF scale the station's own page draws. Only <see cref="AudioRate"/> is written down
/// in the config, and it has to divide <see cref="ChannelRate"/>.
/// </remarks>
public sealed record UplinkSettings
{
    /// <summary>The monitor's uplink endpoint, an absolute <c>ws</c> or <c>wss</c> URL.</summary>
    public required string Url { get; init; }

    /// <summary>The token the site issued this station, presented as a bearer credential.</summary>
    public required string Token { get; init; }

    /// <summary>This station's callsign, which the monitor checks against the token.</summary>
    public required string Callsign { get; init; }

    /// <summary>Who runs it, for the credit line. Null says nothing rather than guessing.</summary>
    public string? Operator { get; init; }

    /// <summary>Roughly where it is, for the picker row.</summary>
    public string? Location { get; init; }

    /// <summary>What the radio and antenna are, for the credit line.</summary>
    public string? Radio { get; init; }

    /// <summary>The operator's own page, absolute http or https, or null.</summary>
    public string? Site { get; init; }

    /// <summary>The station channel's DSP rate: the rate the relay hands audio over at.</summary>
    public required int ChannelRate { get; init; }

    /// <summary>The rate the audio is published at, an integer divisor of <see cref="ChannelRate"/>.</summary>
    public required int AudioRate { get; init; }

    /// <summary>
    /// <c>"frames": "watched"</c>: hold decoded frames back until somebody is watching. False
    /// (<c>"always"</c>, the default) sends them whether or not anybody is, which is what makes a
    /// quiet band look alive to somebody arriving an hour later.
    /// </summary>
    public bool FramesOnlyWhileWatched { get; init; }

    /// <summary>The dial the band plan settled on, in Hz, or 0 for a station that has no dial.</summary>
    public double DialHz { get; init; }

    /// <summary>"usb" or "lsb", as the station's own page is drawn.</summary>
    public string Sideband { get; init; } = "usb";
}

/// <summary>
/// The station's side of the uplink: one outbound WebSocket to a public monitor site, carrying
/// this station's receive audio, its own transmissions, the frames it decoded and its status
/// sentence, and carrying one thing back - how many people are watching.
/// </summary>
/// <remarks>
/// <para>Phase 2 of <c>docs/uplink-plan.md</c>; section 4.2 is normative for the wire and 4.3 for
/// this class. A station with no <c>publish</c> block never builds one of these, and
/// <see cref="WaterfallWebServer.Relay"/> stays null.</para>
/// <para><b>It cannot act on the station, structurally</b> (4.6). It holds a
/// <see cref="WaterfallWebServer"/>, a settings record, a socket and some buffers: no
/// <see cref="Channel.SoundModemChannel"/>, so it cannot enqueue a transmission; no PTT, no KISS
/// server, no configuration API and no station. The one message it will read carries one integer.
/// <c>UplinkClientTests</c> holds that claim to a reflection test over this type's fields, so
/// adding a field that could act fails the build rather than review.</para>
/// <para><b>It never faults the station</b> (decision 8). Every failure is a journal line and a
/// wait: nothing here raises an event, sets an exit code or ends a process, and there is no give-up
/// clock. A node passing traffic at 3 a.m. does not stop because a website is unreachable, so a
/// permanently wrong token is a line every hour and nothing louder.</para>
/// <para><b>Nothing is offered to it that it does not want.</b> <see cref="Wanted"/> is false
/// whenever the socket is down or nobody is watching, and the server reads it before it does
/// anything at all with a block of audio. Frames are not gated on it, by decision 3.</para>
/// </remarks>
public sealed class UplinkClient : IWaterfallRelay, IAsyncDisposable
{
    /// <summary>The wire version this speaks. A monitor that does not know it says so and closes.</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>Audio message length, matching the browser format's block (4.2).</summary>
    private const int AudioBlockMilliseconds = 40;

    /// <summary>Type byte, kind byte and two of padding: the browser format's aligned header.</summary>
    private const int AudioHeaderBytes = 4;

    /// <summary>Binary message type byte, the browser format's (3.1).</summary>
    private const byte AudioMessage = 0x02;

    /// <summary>
    /// How long the socket may go without a word from the monitor before it is treated as dead.
    /// A <c>demand</c> arrives on every change and at least every 20 s, so this is two of them
    /// missed plus a breath, which is what 4.3 asks for.
    /// </summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How often the silence above is checked.</summary>
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// WebSocket ping interval and how long a missing pong is tolerated. Set because .NET's
    /// default sends pings and never times out missing pongs, so a half-open TCP connection would
    /// leave this client believing it is live and the site showing a station that has gone.
    /// </summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A session shorter than this did not really work, whatever it looked like. A monitor that
    /// accepts and immediately drops would otherwise be reconnected to every second for ever, by
    /// every station it does that to; this is what <see cref="UberSdrReconnectOutcome.ShortSession"/>
    /// was added for on the receive side and it is the same failure here.
    /// </summary>
    private static readonly TimeSpan ShortSession = TimeSpan.FromSeconds(30);

    /// <summary>How long a planned goodbye is given to reach the site before the socket goes.</summary>
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>An outage says so once and then at this interval, not once a minute.</summary>
    private static readonly TimeSpan OutageSaidEvery = TimeSpan.FromMinutes(15);

    /// <summary>A refused token is a mistake somebody has to fix, so it is said this often.</summary>
    private static readonly TimeSpan RefusalSaidEvery = TimeSpan.FromHours(1);

    /// <summary>Viewers arriving and leaving is a state change, not news; capped at this.</summary>
    private static readonly TimeSpan ViewersSaidEvery = TimeSpan.FromMinutes(1);

    /// <summary>An upstream that cannot keep up says so this often, with the count since last time.</summary>
    private static readonly TimeSpan DropsSaidEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Messages the send queue holds before the oldest is dropped: eight seconds of audio at 25
    /// blocks a second. Bounded because the alternative is an unbounded buffer on a station whose
    /// upstream cannot carry what its config asked for, which is exactly the ADSL case of 4.5.
    /// </summary>
    private const int QueueDepth = 200;

    /// <summary>Largest inbound message accepted. The one we expect is under a hundred bytes.</summary>
    private const int MaxIncomingBytes = 16 * 1024;

    /// <summary>
    /// Longest the site's own words are allowed to be in this station's journal. The site is a
    /// semi-trusted publisher in this direction too: it may reconnect as often as it likes, and
    /// each reconnect is a line.
    /// </summary>
    private const int MaxSiteTextLength = 200;

    /// <summary>How many reconnect waits are remembered, so the record cannot grow for ever.</summary>
    private const int RetryWaitsKept = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WaterfallWebServer _server;
    private readonly UplinkSettings _settings;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Uri _uri;
    private readonly UberSdrReconnectPolicy _policy = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly int _blockSamples;

    /// <summary>
    /// One accumulator, one decimator, one scratch buffer and one generation counter per kind:
    /// a block is never half heard and half transmitted, and nothing about one kind's stream is
    /// reachable from the other's.
    /// </summary>
    private readonly short[][] _pending;
    private readonly int[] _fill = new int[2];
    private readonly Decimator?[] _decimators = new Decimator?[2];
    private readonly float[][] _decimated;
    private readonly int[] _seenGeneration = new int[2];

    /// <summary>
    /// Held across the body of <see cref="Audio"/>, which is the only thing that touches the
    /// buffers above.
    /// </summary>
    /// <remarks>
    /// <para><b>The two callers really can overlap</b>, and the reason is worth writing down
    /// because the interface's own remarks read as though they cannot. Received audio arrives on
    /// the station's audio read thread and the station's own transmission arrives from the
    /// display pacer's timer callback, on whatever thread pool thread the timer fires on. At the
    /// tail of a key-up the receive tap can pass its gate, queue on the server's display lock
    /// behind the pacer, and be released exactly as the pacer moves on to its own offers, at
    /// which point both are running outside every lock. Measured, once per key-up per 40 of them
    /// at 200 us of work per call and rising from there; what it produced without this lock was a
    /// block of the right length carrying audio of both kinds, which is undetectable at the far
    /// end and is the one thing 4.2 says cannot happen.</para>
    /// <para>Nothing under it blocks: a decimation, a float-to-s16 conversion and a
    /// non-blocking queue write. It cannot park the station's audio thread on a socket.</para>
    /// </remarks>
    private readonly Lock _audioLock = new();

    /// <summary>Where the audio thread hands blocks to the sender; null while the socket is down.</summary>
    private volatile Channel<Outgoing>? _outgoing;

    private volatile Task? _run;
    private volatile Task? _sending;
    private int _viewers;
    private int _generation;
    private int _connectAttempts;
    private int _ignoredMessages;
    private long _droppedMessages;
    private long _droppedSaid;
    private long _outageSaidAt;
    private long _refusalSaidAt;
    private long _viewersSaidAt;
    private long _dropsSaidAt;
    private long _lastHeardAt;
    private volatile bool _freshOutage = true;
    private volatile bool _saidWatching;
    private long _sessionOpenedAt;
    private int _retryCount;
    private readonly List<TimeSpan> _retryWaits = [];
    private readonly Lock _waitsLock = new();

    /// <summary>
    /// Builds the client. Nothing happens until <see cref="Start"/>: constructing one is not
    /// opening a socket, so a station that fails start-up after this line has published nothing.
    /// </summary>
    /// <param name="server">
    /// The station's waterfall server, for the one thing this needs from it: the measured modem
    /// bands, which go into the <c>hello</c> so the monitor can draw the overlays for a station
    /// whose modems it is not running. Nothing is written to it.
    /// </param>
    /// <param name="settings">The <c>publish</c> block, already validated by the daemon.</param>
    /// <param name="timeProvider">The clock the ladder, the watchdog and the rate limits run on.</param>
    /// <param name="log">Where this client's journal lines go, or null for a client that is quiet.</param>
    public UplinkClient(
        WaterfallWebServer server,
        UplinkSettings settings,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);
        _server = server;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _uri = new Uri(settings.Url);
        _lastHeardAt = _time.GetTimestamp();

        _blockSamples = settings.AudioRate * AudioBlockMilliseconds / 1000;
        _pending = [new short[_blockSamples], new short[_blockSamples]];

        if (settings.ChannelRate != settings.AudioRate)
        {
            // One per kind: the two streams are separate signals, and a filter carrying state
            // from a keyup into the band noise that follows it would smear one into the other.
            int factor = settings.ChannelRate / settings.AudioRate;
            _decimators[0] = new Decimator(settings.ChannelRate, factor);
            _decimators[1] = new Decimator(settings.ChannelRate, factor);
        }

        int scratch = _decimators[0]?.MaxOutput(DecimationChunk) ?? DecimationChunk;
        _decimated = [new float[scratch], new float[scratch]];
    }

    /// <summary>Samples decimated at a time, so the scratch buffer is a fixed size.</summary>
    private const int DecimationChunk = 4096;

    /// <inheritdoc />
    /// <remarks>
    /// True only while the socket is up, the monitor has said <c>welcome</c>, and its last
    /// <c>demand</c> said somebody is watching. It is read by the server before it does anything
    /// with a block of audio, so a station nobody has picked - which is nearly always - spends one
    /// property read per block on publishing and nothing else.
    /// </remarks>
    public bool Wanted => _outgoing is not null && Volatile.Read(ref _viewers) > 0;

    /// <summary>How many people the monitor last said were watching. 0 while the socket is down.</summary>
    public int Viewers => _outgoing is null ? 0 : Volatile.Read(ref _viewers);

    /// <summary>
    /// Whether the socket is up and the monitor has said <c>welcome</c>: frames and the status
    /// sentence are going up. Whether audio is as well is <see cref="Wanted"/>.
    /// </summary>
    public bool Publishing => _outgoing is not null;

    /// <summary>Connection attempts made since this client started, for tests and diagnostics.</summary>
    internal int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    /// <summary>Inbound messages that were not a <c>demand</c> and were dropped (4.6).</summary>
    internal int IgnoredMessages => Volatile.Read(ref _ignoredMessages);

    /// <summary>Queued messages the bounded send queue dropped because the upstream could not keep up.</summary>
    internal long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>
    /// The first <see cref="RetryWaitsKept"/> reconnect waits taken, in order, for tests to hold
    /// the ladder to 4.3. Capped: a station whose site is unreachable for a year would otherwise
    /// retain a million of these to serve a diagnostic nobody is reading.
    /// </summary>
    internal IReadOnlyList<TimeSpan> RetryWaits
    {
        get { lock (_waitsLock) { return [.. _retryWaits]; } }
    }

    /// <summary>Reconnect waits taken in total, capped or not.</summary>
    internal int RetryCount => Volatile.Read(ref _retryCount);

    /// <summary>The loop, for a test that wants to be sure it has not ended or faulted.</summary>
    internal Task? RunTask => _run;

    /// <summary>
    /// Starts publishing: connect, say hello, and keep the socket for as long as the process
    /// lives, reconnecting for ever on the ladder.
    /// </summary>
    /// <remarks>
    /// Returns immediately. The loop runs on the thread pool and every exception in it is caught
    /// and turned into a journal line and a wait, because the uplink is a courtesy and this
    /// process has a radio to run.
    /// </remarks>
    public void Start()
    {
        if (_run is not null)
        {
            return;
        }

        _run = Task.Run(RunAsync);
    }

    /// <inheritdoc />
    public void Audio(ReadOnlySpan<float> samples, bool transmitted)
    {
        Channel<Outgoing>? outgoing = _outgoing;
        if (outgoing is null || Volatile.Read(ref _viewers) <= 0)
        {
            return;
        }

        int kind = transmitted ? 1 : 0;
        lock (_audioLock)
        {
            // A new session, or a gap in which nobody was watching: whatever was half assembled
            // belongs to a stream that stopped, and sending it would put a splice on somebody's
            // waterfall. Per kind, because the other kind's half-block is not this caller's to
            // throw away and the two arrive on different threads.
            int generation = Volatile.Read(ref _generation);
            if (generation != _seenGeneration[kind])
            {
                _seenGeneration[kind] = generation;
                _fill[kind] = 0;
            }

            if (_decimators[kind] is not { } decimator)
            {
                Append(samples, kind, outgoing);
                return;
            }

            float[] scratch = _decimated[kind];
            for (int offset = 0; offset < samples.Length; offset += DecimationChunk)
            {
                int take = Math.Min(DecimationChunk, samples.Length - offset);
                int produced = decimator.Process(samples.Slice(offset, take), scratch);
                Append(scratch.AsSpan(0, produced), kind, outgoing);
            }
        }
    }

    /// <inheritdoc />
    public void Frame(RelayedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Channel<Outgoing>? outgoing = _outgoing;
        if (outgoing is null || (_settings.FramesOnlyWhileWatched && Volatile.Read(ref _viewers) <= 0))
        {
            return;
        }

        Send(outgoing, WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "frame",
                sub = frame.SubChannel,
                mode = frame.Mode,
                from = frame.From,
                to = frame.To,
                lenBytes = frame.LengthBytes,
                snrDb = frame.SnrDb,
                burstLines = frame.BurstLines,
                offsetHz = frame.OffsetHz,
                corrected = frame.CorrectedBytes,
                crc = frame.CrcValid,
                id = frame.IdBeacon ? true : (bool?)null,
                tx = frame.Transmitted ? true : (bool?)null,
                txTrimHz = frame.TransmitTrimHz,
                why = frame.Note,
                il2p = frame.HeaderType,
                hex = frame.FrameHex,
                plain = frame.PlainIl2p ? true : (bool?)null,
                monitorOnly = frame.MonitorOnly ? true : (bool?)null,
                at = frame.At.ToUniversalTime()
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                raw = frame.Raw is null ? null : Convert.ToBase64String(frame.Raw),
            }, Json));
    }

    /// <inheritdoc />
    public void Radio(string? status)
    {
        Channel<Outgoing>? outgoing = _outgoing;
        if (outgoing is null)
        {
            return;
        }

        Send(outgoing, WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
            new { type = "radio", status }, Json));
    }

    /// <summary>Stops publishing: says goodbye if there is anybody to say it to, and closes.</summary>
    /// <remarks>
    /// The <c>bye</c> of 4.2 goes through the ordinary send queue and is waited for, and only then
    /// is anything cancelled. It has to be that way round: cancelling the receive loop aborts the
    /// socket, so a goodbye written afterwards would have nowhere to go and the site's journal
    /// would read "connection closed" rather than "GB7RDG-2 is shutting down". It is a courtesy on
    /// a courtesy, so it is bounded and every failure of it is shrugged off.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_outgoing is { } outgoing && _sending is { } sending)
        {
            Send(outgoing, WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
                new { type = "bye", reason = "the station is shutting down" }, Json));
            outgoing.Writer.TryComplete();
            try
            {
                // TimeProvider.System deliberately, and it is the one place in this class that
                // does not take the injected clock. This is a safety net on a shutdown path, and
                // under a FakeTimeProvider - which is how every test here is written - a delay on
                // the injected clock never fires, so the net would be inert in exactly the tests
                // that would catch it being needed.
                await Task.WhenAny(sending, Task.Delay(GoodbyeTimeout, TimeProvider.System))
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A socket that has already gone. There was nobody to say goodbye to.
            }
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_run is { } run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop catches its own; this is belt and braces on a shutdown path.
            }
        }

        _stopping.Dispose();
    }

    /// <summary>The reconnect loop: for ever, because the alternative is a station that quietly
    /// stops publishing and nobody finds out until somebody looks at the site.</summary>
    private async Task RunAsync()
    {
        Say($"publishing to {_uri.Host} as {_settings.Callsign}");

        while (!_stopping.IsCancellationRequested)
        {
            UberSdrReconnectOutcome outcome;
            string reason;
            try
            {
                (outcome, reason) = await SessionAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (UberSdrRefusedException refused)
            {
                outcome = UberSdrReconnectOutcome.Refused;
                reason = Ascii(refused.Message);
            }
            catch (Exception failure)
            {
                // A session that opened and then died is not a transport failure, whatever killed
                // it, so it takes the same rung the normal return path would have given it. Only
                // a connect that never opened a socket is Transient.
                outcome = Classify(Interlocked.Read(ref _sessionOpenedAt));
                reason = Ascii(failure.Message);
            }

            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            // The wait first, so the line an operator reads says when this will be tried again
            // rather than leaving them to guess whether anything is still happening.
            TimeSpan wait = _policy.After(outcome);
            Interlocked.Increment(ref _retryCount);
            lock (_waitsLock)
            {
                if (_retryWaits.Count < RetryWaitsKept)
                {
                    _retryWaits.Add(wait);
                }
            }

            if (outcome == UberSdrReconnectOutcome.Refused)
            {
                SayRefused(reason, wait);
            }
            else
            {
                SayOutage(reason, wait);
            }

            try
            {
                await Task.Delay(wait, _time, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection, from the upgrade to whatever ends it, and why it ended.</summary>
    private async Task<(UberSdrReconnectOutcome Outcome, string Reason)> SessionAsync()
    {
        Interlocked.Increment(ref _connectAttempts);
        Interlocked.Exchange(ref _sessionOpenedAt, 0);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        using ClientWebSocket socket = await ConnectAsync(session.Token).ConfigureAwait(false);

        long opened = _time.GetTimestamp();
        Interlocked.Exchange(ref _sessionOpenedAt, opened);
        Volatile.Write(ref _lastHeardAt, opened);
        var outgoing = System.Threading.Channels.Channel.CreateBounded<Outgoing>(
            new BoundedChannelOptions(QueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            dropped =>
            {
                Interlocked.Increment(ref _droppedMessages);
                dropped.Return();
            });

        // Before the queue is published to the audio thread: the hello is first on the wire by
        // construction rather than by ordering luck (4.2).
        await SendHelloAsync(socket, session.Token).ConfigureAwait(false);

        // Awaited on the way out, and declared after the source it cancels so that it is disposed
        // first. Both halves matter: a plain Dispose does not wait for a callback already running,
        // so a tick that had read the clock and was about to cancel would reach a disposed source
        // and throw ObjectDisposedException on a thread pool thread with nothing above it, which
        // ends the process. That is decision 8 broken by the one piece of this class that was not
        // inside a try, so it is now inside one as well.
        await using ITimer watchdog = _time.CreateTimer(
            _ =>
            {
                try
                {
                    if (_time.GetElapsedTime(Volatile.Read(ref _lastHeardAt)) > SilenceTimeout)
                    {
                        // Two demands missed. A hung established socket starves nothing here, but
                        // it does leave the site listing a station that has gone, so it is ended
                        // and remade rather than believed.
                        session.Cancel();
                    }
                }
                catch (Exception)
                {
                    // A timer callback is the one place in this class with no caller to catch for
                    // it, and an unhandled exception here would abort the daemon. The session is
                    // ending under it, which is the only way this is reached.
                }
            },
            null,
            WatchdogPeriod,
            WatchdogPeriod);

        Task send = SendLoopAsync(socket, outgoing, session.Token);
        _sending = send;
        string? reason;
        try
        {
            reason = await ReceiveLoopAsync(socket, outgoing, session.Token).ConfigureAwait(false);
        }
        finally
        {
            // The audio thread stops finding a queue the moment the session ends, so nothing is
            // accumulated for a socket that has gone.
            _outgoing = null;
            Volatile.Write(ref _viewers, 0);
            _saidWatching = false;
            Interlocked.Increment(ref _generation);
            outgoing.Writer.TryComplete();
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await send.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A send that failed is why the session is ending; the receive side has the reason.
            }

            DrainQueue(outgoing);
            _sending = null;
        }

        TimeSpan lived = _time.GetElapsedTime(opened);
        if (_stopping.IsCancellationRequested)
        {
            return (UberSdrReconnectOutcome.Healthy, "");
        }

        return Classify(opened) == UberSdrReconnectOutcome.Healthy
            ? (UberSdrReconnectOutcome.Healthy,
                reason ?? $"{_uri.Host} closed the uplink after {lived.TotalMinutes:F0} min")
            : (UberSdrReconnectOutcome.ShortSession,
                reason ?? $"{_uri.Host} dropped the uplink after {lived.TotalSeconds:F0} s");
    }

    /// <summary>
    /// Which rung a session that has just ended belongs on. A session that ran is a healthy one,
    /// whatever ended it; one that did not is the failure the ShortSession rung exists for, since
    /// a monitor accepting and dropping at once would otherwise be reconnected to every second
    /// for ever, by every station it does it to. A connect that never opened is Transient.
    /// </summary>
    private UberSdrReconnectOutcome Classify(long opened) => opened == 0
        ? UberSdrReconnectOutcome.Transient
        : _time.GetElapsedTime(opened) >= ShortSession
            ? UberSdrReconnectOutcome.Healthy
            : UberSdrReconnectOutcome.ShortSession;

    /// <summary>Opens the socket, with the token as a header rather than a query parameter.</summary>
    private async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "pdn-soundmodem (publish: station uplink)");

        // A header, not a query parameter: a query parameter is written down by every log between
        // here and there, this site's reverse proxy included.
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_settings.Token}");

        // .NET sends pings by default and never times out missing pongs, which leaves a half-open
        // connection looking live for ever. Both halves are set for that reason.
        socket.Options.KeepAliveInterval = KeepAlive;
        socket.Options.KeepAliveTimeout = KeepAlive;

        // Keeps the upgrade's HTTP status, so a token the site will not accept is distinguishable
        // from a site that is merely down: the two deserve very different retry cadences.
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(_uri, cancellation).ConfigureAwait(false);
        }
        catch (WebSocketException e) when (socket.HttpStatusCode
            is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            HttpStatusCode status = socket.HttpStatusCode;
            socket.Dispose();
            throw new UberSdrRefusedException(
                status == HttpStatusCode.TooManyRequests
                    ? $"{_uri.Host} said HTTP 429 - too many connections for now"
                    : $"{_uri.Host} would not accept this station's token (HTTP {(int)status}). "
                        + "Check \"publish\".\"token\" and \"publish\".\"callsign\" against what "
                        + "the site issued",
                e);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    /// <summary>Everything this station is, said once and first.</summary>
    private async Task SendHelloAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        byte[] hello = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "hello",
                protocol = ProtocolVersion,
                version = DaemonVersion,
                callsign = _settings.Callsign,
                @operator = _settings.Operator,
                location = _settings.Location,
                radio = _settings.Radio,
                site = _settings.Site,
                audioRate = _settings.AudioRate,
                blockSamples = _blockSamples,
                dialHz = _settings.DialHz,
                sideband = _settings.Sideband,
                frames = _settings.FramesOnlyWhileWatched ? "watched" : "always",
                modems = _server.Bands.Select(b => new
                {
                    sub = b.SubChannel,
                    mode = b.Mode,
                    lowHz = Math.Round(b.LowHz, 1),
                    highHz = Math.Round(b.HighHz, 1),
                    centreHz = Math.Round(b.CentreHz, 1),
                }),
            }, Json);

        await socket.SendAsync(
            hello, WebSocketMessageType.Text, endOfMessage: true, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The one message the monitor may send that does anything, and the counter for everything
    /// else it might send.
    /// </summary>
    /// <remarks>
    /// The <c>switch</c> below is the whole inbound surface of a published station: two names, one
    /// of which carries an integer and the other a slug. Anything else - a <c>config</c>, a KISS
    /// frame, a request to transmit - is counted and dropped, because there is nothing here for it
    /// to reach (4.6).
    /// </remarks>
    private async Task<string?> ReceiveLoopAsync(
        ClientWebSocket socket, Channel<Outgoing> outgoing, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var message = new ArrayBufferWriter<byte>(1024);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                message.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    if (message.WrittenCount + result.Count > MaxIncomingBytes)
                    {
                        // Reassembled with a hard cap, as 4.2 requires of both ends. A monitor
                        // that sends more than this is not one this station understands.
                        return $"{_uri.Host} sent a message over {MaxIncomingBytes} bytes";
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                Volatile.Write(ref _lastHeardAt, _time.GetTimestamp());
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    Interlocked.Increment(ref _ignoredMessages);
                    continue;
                }

                Apply(message.WrittenSpan, outgoing);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // The session ending, from the watchdog or from a stop. Not news in itself.
            return null;
        }
        catch (WebSocketException e)
        {
            return Ascii(e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads one inbound message. Two names are known; everything else is dropped.</summary>
    private void Apply(ReadOnlySpan<byte> utf8, Channel<Outgoing> outgoing)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out JsonElement type))
            {
                Interlocked.Increment(ref _ignoredMessages);
                return;
            }

            if (type.ValueKind != JsonValueKind.String)
            {
                Interlocked.Increment(ref _ignoredMessages);
                return;
            }

            switch (type.GetString())
            {
                case "demand":
                    ApplyDemand(document.RootElement);
                    break;

                case "welcome":
                    ApplyWelcome(document.RootElement, outgoing);
                    break;

                default:
                    // No arm reaches anything: the station has nothing here that could act on a
                    // message even if the site were taken over.
                    Interlocked.Increment(ref _ignoredMessages);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Unreadable, or readable and not the shape it claimed. Either way it is counted and
            // dropped: 4.6's promise is that nothing the monitor sends can do anything to this
            // station, and a message that ended the session would be doing something.
            Interlocked.Increment(ref _ignoredMessages);
        }
    }

    /// <summary>The text of a property, or null if it is absent or is not a string.</summary>
    private static string? TextOrNull(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void ApplyWelcome(JsonElement message, Channel<Outgoing> outgoing)
    {
        if (_outgoing is not null)
        {
            // A second welcome says nothing new and must not restart anything.
            Interlocked.Increment(ref _ignoredMessages);
            return;
        }

        string? url = TextOrNull(message, "url");
        string? slug = TextOrNull(message, "slug");

        // Only now: everything before this point could still be refused, and audio must not be
        // assembled for a socket the far end has not agreed to.
        Interlocked.Increment(ref _generation);
        _outgoing = outgoing;
        _freshOutage = true;
        Say(url is { Length: > 0 }
            ? $"live at {Site(url)}"
            : $"live as {Site(slug ?? _settings.Callsign)}");

        // The sentence the station has now, not the next change to it. SetRadioStatus fires only
        // when it changes, and a Flex station's frequency reference is set long before the uplink
        // exists, so without this a relayed station's status chip would stay empty until the
        // radio said something new - and would empty again on every reconnect.
        if (_server.RadioStatus is { Length: > 0 } status)
        {
            Radio(status);
        }
    }

    private void ApplyDemand(JsonElement message)
    {
        if (_outgoing is null)
        {
            // A demand before the welcome. Heard, so the watchdog is satisfied, and otherwise
            // ignored: the handshake is not finished.
            Interlocked.Increment(ref _ignoredMessages);
            return;
        }

        // Guarded on the kind rather than caught: TryGetInt32 throws on anything that is not a
        // number, and a monitor that serialised its viewer count as a string would otherwise put
        // every station on the site into a permanent connect-and-drop loop.
        int viewers = message.TryGetProperty("viewers", out JsonElement count)
            && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out int parsed) && parsed > 0
                ? parsed
                : 0;

        int before = Interlocked.Exchange(ref _viewers, viewers);
        if ((before > 0) == (viewers > 0))
        {
            return;
        }

        // Whichever way it went, a half-assembled block belongs to the other side of it.
        Interlocked.Increment(ref _generation);

        // Said in pairs or not at all. Rate-limiting both halves through one gate let a viewer who
        // arrived and left inside a minute leave "1 watching, sending audio" as the last word on
        // the subject, so the journal read as though the station were still sending.
        if (viewers > 0)
        {
            if (DueToSay(ref _viewersSaidAt, ViewersSaidEvery))
            {
                _saidWatching = true;
                Say($"{viewers} watching, sending audio");
            }
        }
        else if (_saidWatching)
        {
            _saidWatching = false;
            Say("nobody watching, audio stopped");
        }
    }

    /// <summary>Pumps the queue onto the socket. One writer of the socket, which is the rule.</summary>
    private async Task SendLoopAsync(
        ClientWebSocket socket, Channel<Outgoing> outgoing, CancellationToken cancellation)
    {
        try
        {
            while (await outgoing.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            {
                while (outgoing.Reader.TryRead(out Outgoing message))
                {
                    try
                    {
                        await socket.SendAsync(
                            message.Payload, message.Type, endOfMessage: true, cancellation)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        message.Return();
                    }
                }

                ReportDrops();
            }
        }
        catch (OperationCanceledException)
        {
            // The session ending. The receive side has the reason and the journal line.
        }
    }

    /// <summary>
    /// Says, at a sane rate, that the send queue is overflowing - which on a station with no
    /// codec means the upload cannot carry the rate its config asked for (4.5's ADSL case). The
    /// operator has one lever and the line names it.
    /// </summary>
    private void ReportDrops()
    {
        long dropped = Interlocked.Read(ref _droppedMessages);
        if (dropped <= Interlocked.Read(ref _droppedSaid) || !DueToSay(ref _dropsSaidAt, DropsSaidEvery))
        {
            return;
        }

        long since = dropped - Interlocked.Exchange(ref _droppedSaid, dropped);
        Say($"{since} blocks dropped: this station's upload cannot carry "
            + $"{_settings.AudioRate} Hz audio. Lower \"publish\".\"audioRate\"");
    }

    /// <summary>Blocks the samples into 40 ms messages and queues each as it fills.</summary>
    private void Append(ReadOnlySpan<float> samples, int kind, Channel<Outgoing> outgoing)
    {
        short[] pending = _pending[kind];
        int fill = _fill[kind];
        foreach (float sample in samples)
        {
            pending[fill++] = Pcm16.FromFloat(sample);
            if (fill < _blockSamples)
            {
                continue;
            }

            Emit(pending, kind, outgoing);
            fill = 0;
        }

        _fill[kind] = fill;
    }

    /// <summary>One audio message: <c>[0x02][kind][2 bytes pad][s16 LE mono]</c> (4.2).</summary>
    private void Emit(short[] block, int kind, Channel<Outgoing> outgoing)
    {
        int length = AudioHeaderBytes + (_blockSamples * 2);
        byte[] payload = ArrayPool<byte>.Shared.Rent(length);
        payload[0] = AudioMessage;
        payload[1] = (byte)kind;
        payload[2] = 0;
        payload[3] = 0;
        for (int i = 0; i < _blockSamples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(AudioHeaderBytes + (i * 2)), block[i]);
        }

        Queue(outgoing, new Outgoing(WebSocketMessageType.Binary, payload, length, Pooled: true));
    }

    /// <summary>Queues a text message, which owns its own array rather than a pooled one.</summary>
    private void Send(Channel<Outgoing> outgoing, WebSocketMessageType type, byte[] payload) =>
        Queue(outgoing, new Outgoing(type, payload, payload.Length, Pooled: false));

    private void Queue(Channel<Outgoing> outgoing, Outgoing message)
    {
        if (!outgoing.Writer.TryWrite(message))
        {
            // The queue is completed: the session has ended under us.
            Interlocked.Increment(ref _droppedMessages);
            message.Return();
        }
    }

    private static void DrainQueue(Channel<Outgoing> outgoing)
    {
        while (outgoing.Reader.TryRead(out Outgoing stale))
        {
            stale.Return();
        }
    }

    /// <summary>One journal line, prefixed as this station's other subsystems are.</summary>
    private void Say(string line) => _log?.Invoke($"publish: {line}");

    /// <summary>
    /// An outage says so at once and then at a sane rate, because it may last all night and the
    /// journal belongs to somebody running a radio.
    /// </summary>
    private void SayOutage(string reason, TimeSpan wait)
    {
        // One gate, always. A welcome used to clear the "said it once" flag, so a site that
        // accepted, welcomed and dropped at 31 s was classified healthy, reconnected a second
        // later and wrote a line every 32 s for ever: about 2700 a day, with the fifteen-minute
        // limit never once applying. Decision 8's standard is a line every fifteen minutes and
        // nothing louder, and this is now that whatever the site does.
        if (!DueToSay(ref _outageSaidAt, OutageSaidEvery))
        {
            return;
        }

        Say(_freshOutage
            ? $"{reason}. Retrying in {Plain(wait)}; the station is unaffected"
            : $"still not publishing: {reason}. Retrying in {Plain(wait)}; the station is "
                + "unaffected");
        _freshOutage = false;
    }

    /// <summary>
    /// A refused token is somebody's mistake to fix rather than a condition that clears itself,
    /// so it is said once an hour and not once a minute.
    /// </summary>
    private void SayRefused(string reason, TimeSpan wait)
    {
        _freshOutage = false;
        if (DueToSay(ref _refusalSaidAt, RefusalSaidEvery))
        {
            Say($"{reason}. Retrying in {Plain(wait)} and saying no more about it for an hour; "
                + "the station is unaffected");
        }
    }

    /// <summary>A wait as an operator would say it, in ASCII.</summary>
    private static string Plain(TimeSpan wait) => wait.TotalSeconds < 60
        ? $"{wait.TotalSeconds:F0} s"
        : $"{wait.TotalMinutes:F0} min";

    /// <summary>Whether enough time has passed to say a repeating thing again.</summary>
    private bool DueToSay(ref long lastAt, TimeSpan gap)
    {
        long previous = Volatile.Read(ref lastAt);
        if (previous != 0 && _time.GetElapsedTime(previous) < gap)
        {
            return false;
        }

        Volatile.Write(ref lastAt, _time.GetTimestamp());
        return true;
    }

    /// <summary>
    /// A string safe to put in the journal. Some of these came off a socket, and journald's pager
    /// under a C locale renders a byte above 0x7F as three hex escapes.
    /// </summary>
    private static string Ascii(string text)
    {
        if (!text.Any(c => c is < ' ' or > '~'))
        {
            return text;
        }

        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            clean.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return clean.ToString();
    }

    /// <summary>
    /// The site's own words, fit to put in this station's journal: ASCII, and short. The site is
    /// trusted to name itself and nothing more, and it may reconnect as often as it likes.
    /// </summary>
    private static string Site(string text) => Ascii(
        text.Length <= MaxSiteTextLength ? text : text[..MaxSiteTextLength] + "...");

    /// <summary>What this daemon is, for the monitor's own diagnostics.</summary>
    private static readonly string DaemonVersion =
        (typeof(UplinkClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UplinkClient).Assembly.GetName().Version?.ToString()
            ?? "unknown").Split('+')[0];

    /// <summary>One queued message and where its array came from.</summary>
    private readonly record struct Outgoing(
        WebSocketMessageType Type, byte[] Buffer, int Length, bool Pooled)
    {
        public ReadOnlyMemory<byte> Payload => Buffer.AsMemory(0, Length);

        public void Return()
        {
            if (Pooled)
            {
                ArrayPool<byte>.Shared.Return(Buffer);
            }
        }
    }
}
