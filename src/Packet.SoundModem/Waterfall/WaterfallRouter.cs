using System.Net;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// One listener on one port in front of several <see cref="WaterfallWebServer"/>s, each serving
/// its own receiver's page under its own path prefix - <c>/r/m9psy-1/</c>, <c>/r/g4eyr/</c> - plus
/// a front door for everything no prefix claims.
/// </summary>
/// <remarks>
/// <para>A site that offers fifty receivers is one hostname, one tunnel and one port. Giving every
/// receiver a listener of its own would mean fifty sockets to bind, a reverse proxy in front of
/// them and a proxy hop for every waterfall line and audio packet, so the port moves out here
/// instead: the router owns the one thing that has to be single, and each server keeps everything
/// that is genuinely its own.</para>
/// <para>The router serves the servers; it does not own them. Whoever created a server calls
/// <see cref="WaterfallWebServer.Start"/> on it (the band probe and the channel subscriptions are
/// the station's own work, and have to be done before its first browser arrives) and disposes it
/// afterwards. Disposing the router stops the listening, nothing else.</para>
/// <para>A station that is its own site - the single-receiver deployment - needs none of this and
/// is untouched by it: it keeps its own listener and its own port, and its pages are served at
/// the base "/" exactly as they always were.</para>
/// </remarks>
public sealed class WaterfallRouter : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();

    // Read on every request and written when a receiver is first picked, which in the many-receiver
    // flavour happens while the accept loop is running. Copy-on-write under the lock, so a reader
    // holds a whole snapshot and never a half-updated table.
    private readonly object _routesLock = new();
    private volatile KeyValuePair<string, WaterfallWebServer>[] _routes = [];
    private Task? _acceptLoop;

    /// <summary>Creates a front door on <paramref name="port"/>.</summary>
    /// <param name="port">HTTP listen port, the one port the whole site answers on.</param>
    /// <param name="bind">Bind address; "*" listens on all interfaces.</param>
    public WaterfallRouter(int port, string bind = "127.0.0.1")
    {
        Port = port;
        _listener = new HttpListener();
        // The same spelling translation the single-station server does, for the same reason: the
        // daemon has one bind setting and HttpListener wants "+" where a TcpListener wants 0.0.0.0.
        bool everyInterface = bind is "*" or "0.0.0.0" or "::" or "[::]";
        _listener.Prefixes.Add($"http://{(everyInterface ? "+" : bind)}:{port}/");
        Url = $"http://{(everyInterface ? "127.0.0.1" : bind)}:{port}/";
    }

    /// <summary>The listen port.</summary>
    public int Port { get; }

    /// <summary>A URL the site is reachable at.</summary>
    public string Url { get; }

    /// <summary>
    /// Handles anything no registered prefix claims - the front page at <c>/</c> and the site's
    /// own API - returning true if it dealt with the request. Null (the default) leaves every
    /// such path a 404.
    /// </summary>
    /// <remarks>
    /// The same seam, and for the same reason, as <see cref="WaterfallWebServer.ApiHandler"/>:
    /// what a list of receivers is and how it is chosen belongs to the host that reads the
    /// directory, not to a library that draws waterfalls.
    /// </remarks>
    public Func<HttpListenerContext, Task<bool>>? FrontDoor { get; set; }

    /// <summary>
    /// Puts <paramref name="server"/> under <paramref name="pathBase"/>, from now on. The base
    /// starts and ends with a slash: "/r/m9psy-1/".
    /// </summary>
    /// <remarks>
    /// <para>Call <see cref="WaterfallWebServer.Start"/> on the server first. A server that has
    /// not started has measured no bands and has no config message to hand a browser, and the
    /// first request can arrive on the next line.</para>
    /// <para>Registration is what tells a routed server where it is, so that it can say so: its
    /// <see cref="WaterfallWebServer.Url"/> and <see cref="WaterfallWebServer.Port"/> are this
    /// router's while it is registered, and empty and zero either side of that. One server serves
    /// one receiver under one base, so registering the same server twice is refused rather than
    /// left to say whichever of the two it was told last.</para>
    /// </remarks>
    public void Add(string pathBase, WaterfallWebServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        WaterfallWebServer.ValidatePathBase(pathBase, nameof(pathBase));

        lock (_routesLock)
        {
            if (Array.Exists(_routes, r => r.Key == pathBase))
            {
                throw new ArgumentException($"{pathBase} is already served", nameof(pathBase));
            }

            if (Array.Exists(_routes, r => ReferenceEquals(r.Value, server)))
            {
                throw new ArgumentException("that server is already served under another base", nameof(server));
            }

            _routes = [.. _routes, new KeyValuePair<string, WaterfallWebServer>(pathBase, server)];
        }

        server.ServedAt(Port, Url + pathBase[1..]);
    }

    /// <summary>
    /// Stops serving <paramref name="pathBase"/>. True if it was being served. The server it was
    /// serving is left alone, apart from being told it is nowhere: a URL it no longer answers on
    /// is worse than none at all.
    /// </summary>
    public bool Remove(string pathBase)
    {
        WaterfallWebServer? removed = null;
        lock (_routesLock)
        {
            foreach (KeyValuePair<string, WaterfallWebServer> route in _routes)
            {
                if (route.Key == pathBase)
                {
                    removed = route.Value;
                }
            }

            if (removed is null)
            {
                return false;
            }

            _routes = Array.FindAll(_routes, r => r.Key != pathBase);
        }

        removed.ServedAt(0, "");
        return true;
    }

    /// <summary>Starts listening. Servers can be added before or after.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            // Not awaited: a WebSocket upgrade is served for as long as the browser keeps it open,
            // and this loop has the rest of the site to accept in the meantime.
            _ = DispatchAsync(context);
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        try
        {
            string? path = context.Request.Url?.AbsolutePath;
            if (path is not null
                && Match(path) is { } route
                && await route.Value.TryServeAsync(context, route.Key).ConfigureAwait(false))
            {
                return;
            }

            // A prefix typed without its trailing slash. The page works out what its socket and
            // its links are relative to from its own path, so /r/m9psy-1 would leave it hanging
            // them off /r/ and connecting to nothing at all. One redirect saves a page that would
            // otherwise load and then sit there dead. A GET or a HEAD only: this is here for an
            // address somebody typed, and redirecting anything else would invite a client to
            // repeat a request that was never going to be served.
            if (path is not null
                && context.Request.HttpMethod is "GET" or "HEAD"
                && Match(path + "/") is not null)
            {
                context.Response.Redirect(path + "/" + context.Request.Url?.Query);
                context.Response.Close();
                return;
            }

            if (FrontDoor is { } front && await front(context).ConfigureAwait(false))
            {
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// The registered base this path is under, longest first - so a base nested inside another
    /// gets its own requests rather than its parent's, however they were registered.
    /// </summary>
    private KeyValuePair<string, WaterfallWebServer>? Match(string path)
    {
        KeyValuePair<string, WaterfallWebServer>? best = null;
        foreach (KeyValuePair<string, WaterfallWebServer> route in _routes)
        {
            if (path.StartsWith(route.Key, StringComparison.Ordinal)
                && (best is null || route.Key.Length > best.Value.Key.Length))
            {
                best = route;
            }
        }

        return best;
    }

    /// <summary>Stops listening. The servers it was serving are the caller's to dispose.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _stopping.Dispose();
    }
}
