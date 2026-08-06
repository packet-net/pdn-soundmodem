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
    /// with - each browser can retune its own copy. 0 = not set: the page shows audio
    /// frequencies only until the operator enters a dial.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>"usb" (RF = dial + audio) or "lsb" (RF = dial − audio); the page default.</summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Waterfall line rate (display frame rate). Default 30.</summary>
    public int LinesPerSecond { get; set; } = 30;

    /// <summary>FFT length; 0 picks the rate default (2048 at 12 kHz, 8192 at 48 kHz).</summary>
    public int FftSize { get; set; }

    /// <summary>Clock used to pace our own transmissions onto the display. Injected for tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// What the host says each modem is meant to occupy, by sub-channel. Two things the
    /// waterfall cannot work out for itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The centre.</b> Probing gives measured band edges, and their midpoint is a few
    /// Hz off the centre the operator asked for - enough that a modem placed at 7051600 renders
    /// as 7.05159 and reads like a bug. The configured centre is the truth for a label; the
    /// measured edges remain the truth for the drawn band.</para>
    /// <para><b>Bands that cannot be probed.</b> ARDOP is not an <see cref="IModem"/> - it is a
    /// receive tap with its own transmitter - so nothing enumerable carries it and it was
    /// simply absent from the display. Declared here, it is drawn from its centre and width
    /// like anything else.</para>
    /// </remarks>
    public IReadOnlyList<DeclaredBand> DeclaredBands { get; set; } = [];

    /// <summary>
    /// Where the decoded-frames panel's opening backlog comes from - the station's frame log,
    /// when it keeps one. Called once per browser that connects, with the number of frames
    /// wanted; returns them <b>oldest first</b>. Null (the default) opens the panel empty.
    /// </summary>
    /// <remarks>
    /// <para>A panel that starts empty says nothing about a channel that has been busy all
    /// morning, and on a quiet band it is indistinguishable from a modem that is not working. The
    /// station already writes down every frame it hears and every frame it sends; this is that
    /// record, shown - transmissions included, marked as ours.</para>
    /// <para>A delegate rather than a database handle because the log lives in the daemon and
    /// this server lives in the library, and because whatever provides it should be free to
    /// decide what "recent" means. It is called on the connection's own thread, so it should be
    /// quick and must not throw - an exception here costs the browser its backlog, which is
    /// caught and shrugged off, but it should not be the way that is discovered.</para>
    /// </remarks>
    public Func<int, IReadOnlyList<LoggedFrame>>? FrameHistory { get; set; }
}

/// <summary>
/// One frame out of the station's log, for the decoded-frames panel's opening backlog. The
/// receive-side subset of <see cref="Modems.FrameQuality"/> plus when it happened, which is
/// what a written-down frame has and a live one does not need.
/// </summary>
/// <param name="HeardAt">
/// When the station decoded it, or - on a transmitted frame - when it sent it (UTC); rendered
/// in the viewer's zone. The name matches the log's own <c>heard_at</c> column, which kept its
/// name so that queries written against it kept working.
/// </param>
/// <param name="SubChannel">Which modem heard or sent it.</param>
/// <param name="Mode">Its mode string, as the modem reported it.</param>
/// <param name="From">Source callsign where the frame carried one.</param>
/// <param name="To">Destination callsign where the frame carried one.</param>
/// <param name="LengthBytes">Decoded frame length.</param>
/// <param name="CorrectedBytes">Bytes FEC repaired, where the framing counts them.</param>
/// <param name="CrcValid">CRC verdict, where the framing carries one.</param>
/// <param name="OffsetHz">Measured carrier offset, where the decoder measured one.</param>
/// <param name="Transmitted">
/// True for a frame this station sent - badged TX in the panel rather than read as somebody
/// heard. Defaults to false: a log with nothing to say about direction holds receives.
/// </param>
public sealed record LoggedFrame(
    DateTimeOffset HeardAt,
    int SubChannel,
    string Mode,
    string? From,
    string? To,
    int LengthBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? OffsetHz,
    bool Transmitted = false);

/// <summary>A band the host declares rather than the waterfall measuring it.</summary>
/// <param name="SubChannel">Which modem, for ordering and labels.</param>
/// <param name="Mode">Its mode string; rendered for display by <see cref="ModeNames"/>.</param>
/// <param name="CentreHz">The audio centre the operator configured.</param>
/// <param name="BandwidthHz">
/// Its width, used only when the band cannot be measured (ARDOP). Null means "measure it".
/// </param>
public sealed record DeclaredBand(int SubChannel, string Mode, double CentreHz, double? BandwidthHz);

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
/// frequency offset - so a burst on screen reads directly as "who".
/// </summary>
/// <remarks>
/// Modem bands are measured, not tabulated: at <see cref="Start"/> each modem modulates a
/// short throwaway frame and the ITU-R SM.443 99 % occupied bandwidth of that audio is the
/// band the page shades. A new mode gets a correct overlay with no table to maintain.
/// Per-frame SNR/extent come from <see cref="BandActivityTracker"/> over the same lines the
/// display draws. Call <see cref="Start"/> before audio flows (it registers the channel
/// receive tap); received-side only - during transmit the display pauses, as half-duplex
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
    private readonly List<WaterfallClient> _clients = [];
    private readonly List<short> _audioBlock = [];

    // The spectrum source is fed from two threads - the receive loop and the transmitter - and
    // is not itself thread-safe. Half duplex keeps them apart in practice, but not across the
    // instant the transmit flag flips, which is exactly when both are live.
    private readonly object _sourceLock = new();
    private readonly object _transmitLock = new();
    private readonly Queue<float[]> _transmitPending = new();
    private int _transmitPendingSamples;
    private int _transmitPendingOffset;
    private long _transmitPacedAt;
    private float[] _transmitSilence = [];
    private ITimer? _transmitPacer;

    // Set while feeding our own transmission, so the line that comes back out can be told apart
    // from a received one. Lines are emitted synchronously inside Process, so this is exact.
    private bool _lineIsTransmit;

    /// <summary>
    /// One connected browser. Audio is per-client and off by default: a viewer who opened the
    /// page to look at a waterfall should not silently start pulling 24 KB/s, and several
    /// viewers should not each cost that unless they asked.
    /// </summary>
    private sealed class WaterfallClient
    {
        public required Channel<(WebSocketMessageType Kind, byte[] Payload)> Queue { get; init; }

        private int _audio;

        public bool AudioEnabled
        {
            get => Volatile.Read(ref _audio) != 0;
            set => Volatile.Write(ref _audio, value ? 1 : 0);
        }
    }
    private WaterfallSource? _source;
    private string? _transmitStatus;
    private byte[]? _surveyMessage;
    private string _surveyDirectory = "";
    private volatile bool _keyed;
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
        // HttpListener wants "+" for "every interface" and rejects the literal 0.0.0.0 that a
        // TcpListener is perfectly happy with - the daemon uses one bind setting for both, so
        // translate here rather than make the operator know which listener wants which spelling.
        bool everyInterface = bind is "*" or "0.0.0.0" or "::" or "[::]";
        _listener.Prefixes.Add($"http://{(everyInterface ? "+" : bind)}:{port}/");
        Url = $"http://{(everyInterface ? "127.0.0.1" : bind)}:{port}/";
    }

    /// <summary>The listen port.</summary>
    public int Port { get; }

    /// <summary>A URL the page is reachable at.</summary>
    public string Url { get; }

    /// <summary>
    /// What the transmitter is doing right now - forward power and SWR - or null when it is not
    /// keyed. Shown next to the radio status and cleared on key-up.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetRadioStatus"/> because it changes on every keyup rather than
    /// once in a session, and because an empty transmit status is the normal state rather than a
    /// missing reading.
    /// </remarks>
    public void SetTransmitStatus(string? status)
    {
        if (status == _transmitStatus)
        {
            return;
        }

        _transmitStatus = status;
        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
            new { type = "tx", status }, Json));
    }

    /// <summary>
    /// A line about the radio for the page's top bar - the frequency reference on a FlexRadio.
    /// Null means there is nothing to say (no radio that reports one), and the page shows
    /// nothing rather than an empty label.
    /// </summary>
    /// <remarks>
    /// Set whenever it changes; late-joining browsers get the current value with their config,
    /// so a page opened an hour in is not blank until the next change.
    /// </remarks>
    public void SetRadioStatus(string? status)
    {
        if (status == _radioStatus)
        {
            return;
        }

        _radioStatus = status;
        _configMessage = BuildConfigMessage();
        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
            new { type = "radio", status }, Json));
    }

    private string? _radioStatus;

    /// <summary>The measured per-modem display bands (populated by <see cref="Start"/>).</summary>
    public IReadOnlyList<ModemBand> Bands => _bands;

    /// <summary>
    /// What the signal survey has been doing - captures kept, captures a budget refused, and the
    /// disk it is using. Pushed rather than polled, and only on a change.
    /// </summary>
    /// <remarks>
    /// The refusals are the reason this exists. A survey left running for a week silently becomes
    /// a sample rather than the set when the channel is busier than its rate limit, and nothing
    /// anywhere reported that: an operator would have had to count files per hour and notice the
    /// number was exactly the cap. A count on the page answers it at a glance - and it is state
    /// rather than an event, which is why it belongs on a display and not in a journal.
    /// </remarks>
    /// <param name="captured">Captures written to disk.</param>
    /// <param name="skipped">Bursts worth keeping that a rate limit, cooldown or missing audio
    /// refused.</param>
    /// <param name="bytes">Bytes the capture directory holds.</param>
    /// <param name="directory">Where they are, so their audio can be served from it.</param>
    public void SetSurveyStatus(long captured, long skipped, long bytes, string directory)
    {
        _surveyDirectory = directory;
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(
            new { type = "survey", captured, skipped, bytes }, Json);
        if (_surveyMessage is { } previous && previous.AsSpan().SequenceEqual(message))
        {
            return;
        }

        _surveyMessage = message;
        Broadcast(WebSocketMessageType.Text, message);
    }


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
            if (!TryMeasureBand(sub, modem, _channel.SampleRate, out ModemBand band))
            {
                continue;
            }

            // Measured edges, but the configured centre where the host stated one: the
            // midpoint of a measurement is a few Hz off what the operator asked for.
            if (Declared(sub) is { } declared)
            {
                band = band with { CentreHz = declared.CentreHz };
            }

            AddBand(band, source);
        }

        // Bands nothing enumerable carries - ARDOP, which is a tap rather than a modem.
        foreach (DeclaredBand declared in _options.DeclaredBands
                     .Where(d => !_channel.Modems.ContainsKey(d.SubChannel) && d.BandwidthHz is > 0)
                     .OrderBy(d => d.SubChannel))
        {
            double half = declared.BandwidthHz!.Value / 2;
            AddBand(
                new ModemBand(
                    declared.SubChannel, declared.Mode,
                    declared.CentreHz - half, declared.CentreHz + half, declared.CentreHz),
                source);
        }

        _bands.Sort((a, b) => a.SubChannel.CompareTo(b.SubChannel));

        _configMessage = BuildConfigMessage();
        _channel.AddReceiveTap(samples =>
        {
            // Not while our own transmission is still being painted. The two are separate audio
            // streams and the transform has one accumulator: interleaved, a single window holds
            // part of a burst and part of the band noise, and comes out broadband - a full-width
            // haze over the back half of every keyup, with the line type flickering between the
            // two ramps as it goes. Receive processing is gated during a keyup, but the paced
            // painting outlives it whenever the audio device's Drain returns before the audio has
            // actually left the radio, which is the normal case.
            if (!_keyed && Volatile.Read(ref _transmitPendingSamples) == 0)
            {
                lock (_sourceLock)
                {
                    source.Process(samples);
                }
            }

            // The listener feed is a stream of its own and has nothing to do with the transform.
            BroadcastAudio(samples);
        });

        // Draw what we transmit, so the display stays continuous across a keyup instead of
        // freezing and silently compressing the time axis.
        _channel.TransmittedAudio += OnTransmittedAudio;
        _channel.TransmittingChanged += OnTransmittingChanged;
        _channel.FrameReceivedWithQuality += OnFrame;
        _channel.FrameTransmitted += OnFrameTransmitted;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>
    /// Measures a modem's occupied band for the overlay. Delegates to
    /// <see cref="ModemBandProbe"/>, which the RF band planner uses too - one measurement, so
    /// what the waterfall draws and what the planner fits can never disagree.
    /// </summary>
    internal static bool TryMeasureBand(int subChannel, IModem modem, int sampleRate, out ModemBand band)
    {
        band = default;
        if (!ModemBandProbe.TryMeasure(modem, sampleRate, out double low, out double high))
        {
            return false;
        }

        band = new ModemBand(subChannel, modem.Mode, low, high, (low + high) / 2);
        return true;
    }

    private DeclaredBand? Declared(int subChannel) =>
        _options.DeclaredBands.FirstOrDefault(d => d.SubChannel == subChannel);

    private void AddBand(ModemBand band, WaterfallSource source)
    {
        _bands.Add(band);
        _trackers[band.SubChannel] = new BandActivityTracker(
            source.BinWidthHz, source.LinesPerSecond, source.LineLength, band.LowHz, band.HighHz);
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
            radioStatus = _radioStatus,
            sideband = _options.Sideband,
            modems = _bands.Select(b => new
            {
                sub = b.SubChannel,
                mode = b.Mode,
                modeName = ModeNames.Display(b.Mode),
                lowHz = Math.Round(b.LowHz, 1),
                highHz = Math.Round(b.HighHz, 1),
                centreHz = Math.Round(b.CentreHz, 1),
            }),
        }, Json);
    }

    /// <summary>
    /// Our own transmitted audio, queued to be painted at the rate it will actually go out.
    /// </summary>
    /// <remarks>
    /// <para>The whole keyup arrives here in one call: the modulator produces the burst as a
    /// single array long before the sound card has played a sample of it. Painting it on arrival
    /// put a two-second burst on the display as sixty lines inside a few milliseconds, followed
    /// by two seconds of nothing at all - receive processing is gated off while transmitting, so
    /// there is no other line source during a keyup. That is the judder: a lurch at key-down and
    /// then a frozen display for the length of the transmission.</para>
    /// <para>This is called <em>before</em> the audio is written to the device, which is what
    /// makes the pacing below line up with reality. A device's write blocks until its buffer has
    /// room, so being told afterwards meant the queue stayed empty for most of the transmission -
    /// and the pacer painted silence throughout it and then the burst all over again. See
    /// <see cref="Channel.SoundModemChannel.TransmittedAudio"/>.</para>
    /// <para>So it is queued and released by <see cref="PaceTransmitLines"/> at the rate real
    /// time passes, which is the rate the audio is leaving the radio. The pacing lives here and
    /// not in the channel because the transmitter must never wait on a picture - this returns
    /// immediately however long the burst is.</para>
    /// </remarks>
    /// <summary>
    /// Key-down and key-up. Painting starts at key-down rather than when the first audio arrives.
    /// </summary>
    /// <remarks>
    /// Receive processing stops the instant the transmitter takes the channel, but the first
    /// transmitted audio does not exist until the frame has been modulated and handed to the
    /// device. Nothing at all is drawn in between, so the waterfall visibly stalls as the PTT
    /// engages. Starting here means the time axis keeps moving through that gap - and through
    /// the gaps between frames in one keyup - with silence, which is what was on the air.
    /// </remarks>
    private void OnTransmittingChanged(bool keyed)
    {
        _keyed = keyed;
        if (keyed)
        {
            StartPacing();
        }
    }

    private void OnTransmittedAudio(ReadOnlyMemory<float> samples)
    {
        if (_source is null || samples.IsEmpty)
        {
            return;
        }

        float[] display = ForDisplay(samples.Span);

        lock (_transmitLock)
        {
            _transmitPending.Enqueue(display);
            Volatile.Write(ref _transmitPendingSamples, _transmitPendingSamples + display.Length);
        }

        StartPacing();
    }

    /// <summary>Starts the pacing clock if it is not already running.</summary>
    private void StartPacing()
    {
        lock (_transmitLock)
        {
            if (_transmitPacer is not null)
            {
                return;
            }

            // Start the clock here, so the first tick releases one tick's worth rather than a
            // backlog measured from whenever the timer last ran.
            _transmitPacedAt = _options.TimeProvider.GetTimestamp();
            var period = TimeSpan.FromMilliseconds(1000.0 / _options.LinesPerSecond);
            _transmitPacer = _options.TimeProvider.CreateTimer(
                _ => PaceTransmitLines(), null, period, period);
        }
    }

    /// <summary>
    /// How far our own transmission is turned down before it is drawn.
    /// </summary>
    /// <remarks>
    /// <para>A modulator emits around −5 dBFS rms - some 35 dB hotter than anything the display
    /// ever sees on receive, and the page's window is −95..−35 dB. Drawn as-is, the transform's
    /// own leakage skirt (40 dB below the peak, buried in the noise for a received signal) lands
    /// well above the display floor and smears across the full span: measured, 1021 of 1024 bins
    /// lit. Turned down by this, the same burst lights 539 and peaks at 77 % brightness, against
    /// 562 and 79 % for a genuinely strong received station.</para>
    /// <para><b>A fixed gain, deliberately, and not a normalisation to a target level.</b>
    /// Normalising each buffer is an automatic gain control, and an AGC's whole purpose is to
    /// make quiet things loud - so the quiet buffers in a keyup (a ramp-down, a tail, an idle
    /// stretch of a shifted ARDOP burst) get multiplied by an enormous gain and their noise floor
    /// fills the entire span. Measured: near-silence at −65 dBFS rms normalised to −40 lights
    /// 1011 of 1024 bins. That is the same full-width haze as the original bug, arrived at from
    /// the opposite direction, and it is why this is a constant. The modulator's level is a
    /// stable property of the system; there is nothing here that needs tracking.</para>
    /// </remarks>
    internal const double TransmitDisplayGainDb = -35;

    /// <summary>
    /// Scales transmitted audio to the level the display is calibrated for.
    /// </summary>
    /// <remarks>
    /// Our own transmit level is not a measurement of anything the display can show: it is
    /// whatever the modulator happens to emit, which differs per mode and which no receiver would
    /// ever see at that strength. Drawing it literally does not produce a hot signal, it produces
    /// a saturated one. Purely a gain - the spectrum's shape, and so the bandwidth and placement
    /// the operator reads off it, is untouched, and quiet stays quiet.
    /// </remarks>
    internal static float[] ForDisplay(ReadOnlySpan<float> samples)
    {
        double gain = Math.Pow(10, TransmitDisplayGainDb / 20);
        var scaled = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            scaled[i] = (float)(samples[i] * gain);
        }

        return scaled;
    }

    /// <summary>
    /// Releases however much transmitted audio real time has passed for, and stops once the
    /// queue is empty.
    /// </summary>
    /// <remarks>
    /// <para>Paced by elapsed time rather than by counting ticks, because a timer that fires late
    /// - and at a 33 ms period on a busy box it will - must still paint the right amount rather
    /// than fall progressively behind the transmission it is drawing. That also removes any need
    /// to detect and correct a backlog: a late tick simply releases proportionally more.</para>
    /// <para>The queue is not a backlog. It is audio that has not gone out yet - the display is
    /// meant to trail the modulator by exactly as much as the sound card does.</para>
    /// </remarks>
    private void PaceTransmitLines()
    {
        WaterfallSource? source = _source;
        if (source is null)
        {
            return;
        }

        long now = _options.TimeProvider.GetTimestamp();
        var due = new List<ArraySegment<float>>();

        lock (_transmitLock)
        {
            double elapsed = _options.TimeProvider.GetElapsedTime(_transmitPacedAt, now).TotalSeconds;
            _transmitPacedAt = now;
            int budget = (int)(elapsed * _channel.SampleRate);

            while (budget > 0 && _transmitPending.Count > 0)
            {
                float[] head = _transmitPending.Peek();
                int available = head.Length - _transmitPendingOffset;
                int take = Math.Min(available, budget);
                due.Add(new ArraySegment<float>(head, _transmitPendingOffset, take));

                budget -= take;
                Volatile.Write(ref _transmitPendingSamples, _transmitPendingSamples - take);
                if (take == available)
                {
                    _transmitPending.Dequeue();
                    _transmitPendingOffset = 0;
                }
                else
                {
                    _transmitPendingOffset += take;
                }
            }

            // Still keyed with nothing queued - the gap between key-down and the first modulated
            // frame, or between frames. Silence is what is on the air, and drawing it is what
            // keeps the time axis moving instead of stalling until audio turns up.
            if (due.Count == 0 && _keyed && budget > 0)
            {
                if (_transmitSilence.Length < budget)
                {
                    _transmitSilence = new float[budget];
                }

                due.Add(new ArraySegment<float>(_transmitSilence, 0, budget));
            }

            if (_transmitPending.Count == 0 && !_keyed)
            {
                _transmitPacer?.Dispose();
                _transmitPacer = null;
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        lock (_sourceLock)
        {
            _lineIsTransmit = true;
            try
            {
                foreach (ArraySegment<float> piece in due)
                {
                    source.Process(piece.AsSpan());
                }
            }
            finally
            {
                _lineIsTransmit = false;
            }
        }
    }



    private void OnLine(long index, ReadOnlyMemory<byte> line)
    {
        ReadOnlySpan<byte> bins = line.Span;
        bool transmit = _lineIsTransmit;

        // Our own transmission is not a signal we heard. Feeding it to the trackers would report
        // a huge SNR and attribute it to whatever frame decoded next.
        if (!transmit)
        {
            foreach (BandActivityTracker tracker in _trackers.Values)
            {
                tracker.AddLine(bins);
            }
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
        // 0x01 heard, 0x03 transmitted - the page draws them differently, because a burst of
        // your own must not read as a strong station.
        message[0] = transmit ? (byte)0x03 : (byte)0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), (uint)index);
        bins.CopyTo(message.AsSpan(5));
        Broadcast(WebSocketMessageType.Binary, message);
    }

    /// <summary>Frame event (receive thread): attribute the just-decoded frame to its burst
    /// - callsigns off the frame, SNR/extent off the band tracker, offset off the winning
    /// decoder branch - and fan it out as JSON.</summary>
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

        BroadcastFrame(
            subChannel, quality.Mode, from, to, quality.FrameBytes, snrDb, burstLines,
            quality.FrequencyOffsetHz is { } offset ? Math.Round(offset, 1) : null,
            quality.CorrectedBytes, quality.CrcValid,
            // A frame that decoded and would not yield callsigns is where the panel used to pose
            // a question instead of answering one: it said "unattributed" and stopped. It has
            // already passed Reed-Solomon and, on an IL2P+CRC link, the CRC - so the bits are
            // right and the reading of them is not, and which encapsulation carried it is the
            // first thing worth knowing. The bytes come too, because the panel is where an
            // operator notices one of these and the next thing they will want is to copy them.
            note: Ax25AttributionNote.For(frame),
            headerType: quality.HeaderType?.ToString(),
            frameHex: from is null && to is null ? Convert.ToHexString(frame) : null,
            // Reed-Solomon and nothing else stood behind this frame, and on an -il2pc modem the
            // mode label beside it says the opposite. The panel is the one place an operator sees
            // both at once, so it is the place to say so - and to say whether the frame was
            // handed on, which is the operator's own configuration answering back.
            plainIl2p: quality.PlainIl2p,
            monitorOnly: quality.MonitorOnly);
    }

    /// <summary>Frame event (transmit thread): list what this station has just sent, so the
    /// panel is a record of the channel rather than of half of it.</summary>
    /// <remarks>
    /// <para>The burst is already drawn - transmitted audio is painted in its own style so a
    /// keyup does not read as a strong station - but until now nothing said <em>what</em> it
    /// was, and an operator watching their own beacon go out had to take it on trust. Raised
    /// after the audio has left, so a listed frame is one that actually went on air.</para>
    /// <para>No SNR, offset, FEC count or CRC: those are receive measurements, and inventing
    /// them for our own transmission would be inventing a measurement of ourselves. No burst
    /// tag either - a received frame's tag lands on the energy that carried it, but transmitted
    /// audio is queued and repainted in real time while this fires as soon as the device has
    /// taken it, so the tag would sit somewhere up the burst rather than on it.</para>
    /// </remarks>
    private void OnFrameTransmitted(int subChannel, byte[] frame)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        string? from = null;
        string? to = null;
        if (Ax25AddressParser.TryParse(frame, out string source, out string destination))
        {
            from = source;
            to = destination;
        }

        BroadcastFrame(
            subChannel,
            _channel.Modems.TryGetValue(subChannel, out IModem? modem) ? modem.Mode : "?",
            from, to, frame.Length, snrDb: null, burstLines: null, offsetHz: null,
            corrected: null, crc: null, transmitted: true);
    }

    /// <summary>
    /// Reports a frame heard by a demodulator that is not one of the channel's sub-channel
    /// modems - ARDOP, whose demodulator is inside the virtual TNC and never raises
    /// <see cref="SoundModemChannel.FrameReceivedWithQuality"/>.
    /// </summary>
    /// <remarks>
    /// Without this the panel is silently partial: the ARDOP band is drawn, its bursts paint the
    /// waterfall, and nothing is ever listed for it. SNR comes from the caller because the
    /// demodulator's own measurement is better than anything the band tracker can infer from a
    /// burst that overlaps the packet slots.
    /// </remarks>
    public void ReportFrame(
        int subChannel,
        string mode,
        string? from,
        string? to,
        int lengthBytes,
        double? snrDb,
        bool? decodedOk)
    {
        if (_source is null)
        {
            return;   // not started; nothing to attribute the frame to and nobody to tell
        }

        BroadcastFrame(
            subChannel, mode, from, to, lengthBytes, snrDb,
            burstLines: null, offsetHz: null, corrected: null, crc: decodedOk);
    }

    /// <summary>
    /// Reports a station identification heard by an <see cref="Modems.IdBeaconGhost"/> - the 300
    /// AFSK AX.25 ident a NinoTNC sends alongside, and not inside, its PSK SSB data mode.
    /// </summary>
    /// <remarks>
    /// <para>Listed and tagged like anything else, and marked as an ident so both can say what it
    /// is. What a ghost does not get is a <em>band</em>: it has no slot of its own to shade, being
    /// a second listener on a modem that is already drawn. The tag therefore lands against the
    /// band of the modem it accompanies, which is where the ident sits - a couple of hundred Hz
    /// above it.</para>
    /// <para>No SNR - a ghost has no <see cref="BandActivityTracker"/>, the trackers being keyed
    /// by sub-channel and it sharing the base modem's. The offset is real: the ghost's
    /// demodulator measures the carrier against its own centre, so it says how far the
    /// identifying station's dial sits from ours.</para>
    /// </remarks>
    /// <param name="subChannel">The sub-channel of the modem the ghost accompanies, for the label.</param>
    /// <param name="mode">The ghost's mode, as it reports itself.</param>
    /// <param name="from">The identifying station.</param>
    /// <param name="to">Its destination - <c>IDENT</c> on a NinoTNC.</param>
    /// <param name="lengthBytes">Frame length.</param>
    /// <param name="offsetHz">How far off our centre the ident arrived.</param>
    public void ReportIdBeacon(
        int subChannel, string mode, string? from, string? to, int lengthBytes, double? offsetHz = null)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        BroadcastFrame(
            subChannel, mode, from, to, lengthBytes, snrDb: null, burstLines: null,
            offsetHz: offsetHz is { } offset ? Math.Round(offset, 1) : null,
            corrected: null, crc: true, idBeacon: true);
    }

    private void BroadcastFrame(
        int subChannel, string mode, string? from, string? to, int lengthBytes,
        double? snrDb, int? burstLines, double? offsetHz, int? corrected, bool? crc,
        bool idBeacon = false, bool transmitted = false,
        string? note = null, string? headerType = null, string? frameHex = null,
        bool plainIl2p = false, bool monitorOnly = false)
    {
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "frame",
            line = _source!.NextLineIndex,
            sub = subChannel,
            mode,
            from,
            to,
            lenBytes = lengthBytes,
            snrDb,
            burstLines,
            offsetHz,
            corrected,
            crc,
            // True on an ident, null otherwise - same nullable-optional shape as the fields
            // above, and the page tests it for truthiness either way.
            id = idBeacon ? true : (bool?)null,
            // True on our own transmission: the page lists it and, unlike everything else,
            // does not tag it onto the waterfall (see OnFrameTransmitted).
            tx = transmitted ? true : (bool?)null,
            // Only on a frame whose addresses would not read: why, which IL2P encapsulation
            // carried it, and the bytes themselves.
            why = note,
            il2p = headerType,
            hex = frameHex,
            // True on a frame read as plain IL2P - Reed-Solomon alone stood behind it - and null
            // otherwise, the same shape as id/tx above. Its own field rather than the page
            // inferring it from a null crc: crc is also null on our own transmissions, on HDLC
            // and on ARDOP, and a badge that says "no CRC checked" on a mode that has no CRC to
            // check would be noise on three paths to be right on one.
            plain = plainIl2p ? true : (bool?)null,
            // And whether it stopped here. Only meaningful alongside plain; the badge is the same
            // either way, because the frame is RS-only either way, but the operator reading it
            // wants to know which of the two things their configuration did.
            monitorOnly = monitorOnly ? true : (bool?)null,
        }, Json);
        Broadcast(WebSocketMessageType.Text, message);
    }

    /// <summary>How many audio blocks a second at <see cref="AudioBlockMilliseconds"/> each.</summary>
    private const int AudioBlocksPerSecond = 1000 / AudioBlockMilliseconds;

    /// <summary>
    /// Audio block length. Short enough that a browser can start playing promptly and that a
    /// dropped block is a click rather than a gap; long enough not to spend the whole budget on
    /// WebSocket framing.
    /// </summary>
    private const int AudioBlockMilliseconds = 40;

    /// <summary>
    /// Type byte plus padding to a 4-byte boundary, so the samples that follow can be viewed
    /// directly as 16-bit (or later 32-bit) values without copying.
    /// </summary>
    internal const int AudioHeaderBytes = 4;

    private static void TryApplyClientRequest(WaterfallClient client, ReadOnlySpan<byte> utf8)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(utf8.ToArray());
            if (request.RootElement.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "audio"
                && request.RootElement.TryGetProperty("on", out JsonElement on))
            {
                client.AudioEnabled = on.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            // A browser sending nonsense loses nothing but its own request.
        }
    }

    /// <summary>
    /// Receive audio to whoever asked for it, as [0x02][s16 LE mono] at the channel rate.
    /// </summary>
    /// <remarks>
    /// Nothing is received while transmitting - the channel gates its receive tap on half
    /// duplex - so the stream simply stops for the length of a keyup. The browser hears that as
    /// silence, which is what it is, rather than as a glitch or a desync: blocks carry no
    /// timestamps and are played in arrival order, so a gap costs nothing to recover from.
    /// </remarks>
    private void BroadcastAudio(ReadOnlySpan<float> samples)
    {
        bool wanted;
        lock (_clientsLock)
        {
            wanted = _clients.Any(c => c.AudioEnabled);
        }

        if (!wanted)
        {
            // Do not accumulate for nobody, and do not carry a stale half-block into the moment
            // somebody starts listening.
            _audioBlock.Clear();
            return;
        }

        int blockSamples = _channel.SampleRate * AudioBlockMilliseconds / 1000;
        foreach (float sample in samples)
        {
            _audioBlock.Add((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
        }

        while (_audioBlock.Count >= blockSamples)
        {
            // 4-byte header, not 1: a browser's Int16Array view needs its byte offset aligned
            // to the element size, and samples starting at byte 1 threw RangeError on arrival.
            var message = new byte[AudioHeaderBytes + (blockSamples * 2)];
            message[0] = 0x02;
            for (int i = 0; i < blockSamples; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    message.AsSpan(AudioHeaderBytes + (i * 2)), _audioBlock[i]);
            }

            _audioBlock.RemoveRange(0, blockSamples);
            lock (_clientsLock)
            {
                foreach (WaterfallClient listener in _clients.Where(c => c.AudioEnabled))
                {
                    listener.Queue.Writer.TryWrite((WebSocketMessageType.Binary, message));
                }
            }
        }
    }

    private void Broadcast(WebSocketMessageType kind, byte[] payload)
    {
        lock (_clientsLock)
        {
            foreach (WaterfallClient client in _clients)
            {
                client.Queue.Writer.TryWrite((kind, payload));
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

            if (context.Request.HttpMethod == "GET"
                && context.Request.Url?.AbsolutePath is { } path
                && path.StartsWith("/survey/", StringComparison.Ordinal)
                && TryResolveCapture(path["/survey/".Length..], out string file))
            {
                context.Response.ContentType = file.EndsWith(".wav", StringComparison.Ordinal)
                    ? "audio/wav"
                    : "application/json";
                using (FileStream capture = File.OpenRead(file))
                {
                    context.Response.ContentLength64 = capture.Length;
                    await capture.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                }

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
            // Deep enough to hold a second of waterfall lines plus a second of audio blocks;
            // audio that arrives late is worse than audio dropped, so the queue stays shallow.
            new BoundedChannelOptions(_options.LinesPerSecond + AudioBlocksPerSecond)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        var client = new WaterfallClient { Queue = queue };
        lock (_clientsLock)
        {
            _clients.Add(client);
        }

        try
        {
            await socket.SendAsync(_configMessage, WebSocketMessageType.Text, true, _stopping.Token)
                .ConfigureAwait(false);
            if (_surveyMessage is { } survey)
            {
                await socket.SendAsync(survey, WebSocketMessageType.Text, true, _stopping.Token)
                    .ConfigureAwait(false);
            }

            if (BuildHistoryMessage() is { } history)
            {
                await socket.SendAsync(history, WebSocketMessageType.Text, true, _stopping.Token)
                    .ConfigureAwait(false);
            }

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

                // The only thing a browser asks for: start or stop sending it audio.
                if (received.MessageType == WebSocketMessageType.Text && received.Count > 0)
                {
                    TryApplyClientRequest(client, buffer.AsSpan(0, received.Count));
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
                _clients.Remove(client);
            }

            queue.Writer.TryComplete();
            socket.Dispose();
        }
    }

    /// <summary>How much of the station's log a browser opens with. Half the panel's own
    /// 100-row cap: enough to say what the channel has been doing, and not so much that the
    /// backlog pushes out live traffic before the operator has read any.</summary>
    private const int HistoryFrames = 50;

    /// <summary>
    /// The decoded-frames panel's opening backlog, or null when there is no log to draw it from.
    /// Sent once per connection, straight after the config and before the send loop starts, so
    /// it can never be interleaved with - or land on top of - live frames decoded meanwhile.
    /// </summary>
    /// <remarks>
    /// Oldest first: the page prepends each row, so the newest ends up on top, and a live frame
    /// arriving during the handshake is queued behind this and lands above it. Marked
    /// <c>hist</c>, so the page lists it without tagging it onto the waterfall - these frames
    /// were heard before the scroll on screen began and belong to no burst on it.
    /// </remarks>
    private byte[]? BuildHistoryMessage()
    {
        if (_options.FrameHistory is not { } source)
        {
            return null;
        }

        IReadOnlyList<LoggedFrame> frames;
        try
        {
            frames = source(HistoryFrames);
        }
        catch (Exception)
        {
            // A log that cannot be read costs this browser its backlog and nothing else. The
            // station is still decoding, and the panel still fills from the air.
            return null;
        }

        if (frames.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "history",
            frames = frames.Select(f => new
            {
                at = f.HeardAt.ToUniversalTime().ToString("O"),
                sub = f.SubChannel,
                mode = f.Mode,
                from = f.From,
                to = f.To,
                lenBytes = f.LengthBytes,
                offsetHz = f.OffsetHz is { } offset ? Math.Round(offset, 1) : (double?)null,
                corrected = f.CorrectedBytes,
                crc = f.CrcValid,
                // Same nullable-optional shape as a live frame's, so the page's one row builder
                // badges a logged transmission TX exactly as it badges a live one.
                tx = f.Transmitted ? true : (bool?)null,
                hist = true,
            }),
        }, Json);
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

    /// <summary>
    /// Resolves a capture file name to a path inside the survey directory, or refuses.
    /// </summary>
    /// <remarks>
    /// Only the exact shape the writer produces - <c>20260804-151909-862hz-unclaimed.wav</c> - is
    /// served, and only from the configured directory. A name is not a path: anything carrying a
    /// separator, a drive or a <c>..</c> is refused before it reaches the filesystem, so this
    /// route cannot be talked into reading the frame log, a key, or /etc/passwd.
    /// </remarks>
    private bool TryResolveCapture(string name, out string file)
    {
        file = "";
        if (_surveyDirectory.Length == 0 || name.Length is 0 or > 128)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
            {
                return false;
            }
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || (!name.EndsWith(".wav", StringComparison.Ordinal)
                && !name.EndsWith(".json", StringComparison.Ordinal)))
        {
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(_surveyDirectory, name));
        string root = Path.GetFullPath(_surveyDirectory);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(candidate))
        {
            return false;
        }

        file = candidate;
        return true;
    }

    /// <summary>
    /// Reports a survey capture - a burst this station could not read, kept for later.
    /// </summary>
    /// <remarks>
    /// <para>Drawn where it happened. A capture has a frequency, a width and a time, which is
    /// exactly what this display's two axes are, so "something we could not read went past
    /// <em>there</em>" is a statement the waterfall can make and a list of filenames cannot.</para>
    /// <para><b>Placed by age, not by line index.</b> The survey runs its own spectrum feed so it
    /// keeps working on a station with nobody watching, and its line clock is therefore not this
    /// one's. Seconds-ago is a quantity both agree on.</para>
    /// </remarks>
    /// <param name="verdict">Why it was kept - <c>unclaimed</c>, <c>missed</c>, <c>unattributed</c>.</param>
    /// <param name="centreHz">Measured audio centre.</param>
    /// <param name="lowHz">Measured low edge.</param>
    /// <param name="highHz">Measured high edge.</param>
    /// <param name="durationSeconds">How long the burst lasted.</param>
    /// <param name="snrDb">Peak SNR over the noise floor.</param>
    /// <param name="secondsAgo">How long ago it ended.</param>
    /// <param name="file">Its audio file name, for the download link.</param>
    public void ReportCapture(
        string verdict, double centreHz, double lowHz, double highHz,
        double durationSeconds, double snrDb, double secondsAgo, string file)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "capture",
            verdict,
            centreHz = Math.Round(centreHz, 1),
            lowHz = Math.Round(lowHz, 1),
            highHz = Math.Round(highHz, 1),
            durationSeconds = Math.Round(durationSeconds, 2),
            snrDb = Math.Round(snrDb, 1),
            secondsAgo = Math.Round(secondsAgo, 2),
            file,
        }, Json));
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
        _channel.FrameTransmitted -= OnFrameTransmitted;
        _channel.TransmittedAudio -= OnTransmittedAudio;
        _channel.TransmittingChanged -= OnTransmittingChanged;
        lock (_transmitLock)
        {
            _transmitPacer?.Dispose();
            _transmitPacer = null;
            _transmitPending.Clear();
            Volatile.Write(ref _transmitPendingSamples, 0);
            _transmitPendingOffset = 0;
        }

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
                client.Queue.Writer.TryComplete();
            }

            _clients.Clear();
        }

        _stopping.Dispose();
    }
}
