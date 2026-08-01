using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Waterfall;

/// <summary>Waterfall server tunables (the daemon's <c>waterfall</c> config section).</summary>
public sealed class WaterfallOptions
{
    /// <summary>Rig dial (suppressed-carrier) frequency in Hz, the default the page opens
    /// with — each browser can retune its own copy. 0 = not set: the page shows audio
    /// frequencies only until the operator enters a dial.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>"usb" (RF = dial + audio) or "lsb" (RF = dial − audio); the page default.</summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Waterfall line rate (display frame rate). Default 30.</summary>
    public int LinesPerSecond { get; set; } = 30;

    /// <summary>FFT length; 0 picks the rate default (2048 at 12 kHz, 8192 at 48 kHz).</summary>
    public int FftSize { get; set; }
}

/// <summary>One modem's display band, measured off its own modulator at start-up.</summary>
/// <param name="SubChannel">KISS sub-channel.</param>
/// <param name="Mode">Mode name.</param>
/// <param name="LowHz">Measured 99 % occupied-bandwidth low edge.</param>
/// <param name="HighHz">Measured 99 % occupied-bandwidth high edge.</param>
/// <param name="CentreHz">Band midpoint.</param>
public readonly record struct ModemBand(int SubChannel, string Mode, double LowHz, double HighHz, double CentreHz);

/// <summary>
/// The browser waterfall: an HTTP server (single embedded page, no external assets) plus a
/// WebSocket feed of display-rate spectrum lines and per-frame decode events. The page
/// draws the shared audio passband as a spectrum view over a scrolling waterfall, overlays
/// every configured modem's measured band (audio offset and rig-dial-derived RF centre),
/// and tags each decoded frame's energy burst with its source callsign, band SNR and
/// frequency offset — so a burst on screen reads directly as "who".
/// </summary>
/// <remarks>
/// Modem bands are measured, not tabulated: at <see cref="Start"/> each modem modulates a
/// short throwaway frame and the ITU-R SM.443 99 % occupied bandwidth of that audio is the
/// band the page shades. A new mode gets a correct overlay with no table to maintain.
/// Per-frame SNR/extent come from <see cref="BandActivityTracker"/> over the same lines the
/// display draws. Call <see cref="Start"/> before audio flows (it registers the channel
/// receive tap); received-side only — during transmit the display pauses, as half-duplex
/// hearing does.
/// </remarks>
public sealed class WaterfallWebServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SoundModemChannel _channel;
    private readonly WaterfallOptions _options;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<int, BandActivityTracker> _trackers = [];
    private readonly List<ModemBand> _bands = [];
    private readonly object _clientsLock = new();
    private readonly List<Channel<(WebSocketMessageType Kind, byte[] Payload)>> _clients = [];
    private WaterfallSource? _source;
    private byte[] _configMessage = [];
    private Task? _acceptLoop;

    /// <summary>Creates a server for <paramref name="channel"/>'s audio on
    /// <paramref name="port"/>.</summary>
    /// <param name="channel">The channel whose audio and decodes feed the display.</param>
    /// <param name="port">HTTP listen port.</param>
    /// <param name="options">Display defaults; null = defaults.</param>
    /// <param name="bind">Bind address; "*" listens on all interfaces.</param>
    public WaterfallWebServer(SoundModemChannel channel, int port, WaterfallOptions? options = null, string bind = "127.0.0.1")
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _options = options ?? new WaterfallOptions();
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{(bind == "*" ? "+" : bind)}:{port}/");
        Url = $"http://{(bind == "*" ? "127.0.0.1" : bind)}:{port}/";
    }

    /// <summary>The listen port.</summary>
    public int Port { get; }

    /// <summary>A URL the page is reachable at.</summary>
    public string Url { get; }

    /// <summary>The measured per-modem display bands (populated by <see cref="Start"/>).</summary>
    public IReadOnlyList<ModemBand> Bands => _bands;

    /// <summary>
    /// Measures every modem's band, hooks the channel (receive tap + frame events) and
    /// starts listening. Call before audio flows and after all modems are added.
    /// </summary>
    public void Start()
    {
        var source = new WaterfallSource(
            _channel.SampleRate, OnLine, _options.LinesPerSecond, _options.FftSize);
        _source = source;

        foreach ((int sub, IModem modem) in _channel.Modems.OrderBy(m => m.Key))
        {
            if (TryMeasureBand(sub, modem, _channel.SampleRate, out ModemBand band))
            {
                _bands.Add(band);
                _trackers[sub] = new BandActivityTracker(
                    source.BinWidthHz, source.LinesPerSecond, source.LineLength,
                    band.LowHz, band.HighHz);
            }
        }

        _configMessage = BuildConfigMessage();
        _channel.AddReceiveTap(samples => source.Process(samples));
        _channel.FrameReceivedWithQuality += OnFrame;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>
    /// Measures the 99 % occupied bandwidth of <paramref name="modem"/>'s own transmit
    /// audio (a throwaway frame, preamble skipped). False when the modem cannot render the
    /// probe frame — that modem's overlay is simply omitted.
    /// </summary>
    internal static bool TryMeasureBand(int subChannel, IModem modem, int sampleRate, out ModemBand band)
    {
        band = default;
        // A representative little UI frame: PDNSM>PDNSM with a short payload — enough
        // symbols for a stable Welch estimate in every mode.
        var probe = BuildProbeFrame();
        float[] audio;
        try
        {
            audio = modem.Modulate(probe, txDelayMilliseconds: 60);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Skip the leading third: preamble/training, which some modes shape differently
        // from steady-state modulation (the OBW meter wants steady state).
        int skip = audio.Length / 3;
        if (audio.Length - skip < 2048)
        {
            return false;
        }

        var (low, high, _, _) = OccupiedBandwidth.Measure(
            audio.AsSpan(skip), sampleRate, fftSize: sampleRate >= 24000 ? 4096 : 1024);
        band = new ModemBand(subChannel, modem.Mode, low, high, (low + high) / 2);
        return true;
    }

    private static byte[] BuildProbeFrame()
    {
        var frame = new byte[48];
        WriteAddress(frame, 0, "PDNSM", last: false);
        WriteAddress(frame, 7, "PDNSM", last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        for (int n = 16; n < frame.Length; n++)
        {
            frame[n] = (byte)(n * 37); // arbitrary non-repeating payload
        }

        return frame;

        static void WriteAddress(byte[] frame, int at, string call, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }

    private byte[] BuildConfigMessage()
    {
        WaterfallSource source = _source!;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "config",
            sampleRate = _channel.SampleRate,
            binWidthHz = source.BinWidthHz,
            lineLength = source.LineLength,
            linesPerSecond = source.LinesPerSecond,
            dialHz = _options.DialFrequencyHz,
            sideband = _options.Sideband,
            modems = _bands.Select(b => new
            {
                sub = b.SubChannel,
                mode = b.Mode,
                lowHz = Math.Round(b.LowHz, 1),
                highHz = Math.Round(b.HighHz, 1),
                centreHz = Math.Round(b.CentreHz, 1),
            }),
        }, Json);
    }

    /// <summary>Line sink (receive thread): feed the SNR trackers, then fan the line out
    /// to every client as [0x01][u32 LE line index][bins].</summary>
    private void OnLine(long index, ReadOnlyMemory<byte> line)
    {
        ReadOnlySpan<byte> bins = line.Span;
        foreach (BandActivityTracker tracker in _trackers.Values)
        {
            tracker.AddLine(bins);
        }

        bool anyClients;
        lock (_clientsLock)
        {
            anyClients = _clients.Count > 0;
        }

        if (!anyClients)
        {
            return;
        }

        var message = new byte[5 + bins.Length];
        message[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), (uint)index);
        bins.CopyTo(message.AsSpan(5));
        Broadcast(WebSocketMessageType.Binary, message);
    }

    /// <summary>Frame event (receive thread): attribute the just-decoded frame to its burst
    /// — callsigns off the frame, SNR/extent off the band tracker, offset off the winning
    /// decoder branch — and fan it out as JSON.</summary>
    private void OnFrame(int subChannel, byte[] frame, FrameQuality quality)
    {
        double? snrDb = null;
        int? burstLines = null;
        if (_trackers.TryGetValue(subChannel, out BandActivityTracker? tracker)
            && tracker.TryMeasureBurst(out double snr, out int lines))
        {
            snrDb = Math.Round(snr, 1);
            burstLines = lines;
        }

        string? from = null;
        string? to = null;
        if (Ax25AddressParser.TryParse(frame, out string source, out string destination))
        {
            from = source;
            to = destination;
        }

        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "frame",
            line = _source!.NextLineIndex,
            sub = subChannel,
            mode = quality.Mode,
            from,
            to,
            lenBytes = quality.FrameBytes,
            snrDb,
            burstLines,
            offsetHz = quality.FrequencyOffsetHz is { } offset ? Math.Round(offset, 1) : (double?)null,
            corrected = quality.CorrectedBytes,
            crc = quality.CrcValid,
        }, Json);
        Broadcast(WebSocketMessageType.Text, message);
    }

    private void Broadcast(WebSocketMessageType kind, byte[] payload)
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Writer.TryWrite((kind, payload));
            }
        }
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

            _ = ServeAsync(context);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext upgrade = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await ServeWebSocketAsync(upgrade.WebSocket).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" &&
                context.Request.Url?.AbsolutePath is "/" or "/index.html")
            {
                byte[] page = LoadPage();
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = page.Length;
                await context.Response.OutputStream.WriteAsync(page).ConfigureAwait(false);
                context.Response.Close();
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

    private async Task ServeWebSocketAsync(WebSocket socket)
    {
        // Bounded per-client queue, oldest lines dropped: a stalled browser loses history,
        // never stalls the receive thread or other clients.
        var queue = System.Threading.Channels.Channel.CreateBounded<(WebSocketMessageType, byte[])>(
            new BoundedChannelOptions(_options.LinesPerSecond)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        lock (_clientsLock)
        {
            _clients.Add(queue);
        }

        try
        {
            await socket.SendAsync(_configMessage, WebSocketMessageType.Text, true, _stopping.Token)
                .ConfigureAwait(false);
            Task send = SendLoopAsync(socket, queue);
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
            {
                WebSocketReceiveResult received =
                    await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }

            queue.Writer.TryComplete();
            await send.ConfigureAwait(false);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A vanished client is normal shutdown for its connection, nothing more.
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(queue);
            }

            queue.Writer.TryComplete();
            socket.Dispose();
        }
    }

    private async Task SendLoopAsync(WebSocket socket, Channel<(WebSocketMessageType Kind, byte[] Payload)> queue)
    {
        try
        {
            await foreach ((WebSocketMessageType kind, byte[] payload) in
                queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                await socket.SendAsync(payload, kind, true, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static byte[] LoadPage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("waterfall.html", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Stops listening and drops every client.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _channel.FrameReceivedWithQuality -= OnFrame;
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

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Writer.TryComplete();
            }

            _clients.Clear();
        }

        _stopping.Dispose();
    }
}
