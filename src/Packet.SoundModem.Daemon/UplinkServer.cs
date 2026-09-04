using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// <c>/uplink</c>: the one door a private station comes in by, and everything that has to be true
/// before it is let past.
/// </summary>
/// <remarks>
/// <para><b>This is on the public port.</b> Everything a station sends is untrusted, in exactly
/// the sense the third-party directory's strings are: sizes are capped and enforced before
/// anything is parsed, every string is length-capped at this boundary rather than truncated
/// later, a URL has to be an absolute http or https one, and anything that reaches the journal
/// goes through <see cref="UberSdrDirectory.Ascii"/>. What gets past here is a semi-trusted
/// publisher, and the site vouches for nothing except that the token belongs to that operator.
/// See <c>docs/uplink-plan.md</c> 4.6.</para>
/// <para><b>Nothing is allocated for an unauthenticated connection.</b> The token is checked on
/// the HTTP upgrade, before the WebSocket is accepted and before any station exists; the size of
/// the token table is therefore the cap on how many stations this site can ever hold. A token
/// that does not match costs a fixed delay and a counted journal line, so a guessing run is slow
/// and visible as well as being behind the rate limit in front of the site.</para>
/// <para><b>Strictly one way.</b> Two message types go down this socket - <c>welcome</c>, once,
/// and <c>demand</c>, which carries one integer - and there is no third. Nothing here can key a
/// radio, change a configuration, or make a station do anything but send audio it is already
/// receiving over a socket it opened itself.</para>
/// </remarks>
internal sealed class UplinkServer : IAsyncDisposable
{
    /// <summary>The wire version this monitor speaks. See <c>docs/uplink-plan.md</c> 4.2.</summary>
    /// <remarks>
    /// The protocol spans two machines running whatever versions their operators have installed,
    /// so it is versioned and additive: a station announcing a version this does not know is
    /// refused with a sentence rather than half-understood, and a field this does not know is
    /// ignored rather than fatal.
    /// </remarks>
    internal const int Protocol = 1;

    /// <summary>The most a <c>hello</c> may be. Bigger is a closed connection, not a truncation.</summary>
    internal const int MaxHelloBytes = 8 * 1024;

    /// <summary>The most any other text message may be.</summary>
    internal const int MaxTextBytes = 16 * 1024;

    /// <summary>The most a relayed frame's own bytes may be, decoded.</summary>
    internal const int MaxRawFrameBytes = 2048;

    /// <summary>
    /// The most samples one audio message may carry, which is what a message may carry less its
    /// four-byte header.
    /// </summary>
    /// <remarks>
    /// Derived from the message cap rather than stated beside it, because the two used to be
    /// separate numbers that disagreed: a station could declare a block the hello accepted and
    /// the reader then refused, with a sentence about message sizes rather than about the block
    /// length that caused it. 4.2's own blocks are 40 ms, which is 1920 samples at 48 kHz, so
    /// this is four times more than the format asks for.
    /// </remarks>
    internal const int MaxBlockSamples = (MaxTextBytes - 4) / 2;

    /// <summary>The lowest and highest relayed audio rate this will accept.</summary>
    internal const int MinAudioRate = 6000;

    /// <summary>The highest relayed audio rate this will accept.</summary>
    internal const int MaxAudioRate = 48000;

    /// <summary>Most declared bands on one station, which is the channel's sub-channel count.</summary>
    internal const int MaxBands = 16;

    /// <summary>
    /// How long a connection presenting a token that is not in the table is held before it is
    /// told so.
    /// </summary>
    /// <remarks>
    /// A guessing run at 60 requests per 10 seconds - which is what the rate limit in front of
    /// this site allows - becomes 60 seconds of held sockets for every 60 guesses, against a
    /// 256-bit token. It is not the defence; the token's size is. It is what stops the attempt
    /// being free.
    /// </remarks>
    internal static readonly TimeSpan BadTokenDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a station is refused after it breaks the protocol - an oversized message, an
    /// audio block of the wrong length, a flood.
    /// </summary>
    /// <remarks>
    /// The station's own reconnect ladder treats a refusal as something a person has to fix and
    /// backs off to quarter hours; this is the monitor's half of the same agreement, so a station
    /// with a bug cannot spend the site's time reconnecting every second.
    /// </remarks>
    internal static readonly TimeSpan RefusedFor = TimeSpan.FromMinutes(1);

    /// <summary>How often the viewer count is repeated even when it has not changed.</summary>
    /// <remarks>
    /// Doubles as the heartbeat, which is why it exists at all: neither Cloudflare nor
    /// <c>cloudflared</c> promises to keep an idle WebSocket open, and a message every twenty
    /// seconds is an answer this side controls rather than one it hopes for.
    /// </remarks>
    internal static readonly TimeSpan DemandHeartbeat = TimeSpan.FromSeconds(20);

    /// <summary>How often, at most, a run of bad tokens is mentioned in the journal.</summary>
    private static readonly TimeSpan BadTokenQuiet = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a close frame is given to reach a station before the socket is simply dropped.
    /// </summary>
    /// <remarks>
    /// Every wait on this path is bounded, including the polite ones. A station that has stopped
    /// reading its socket - crashed, suspended, behind a NAT that has forgotten it - must not be
    /// able to hold a send here for ever, because a shutdown or a rebuild waits behind it.
    /// </remarks>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long an authenticated socket has to say <c>hello</c> before it is closed.
    /// </summary>
    /// <remarks>
    /// <para>The token check happens on the upgrade, and until a hello arrives there is no
    /// station, no slug and nothing for the one-connection-per-token rule to apply to. So an
    /// authenticated socket that simply said nothing was held for as long as the process lived,
    /// costing a <c>WebSocket</c>, a semaphore, a linked cancellation source and a read buffer
    /// each, with nothing in the journal about it. The class's claim that the token table caps
    /// how many stations this site can hold was true of stations and not of sockets.</para>
    /// <para>Ten seconds is far longer than a hello takes on any link a station could publish
    /// over, and short enough that a mistake clears itself.</para>
    /// </remarks>
    internal static readonly TimeSpan HelloDeadline = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly UplinkServerOptions _options;
    private readonly StationJournal _journal;
    private readonly IReadOnlyList<UplinkEntry> _entries;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, UplinkSession> _live = new(StringComparer.Ordinal);

    private long _badTokens;
    private long _badTokensSaid;
    private DateTimeOffset _badTokenLine = DateTimeOffset.MinValue;
    private ITimer? _badTokenTail;

    internal UplinkServer(UplinkServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _journal = options.Journal;
        _entries = [.. options.Uplinks.Select(u => new UplinkEntry(
            u.Callsign, u.Slug, Convert.FromHexString(u.TokenSha256)))];
    }

    /// <summary>The stations this site would accept, whether or not any of them is connected.</summary>
    internal IReadOnlyList<UplinkEntry> Entries => _entries;

    /// <summary>
    /// Answers <c>/uplink</c>: checks the token, accepts the socket and runs the session, or
    /// refuses. Returns false for anything that is not this endpoint.
    /// </summary>
    internal async Task<bool> TryServeAsync(HttpListenerContext context)
    {
        if (context.Request.Url?.AbsolutePath != "/uplink")
        {
            return false;
        }

        if (_entries.Count == 0)
        {
            // A site that accepts no uplinks does not have this endpoint, and says so with the
            // same 404 as any other path it does not serve: a monitor with no stations
            // configured should not advertise that it could have some.
            return false;
        }

        if (!context.Request.IsWebSocketRequest)
        {
            await RefuseAsync(context, HttpStatusCode.BadRequest, "uplink: expected a WebSocket")
                .ConfigureAwait(false);
            return true;
        }

        (bool offered, string? presented) = BearerToken(context.Request);
        if (!offered)
        {
            // No delay: this is not a guess, it is a client that did not bring a token at all -
            // a crawler, a health check, somebody reading the plan.
            await RefuseAsync(
                context, HttpStatusCode.Unauthorized,
                "uplink: this endpoint needs the token the site issued, as "
                + "\"Authorization: Bearer ...\"").ConfigureAwait(false);
            return true;
        }

        // A token that was offered and is unusable - empty, or longer than any token this site
        // issues - is still somebody presenting something, so it is delayed and counted like any
        // other wrong one. It used to fall into the arm above and be refused instantly and
        // silently, which made a run of them invisible in the journal.
        if (presented is null || Match(presented) is not { } entry)
        {
            await Task.Delay(BadTokenDelay, _options.Stopping).ConfigureAwait(false);
            NoteBadToken(context);
            await RefuseAsync(context, HttpStatusCode.Unauthorized, "uplink: unknown token")
                .ConfigureAwait(false);
            return true;
        }

        if (RefusedUntil(entry) is { } until)
        {
            await RefuseAsync(
                context, HttpStatusCode.TooManyRequests,
                $"uplink: refused until {until.UtcDateTime:HH:mm:ss} UTC").ConfigureAwait(false);
            return true;
        }

        // One socket per token may be waiting to say hello. After the hello there is a slug and
        // Supersede's newcomer-wins rule takes over; before it there was nothing at all, so one
        // token could open sockets until this process ran out of handles.
        if (Interlocked.Increment(ref entry.Pending) > 1)
        {
            Interlocked.Decrement(ref entry.Pending);
            _journal.WriteError(
                $"uplink: {UberSdrDirectory.Ascii(entry.Callsign)} already has a connection "
                + "waiting to say hello, so this one is refused. A station opens one socket; more "
                + "than one at a time is a bug in whatever is connecting.");
            await RefuseAsync(
                context, HttpStatusCode.TooManyRequests,
                "uplink: a connection from this station is already waiting to say hello")
                .ConfigureAwait(false);
            return true;
        }

        WebSocket socket;
        try
        {
            HttpListenerWebSocketContext accepted = await context
                .AcceptWebSocketAsync(subProtocol: null, DemandHeartbeat)
                .ConfigureAwait(false);
            socket = accepted.WebSocket;
        }
        catch (Exception e) when (e is WebSocketException or HttpListenerException or IOException)
        {
            Interlocked.Decrement(ref entry.Pending);
            return true;   // the upgrade died under us; there is nobody left to tell
        }

        var session = new UplinkSession(this, entry, socket, PublicUrlFor(context.Request));
        await session.RunAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// This site's own public origin, if the upgrade came in under a name worth repeating back.
    /// </summary>
    /// <remarks>
    /// So that a station can journal "publish: live at https://monitor.example/r/gb7rdg-2/" rather
    /// than just its slug. Null where the <c>Host</c> header is a loopback or a bare address,
    /// which is what a tunnel that rewrites the header leaves - CT 146's ingress does exactly
    /// that, deliberately, because this process's listener matches on Host. A guess would be
    /// worse than nothing: the station falls back to naming its slug, and it is the one that
    /// knows the public hostname, because it is in its own config.
    /// </remarks>
    private static string? PublicUrlFor(HttpListenerRequest request)
    {
        if (request.Url?.Host is not { Length: > 0 } host
            || host is "localhost" || IPAddress.TryParse(host, out _))
        {
            return null;
        }

        // HttpListener never yields a ws or wss scheme, so the only two answers are what the
        // proxy in front said and http.
        string scheme = request.Headers["X-Forwarded-Proto"] is "https" ? "https" : "http";
        return $"{scheme}://{request.Url.Authority}";
    }

    /// <summary>The entry this token belongs to, or null.</summary>
    /// <remarks>
    /// Hashed once and compared with <see cref="CryptographicOperations.FixedTimeEquals"/> over
    /// the 32 raw bytes, as the config API's key check does: a byte-at-a-time comparison leaks a
    /// secret one byte at a time. Every entry is compared even after one has matched, so the time
    /// this takes says nothing about which station a token belongs to either.
    /// </remarks>
    private UplinkEntry? Match(string presented)
    {
        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(Encoding.UTF8.GetBytes(presented), hash, out _))
        {
            return null;
        }

        UplinkEntry? found = null;
        foreach (UplinkEntry entry in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, entry.TokenSha256))
            {
                found = entry;
            }
        }

        return found;
    }

    /// <summary>The bearer token on the upgrade, if there is one worth hashing.</summary>
    /// <remarks>
    /// A header rather than a query parameter because a query parameter is written to every log
    /// between here and the station, Cloudflare's included.
    /// </remarks>
    /// <returns>
    /// Whether a bearer credential was offered at all, and the token where it is one this site
    /// could have issued. The two are separate answers: nothing offered is a client that is not
    /// trying, and something unusable offered is a client that is.
    /// </returns>
    private static (bool Offered, string? Token) BearerToken(HttpListenerRequest request)
    {
        if (request.Headers["Authorization"] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        string token = header["Bearer ".Length..].Trim();
        return (true, token.Length is 0 or > 512 ? null : token);
    }

    /// <summary>One line for a run of bad tokens, at most one a minute, with the count.</summary>
    /// <remarks>
    /// The line carries the count accumulated up to it, so a run that stops before the next line
    /// is due used to end with the journal reporting the first attempt and never mentioning the
    /// other two hundred. A timer closes the window instead: whatever the run had reached when it
    /// stopped is said once, when the quiet period is up.
    /// </remarks>
    private void NoteBadToken(HttpListenerContext context)
    {
        long total = Interlocked.Increment(ref _badTokens);
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        string from = context.Request.RemoteEndPoint?.Address?.ToString() ?? "somewhere";
        bool sayNow;
        bool armTail = false;
        lock (_gate)
        {
            sayNow = now - _badTokenLine >= BadTokenQuiet;
            if (sayNow)
            {
                _badTokenLine = now;
                armTail = _badTokenTail is null;
            }
        }

        if (sayNow)
        {
            SayBadTokens(from);
        }

        if (!armTail)
        {
            return;
        }

        // Outside the lock: a TimeProvider holds its own lock while running a callback, and this
        // callback takes ours.
        ITimer tail = _options.TimeProvider.CreateTimer(
            _ => BadTokenTail(from), null, BadTokenQuiet, Timeout.InfiniteTimeSpan);
        ITimer? spare = null;
        lock (_gate)
        {
            if (_badTokenTail is null)
            {
                _badTokenTail = tail;
            }
            else
            {
                spare = tail;
            }
        }

        spare?.Dispose();
    }

    /// <summary>The quiet period is up: say what the run reached, if anything is left to say.</summary>
    private void BadTokenTail(string from)
    {
        ITimer? spent;
        lock (_gate)
        {
            spent = _badTokenTail;
            _badTokenTail = null;
        }

        spent?.Dispose();
        if (Interlocked.Read(ref _badTokens) > Interlocked.Read(ref _badTokensSaid))
        {
            SayBadTokens(from);
        }
    }

    private void SayBadTokens(string from)
    {
        long total = Interlocked.Read(ref _badTokens);
        long since = total - Interlocked.Exchange(ref _badTokensSaid, total);
        if (since <= 0)
        {
            return;
        }

        _journal.WriteError(
            $"uplink: refused {since} connection{(since == 1 ? "" : "s")} presenting a token this "
            + $"site has not issued (most recently from {UberSdrDirectory.Ascii(from)}, {total} "
            + "in all). Nothing is wrong here; a station whose operator says it cannot connect "
            + "has the wrong token, and the site owner issues a new one.");
    }

    private DateTimeOffset? RefusedUntil(UplinkEntry entry)
    {
        lock (_gate)
        {
            return entry.RefusedUntil > _options.TimeProvider.GetUtcNow()
                ? entry.RefusedUntil
                : null;
        }
    }

    /// <summary>
    /// Refuses this station's connections for a while, after it broke the protocol.
    /// </summary>
    internal void Refuse(UplinkEntry entry, string reason)
    {
        lock (_gate)
        {
            entry.RefusedUntil = _options.TimeProvider.GetUtcNow() + RefusedFor;
        }

        _options.Journal.WriteError(
            $"uplink: {UberSdrDirectory.Ascii(entry.Callsign)} {reason}. Not accepting its "
            + $"connections for {RefusedFor.TotalMinutes:F0} minute(s); this is a bug in what it "
            + "is sending rather than something that clears itself.");
    }

    private static async Task RefuseAsync(
        HttpListenerContext context, HttpStatusCode status, string reason)
    {
        try
        {
            byte[] body = Encoding.ASCII.GetBytes(reason + "\n");
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException
                                      or ObjectDisposedException)
        {
            // The client hung up while being told no. There is nothing further to say and
            // nothing here worth a journal line.
        }
    }

    /// <summary>
    /// Registers a session as the one live connection for its station, closing whichever one was
    /// there before.
    /// </summary>
    /// <remarks>
    /// A station whose socket has half-closed - a NAT table entry dropped, a router rebooted -
    /// must not be locked out by its own ghost, so the newcomer wins and the old one is told
    /// exactly why in its close reason.
    /// </remarks>
    private UplinkSession? Supersede(string slug, UplinkSession session)
    {
        lock (_gate)
        {
            _live.Remove(slug, out UplinkSession? previous);
            _live[slug] = session;
            return previous;
        }
    }

    private void Forget(string slug, UplinkSession session)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(slug, out UplinkSession? live) && ReferenceEquals(live, session))
            {
                _live.Remove(slug);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        UplinkSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _live.Values];
            _live.Clear();
        }

        ITimer? tail;
        lock (_gate)
        {
            tail = _badTokenTail;
            _badTokenTail = null;
        }

        tail?.Dispose();

        foreach (UplinkSession session in sessions)
        {
            try
            {
                await session.CloseAsync("the monitor is shutting down")
                    .WaitAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or WebSocketException
                                          or ObjectDisposedException)
            {
                // One station that will not come down tidily must not stop the others being told,
                // and must not stop this process shutting down at all.
            }
        }
    }

    /// <summary>
    /// One station's socket: the reader that turns its messages into audio, frames and a status
    /// sentence, and the writer that sends it a viewer count and nothing else.
    /// </summary>
    private sealed class UplinkSession : IUplinkLink
    {
        private readonly UplinkServer _server;
        private readonly UplinkEntry _entry;
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly CancellationTokenSource _stopping;

        // One buffer per session, rented once and reused for every message, rather than a
        // MemoryStream and an 8 kB chunk per 40 ms block. CLAUDE.md's zero-steady-state-allocation
        // rule is about DSP hot paths, and a watched station's audio is one: 25 messages a second
        // each throwing away 25 kB was 600 kB/s of garbage per station. The station's own client
        // pools the identical path.
        private byte[]? _message;
        private byte[]? _chunk;

        private readonly string? _origin;

        private RelayStation? _station;
        private UplinkHello? _hello;
        private int _demanded = -1;
        private ITimer? _heartbeat;
        private long _bytes;
        private long _windowStart;
        private long _byteAllowance;
        private int _closing;
        private int _helloed;
        private int _pendingCleared;
        private string? _closingBecause;

        internal UplinkSession(
            UplinkServer server, UplinkEntry entry, WebSocket socket, string? origin)
        {
            _server = server;
            _entry = entry;
            _socket = socket;
            _origin = origin;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(server._options.Stopping);
            _windowStart = server._options.TimeProvider.GetTimestamp();
        }

        /// <summary>Reads until the socket ends, one message at a time.</summary>
        internal async Task RunAsync()
        {
            string closing = "closed";
            using ITimer deadline = _server._options.TimeProvider.CreateTimer(
                _ =>
                {
                    if (Volatile.Read(ref _helloed) == 0)
                    {
                        _ = CloseAsync(
                            $"said nothing for {HelloDeadline.TotalSeconds:F0} s; a station says "
                            + "hello first");
                    }
                },
                null, HelloDeadline, Timeout.InfiniteTimeSpan);

            try
            {
                closing = await ReadAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                closing = "the monitor is shutting down";
            }
            catch (Exception e) when (e is WebSocketException or IOException
                                          or ObjectDisposedException)
            {
                closing = $"connection lost ({UberSdrDirectory.Ascii(e.Message)})";
            }
            finally
            {
                // A supersede or a shutdown said why before this loop noticed, and its sentence is
                // the true one: what the loop saw was the close frame that sentence produced.
                closing = _closingBecause ?? closing;
                ClearPending();
                _heartbeat?.Dispose();
                if (_hello is { } hello && _station is { } station)
                {
                    station.Detach(this);
                    station.Journal.Write(
                        $"uplink: {UberSdrDirectory.Ascii(hello.Callsign)} disconnected - "
                        + UberSdrDirectory.Ascii(closing));
                    _server.Forget(station.Slug, this);
                }

                await CloseAsync(closing).ConfigureAwait(false);
                if (_message is { } message)
                {
                    ArrayPool<byte>.Shared.Return(message);
                    _message = null;
                }

                if (_chunk is { } chunk)
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                    _chunk = null;
                }

                _socket.Dispose();
                _stopping.Dispose();
                _sending.Dispose();
            }
        }

        /// <summary>The read loop. Returns the sentence that describes how it ended.</summary>
        private async Task<string> ReadAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                (WebSocketMessageType kind, int? length, string? refusal) =
                    await ReceiveAsync().ConfigureAwait(false);

                if (refusal is not null)
                {
                    return End(new Stop(refusal, Refuse: true));
                }

                if (length is not { } bytes)
                {
                    return "the station closed the connection";
                }

                if (!Allowed(bytes))
                {
                    return End(new Stop(
                        "sent more than twice the rate its own hello declared", Refuse: true));
                }

                Stop? stop = kind == WebSocketMessageType.Binary
                    ? OnBinary(_message.AsSpan(0, bytes))
                    : await OnTextAsync(_message.AsMemory(0, bytes)).ConfigureAwait(false);
                if (stop is { } ending)
                {
                    return End(ending);
                }
            }

            return "the monitor is shutting down";
        }

        /// <summary>
        /// Says why this connection is over, and applies the backoff where it is deserved.
        /// </summary>
        /// <remarks>
        /// Two kinds of ending, and they are not the same thing. Breaking a size or a rate cap is
        /// a bug in what the station is sending, so its token is refused for a while and its own
        /// reconnect ladder treats that as something a person has to fix. Everything else - a
        /// mismatched callsign, a protocol version, a goodbye - is one closed connection and one
        /// journal line, because a station that reconnects having fixed it should be let straight
        /// back in.
        /// </remarks>
        private string End(Stop stop)
        {
            if (stop.Refuse)
            {
                _server.Refuse(_entry, stop.Reason);
            }
            else
            {
                _server._journal.Write(
                    $"uplink: {UberSdrDirectory.Ascii(_entry.Callsign)} "
                    + UberSdrDirectory.Ascii(stop.Reason));
            }

            return stop.Reason;
        }

        /// <summary>
        /// One whole message, reassembled on <c>EndOfMessage</c> and capped before it is parsed.
        /// </summary>
        /// <returns>
        /// The message, or a null payload for a clean close, or a refusal sentence for a message
        /// that broke a cap.
        /// </returns>
        private async Task<(WebSocketMessageType Kind, int? Length, string? Refusal)> ReceiveAsync()
        {
            // Capped before a byte of it is parsed, and the cap is the message's own limit rather
            // than the buffer's: an accumulator that grows to whatever arrives is the one
            // unbounded WebSocket buffer this tree already has, and it is not being copied here.
            int cap = _hello is null ? MaxHelloBytes : MaxTextBytes;
            _message ??= ArrayPool<byte>.Shared.Rent(MaxTextBytes);
            _chunk ??= ArrayPool<byte>.Shared.Rent(8192);
            int at = 0;
            WebSocketMessageType kind = WebSocketMessageType.Text;

            while (true)
            {
                WebSocketReceiveResult result = await _socket
                    .ReceiveAsync(_chunk, _stopping.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (kind, null, null);   // null length is a clean close, not an empty one
                }

                kind = result.MessageType;
                if (at + result.Count > cap)
                {
                    return (kind, null,
                        $"sent a message over {cap} bytes, which this does not accept");
                }

                _chunk.AsSpan(0, result.Count).CopyTo(_message.AsSpan(at));
                at += result.Count;
                if (result.EndOfMessage)
                {
                    return (kind, at, null);
                }
            }
        }

        /// <summary>
        /// Whether this station is inside the rate it declared. Twice the bitrate its own
        /// <c>hello</c> asked for, plus room for frames, measured over ten seconds.
        /// </summary>
        private bool Allowed(int bytes)
        {
            long allowance = Interlocked.Read(ref _byteAllowance);
            if (allowance <= 0)
            {
                return true;   // before the hello, the message cap is the only limit that applies
            }

            TimeProvider time = _server._options.TimeProvider;
            long start = Interlocked.Read(ref _windowStart);
            if (time.GetElapsedTime(start) >= TimeSpan.FromSeconds(10))
            {
                Interlocked.Exchange(ref _windowStart, time.GetTimestamp());
                Interlocked.Exchange(ref _bytes, 0);
            }

            return Interlocked.Add(ref _bytes, bytes) <= allowance;
        }

        /// <summary>A binary message, which is audio and can be nothing else.</summary>
        private Stop? OnBinary(ReadOnlySpan<byte> payload)
        {
            if (_hello is not { } hello || _station is not { } station)
            {
                return new Stop("sent audio before its hello", Refuse: true);
            }

            // Exactly the length its hello declared, checked before a sample is read. Nothing in
            // this payload is decoded, so there is nothing here for a malformed one to reach.
            int expected = 4 + (2 * hello.BlockSamples);
            if (payload.Length != expected)
            {
                return new Stop(
                    $"sent an audio message of {payload.Length} bytes where its hello declared "
                    + $"{expected}", Refuse: true);
            }

            if (payload[0] != 0x02)
            {
                return new Stop(
                    $"sent a binary message of type {payload[0]}, and 0x02 (audio) is the only "
                    + "one there is", Refuse: true);
            }

            // The samples straight off the wire, with nothing allocated in between: the input
            // reads them little-endian as it converts.
            station.PushAudio(payload[4..], payload[1] != 0);
            return null;
        }

        /// <summary>A text message: the hello, a frame, a status sentence, or a goodbye.</summary>
        private async Task<Stop?> OnTextAsync(ReadOnlyMemory<byte> payload)
        {
            JsonElement root;
            try
            {
                // Cloned, because the buffer it was parsed out of is the pooled one and is about
                // to be reused for the next message.
                using JsonDocument document = JsonDocument.Parse(payload);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return new Stop("sent a text message that is not JSON", Refuse: true);
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String)
            {
                return new Stop("sent a message with no \"type\"", Refuse: true);
            }

            switch (type.GetString())
            {
                case "hello":
                    return await OnHelloAsync(root).ConfigureAwait(false);

                case "frame":
                    // Dropped rather than fatal: one frame that will not read is one row missing
                    // from a page, and closing the socket over it would cost the station's whole
                    // listing for the sake of a field.
                    if (_station is { } forFrame
                        && UplinkWire.ReadFrame(root, _server._options.TimeProvider.GetUtcNow())
                            is { } frame)
                    {
                        forFrame.PushFrame(frame);
                    }

                    return null;

                case "radio":
                    if (_station is { } forRadio)
                    {
                        forRadio.PushRadio(UplinkWire.Capped(root, "status", 200));
                    }

                    return null;

                case "bye":
                    string? why = UplinkWire.Capped(root, "reason", 200);
                    return new Stop(
                        why is { Length: > 0 } ? UberSdrDirectory.Ascii(why) : "said goodbye",
                        Refuse: false);

                default:
                    // Counted by being ignored. A message type this version does not know is what
                    // an additive protocol looks like from the older end, and there is nothing
                    // here a station could name that would do anything.
                    return null;
            }
        }

        private async Task<Stop?> OnHelloAsync(JsonElement root)
        {
            if (_hello is not null)
            {
                return new Stop("sent a second hello on one connection", Refuse: false);
            }

            if (!UplinkWire.TryReadHello(
                    root, _entry, _server._options.DisplayLineRate,
                    out UplinkHello? hello, out string? why))
            {
                // Closed with the sentence and no backoff: a mismatched callsign or a protocol
                // version is something a person fixes and reconnects, and refusing the token for
                // a quarter of an hour would punish them for having noticed.
                await SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new { type = "refused", reason = why }, Json)).ConfigureAwait(false);
                return new Stop(why!, Refuse: false);
            }

            _hello = hello;
            Volatile.Write(ref _helloed, 1);
            ClearPending();

            // Twice the bitrate its own hello declared, over ten seconds, plus 16 kB of headroom
            // for the frames and the status sentences that ride the same socket.
            long perSecond = (2L * hello!.AudioRate) + (hello.BlockSamples > 0
                ? 4L * hello.AudioRate / hello.BlockSamples
                : 0);
            Interlocked.Exchange(ref _byteAllowance, (10 * 2 * perSecond) + (16 * 1024));
            Interlocked.Exchange(ref _windowStart, _server._options.TimeProvider.GetTimestamp());
            Interlocked.Exchange(ref _bytes, 0);

            RelayStation station;
            try
            {
                station = await _server._options.Station(_entry, hello).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // e.Message, never e.ToString(). A station chooses what it says, and a public
                // site's journal should not carry a stack trace with this process's own source
                // paths in it because somebody sent an awkward number. Everything a station can
                // actually get wrong is refused above, by name; anything reaching here is this
                // site's own bug, and the one line says so.
                _server._journal.WriteError(
                    $"uplink: could not build a station for "
                    + $"{UberSdrDirectory.Ascii(_entry.Callsign)} - "
                    + $"{e.GetType().Name}: {UberSdrDirectory.Ascii(e.Message)}. This is a fault "
                    + "in this site rather than in what the station sent.");
                return new Stop("this site could not build a station for it", Refuse: false);
            }

            _station = station;

            // The newcomer wins, and the ghost is told why. Superseded before the welcome, so
            // there is never a moment when two sockets both believe they are the live one.
            UplinkSession? previous = _server.Supersede(station.Slug, this);
            if (previous is not null)
            {
                await previous.CloseAsync(
                    "another connection authenticated with this station's token; if that was not "
                    + "you, tell the site owner").ConfigureAwait(false);
            }

            station.Attach(this);
            station.Journal.Write(
                $"uplink: {UberSdrDirectory.Ascii(hello.Callsign)} connected"
                + (hello.Daemon is { Length: > 0 } daemon
                    ? $" (pdn-soundmodem {UberSdrDirectory.Ascii(daemon)})"
                    : "")
                + $", relaying {hello.AudioRate} Hz audio to /r/{station.Slug}/");

            await SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        type = "welcome",
                        protocol = Protocol,
                        slug = station.Slug,
                        path = $"/r/{station.Slug}/",
                        // Only where the upgrade arrived under a name worth repeating back. A
                        // tunnel that rewrites the Host header leaves this null, and the station
                        // names its slug instead: a guessed URL in somebody's journal is worse
                        // than none, and the station is the end that knows the public hostname.
                        url = _origin is null ? null : $"{_origin}/r/{station.Slug}/",
                    }, Json)).ConfigureAwait(false);

            // Straight away, so a station that connects while somebody is already watching starts
            // sending without waiting for the first heartbeat.
            station.Announce(this);
            _heartbeat = _server._options.TimeProvider.CreateTimer(
                _ => station.Announce(this), null, DemandHeartbeat, DemandHeartbeat);
            return null;
        }

        /// <inheritdoc />
        public void Demand(int viewers)
        {
            // Sent on every change and at least every heartbeat, so a station that missed one
            // still finds out; the repeat is what makes this the keepalive as well.
            Interlocked.Exchange(ref _demanded, viewers);
            _ = SendAsync(JsonSerializer.SerializeToUtf8Bytes(
                new { type = "demand", viewers }, Json));
        }

        /// <inheritdoc />
        public void Close(string reason) => _ = CloseAsync(reason);

        /// <summary>
        /// Sends the close frame, once, with the first reason anybody gave.
        /// </summary>
        /// <remarks>
        /// <para><c>CloseOutputAsync</c> rather than <c>CloseAsync</c>, and no cancellation
        /// anywhere near it. Cancelling the token a <c>ReceiveAsync</c> is waiting on makes .NET
        /// abort the WebSocket, which is a TCP reset: the station's operator would read "the
        /// remote party closed the connection without completing the close handshake" instead of
        /// the sentence saying what happened, which is the one thing a close reason is for.</para>
        /// <para>The socket is disposed by <see cref="RunAsync"/> and by nothing else, so a close
        /// asked for from another connection's thread cannot pull it out from under the loop that
        /// owns it. If the station never answers the close frame, the deadline below aborts the
        /// read rather than leaving it there for ever.</para>
        /// </remarks>
        internal async Task CloseAsync(string reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) == 1)
            {
                return;
            }

            _closingBecause = reason;
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Truncated to what a close frame can carry (123 bytes), and ASCII, because
                    // it is read on somebody else's console. Bounded, because a station that has
                    // stopped reading must not hold this send open.
                    string ascii = UberSdrDirectory.Ascii(reason);
                    using var giveUp = new CancellationTokenSource(CloseTimeout);
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        ascii.Length > 120 ? ascii[..120] : ascii,
                        giveUp.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException
                                          or ObjectDisposedException or OperationCanceledException)
            {
                // Closing a socket that has already gone, or one that would not take the frame
                // inside the timeout. Either way it is going.
            }

            try
            {
                _stopping.CancelAfter(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                // The loop this would have woken has already come down of its own accord.
            }
        }

        /// <summary>
        /// Gives this token's one waiting-to-say-hello slot back, once and once only.
        /// </summary>
        /// <remarks>
        /// Called both when a hello arrives and when the session ends, because either may come
        /// first and neither may leave the slot held. A hello that is read and then refused - a
        /// mismatched callsign, an unusable rate - still gives the slot back, so an operator
        /// correcting their config is not locked out by their own last attempt.
        /// </remarks>
        private void ClearPending()
        {
            if (Interlocked.Exchange(ref _pendingCleared, 1) == 0)
            {
                Interlocked.Decrement(ref _entry.Pending);
            }
        }

        /// <summary>Why a connection is ending, and whether the station's token pays for it.</summary>
        private readonly record struct Stop(string Reason, bool Refuse);

        private async Task SendAsync(byte[] message)
        {
            try
            {
                await _sending.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;   // a heartbeat that fired as this connection was going down
            }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var giveUp = new CancellationTokenSource(CloseTimeout);
                    await _socket.SendAsync(
                        message, WebSocketMessageType.Text, endOfMessage: true,
                        giveUp.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException
                                          or ObjectDisposedException or OperationCanceledException)
            {
                // The station has gone, or has stopped reading. The read loop is about to find
                // that out and say so once.
            }
            finally
            {
                try
                {
                    _sending.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}

/// <summary>
/// The monitor's end of one station's socket, as the station's own listing sees it: a viewer
/// count down, and the ability to hang up. There is no third thing, and that is the design.
/// </summary>
internal interface IUplinkLink
{
    /// <summary>Tells the station how many people are watching it.</summary>
    void Demand(int viewers);

    /// <summary>Hangs up, with a sentence the station can put in its journal.</summary>
    void Close(string reason);
}

/// <summary>One configured station, and what this process knows about it at runtime.</summary>
internal sealed class UplinkEntry(string callsign, string slug, byte[] tokenSha256)
{
    /// <summary>The callsign its hello has to match.</summary>
    internal string Callsign { get; } = callsign;

    /// <summary>The path segment its page is served under. Never comes off the wire.</summary>
    internal string Slug { get; } = slug;

    /// <summary>The 32 raw bytes of the token's SHA-256.</summary>
    internal byte[] TokenSha256 { get; } = tokenSha256;

    /// <summary>When this station's connections stop being refused, after it broke the
    /// protocol.</summary>
    internal DateTimeOffset RefusedUntil { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Sockets authenticated on this token that have not yet said hello. At most one, which is
    /// the cap that did not exist before a hello arrived. A field rather than a property because
    /// it is only ever touched through <see cref="Interlocked"/>.
    /// </summary>
    internal int Pending;
}

/// <summary>What an <see cref="UplinkServer"/> needs to answer <c>/uplink</c>.</summary>
internal sealed record UplinkServerOptions
{
    /// <summary>The stations this site will accept, from <c>monitor.uplinks</c>.</summary>
    public required IReadOnlyList<UplinkConfig> Uplinks { get; init; }

    /// <summary>Where this endpoint's own lines go. Station lines go to the station's journal.</summary>
    public required StationJournal Journal { get; init; }

    /// <summary>
    /// The station a hello belongs to, built if this is the first time it has connected and kept
    /// for the life of the process afterwards.
    /// </summary>
    public required Func<UplinkEntry, UplinkHello, Task<RelayStation>> Station { get; init; }

    /// <summary>
    /// The display line rate this site would draw a given relayed audio rate at, or 0 for one it
    /// cannot draw at all. Asked at the boundary so that a rate this site cannot use is a
    /// sentence about rates rather than an exception from inside a constructor.
    /// </summary>
    public required Func<int, int> DisplayLineRate { get; init; }

    /// <summary>The clock the delays, the heartbeat and the rate window run on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Cancelled when the site is shutting down.</summary>
    public CancellationToken Stopping { get; init; }
}
