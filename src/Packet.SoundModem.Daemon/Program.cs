using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using M0LTE.Dsp;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Station;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Telemetry;
using Packet.SoundModem.Waterfall;
using Packet.SoundModem.Ms110d;

// pdn-soundmodem: headless soundcard packet modem daemon.
//
//   pdn-soundmodem [--config soundmodem.json]
//   pdn-soundmodem --uplink-token CALLSIGN
//   pdn-soundmodem [--device default] [--capture-rate 48000] [--kiss 8105]
//                  [--bind 127.0.0.1|*]
//                  [--modem N:MODE[:FREQ]]... [--ptt serial:/dev/ttyUSB0[:rts|:dtr]]
//                  [--ptt cm108:/dev/hidraw0[:gpio]]
//                  [--txdelay MS] [--wav FILE] [--wav-loop FILE] [--quality-frames]
//                  [--psk-detector coherent|differential]
//                  [--paging PORT[:BAUD]]
//                  [--ardop PORT]
//                  [--waterfall PORT] [--dial HZ]
//                  [--two-tone SECONDS] [--tone HZ SECONDS]
//
// Modes: afsk1200, bpsk300 (IL2P+CRC), bpsk300-nocrc, bpsk1200 - the BPSK modes are a
// differential frequency-diversity bank by default (parallel branches at stepped centres;
// bpsk300-multi/bpsk1200-multi are aliases; offsetPairs/offsetStepHz tune it, offsetPairs:0 =
// single modem; --psk-detector coherent forces coherent), qpsk2400, qpsk3600 (both IL2P+CRC),
// fsk9600 (classic G3RUH), fsk9600-il2p (IL2P+CRC), freedv-datac0/1/3/4/13/14 (FreeDV datac
// OFDM waveform; payloads carry the family-standard IL2P+CRC bit stream - a pdn convention,
// FreeDV defines no framing at the raw-data layer), ms110d-wn0/1/2/3/4/5/6/7/8/13
// (MIL-STD-188-110D App D 3 kHz serial-tone, 75-3200 bps; same IL2P+CRC payload
// convention; RX is autobaud - the wnN suffix selects the transmit waveform only).
// Multiple --modem options share the
// audio channel and are addressed by the KISS port nibble (QtSoundModem multiplex model).
//
// The optional N:MODE:FREQ third field sets the modem's audio centre in Hz (both TX and
// RX), QtSoundModem-style - e.g. --modem 0:bpsk300:1459 places 300 BPSK at 1459 Hz to
// meet a peer that sits off the usual centre. It applies to the AFSK tone-pair modes
// (afsk*, centre = mark/space midpoint, default 1700), the BPSK/QPSK carrier modes
// (bpsk*/qpsk*, default 1500, 1650 for qpsk3600), and the spec-fixed waveforms (freedv-*,
// ms110d-*), which keep their standard centre as the default (1800 for ms110d, the OFDM
// centre for datac) and are moved by frequency translation around their unchanged DSP -
// interop is set by the RF centre, so a moved waveform is still standard on air. The
// baseband FSK families (fsk*/c4fsk*) occupy DC-to-Nyquist and have no audio centre; a
// :FREQ on those is an error, not silently ignored.
// --wav decodes a file instead of live audio (testing/corpus runs) and exits.
// --wav-loop replays a file forever at wall-clock pace as if it were the capture device -
// the whole live daemon (KISS, waterfall) runs off the recording; no soundcard needed.
//
// --waterfall serves the browser waterfall on PORT (default 8107 via the config section):
// 30 fps spectrum + waterfall over the shared passband, every modem's measured band drawn
// with its audio and RF centre marked, and each decoded frame tagged on its energy burst
// with source callsign, band SNR and frequency offset. --dial presets the rig dial
// frequency in Hz the RF scale derives from (operators can retune per-browser); the
// config section adds bind/sideband/rate knobs.
// --psk-detector selects the BPSK/QPSK detection method: coherent (default, matches the
// NinoTNC's Costas loop and noise margin) or differential (opt-in, acquires at zero preamble
// at a ~1-2 dB noise cost - for short-preamble links). See issue #5.
//
// --paging starts the POCSAG paging endpoint (DAPNET/POCSAG-compatible waveform; local
// paging API, pdn). Pages are not AX.25 frames, so they get a line-based TCP service of
// their own instead of a KISS port - one UTF-8 command per line:
//
//   PAGE <ric> <function> ALPHA <text…>     → OK <id> | ERR <reason>
//   PAGE <ric> <function> NUMERIC <text…>
//   PAGE <ric> <function> TONE
//
// Transmissions share the channel-access path (CSMA, PTT, TXDELAY) with the packet
// modems. Every page the POCSAG decoder hears on channel is broadcast to all paging
// clients as "HEARD <ric> <function> ALPHA|NUMERIC|TONE [text]". BAUD defaults to 1200
// (DAPNET); 512 and 2400 are also valid. See PagingTcpServer for the full grammar.
// (Speaking the DAPNET-core transmitter protocol is a possible future follow-up.)
//
// --ardop starts the ARDOP virtual TNC: the ardopcf-compatible TCP host interface
// (command port PORT, data port PORT+1 - Pat and other Winlink hosts connect
// unmodified). Per the dedicated-channel policy (docs/ardop-design.md §2.2) the ARDOP
// channel carries only ARDOP: --ardop is exclusive with --modem/--paging, the KISS
// server is not started, and CSMA persistence is forced to 255 - ARDOP runs its own
// channel discipline (ARQ timing budgets, negotiated leaders), which the daemon's
// p-persistence roll must never delay. PTT keying and sample-domain TX-complete are
// the channel's, exactly as for the packet modes.

// --device flex:<radio>[:slice][@station] uses a FlexRadio 6000-series over the LAN as the
// sound card + PTT: <radio> is `discover` (broadcast), an IP (host[:port]), a discovery spec
// (serial=…/name=…), or `mock` (an in-process fake, for offline testing). <slice> is a
// letter A-H (default A). The DAX transport is auto-picked from the DSP rate (24 kHz s16
// for the 12 kHz modes, 48 kHz float32 for the 48 kHz modes). The radio keys itself, so
// --ptt is rejected alongside flex:.
//
// Selection policy: with no @station the daemon OWNS the radio and brings it up HEADLESS
// (registers as a GUI client, creates its own slice - the "pdn at the radio, no SmartSDR"
// deployment; the default). --flex-freq/--flex-ant/--flex-mode set the created slice's
// working frequency (default 14.100000 MHz), antenna (ANT1) and mode (DIGU); the headless path
// also disables band persistence and explicitly tunes the slice, so it lands on the requested
// QRG regardless of the radio's last-used band. A trailing @station selects ATTACH mode:
// coexist with a running SmartSDR by binding that station's existing slice (the slice params
// are then ignored - SmartSDR configures it). --flex-daxch sets the DAX channel to claim
// (default 1) for BOTH paths; a headless client sharing a box with SmartSDR must pick a channel
// SmartSDR is not using (it grabs DAX 1). See docs/flex-integration.md §4/§8.

// --mixer-show DEVICE prints the sound card's mixer - every control it has, and the capture
// gain, AGC, mic boost and playback level this station would drive, each with the card's own dB
// range - and exits. DEVICE is the same string as --device (plughw:CARD=Device,DEV=0) or the
// card alone (hw:3). It only reads, and reading a mixer does not touch the PCM, so it answers
// "what is my card called, what can it be set to, and where is it now" on a station that is
// running. A control the card publishes no dB scale for says "no dB scale" rather than a
// figure. The "alsa" config section sets the same controls at start-up; see CONFIG.md.

// --device ubersdr:<instance> makes a RECEIVE-ONLY station out of a public UberSDR web
// receiver: <instance> is a host (m9psy-1.instance.ubersdr.org), a host:port, or the https://
// URL you would open in a browser. The daemon takes the receiver's IQ stream (iq48 - 48 kHz of
// complex baseband, ±24 kHz around the tune frequency), demodulates SSB from it in-process and
// hands the modems real audio, so every mode, the waterfall and the frame log work exactly as
// they do on a sound card. IQ rather than the instance's own audio because holding the complex
// baseband means the receive filter is the one the band plan asked for and there is no AGC in
// the path - which is what makes SNR figures off this path comparable with a soundcard's.
//
// There is no transmitter at the far end of a WebSocket. --ptt is rejected, transmissions are
// refused the moment they are queued (with that as the reason), and the band plan's dial is
// used to tune the receiver rather than printed for the operator to dial in. The "ubersdr"
// config section carries the stream's parameters (mode, password, SSB filter edges, gain).

// --uplink-token CALLSIGN mints one uplink token for that station and prints it with the hash
// that goes in this site's "monitor"."uplinks" entry, then exits. Run it on the monitor: the
// hash stays here, the token is given to that station's operator once, and nothing is written
// to any file. See CONFIG.md § monitor.uplinks.

// 9600-family and freedv-* modems need 48 kHz DSP (the FreeDV engine is native 8 kHz, and
// 48000 = 6·8000 while 12000 has no integer ratio); everything else runs at 12 kHz.

string device = "default";
int captureRate = 48000;
int kissPort = 8105;
string bindAddress = "127.0.0.1";
string sideband = "usb";
bool sidebandWasStated = false;
double? dialFrequency = null;
FrameLogConfig? frameLogConfig = null;
// 300 ms is a RADIO allowance, not a modem requirement - the modems themselves acquire
// from 0-20 ms TXDELAY in every mode (150 ms for qpsk2400 facing a NinoTNC), measured and
// CI-enforced (NinoTncParityTests; docs/ninotnc-loop.md § How short can TXDELAY be?).
// The default budgets for a real transmitter's PTT-to-RF settling, which the wired bench
// cannot see and which routinely needs 100-300 ms on FM gear. Wired links, data-port
// radios and bench rigs should configure this down; issue #3 has the full derivation.
int? txDelay = null;
string? wavPath = null;
string? wavLoopPath = null;
string? pttSpec = null;
string? configPath = null;
int? waterfallPort = null;
double? dialHz = null;
// TX test: one bounded test transmission, then exit. --two-tone SECONDS sends the 700/1900 Hz
// pair for a linearity check; --tone HZ SECONDS sends one tone, for a carrier-level check or an
// FM Bessel-null deviation check (999 Hz nulls at 2.4 kHz deviation, 500 at 1.2, 1248 at 3.0,
// 2079 at 5.0). Both go out through the station's real transmit path at its real level, so what
// is measured is what a frame gets, and both are bounded by "txTest"."maxSeconds".
double? twoToneSeconds = null;
(double Hz, double Seconds)? singleTone = null;
bool qualityFrames = false;
// PSK detection method. --psk-detector overrides it for every PSK mode; unset, the modes pick
// their measured-best default: BPSK defaults to Differential (on real off-air HF, benchmarked
// against a NinoTNC, differential + the frequency-diversity bank matches/beats coherent because
// real carriers arrive off-frequency with short preambles - reversing the coherent default of
// #5), while QPSK stays Coherent (its V.26A interop was validated coherent, #5/#6).
PskDetector? pskDetectorOverride = null;
string? pagingSpec = null;
int? ardopPort = null;
var modemSpecs = new List<string>();
// Headless FlexRadio slice params (--device flex: with no @station). Null = unset here;
// resolved against the config's Flex section, then FlexTuning defaults. --flex-daxch applies
// to both paths (headless and attach) - the DAX channel to claim.
string? flexFreq = null;
string? flexAnt = null;
string? flexMode = null;
string? flexDaxCh = null;
// --mixer-show: print a card's mixer and exit. Reading a mixer does not touch the PCM, so this
// answers "what are my card's control names and what is it set to" without stopping the station.
string? mixerShow = null;

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length
        ? args[i]
        : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--config": configPath = Next(); break;
        case "--device": device = Next(); break;
        case "--capture-rate": captureRate = int.Parse(Next()); break;
        case "--kiss": kissPort = int.Parse(Next()); break;
        case "--bind": bindAddress = Next(); break;
        case "--modem": modemSpecs.Add(Next()); break;
        case "--ptt": pttSpec = Next(); break;
        case "--txdelay": txDelay = int.Parse(Next()); break;
        case "--wav": wavPath = Next(); break;
        case "--wav-loop": wavLoopPath = Next(); break;
        case "--waterfall": waterfallPort = int.Parse(Next()); break;
        case "--dial": dialHz = double.Parse(Next()); break;
        case "--two-tone": twoToneSeconds = double.Parse(Next()); break;
        case "--tone":
            double singleToneHz = double.Parse(Next());
            singleTone = (singleToneHz, double.Parse(Next()));
            break;
        case "--quality-frames": qualityFrames = true; break;
        case "--psk-detector": pskDetectorOverride = Enum.Parse<PskDetector>(Next(), ignoreCase: true); break;
        case "--paging": pagingSpec = Next(); break;
        case "--ardop": ardopPort = int.Parse(Next()); break;
        case "--flex-freq": flexFreq = Next(); break;
        case "--flex-ant": flexAnt = Next(); break;
        case "--flex-mode": flexMode = Next(); break;
        case "--flex-daxch": flexDaxCh = Next(); break;
        case "--mixer-show": mixerShow = Next(); break;
        // Takes the callsign the token is for, and says so rather than throwing when it is
        // missing: this is often the first thing a new site owner runs.
        case "--uplink-token": return UplinkToken.Print(i + 1 < args.Length ? args[++i] : null);
        case "--help":
            Console.WriteLine("see source header for usage");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

// Before anything else is built: this reads a card and exits, and it deliberately works while
// another process holds the PCM, which is the state a station's mixer is usually asked about in.
if (mixerShow is not null)
{
    string showCard = AlsaMixer.CardFor(mixerShow);
    if (!AlsaMixer.TryOpen(showCard, out AlsaMixer? showMixer, out string showWhy))
    {
        Console.Error.WriteLine($"{MixerSetup.JournalPrefix}{showCard} has no mixer ({showWhy})");
        return 1;
    }

    using (showMixer)
    {
        // Guarded for the same reason the start-up call is: TryOpen catches a missing symbol
        // among the ten entry points it uses, and Apply reaches twenty more.
        if (MixerSetup.TryApply(showMixer!, new MixerSettings(), Console.WriteLine, out _) is null)
        {
            return 1;
        }
    }

    return 0;
}

var modems = new List<ModemConfig>();
PttConfig? pttConfig = null;
AlsaConfig? alsaConfig = null;
PagingConfig? paging = null;
FlexConfig? flexConfig = null;
UberSdrConfig? uberSdrConfig = null;
WaterfallConfig? waterfallConfig = null;
PublishConfig? publishConfig = null;
SurveyConfig? surveyConfig = null;
MetricsConfig? metricsConfig = null;
FrequencyMatchingConfig? frequencyMatching = null;
RawCaptureConfig? rawCaptureConfig = null;
DeadFeedConfig? deadFeedConfig = null;
bool idBeacons = true;
ApiConfig? apiConfig = null;
TxTestConfig txTestConfig = new();
// What this process is running, verbatim, so the API can serve it back rather than re-serialise
// a parsed object and quietly lose whatever the operator wrote that this version ignores.
string apiConfigJson = "{}";
bool apiEphemeralInForce = false;

if (configPath is not null)
{
    // A one-run configuration left by the API takes precedence for exactly this start-up, and is
    // deleted the moment it has been read - see ConfigApi.ConsumePending for why that ordering is
    // the safety property rather than a detail.
    string? pendingPath = ConfigApi.PendingPath(configPath);
    string loadPath = pendingPath ?? configPath;
    apiEphemeralInForce = pendingPath is not null;

    // A bad config is an operator typo, not a bug: explain it and exit 2. The unit's
    // RestartPreventExitStatus=2 stops systemd retrying, so the journal carries one
    // readable explanation instead of a stack trace every RestartSec.
    // Read before consuming: the API serves back what this process is actually running, and for
    // a one-run configuration the file it came from is about to be deleted.
    apiConfigJson = File.Exists(loadPath) ? File.ReadAllText(loadPath) : "{}";

    DaemonConfig? config = DaemonConfig.TryLoad(loadPath, out string configError);
    if (pendingPath is not null)
    {
        ConfigApi.ConsumePending(configPath);
    }

    if (config is null)
    {
        Console.Error.WriteLine(configError);
        return 2;
    }

    foreach (string warning in config.Warnings)
    {
        Console.Error.WriteLine($"config: WARNING - {warning}");
    }

    device = config.Device;
    captureRate = config.CaptureRate;
    kissPort = config.KissPort;
    bindAddress = config.Bind;
    sideband = config.Sideband;
    sidebandWasStated = config.SidebandWasStated;
    dialFrequency = config.DialFrequency;
    frameLogConfig = config.FrameLog;
    modems = config.Modems;
    pttConfig = config.Ptt;
    alsaConfig = config.Alsa;
    paging = config.Paging;
    flexConfig = config.Flex;
    uberSdrConfig = config.UberSdr;
    waterfallConfig = config.Waterfall;
    publishConfig = config.Publish;
    apiConfig = config.Api;
    surveyConfig = config.Survey;
    metricsConfig = config.Metrics;
    frequencyMatching = config.FrequencyMatching;
    rawCaptureConfig = config.RawCapture;
    deadFeedConfig = config.DeadFeed;
    idBeacons = config.IdBeacons;
    txTestConfig = config.TxTest;
    ardopPort ??= config.Ardop?.Port;
    Console.WriteLine($"config: {configPath}");

    // Before anything asks the catalogue a question - the DSP-rate decision below, the band
    // planner, the transmit-filter plan - because a mode that is not registered yet is a mode
    // that does not exist. Each path is one the config named: nothing is discovered.
    foreach (ModemPluginConfig pluginConfig in config.ModemPlugins)
    {
        ModemPluginLoad load = ModemPluginLoader.Load(pluginConfig.Path);
        if (load.Loaded)
        {
            // The handle is deliberately not kept: plugins load for the life of the process, and
            // a station that unloaded a modem under its own audio would have nothing good to do
            // with the samples already in flight.
            Console.WriteLine(
                $"modem plugin: {load.PluginId} from {load.Path} "
                + $"[{string.Join(", ", load.Modes)}]");
        }
        else
        {
            // Named and non-fatal. A modem entry that wanted one of its modes still fails
            // start-up below, by name, as an unknown mode - which is the right place for it,
            // because that is the station asking for something it cannot do.
            Console.Error.WriteLine($"modem plugin: FAILED {load.Path} - {load.Failure}");
        }
    }

    // Flavour B: this file describes a monitor rather than a station, so nothing below applies to
    // it. There is no device to open, no PTT to check, no KISS port to bind and no transmitter to
    // plan a filter for; the monitor host owns the rest of this process. Dispatched here, after
    // the plugins are loaded and before the first line of station start-up, which is what keeps
    // the station path exactly as it was.
    if (config.Monitor is not null)
    {
        if (twoToneSeconds is not null || singleTone is not null)
        {
            Console.Error.WriteLine(
                "tx test: refused, this configuration describes a monitor rather than a station "
                + "- it fronts other people's web receivers and has no transmitter of its own");
            return 2;
        }

        return await MonitorStartup.RunAsync(config);
    }
}

// One bounded test transmission and out, for a bench with no browser on it. The station is
// built exactly as it would be to carry traffic - same device, same PTT, same level - because
// the whole point is to measure what a frame gets; what does not come up is anything that
// serves somebody else. See TxTestRunner, and the "txTest" section of CONFIG.md.
Packet.SoundModem.Waterfall.TxTestRequest? benchTxTest = null;
if (twoToneSeconds is not null && singleTone is not null)
{
    Console.Error.WriteLine("--two-tone and --tone are two ways to run one test; give one of them");
    return 2;
}
else if (twoToneSeconds is double twoToneSecondsAsked)
{
    benchTxTest = new Packet.SoundModem.Waterfall.TxTestRequest(true, 0, twoToneSecondsAsked);
}
else if (singleTone is { } singleToneAsked)
{
    benchTxTest = new Packet.SoundModem.Waterfall.TxTestRequest(
        false, singleToneAsked.Hz, singleToneAsked.Seconds);
}

// --bind is not validated anywhere the way a config file's "bind" is, so an address that is not
// an address used to travel all the way to a null-forgiving ParseBind and surface as a
// NullReferenceException from the KISS listener - or, once the waterfall gained a warning that
// had to know whether the bind was loopback, as a FormatException before that. Said once, here,
// in the same words the config file's own check uses.
if (DaemonConfig.ParseBind(bindAddress) is null)
{
    Console.Error.WriteLine(
        $"--bind {bindAddress} is not an IP address. Use \"127.0.0.1\" for loopback only, "
        + "\"*\" for every interface, or the address of one interface.");
    return 2;
}

// A web receiver has no transmitter, so a bench test on one is refused before anything is built
// rather than after the station has come up around a page that will not exist.
if (benchTxTest is not null && device.StartsWith("ubersdr:", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        "tx test: refused, this station's audio comes from a web receiver, which is a receiver "
        + "and has no transmitter - there is nothing here to key");
    return 1;
}

// --waterfall/--dial override (or stand in for) the config's waterfall section.
if (waterfallPort is int wfPort)
{
    waterfallConfig ??= new WaterfallConfig();
    waterfallConfig.Port = wfPort;
}

if (dialHz is double dial)
{
    if (waterfallConfig is null)
    {
        Console.Error.WriteLine("--dial only means something with a waterfall (--waterfall PORT)");
        return 2;
    }

    waterfallConfig.DialFrequencyHz = dial;
}

// Headless FlexRadio slice params: CLI flags override the config's Flex section, which
// overrides FlexTuning's defaults (14.100000 MHz / ANT1 / DIGU / DAX 1). --flex-daxch applies
// to both headless and attach.
var flexTuning = new FlexTuning
{
    Frequency = flexFreq ?? flexConfig?.Frequency ?? "14.100000",
    Antenna = flexAnt ?? flexConfig?.Antenna ?? "ANT1",
    Mode = flexMode ?? flexConfig?.Mode ?? "DIGU",
    // Unset, a headless client stays off DAX 1: SmartSDR takes that one and the two contend,
    // so defaulting elsewhere makes the order they are started in stop mattering. Attach mode
    // is SmartSDR's slice by definition, so it keeps 1.
    DaxChannel = flexDaxCh ?? flexConfig?.DaxChannel
        ?? (FlexDevice.IsFlex(device) && FlexDevice.Parse(device).Headless
            ? FlexConfig.DefaultHeadlessDaxChannel
            : "1"),
    TxPowerWatts = flexConfig?.TxPowerWatts,
    // A stated cut-off is used as it stands; 0 means "leave the radio's own filter alone", and
    // unset (the usual case) is filled in below from what the modems actually occupy.
    TransmitFilterHighHz = flexConfig?.TransmitFilterHighHz is > 0
        ? flexConfig.TransmitFilterHighHz
        : null,
    StationName = flexConfig?.StationName ?? "pdn-soundmodem",
    Arbitration = flexConfig?.Arbitration ?? false,
    ReceiveOnly = flexConfig?.ReceiveOnly ?? false,
};

// Nothing stated: the daemon works the transmit filter out rather than inheriting whatever the
// radio was last left on.
bool deriveTransmitFilter = flexConfig?.TransmitFilterHighHz is null;

// Caught here rather than at the radio, which answers an out-of-range power with a bare
// protocol code and no hint about which setting produced it.
if (flexTuning.TxPowerWatts is double requestedWatts
    && (requestedWatts < 0 || requestedWatts > FlexDevice.PaWatts))
{
    Console.Error.WriteLine(
        $"\"flex\".\"txPowerWatts\" is {requestedWatts:0.#} W, outside the 0-{FlexDevice.PaWatts:F0} W "
        + "a 6000-series PA can produce.");
    return 2;
}

if (pagingSpec is not null)
{
    string[] pagingParts = pagingSpec.Split(':');
    paging = new PagingConfig
    {
        Port = int.Parse(pagingParts[0]),
        Baud = pagingParts.Length > 1 ? int.Parse(pagingParts[1]) : 1200,
    };
}

foreach (string spec in modemSpecs)
{
    string[] specParts = spec.Split(':');
    if (specParts.Length > 3
        || !int.TryParse(specParts[0], out int specSubChannel)
        || (specParts.Length > 2 && !double.TryParse(specParts[2], out _)))
    {
        // The commonest way to land here is a plugin mode: --modem 0:ofdm-fm:nb splits into
        // three parts and "nb" is not a frequency. That is not a typo to correct, it is a
        // grammar collision - this option's own separator is the one a plugin mode uses - so
        // say which way out there is rather than printing a number-format exception.
        Console.Error.WriteLine($"--modem {spec} is not N:MODE[:FREQ]");
        Console.Error.WriteLine(
            "  a plugin mode is written pluginId:mode and already contains the ':' this option "
            + "separates on, so it cannot be spelled here - put it in a config file, which is "
            + "where \"modemPlugins\" has to name the plugin anyway");
        return 2;
    }

    modems.Add(new ModemConfig
    {
        SubChannel = specSubChannel,
        Mode = specParts.Length > 1 ? specParts[1] : "afsk1200",
        Frequency = specParts.Length > 2 ? double.Parse(specParts[2]) : null,
    });
}

// ARDOP now shares the channel with the packet modems instead of excluding them: it is a
// modem entry like any other, and an ARQ session holds packet transmissions off the air
// through the channel's TransmitInhibit rather than through a config-level ban.
// On a Flex the slice mode states the sideband, so it is not something to be configured
// separately and disagreed with: DIGL alongside the default "usb" would mirror every modem
// about the dial and say nothing.
if (FlexDevice.IsFlex(device) && FlexDevice.Parse(device).Headless)
{
    string? impliedSideband = flexTuning.Mode.ToUpperInvariant() switch
    {
        "DIGU" or "USB" => "usb",
        "DIGL" or "LSB" => "lsb",
        _ => null,
    };

    if (impliedSideband is not null)
    {
        bool statedExplicitly = sidebandWasStated;
        if (statedExplicitly && !string.Equals(sideband, impliedSideband, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"\"sideband\": \"{sideband}\" contradicts the Flex slice mode {flexTuning.Mode}, "
                + $"which is {impliedSideband.ToUpperInvariant()}. Every modem would land mirrored "
                + "about the dial. Drop \"sideband\" - the slice mode already says which it is.");
            return 2;
        }

        sideband = impliedSideband;
    }
}

ModemConfig? ardopModem = modems.FirstOrDefault(m => DaemonConfig.IsArdop(m.Mode));
if (ardopModem is not null && ardopPort is not null)
{
    Console.Error.WriteLine(
        "ARDOP is configured twice - as a modem and with --ardop/\"ardop\". Keep the modem entry.");
    return 2;
}

if (ardopModem is null && ardopPort is not null)
{
    // The top-level section / --ardop remains supported: fold it into a modem entry so there
    // is one code path from here on.
    ardopModem = new ModemConfig
    {
        Mode = "ardop",
        Port = ardopPort,
        SubChannel = Enumerable.Range(0, 16).First(n => modems.All(m => m.SubChannel != n)),
    };
    modems.Add(ardopModem);
}

if (modems.Count == 0)
{
    modems.Add(new ModemConfig());
}
// Where the station's own lines go. This process runs exactly one station, so its journal
// carries no tag and reads byte for byte as it did before there was a Station type; a host
// running several gives each one its slug, so fifty of them in one journal are readable.
var stationJournal = StationJournal.Console();

// Mode names are checked, and the rate the shared channel runs at is settled, before the band
// planner and the transmit-filter plan: both ask the catalogue what a mode occupies, and the
// catalogue answers an unknown mode with its defaults.
if (!StationFactory.TryResolveDspRate(modems, stationJournal, out int DspRate))
{
    return 2;
}

// The one "publish" check that could not be made while the file was being read: the audio is
// decimated to the published rate rather than resampled, so that rate has to divide the channel's,
// and the channel's is not settled until the modem set is - after any modem plugins have loaded.
if (publishConfig is not null
    && DaemonConfig.PublishRateProblem(publishConfig, DspRate) is { } publishRateProblem)
{
    // Through the same frame every other refusal in the file gets - the file name, the recovery
    // text and the CONFIG.md link - because where a check had to live is not something an
    // operator reading journalctl should be able to tell.
    Console.Error.WriteLine(DaemonConfig.ConfigurationError(configPath!, publishRateProblem));
    return 2;
}

// A FlexRadio provides its own DAX sample clock (24/48 kHz auto-picked from the DSP rate), and
// an UberSDR its own 48 kHz IQ clock, so --capture-rate (an ALSA concept) does not apply to
// either.
bool deviceIsFlex = FlexDevice.IsFlex(device);
// Headless is the deployment where the daemon owns the radio, and so the only one where it sets
// the dial and the transmit filter - in attach mode SmartSDR owns the slice and we would be
// fighting it.
bool flexIsHeadless = deviceIsFlex && FlexDevice.Parse(device).Headless;
bool deviceIsUberSdr = UberSdrDevice.IsUberSdr(device);
UberSdrEndpoint uberSdrEndpoint = default;
if (deviceIsUberSdr)
{
    try
    {
        uberSdrEndpoint = UberSdrDevice.Parse(device);
    }
    catch (InvalidDataException malformed)
    {
        Console.Error.WriteLine(malformed.Message);
        return 2;
    }

    if (uberSdrConfig?.OnDemand == true)
    {
        if (uberSdrConfig.LingerSeconds < 0)
        {
            Console.Error.WriteLine("\"ubersdr\".\"lingerSeconds\" cannot be negative");
            return 2;
        }

        if (waterfallConfig is null)
        {
            Console.Error.WriteLine(
                "\"ubersdr\".\"onDemand\" needs a \"waterfall\" section: the page's viewers are "
                + "what asks for the receiver, and without one the station would never hear anything");
            return 2;
        }
    }
}

if (!deviceIsFlex && !deviceIsUberSdr && captureRate % DspRate != 0)
{
    Console.Error.WriteLine($"--capture-rate must be a multiple of {DspRate}");
    return 2;
}

// A configuration written in RF terms becomes a dial plus an audio centre per modem. Done
// here because it needs the DSP rate (to measure each mode's real occupied width) and must
// land before the modems are built with those centres.
RfPlan.Result? bandPlan;
try
{
    // How far up the audio band this station can be planned. A rig the daemon cannot touch gets
    // the nominal SSB window; a headless Flex, whose transmit and receive filters the daemon sets
    // itself, gets the radio's own ceiling - which is what lets a 3 kHz waveform be planned at all,
    // since one cannot fit inside an ordinary passband however the dial is chosen.
    bandPlan = BandPlanner.Plan(
        modems, sideband, dialFrequency, DspRate,
        flexIsHeadless ? Passband.WideCeilingHz : null);
}
catch (InvalidDataException planFailure)
{
    Console.Error.WriteLine($"band plan: {planFailure.Message}");
    return 2;
}

// Where a self-tuning receiver has to point. A band plan says it outright; failing that the
// operator has to, because an SDR has no dial of its own to read a number off.
double? receiveDialHz = bandPlan?.DialHz ?? dialFrequency;
if (deviceIsUberSdr && receiveDialHz is null)
{
    Console.Error.WriteLine(
        $"the UberSDR instance at {uberSdrEndpoint} has to be told where to listen. Give every "
        + "modem an \"rfFrequency\" and the dial is worked out from them, or set "
        + "\"dialFrequency\" to pin it - unlike a radio there is no dial already set to read off.");
    return 2;
}

if (bandPlan is not null)
{
    BandPlanner.Report(bandPlan, Console.Out, radioIsSelfTuning: flexIsHeadless || deviceIsUberSdr);
    foreach (string warning in bandPlan.Warnings)
    {
        Console.Error.WriteLine($"band plan: WARNING - {warning}");
    }

    // A Flex is its own dial: rather than telling the operator to set it, set it. Headless
    // only - in attach mode SmartSDR owns the slice and we would be fighting it. The transmit
    // filter is a global, persistent radio setting, so state its high cut from the plan or a
    // previous session's narrow filter silently truncates the top of the band.
    if (flexIsHeadless)
    {
        // Overwriting a stated slice frequency without a word would leave someone upgrading a
        // working Flex config with a number that has quietly stopped meaning anything.
        if (flexConfig?.Frequency is not null || flexFreq is not null)
        {
            Console.Error.WriteLine(
                $"flex: WARNING - the slice frequency you set ({flexFreq ?? flexConfig!.Frequency}) "
                + "is superseded by the band plan, which computed "
                + $"{RfPlan.Mhz(bandPlan.DialHz)}. Remove it, or remove the modems' "
                + "\"rfFrequency\" if you meant to place them by audio centre.");
        }

        int filterHigh = BandPlanner.TransmitFilterHighHz(bandPlan);
        flexTuning = flexTuning with
        {
            Frequency = (bandPlan.DialHz / 1_000_000).ToString("F6", CultureInfo.InvariantCulture),
            // A stated cut-off is the operator overruling the plan, which is theirs to do - the
            // clipping check below still says so if it truncates a modem.
            TransmitFilterHighHz = deriveTransmitFilter ? filterHigh : flexTuning.TransmitFilterHighHz,
        };
        Console.WriteLine(
            $"flex: setting the slice to {RfPlan.Mhz(bandPlan.DialHz)}"
            + (deriveTransmitFilter ? $" and the transmit filter high cut to {filterHigh} Hz" : "")
            + " from the band plan");
    }
}

var channel = new SoundModemChannel(DspRate);
if (deviceIsUberSdr)
{
    // Said once, here, so every path that could put something on the air - KISS, paging, ARDOP -
    // gets the same answer for the same reason, rather than each discovering it differently.
    channel.ReceiveOnlyReason =
        $"this station receives only: its audio comes from the UberSDR instance at "
        + $"{uberSdrEndpoint}, which is a receiver and has no transmitter.";
}

// Channel access (TXDELAY, P, SLOTTIME, TXTAIL) belongs to the host, which sets it over KISS
// at runtime - see KissTcpServer. The library's defaults stand until it does; there is
// deliberately no configuration-file equivalent. --txdelay remains as a bench override for
// runs with no host attached.
if (txDelay is int txDelayOverride)
{
    channel.Csma.TxDelayMilliseconds = txDelayOverride;
}

// Where each modem sits, for the log's audio_hz/rf_hz columns. Declared out here because both
// halves of the log - what was heard and what was sent - fill those columns from it, and the
// transmit side is wired further down with the rest of the activity reporting.
var frameLogRfByModem = modems.ToDictionary(
    m => m.SubChannel,
    m => (Audio: m.Frequency, Rf: m.RfFrequency));

// Every frame the station hears, written down.
FrameLog? frameLog = null;
if (frameLogConfig is not null
    && !StationFactory.TryOpenFrameLog(
        frameLogConfig.Path, modems, channel, stationJournal, out frameLog))
{
    return 2;
}

await using var frameLogLifetime = frameLog;

// The PSK detector, for the informational print below: --psk-detector overrides it; unset,
// the catalogue default applies, which is differential for every PSK family (see
// ModemCatalog.DefaultDetectorFor for the bench evidence - an earlier comment here still
// said "QPSK coherent" long after the catalogue changed its mind, which is the kind of
// drift asking the catalogue directly avoids).
PskDetector bpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("bpsk300");
PskDetector qpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("qpsk2400");

// Every configured modem onto the channel, and a line each saying what was built. Shared with
// the many-receiver flavour, which builds the same modems for every receiver it fronts: two
// copies of this loop would be two answers to what a config actually runs.
if (!StationFactory.TryAddModems(channel, modems, DspRate, pskDetectorOverride, stationJournal))
{
    return 2;
}

if (modems.Any(m => m.Mode.StartsWith("bpsk", StringComparison.Ordinal)))
{
    Console.WriteLine($"psk detector (bpsk): {bpskDetector.ToString().ToLowerInvariant()}"
        + (pskDetectorOverride is null ? " [default]" : " [--psk-detector]"));
}

if (modems.Any(m => m.Mode.StartsWith("qpsk", StringComparison.Ordinal)))
{
    Console.WriteLine($"psk detector (qpsk): {qpskDetector.ToString().ToLowerInvariant()}"
        + (pskDetectorOverride is null ? " [default]" : " [--psk-detector]"));
}

// Morse identification, per modem. A data waveform carries nothing a listener can read without
// our software, so a station running one on a real antenna owes anyone sharing the band a
// callsign in a form they can copy by ear. Off unless a modem asks for it: the modes that
// already identify themselves in-band need nothing, and a station that transmits nothing owes
// nothing (see StationIdentifier for why the clock only runs while the modem is transmitting).
var identifiers = new Dictionary<int, StationIdentifier>();
foreach (ModemConfig modemConfig in modems)
{
    if (modemConfig.Identify is not IdentifyConfig id)
    {
        continue;
    }

    int subChannel = modemConfig.SubChannel;
    if (channel.ReceiveOnlyReason is not null)
    {
        // The same shape of refusal "ptt" gets on this device, and for the same reason: there is
        // no transmitter to identify. Queueing one would only turn "cannot" into a timeout.
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" needs a transmitter, and this station receives "
            + "only - drop it, or point the station at a radio");
        return 2;
    }

    if (DaemonConfig.IsArdop(modemConfig.Mode))
    {
        // ARDOP transmits through the delegate path as a whole virtual TNC, so none of its
        // bursts raise the per-sub-channel event this policy counts. Rather than accept the
        // setting and never identify, say so: a silent no-op on a licence condition is the
        // worst of the three outcomes.
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" is not supported on ardop - its ARQ bursts do "
            + "not go out as addressed frames, so there is nothing here to count transmissions "
            + "against. Identify on a packet modem sharing the channel instead.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(id.Callsign))
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" needs a \"callsign\" - there is no default for a "
            + "licence condition");
        return 2;
    }

    if (id.ToneHz is not null && id.RfFrequency is not null)
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" sets both \"toneHz\" and \"rfFrequency\" - they "
            + "say the same thing two ways. Keep one.");
        return 2;
    }

    if (id.RfFrequency is not null && bandPlan is null)
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\".\"rfFrequency\" needs a band plan - without one "
            + "the daemon does not know this station's dial, so it cannot turn an RF frequency "
            + "into a tone. Give the modems \"rfFrequency\", pin \"dialFrequency\", or set "
            + "\"identify\".\"toneHz\" instead.");
        return 2;
    }

    double? toneHz =
        id.ToneHz
        ?? (id.RfFrequency is double identRf && bandPlan is not null
            ? (bandPlan.IsUpperSideband ? identRf - bandPlan.DialHz : bandPlan.DialHz - identRf)
            : modemConfig.Frequency);

    if (toneHz is not double tone)
    {
        // A baseband mode occupies the audio band from DC up and has no centre to borrow.
        Console.Error.WriteLine(
            $"modem {subChannel}: mode '{modemConfig.Mode}' has no audio centre, so there is "
            + "nothing to default the ident tone to - set \"identify\".\"toneHz\"");
        return 2;
    }

    StationIdentifier identifier;
    try
    {
        identifier = new StationIdentifier(
            id.Callsign!,
            id.IncludeMode ? modemConfig.Mode : null,
            tone,
            id.Wpm,
            TimeSpan.FromMinutes(id.IntervalMinutes),
            DspRate,
            id.Amplitude);
    }
    catch (Exception bad) when (bad is ArgumentException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine($"modem {subChannel}: \"identify\" is not usable");
        Console.Error.WriteLine($"  {bad.Message}");
        return 2;
    }

    identifiers[subChannel] = identifier;
    Console.WriteLine(
        $"modem {subChannel}: identifying as {identifier.Text} in CW @ {tone:F0} Hz"
        + (bandPlan is not null
            ? $" = {RfPlan.Mhz(bandPlan.IsUpperSideband ? bandPlan.DialHz + tone : bandPlan.DialHz - tone)}"
            : "")
        + $", {id.Wpm:F0} wpm, every {id.IntervalMinutes:F0} min while transmitting "
        + $"({identifier.DurationSeconds:F1} s)");

    if (bandPlan is not null && (tone < bandPlan.Window.LowHz || tone > bandPlan.Window.HighHz))
    {
        // Not fatal: the window is what the plan fitted the modems into, and an operator who
        // moved the ident deliberately may know something the planner does not. But on a Flex
        // the transmit filter is set from that window, so an ident outside it goes out truncated
        // or not at all, and nothing else would say so.
        Console.Error.WriteLine(
            $"modem {subChannel}: WARNING - the ident tone {tone:F0} Hz is outside the "
            + $"{bandPlan.Window.LowHz:F0}-{bandPlan.Window.HighHz:F0} Hz passband this plan "
            + "plays into, so it may be filtered away on transmit. Leave \"toneHz\" unset to "
            + "identify on this modem's own centre.");
    }
}

// Where each modem sits in the audio band, for the radio's transmit filter: from the plan when
// there is one, else measured off the modems as configured. Flex only - it is the one device
// whose transmit filter the daemon can see, and the measurement costs a modulate per modem.
IReadOnlyList<TransmitFilterPlan.Band> txBands = !deviceIsFlex
    ? []
    : bandPlan is not null
        ? [.. bandPlan.Modems.Select(m => new TransmitFilterPlan.Band(
            m.Slot.SubChannel, m.Slot.Mode,
            m.AudioCentreHz - (m.Slot.BandwidthHz / 2),
            m.AudioCentreHz + (m.Slot.BandwidthHz / 2)))]
        : TransmitFilterPlan.Bands(modems, DspRate);

// The transmit filter is a global, persistent radio setting - whatever last touched the radio -
// so a station placed by audio centre inherits some previous session's filter, and a mode wider
// than it (ms110d-* reaches past 3.1 kHz against a 3000 Hz default) is truncated on air with
// nothing said. The band-planned path states the high cut above; this is the same for a station
// with no plan to read it off.
if (flexIsHeadless && deriveTransmitFilter && flexTuning.TransmitFilterHighHz is null
    && TransmitFilterPlan.HighCutFor(txBands) is int derivedFilterHigh)
{
    TransmitFilterPlan.Band widest = txBands.MaxBy(b => b.HighHz);
    flexTuning = flexTuning with { TransmitFilterHighHz = derivedFilterHigh };
    Console.WriteLine(
        $"flex: setting the transmit filter high cut to {derivedFilterHigh} Hz - modem "
        + $"{widest.SubChannel} ({widest.Mode}) reaches {widest.HighHz:F0} Hz");
}

// The receive half of the same question, and the half that cannot be seen from the transmit side:
// the slice's own filter decides what reaches the modems, so a slice left on an ordinary data
// filter hears nothing above ~3 kHz however wide the transmit filter is opened. Slice state rather
// than global, so unlike the transmit filter this one is ours to set without affecting anything
// else the radio does.
if (flexIsHeadless && txBands.Count > 0)
{
    int receiveLow = BandPlanner.LowCutClearing(txBands.Min(b => b.LowHz));
    int receiveHigh = BandPlanner.HighCutClearing(txBands.Max(b => b.HighHz));
    flexTuning = flexTuning with
    {
        ReceiveFilterLowHz = receiveLow,
        ReceiveFilterHighHz = receiveHigh,
    };
    Console.WriteLine(
        $"flex: setting the slice receive filter to {receiveLow}-{receiveHigh} Hz, to hear "
        + "everything the modems are placed across");
}

// The transmit side has no per-frame quality to report the mode from, so it comes from the
// configuration - the modem that owns the sub-channel.
Dictionary<int, string> modeBySubChannel = modems
    .Where(m => !DaemonConfig.IsArdop(m.Mode))
    .ToDictionary(m => m.SubChannel, m => m.Mode);

// What the station is doing, one line per frame, in the journal.
StationFactory.JournalReceivedFrames(channel, stationJournal);

// How far off our centre each station we hear is transmitting. Measured always: it costs a
// dictionary write per frame, it is the evidence for whether answering them off-centre is worth
// doing, and the transmit side below will not act without it.
var stationOffsets = new StationFrequencyOffsets
{
    MaxSamples = frequencyMatching?.Samples ?? FrequencyMatchingConfig.DefaultSamples,
    MaxAge = TimeSpan.FromSeconds(
        frequencyMatching?.MaxAgeSeconds ?? FrequencyMatchingConfig.DefaultMaxAgeSeconds),
};
// Replay what the station already knew. A restart otherwise starts deaf to the channel's
// frequencies and cannot correct for anybody until it has heard each station afresh - which
// bites hardest when calling a station it has not heard yet, exactly when the correction would
// have helped. The frames come back with their original timestamps, so the age window still
// governs: a log full of yesterday's traffic seeds nothing, and only a restart short enough for
// those frames to still be current carries anything over. Which is the case that matters, since
// the usual reason this process restarts is that somebody upgraded it.
if (frameLog is not null)
{
    int seeded = 0;
    var seenStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (LoggedFrame logged in frameLog.Recent(500))
    {
        if (logged.Transmitted
            || logged.OffsetHz is not double loggedOffset
            || string.IsNullOrWhiteSpace(logged.From))
        {
            continue;
        }

        stationOffsets.Record(logged.From, loggedOffset, logged.HeardAt);
        seenStations.Add(logged.From);
        seeded++;
    }

    if (seeded > 0)
    {
        int current = stationOffsets.Snapshot().Count;
        Console.WriteLine(
            $"frequency offsets: replayed {seeded} logged frames from {seenStations.Count} "
            + $"station(s); {current} still current enough to use");
    }
}

channel.FrameReceivedWithQuality += (_sub, frame, quality) =>
{
    if (quality.FrequencyOffsetHz is double offsetHz
        && Ax25AddressParser.TryParse(frame, out string source, out string _dest))
    {
        stationOffsets.Record(source, offsetHz);
    }
};

// Answer an off-frequency station where its receiver is listening. On unless switched off,
// including when the section is absent entirely: the shift is clamped to a few tens of Hz, which
// is less than several stations on the band are already scattered across, and the station it
// helps is the one at the other end with a fixed-centre modem.
FrequencyMatchingConfig fmConfig = frequencyMatching ?? new FrequencyMatchingConfig();
if (fmConfig.Enabled)
{
    FrequencyMatchingConfig fm = fmConfig;
    Console.WriteLine(
        $"frequency matching: on - answering a station on its own frequency after "
        + $"{fm.MinSamples} frames, if they agree within {fm.MaxSpreadHz:F0} Hz, "
        + $"up to {fm.MaxTrimHz:F0} Hz in full ({fm.Damping:0.##} for any station that has "
        + "moved under it); backs off for "
        + $"{fm.ChaseCooldownSeconds / 60:F0} min from any station whose own frequency then moves "
        + $"more than {fm.ChaseThresholdHz:F0} Hz, and stops after {fm.MaxChases} such moves");

    var matching = new FrequencyMatchingPolicy(
        stationOffsets,
        new FrequencyMatchingOptions
        {
            MinSamples = fm.MinSamples,
            MaxSpreadHz = fm.MaxSpreadHz,
            MaxTrimHz = fm.MaxTrimHz,
            Damping = fm.Damping,
            ChaseThresholdHz = fm.ChaseThresholdHz,
            ChaseCooldown = TimeSpan.FromSeconds(fm.ChaseCooldownSeconds),
            MaxChases = fm.MaxChases,
        },
        TimeProvider.System);

    matching.StoodDown += stand =>
        Console.Error.WriteLine(
            stand.RetryAfter is TimeSpan retry
                ? $"frequency matching: backing off {stand.Callsign} for "
                    + $"{retry.TotalMinutes:0} min (move {stand.Chases}) - {stand.Detail}"
                : $"frequency matching: giving up on {stand.Callsign} - {stand.Detail}");

    channel.TransmitTrimHz = (_sub, frame) =>
    {
        if (!Ax25AddressParser.TryParse(frame, out string _src, out string destination))
        {
            return 0;
        }

        // A beacon or an ID is for everybody, and aiming it at one correspondent's oscillator
        // aims it away from every other listener. Strip the SSID before comparing: ID-1 is as
        // much a broadcast as ID.
        string bare = destination.Split('-')[0];
        return FrequencyMatchingConfig.BroadcastDestinations.Contains(
            bare, StringComparer.OrdinalIgnoreCase)
            ? 0
            : matching.TrimFor(destination);
    };
}
// One transmitter at a time makes every receiver on this radio deaf, however many there are. So
// after a frame that will be answered, stay off the air long enough for the answer to arrive
// rather than keying over it with somebody else's traffic. Carrier sense cannot do this: at the
// moment we would roll p-persistence the reply has not started, so there is nothing to hear.
// Only AX.25 traffic gets a hold - Ax25ReplyExpectation says "no opinion" for anything it cannot
// read as AX.25, and no opinion means the channel behaves exactly as it always did.
channel.QuietAfterTransmit = (_sub, frame) =>
    Packet.SoundModem.Modems.Ax25ReplyExpectation.ExpectsReply(frame)
        ? channel.TurnaroundHold
        : null;

channel.FrameTransmittedWithTrim += (subChannel, frame, trimHz) =>
{
    Console.WriteLine(ActivityLog.Transmitted(
        subChannel, modeBySubChannel.GetValueOrDefault(subChannel, "?"), frame, trimHz));

    // And into the station's journal, alongside what it heard: a log that records every frame
    // received and none sent is half a record. Raised after the audio has gone to the device, so
    // a logged row is a frame that actually went on air. Same placement lookup as the receive
    // side, so audio_hz/rf_hz mean the same thing in both directions.
    //
    // The mode is the catalogue identity of what went on air - the modem's own report of
    // itself minus the diversity bank's branch-count suffix, which is receiver construction
    // rather than a property of the transmission (issue #343). That is the spelling the
    // receive side writes into this column (FrameQuality.Mode) and the spelling the waterfall
    // stamps on its TX rows, so one modem's traffic stays under one spelling and "everything
    // on bpsk300-il2pc today" is one query that includes our own. The configured name the
    // console line uses ("bpsk300") stays out of it, as before.
    (double? audio, double? rf) = frameLogRfByModem.TryGetValue(subChannel, out var placement)
        ? placement
        : (null, null);
    frameLog?.RecordTransmitted(
        subChannel,
        frame,
        channel.Modems.TryGetValue(subChannel, out IModem? sender)
            ? ModeNames.Identity(sender.Mode)
            : modeBySubChannel.GetValueOrDefault(subChannel, "?"),
        audio,
        rf,
        trimHz == 0 ? null : trimHz);
};
// Dropped frames are rate-limited per reason. A station that loses its slice rejects every
// frame it is handed, for as long as the fault lasts: the unmitigated version of this line
// wrote 10,532 identical entries over six days on GB7RDG, which buried the one line that
// mattered and told a reader nothing the first line had not. The first drop of a reason is
// always printed; repeats are counted and folded into a periodic line.
var dropGate = new object();
var dropSuppressed = new Dictionary<string, int>(StringComparer.Ordinal);
var dropLastLogged = new Dictionary<string, long>(StringComparer.Ordinal);
const long DropLogIntervalMs = 60_000;

channel.TransmitRejected += (subChannel, frame, reason) =>
{
    string key = reason.Message;
    bool report;
    int suppressed = 0;
    lock (dropGate)
    {
        long now = Environment.TickCount64;
        report = !dropLastLogged.TryGetValue(key, out long last)
            || now - last >= DropLogIntervalMs;
        if (report)
        {
            dropLastLogged[key] = now;
            dropSuppressed.Remove(key, out suppressed);
        }
        else
        {
            dropSuppressed[key] = dropSuppressed.GetValueOrDefault(key) + 1;
        }
    }

    if (!report)
    {
        return;
    }

    Console.Error.WriteLine(
        ActivityLog.Dropped(subChannel, frame, reason)
        + (suppressed > 0 ? $" (and {suppressed} more like it in the last minute)" : ""));
};

if (wavPath is not null)
{
    var (samples, rate) = WavFile.ReadMono(wavPath);
    Array.Resize(ref samples, samples.Length + rate / 2);
    if (rate != DspRate)
    {
        if (rate % DspRate != 0)
        {
            Console.Error.WriteLine($"wav rate {rate} is not a multiple of {DspRate}");
            return 2;
        }

        var decimator = new Decimator(rate, rate / DspRate);
        var decimated = new float[decimator.MaxOutput(samples.Length)];
        int produced = decimator.Process(samples, decimated);
        samples = decimated[..produced];
    }

    int frames = 0;
    channel.FrameReceived += (_, _) => frames++;
    channel.ProcessReceive(samples);
    Console.WriteLine($"{frames} frames decoded");
    return 0;
}

// The browser waterfall (spectrum + waterfall + per-frame burst attribution). Started
// before audio flows: Start() measures every modem's band off its own modulator and
// registers the channel receive tap.
Packet.SoundModem.Waterfall.WaterfallWebServer? waterfallServer = null;
// Whether the page's Mixer group and /api/mixer answer with no api.key, from
// "waterfall"."enableAudioControls". Decided in the block below, where the page it opens is
// started, and used again at the configuration API further down.
bool openAudioControls = false;
// Not on a --two-tone/--tone run: such a run lives for a few seconds, and a page or a scraper
// that attached to it would lose it again immediately. It also means a bench run does not stop
// with "cannot serve the waterfall" when the operator has left the service holding the port -
// which would be true, and would say nothing about the test they actually asked for.
if (benchTxTest is null && waterfallConfig is not null)
{
    waterfallServer = new Packet.SoundModem.Waterfall.WaterfallWebServer(
        channel,
        waterfallConfig.Port,
        new Packet.SoundModem.Waterfall.WaterfallOptions
        {
            // The band plan (or a pinned "dialFrequency") already knows the dial; the waterfall's
            // RF scale should not have to be told it a second time, and then disagree when one of
            // them is edited.
            DialFrequencyHz = waterfallConfig.DialFrequencyHz != 0
                ? waterfallConfig.DialFrequencyHz
                : receiveDialHz ?? 0,
            Sideband = bandPlan?.Sideband ?? waterfallConfig.Sideband,
            LinesPerSecond = waterfallConfig.LinesPerSecond,
            FftSize = waterfallConfig.FftSize,
            Public = waterfallConfig.Public,
            Title = waterfallConfig.Title,
            About = waterfallConfig.About,
            // What each modem is meant to occupy; see StationFactory.DeclaredBandsFor, which the
            // many-receiver flavour calls too.
            DeclaredBands = StationFactory.DeclaredBandsFor(modems),
            // The decoded-frames panel opens on what the station has already written down, so a
            // browser arriving mid-afternoon is shown the channel rather than an empty list. Null
            // when there is no frame log: nothing to show, and nothing pretending otherwise.
            FrameHistory = frameLog is null ? null : frameLog.Recent,
            // Where a page dropped for not answering says so. Untagged, like every other line
            // this process writes, because it runs one station.
            Log = stationJournal.Write,
        },
        // One bind for every listener; the waterfall no longer carries its own.
        bindAddress);

    // The links pane opens on the links the station already knows about; see
    // StationFactory.BackfillLinks, which the many-receiver flavour calls too.
    if (frameLog is not null)
    {
        StationFactory.BackfillLinks(waterfallServer, frameLog);
    }

    try
    {
        waterfallServer.Start();
    }
    catch (Exception e) when (e is HttpListenerException or SocketException)
    {
        Console.Error.WriteLine(
            $"cannot serve the waterfall on {bindAddress}:{waterfallConfig.Port}\n"
            + $"  {e.Message}\n"
            + "  Set by \"waterfall\".\"port\" and the top-level \"bind\". Another process may\n"
            + "  already hold the port; \"*\" or \"0.0.0.0\" serves every interface.");
        return 2;
    }
    catch (ArgumentException e)
    {
        // The spectrum source refusing its geometry - a wrong answer in the config, not a
        // busy port. Without this catch the raw exception took a non-2 exit code, and
        // Restart=on-failure (which only spares exit 2) crash-looped the unit every five
        // seconds - exactly the failure mode the config-validation contract exists to
        // prevent.
        Console.Error.WriteLine(
            "invalid waterfall settings\n"
            + $"  {e.Message}\n"
            + "  Set by \"waterfall\".\"fftSize\" and \"waterfall\".\"linesPerSecond\". The FFT\n"
            + "  size must be a power of two no shorter than one line's hop (the DSP rate\n"
            + "  divided by linesPerSecond), and linesPerSecond must divide the DSP rate.\n"
            + "  Remove the settings to use the defaults.");
        return 2;
    }

    Console.WriteLine($"waterfall: {waterfallServer.Url}");

    // The sound card's controls without a key, for the operator who reaches this page over their
    // own LAN or over SSH. Never on a public page: the group is not on one whatever the config
    // says, so opening the endpoint behind it would put the card in reach of strangers and put
    // nothing on the page for the operator. Said rather than ignored quietly.
    openAudioControls = waterfallConfig.AudioControlsOpen;
    if (waterfallConfig.EnableAudioControls && waterfallConfig.Public)
    {
        Console.Error.WriteLine(
            "waterfall: \"enableAudioControls\" is IGNORED on a \"public\" page. The Mixer group "
            + "is the operator's and a public page never carries it; set \"api\".\"key\" and use "
            + "api/mixer if a script has to reach this card.");
    }

    // The same warning the KISS ports carry, for the same reason and now with a sharper one.
    // The page is read-only on a public deployment, but on an operator's own station it carries
    // the transmit test, and there is no password on it.
    // Through the helper this file already uses rather than IPAddress.Parse, which throws on
    // anything that is not an IP literal: the bind is checked at start-up above, so by here it
    // reads, and asking twice with two different parsers is how the two answers drift apart.
    if (!Equals(DaemonConfig.ParseBind(bindAddress), System.Net.IPAddress.Loopback))
    {
        Console.WriteLine(
            "waterfall: WARNING - listening beyond loopback. The page has no authentication, and "
            + (waterfallConfig.Public
                ? "anything that can reach this port can watch this station."
                : "on an operator's page it carries a transmit test: anything that can reach this "
                  + "port can key your transmitter on your licence.")
            + (openAudioControls
                ? " \"enableAudioControls\" is on, so it also carries this sound card's mixer "
                  + "with no key: anything that can reach this port can change your capture gain."
                : ""));
    }
}

await using var waterfallLifetime = waterfallServer;


// The signal survey: watch the whole passband for transmissions this station cannot read, and
// keep the ones worth looking at later (issue #206). Off unless configured - it writes audio to
// disk unattended for as long as the station runs.
//
// Its own spectrum feed rather than the waterfall's. The two compute the same transform at the
// same rate and it is cheap (a 2048-point FFT thirty times a second), and the alternative is a
// survey that only works when somebody has also asked for a browser page, plus a shared
// accumulator fed from two places. What they do share is the band probe below, so what the
// waterfall draws and what the survey calls "ours" can never disagree.
SignalSurvey? survey = null;
ProspectorWorker? prospectorLifetime = null;
Func<IReadOnlyList<ModemProposal>>? proposals = null;
Func<(long Examined, long Read, long Dropped)>? prospectorCounts = null;
if (surveyConfig is not null)
{
    var surveyBands = new List<ModemBand>();
    foreach ((int sub, IModem modem) in channel.Modems.OrderBy(m => m.Key))
    {
        if (ModemBandProbe.TryMeasure(modem, DspRate, out double low, out double high))
        {
            surveyBands.Add(new ModemBand(sub, modem.Mode, low, high, (low + high) / 2));
        }
    }

    // ARDOP is a receive tap rather than an IModem, so nothing enumerable carries it and nothing
    // can probe it. Left out, every ARDOP burst on the channel would be reported as a signal
    // nobody was listening to - which is the opposite of true.
    foreach (ModemConfig ardop in modems.Where(m => DaemonConfig.IsArdop(m.Mode)))
    {
        double centre = ardop.Frequency ?? ArdopChannelBridge.NativeCentreHz;
        double half = (ardop.Bandwidth ?? ArdopChannelBridge.WidestBandwidthHz) / 2;
        surveyBands.Add(new ModemBand(ardop.SubChannel, "ardop", centre - half, centre + half, centre));
    }

    // And the id-beacon ghosts, for exactly the same reason and by exactly the same oversight.
    // A ghost is a receive tap too, so it is not in channel.Modems and cannot be probed - and a
    // NinoTNC ident landing on its frequency was being reported as a signal nobody was listening
    // to, on a frequency where a receiver is listening specifically for it. Placed from the same
    // arithmetic the ghost itself uses, so the two cannot disagree about where it sits; its
    // width is the afsk300 bank's coverage, which is what a ghost is built from.
    if (idBeacons)
    {
        var ghostCentresSeen = new HashSet<long>();
        foreach (ModemConfig psk in modems.Where(m => IdBeaconGhost.AppliesTo(m.Mode)))
        {
            double centre = IdBeaconGhost.CentreHzFor(psk.Frequency);
            if (!ghostCentresSeen.Add((long)Math.Round(centre)))
            {
                continue;   // two PSK modems idented at the same place; one ghost serves both
            }

            surveyBands.Add(new ModemBand(
                psk.SubChannel, "afsk300 (id beacon)",
                centre - IdBeaconGhost.CoverageHalfWidthHz,
                centre + IdBeaconGhost.CoverageHalfWidthHz,
                centre));
        }
    }

    var surveyOptions = new SignalSurveyOptions
    {
        Directory = surveyConfig.Path,
        MaxBytes = surveyConfig.MaxBytes,
        MaxPerHour = surveyConfig.MaxPerHour,
        CooldownSeconds = surveyConfig.CooldownSeconds,
        MarginSeconds = surveyConfig.MarginSeconds,
        MaxSeconds = surveyConfig.MaxSeconds,
        MinPeakSnrDb = surveyConfig.MinPeakSnrDb,
        // The dial is what turns an audio centre into a band frequency in the sidecar - the whole
        // point of a capture is to say where on 40 m the thing was.
        DialFrequencyHz = waterfallConfig?.DialFrequencyHz is > 0
            ? waterfallConfig.DialFrequencyHz
            : receiveDialHz ?? 0,
        Sideband = bandPlan?.Sideband ?? waterfallConfig?.Sideband ?? "usb",
    };

    if (surveyConfig.Capture is { Length: > 0 } wanted)
    {
        var verdicts = new List<SurveyVerdict>();
        foreach (string name in wanted)
        {
            if (Enum.TryParse(name, ignoreCase: true, out SurveyVerdict verdict))
            {
                verdicts.Add(verdict);
            }
            else
            {
                Console.Error.WriteLine(
                    $"survey: ignoring unknown capture kind \"{name}\" "
                    + "(unclaimed, missed, unattributed)");
            }
        }

        if (verdicts.Count > 0)
        {
            surveyOptions.Capture = verdicts;
        }
    }

    try
    {
        // The source has to exist before the survey (it reports the geometry the survey is built
        // from) and the survey before the source's sink can use it; nothing runs until audio flows.
        SignalSurvey? pending = null;
        var surveySource = new WaterfallSource(
            DspRate, (index, line) => pending?.AddLine(index, line.Span), 30, 0);
        var created = new SignalSurvey(
            surveyOptions, surveyBands, DspRate,
            surveySource.BinWidthHz, surveySource.LinesPerSecond, surveySource.LineLength);
        pending = created;
        survey = created;

        // The channel gates its receive tap while transmitting, so audio and lines stop together
        // - which is the invariant the survey needs to map a burst back to the audio that carried
        // it. Audio first: a line is stamped with the ring position at the moment it arrives.
        channel.AddReceiveTap(samples =>
        {
            created.AddAudio(samples);
            surveySource.Process(samples);
        });
        channel.FrameReceivedWithQuality += (sub, frame, quality) =>
            created.NoteDecode(sub, frame, quality);
        channel.TransmittingChanged += keyed =>
        {
            if (keyed)
            {
                created.Reset();
            }
        };

        // Onto the page: what the survey has kept, what a budget refused, and each capture as it
        // lands - drawn where it happened, since a capture's frequency and time are exactly the
        // axes the waterfall already has.
        if (waterfallServer is { } display)
        {
            void PushStatus() => display.SetSurveyStatus(
                created.Captured, created.SkippedForBudget, created.Bytes, created.Directory);

            PushStatus();
            created.StatusChanged += PushStatus;
            created.CaptureWritten += (capture, wav) =>
            {
                display.ReportCapture(
                    capture.Verdict.ToString().ToLowerInvariant(),
                    capture.AudioCentreHz,
                    capture.AudioLowHz,
                    capture.AudioHighHz,
                    capture.DurationSeconds,
                    capture.PeakSnrDb,
                    // Age rather than a line index: the survey's spectrum clock is its own, and
                    // by the time a capture is on disk a moment more has passed.
                    (DateTimeOffset.UtcNow - capture.CapturedAt).TotalSeconds,
                    Path.GetFileName(wav));
                PushStatus();
            };
        }

        Console.WriteLine($"survey: {surveyConfig.Path}");

        // The prospector: read each capture back with every mode that could have carried it,
        // and say what modem would have read the traffic. Off unless configured - it is DSP
        // work beside a real-time receiver, and a station that wants its CPU for its modems
        // should have it.
        if (surveyConfig.Propose)
        {
            var prospector = new ModemProspector(
                new ModemProspectorOptions
                {
                    MinCaptures = Math.Max(1, surveyConfig.ProposeMinCaptures),
                    TimeProvider = TimeProvider.System,
                },
                surveyBands,
                surveyOptions.DialFrequencyHz,
                surveyOptions.Sideband);
            var prospectorWorker = new ProspectorWorker(prospector, DspRate);
            prospectorLifetime = prospectorWorker;
            proposals = prospector.Proposals;
            prospectorCounts = () =>
                (prospector.Examined, prospector.Read, prospectorWorker.Dropped);

            // Fed from the capture writer rather than from the burst: a capture that a budget
            // refused is not on disk to read, and one that is has already been written by the
            // time this hears about it.
            created.CaptureWritten += prospectorWorker.Examine;
            prospector.Proposed += proposal =>
                Console.WriteLine($"propose: {proposal.Summary()}");

            // What it looked at, and what came back. Work that leaves no trace until it succeeds
            // is indistinguishable from work that is not happening - which is exactly how it read
            // from outside on the day it went live, where the only evidence a capture had been
            // examined was a thread's CPU counter in /proc.
            prospector.ExaminedCapture += (capture, readings) =>
            {
                if (readings.Count == 0)
                {
                    // Every twenty-five, not every one: most captures are unreadable and a line
                    // each would bury the station's own traffic. Silence still has to be
                    // accounted for, though, or "is it running?" has no answer on a quiet band.
                    if (prospector.Examined % 25 == 0)
                    {
                        Console.WriteLine(
                            $"propose: {prospector.Examined} capture(s) examined, "
                            + $"{prospector.Read} readable, {prospectorWorker.Dropped} skipped "
                            + "for backlog");
                    }

                    return;
                }

                foreach (CaptureReading reading in readings)
                {
                    string who = reading.Source is string source
                        ? $"{source}{(reading.Destination is string to ? ">" + to : "")}"
                        : "no readable callsigns";
                    string progress = prospector.Progress(capture, reading) is var (banked, needed)
                        ? $", {banked} of {needed} occasion(s)"
                        : ", not counted (no verified check sequence)";
                    Console.WriteLine(
                        $"propose: {capture.AudioCentreHz:F0} Hz reads as {reading.Mode} - "
                        + $"{reading.Frame.Length} B, {who}{progress}");
                }
            };
            Console.WriteLine(
                $"propose: on, {CaptureSweep.ModesFor(DspRate).Count} modes per capture, "
                + $"{surveyConfig.ProposeMinCaptures} capture(s) of evidence needed "
                + $"(a twentieth of one core; the receive path is never waited on)");
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"cannot open the survey directory at {surveyConfig.Path}\n"
            + $"  {e.Message}\n"
            + "  Set by \"survey\".\"path\". The service user must be able to write to it;\n"
            + "  remove the \"survey\" section to run without one.");
        return 2;
    }
}

// What this station hears, for a monitoring system to come and read. Fed from the monitor path
// like the frame log, so a frame the station read and did not pass to a host still counts as
// heard - which is the honest answer to "how well am I copying this station".
StationTelemetry? metrics = null;
if (metricsConfig is { Enabled: true })
{
    metrics = new StationTelemetry(
        TimeProvider.System,
        metricsConfig.MaxStations,
        TimeSpan.FromSeconds(Math.Max(1, metricsConfig.FrameWindowSeconds)),
        TimeSpan.FromHours(Math.Max(0.1, metricsConfig.StationIdleHours)));
    channel.FrameReceivedWithQuality += (sub, frame, quality) => metrics.Record(sub, frame, quality);

    if (waterfallServer is { } metricsServer)
    {
        metricsServer.Metrics = metrics;
        Console.WriteLine(
            $"metrics: {metricsServer.Url}metrics (prometheus) and {metricsServer.Url}metrics/frames "
            + "(one point per frame, influx line protocol). No authentication.");
    }
    else
    {
        Console.Error.WriteLine(
            "metrics: WARNING - nothing to serve them on. They ride the waterfall's listener; "
            + "add a \"waterfall\" section with a port, or remove \"metrics\".");
    }
}

using var surveyLifetime = new Disposer(() => survey?.Dispose());
using var prospectorDisposer = new Disposer(() => prospectorLifetime?.Dispose());

// Continuous raw capture: everything the channel hears, chunked to disk, so the run can be
// re-scored offline against a later receiver. Curated bursts are the survey's job; this is
// the unedited stream, and it costs disk by design (the budget prunes oldest-first).
RawCaptureWriter? rawCapture = null;
if (rawCaptureConfig is not null)
{
    try
    {
        rawCapture = new RawCaptureWriter(
            rawCaptureConfig.Path, rawCaptureConfig.MaxBytes, rawCaptureConfig.ChunkMinutes,
            DspRate);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"cannot open the raw-capture directory at {rawCaptureConfig.Path}\n"
            + $"  {e.Message}\n"
            + "  Set by \"rawCapture\".\"path\". The service user must be able to write to it;\n"
            + "  remove the \"rawCapture\" section to run without one.");
        return 2;
    }

    rawCapture.Failed = Console.Error.WriteLine;
    channel.AddReceiveTap(rawCapture.Process);
    Console.WriteLine(
        $"raw capture: {rawCaptureConfig.Path}, {rawCaptureConfig.ChunkMinutes} min chunks at "
        + $"{DspRate} Hz, budget {rawCaptureConfig.MaxBytes / (1024.0 * 1024 * 1024):F1} GB "
        + "(oldest pruned)");
}

using var rawCaptureLifetime = new Disposer(() => rawCapture?.Dispose());

// Ghost demodulators for the station identifications a NinoTNC sends alongside its PSK SSB data
// modes rather than within them. Wired here rather than earlier because a ghost reports to the
// waterfall and the frame log, and both have to exist first; what one is and why is in
// StationFactory.WireIdBeacons, which the many-receiver flavour calls too.
if (idBeacons)
{
    StationFactory.WireIdBeacons(
        channel, modems, DspRate, waterfallServer, frameLog,
        // The survey learns its decodes from the channel's own event, which a receive tap does
        // not raise; without this an ident the station read would be filed as a burst nothing
        // decoded, and charged to the capture budget.
        (sub, frame, quality) => survey?.NoteDecode(sub, frame, quality),
        stationJournal);
}

// The radio's transmit meters, when there is a radio that has them.
M0LTE.Flex.FlexMeters? flexMeters = null;
using var flexMetersLifetime = new Disposer(() => flexMeters?.Dispose());

/// <summary>Below this the transmitter is not keyed and the readout is meaningless.</summary>
const double TransmitReadoutFloorWatts = 0.1;

// The Flex owns keying (the slice PTT is an API command), so a conflicting --ptt /
// configured PTT is rejected - matching how --device flex: implicitly keys the radio.
if (deviceIsFlex && (pttSpec is not null || pttConfig is not null))
{
    Console.Error.WriteLine(
        "--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)");
    return 2;
}

if (deviceIsUberSdr && (pttSpec is not null || pttConfig is not null))
{
    Console.Error.WriteLine(
        $"--device ubersdr: is a receive-only station - the instance at {uberSdrEndpoint} has no "
        + "transmitter, so there is nothing for a PTT line to key. Remove \"ptt\".");
    return 2;
}

if (pttSpec is not null)
{
    string[] parts = pttSpec.Split(':');
    if (parts.Length >= 2)
    {
        pttConfig = new PttConfig
        {
            Type = parts[0],
            Device = parts[1],
            Line = parts[0] == "serial" && parts.Length > 2 ? parts[2] : null,
            Gpio = parts[0] == "cm108" && parts.Length > 2 ? int.Parse(parts[2]) : null,
        };
    }
    else
    {
        Console.Error.WriteLine("--ptt expects serial:<dev>[:rts|:dtr] or cm108:<hidraw>[:gpio]");
        return 2;
    }
}

// One bind for every listener - KISS, per-modem ports, waterfall, paging and ARDOP.
System.Net.IPAddress listenAddress = DaemonConfig.ParseBind(bindAddress)!;



// Set when the radio's session dies, so the exit code says "retry me" rather than "I am done".
bool radioLost = false;
// Set by the configuration API once a change has been validated and written: the process ends so
// systemd rebuilds the whole station on it. Exit 1, like a lost radio, because that is the code
// Restart=on-failure reopens - exit 2 is "your configuration is wrong" and is deliberately final.
bool restartRequested = false;

using var cancellation = new CancellationTokenSource();

// Runtime configuration, on the waterfall's listener. Off unless a key is set, and refused
// outright if there is no listener to hang it on - an "api" section on a station with no
// waterfall is a setting that would silently do nothing.
// One other thing installs it: "waterfall"."enableAudioControls", which serves /api/mixer, and
// only /api/mixer, with no key. That flag can only come from a config file and only where the
// waterfall is running, so the two refusals below stay the "api" section's own.
// Held beyond the block below because the sound card's mixer is opened much later, with the
// audio device, and has to be handed to the API once it exists.
ConfigApi? runtimeApi = null;
string apiKey = apiConfig?.Key ?? "";
if (benchTxTest is null && (apiKey.Length > 0 || openAudioControls))
{
    if (waterfallServer is null)
    {
        Console.Error.WriteLine(
            "\"api\" is served on the waterfall's HTTP listener, and this station has no "
            + "\"waterfall\" section - add one, or remove \"api\"");
        return 2;
    }

    if (configPath is null)
    {
        Console.Error.WriteLine(
            "\"api\" needs a --config file to read back and to write changes to; a station "
            + "configured entirely from the command line has nothing for it to act on");
        return 2;
    }

    string ephemeralPath = ConfigApi.EphemeralPathFor(configPath);
    ConfigApi configApi = runtimeApi = new ConfigApi(
        apiKey, configPath, ephemeralPath,
        runningJson: () => apiConfigJson,
        ephemeralInForce: apiEphemeralInForce,
        requestRestart: () =>
        {
            // Exit 1, the same door a lost radio uses, because it is the one systemd's
            // Restart=on-failure reopens. Exit 2 is reserved for "your configuration is wrong"
            // and is deliberately not restarted - which is the wrong answer for a change that
            // has already been validated.
            restartRequested = true;
            cancellation.Cancel();
        },
        openAudioControls: openAudioControls);
    waterfallServer.ApiHandler = configApi.HandleAsync;

    // One line, because this is the station's sound card in reach of whoever can reach the page,
    // and an operator who has forgotten the flag is set should be told at every start-up.
    if (openAudioControls)
    {
        Console.WriteLine(
            $"api: audio controls are OPEN - the page's Mixer group and {waterfallServer.Url}"
            + "api/mixer answer with NO key, because \"waterfall\".\"enableAudioControls\" is "
            + "true. Anything that can reach this port can change this card's levels.");
    }

    if (apiKey.Length > 0)
    {
        if (proposals is not null)
        {
            configApi.ServeProposals(proposals, prospectorCounts!);
            Console.WriteLine(
                $"api: modem proposals over {waterfallServer.Url}api/proposals (key required); "
                + "each carries the configuration to POST back to api/config.");
        }

        Console.WriteLine(
            $"api: configuration over {waterfallServer.Url}api/config (key required). "
            + $"POST replaces it for one run; add ?persist=true to write {configPath}.");

        // Said out loud because the whole apply path assumes something will restart the process.
        // Run by hand, nothing will, and "it applied and then the station stopped" is a bad
        // surprise.
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is null)
        {
            Console.Error.WriteLine(
                "api: WARNING - this daemon does not appear to be running under systemd, so an "
                + "applied change will STOP it rather than restart it onto the new configuration.");
        }
    }
}

if (apiEphemeralInForce)
{
    // Loud, and on every start-up it applies to: a station running something other than its own
    // config file is the kind of thing that gets forgotten and then debugged for an hour.
    Console.Error.WriteLine(
        $"api: this station is running a ONE-RUN configuration applied over the API, not "
        + $"{configPath}. Any restart from here returns it to the file.");
}
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// systemctl stop sends SIGTERM, and until this existed only Ctrl-C ran the graceful path -
// a stopped service skipped every disposal, which on a headless Flex leaks the created slice
// (the radio keeps it, dead handle and all, until its four-slice limit stalls every later
// bring-up). Same route as Ctrl-C: cancel, drain, dispose, slice removed.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    });

var kissServers = new List<KissTcpServer>();

// The same question the journal lines below answer, as state rather than as events: which ports
// have a host on them right now, onto the waterfall's modem labels. A snapshot of every port each
// time one changes - the server drops it when it says nothing new, and a snapshot cannot leave
// the page holding a count that a missed event would have corrected.
void PublishHostPorts()
{
    if (waterfallServer is null)
    {
        return;
    }

    var ports = new List<Packet.SoundModem.Waterfall.HostPortStatus>(kissServers.Count);
    foreach (KissTcpServer server in kissServers)
    {
        ports.Add(new Packet.SoundModem.Waterfall.HostPortStatus(
            server.LocalPort, server.DedicatedSubChannel, server.ClientCount));
    }

    waterfallServer.SetHostPorts(ports);
}

// Who is attached to a KISS port, in the journal. A host that quietly drops its TCP session
// stops passing traffic, and from the modem's side that is indistinguishable from a quiet band -
// so the attach and the loss both get a line, and the loss carries its reason where it had one.
void WatchClients(KissTcpServer server)
{
    server.ClientConnected += e =>
    {
        Console.WriteLine(ActivityLog.ClientConnected(server.LocalPort, server.DedicatedSubChannel, e));
        PublishHostPorts();
    };
    server.ClientDisconnected += e =>
    {
        Console.WriteLine(ActivityLog.ClientDisconnected(server.LocalPort, server.DedicatedSubChannel, e));
        PublishHostPorts();
    };
    server.AcceptFailed += why => Console.Error.WriteLine(
        $"kiss[{server.LocalPort}] accept failed: {why} - listening continues");

    // What a host changed with SETHW, or why it was refused - RAM-only state with no other
    // record, so the journal is where an operator learns which waveform their modem is on.
    server.HardwareCommand += e => Console.WriteLine(e.Applied
        ? $"modem {e.SubChannel}: SETHW -> {e.Description}"
        : $"modem {e.SubChannel}: SETHW ignored - {e.Description}");
}

// KISS serves the packet modems, so it starts whenever there are any - ARDOP sharing the
// channel is no longer a reason to withhold it. (It was, when an ARDOP channel carried nothing
// else; gating on the old top-level "ardop" setting would now silently leave the packet modems
// with no host interface at all.)
// A --two-tone/--tone run offers no KISS port: it lives for a few seconds and a host that
// attached to it would lose its session again immediately.
if (benchTxTest is null && modems.Any(m => !DaemonConfig.IsArdop(m.Mode)))
{
    string shown = Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString();

    // The shared port: every modem, addressed by nibble (the QtSoundModem multiplex model).
    var shared = new KissTcpServer(channel, kissPort, listenAddress);
    shared.EmitQualityFrames = qualityFrames;
    WatchClients(shared);
    shared.Start();
    kissServers.Add(shared);
    if (qualityFrames)
    {
        Console.WriteLine("rx-quality frames: on (KISS command 0x07, JSON payload)");
    }

    Console.WriteLine($"kiss tcp: {shown}:{shared.LocalPort} (all modems, by sub-channel nibble)");

    // Plus a port to itself for any modem that asked for one, so a host that only speaks
    // KISS channel 0 can still reach a modem that is not sub-channel 0.
    // ardop is excluded: its port speaks the ardopcf host interface, not KISS.
    foreach (ModemConfig modemConfig in modems
                 .Where(m => m.Port is not null && !DaemonConfig.IsArdop(m.Mode)))
    {
        var dedicated = new KissTcpServer(
            channel, modemConfig.Port!.Value, listenAddress, subChannel: modemConfig.SubChannel);
        dedicated.EmitQualityFrames = qualityFrames;
        WatchClients(dedicated);
        dedicated.Start();
        kissServers.Add(dedicated);
        Console.WriteLine(
            $"kiss tcp: {shown}:{dedicated.LocalPort} (modem {modemConfig.SubChannel} "
            + $"{modemConfig.Mode} only, as nibble 0)");
    }

    // The opening state, once every port is up: no host attached yet, or - if a node beat the
    // display to it - however many already are. Without this the page would show nothing about
    // attachment until the first connect or disconnect, which on a settled station is never.
    PublishHostPorts();

    if (channel.ReceiveOnlyReason is not null)
    {
        // The usual "anything that can reach this can transmit on your licence" warning is not
        // true here, and saying it anyway would teach operators to ignore it where it is.
        Console.WriteLine(
            "kiss: this station receives only - frames arriving on these ports are refused, not "
            + "transmitted. Everything the modems hear is still delivered.");
    }
    else if (!Equals(listenAddress, System.Net.IPAddress.Loopback))
    {
        Console.WriteLine(
            "kiss: WARNING - listening beyond loopback. KISS has no authentication: anything "
            + "that can reach these ports can transmit on your licence.");
    }
}

await using var kissLifetime = new KissServerSet(kissServers);

M0LTE.Ardop.Host.ArdopHostServer? ardopServer = null;
if (benchTxTest is null && ardopModem is not null)
{
    // ARDOP runs its own channel discipline (ARQ timing budgets, negotiated leaders), so its
    // own bursts must never wait on a p-persistence roll - they go out through the channel's
    // inhibit-bypassing path. The packet modems keep normal CSMA among themselves; what keeps
    // them off an ARQ session is TransmitInhibit, set once the engine exists.
    // Bind the M0LTE.Ardop TNC to this daemon's channel: transmit bursts through the
    // channel-access path, receive audio via a channel tap (the old ForChannel glue,
    // now that the package is audio-device-agnostic).
    // The engine rate, not the channel rate: the centre shift runs at the engine rate however
    // wide the channel is, so its Nyquist is the bound that matters.
    if (ardopModem.Frequency is double ardopCentre
        && ArdopChannelBridge.Concern(ardopCentre, M0LTE.Ardop.ArdopModulator.SampleRate)
            is string ardopConcern)
    {
        Console.Error.WriteLine($"ardop: WARNING - {ardopConcern}");
    }

    if (channel.ReceiveOnlyReason is not null)
    {
        Console.Error.WriteLine(
            "ardop: WARNING - ARDOP is a connected-mode ARQ protocol and this station cannot "
            + "transmit, so no session will ever complete: it will hear the channel and never "
            + "answer. The host port is still served, and every frame it demodulates - including "
            + "other stations' sessions - is still drawn and written down.");
    }

    var ardopShift = ArdopChannelBridge.For(
        ardopModem.Frequency, M0LTE.Ardop.ArdopModulator.SampleRate, DspRate);
    var ardopTnc = new M0LTE.Ardop.Host.ArdopHostTnc(captureDevice: device, playbackDevice: device)
    {
        // Awaited rather than fire-and-forget: the TNC's transmit worker does not survive an
        // exception out of this delegate, and on a receive-only channel every burst is refused.
        // Catching turns "ARDOP silently stops working" into a line saying why.
        Transmitter = async audio =>
        {
            try
            {
                await channel.EnqueueTransmit(
                    _ =>
                    {
                        var floats = new float[audio.Length];
                        for (int i = 0; i < audio.Length; i++)
                        {
                            floats[i] = Packet.SoundModem.Audio.Pcm16.ToFloat(audio[i]);
                        }

                        return ardopShift.Transmit(floats);
                    },
                    rejected: null,
                    // ARDOP owns this channel's timing: its bursts wait on neither the inhibit nor
                    // the p-persistence roll.
                    ownsChannelTiming: true,
                    // The shifter identifies the ARDOP transmitter, so its own consecutive bursts
                    // share a keyup and nothing else joins one: an ARQ turnaround must not be held
                    // up by a packet frame appended behind it, nor a packet frame by ARDOP.
                    source: ardopShift).ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"ardop: transmission dropped - {refused.Message}");
            }
        },
    };
    channel.AddReceiveTap(samples => ardopTnc.ProcessReceive(ardopShift.Receive(samples)));

    // ARDOP demodulates inside the virtual TNC, so its frames never reach the channel event the
    // waterfall and the frame log listen to - without this the ARDOP band is drawn and its
    // bursts paint, but nothing it hears is ever listed. M0LTE.Ardop 0.3.0 raises every frame
    // the demodulator recovers, including other stations' sessions and failed decodes, which is
    // what a monitor wants: "someone transmitted and we could not read them" is information.
    // The survey listens on the same event and for the same reason. It judges a burst against
    // what the station read, and it learns that from NoteDecode on the channel's decode event -
    // which ARDOP does not raise either. Without this, every ARDOP transmission the station
    // successfully copies is still filed as a burst inside a configured band that nothing
    // decoded: "missed". On the live 40 m station that was 15 of 33 misses, the whole ARDOP
    // slot reading as a modem that does not work.
    if (waterfallServer is not null || frameLog is not null || survey is not null)
    {
        int ardopSub = ardopModem.SubChannel;
        double? ardopAudioHz = ardopModem.Frequency ?? ArdopChannelBridge.NativeCentreHz;
        double? ardopRfHz = ardopModem.RfFrequency;
        ardopTnc.FrameDecoded += frame =>
        {
            byte[] data = frame.Data ?? [];
            // Caller/Target are carried in clear by the connect handshake and ID frames; a data
            // frame in someone else's session carries neither, and is listed unattributed.
            string? from = string.IsNullOrWhiteSpace(frame.Caller) ? null : frame.Caller;
            string? to = string.IsNullOrWhiteSpace(frame.Target) ? null : frame.Target;

            waterfallServer?.ReportFrame(
                ardopSub, frame.Name, from, to, data.Length, frame.SnDb, frame.Ok);

            var quality = new FrameQuality(
                "ardop", data.Length, CorrectedBytes: null, CrcValid: frame.Ok,
                FrequencyOffsetHz: null, EmphasisDb: null);

            frameLog?.Record(
                ardopSub, data, quality, ardopAudioHz, ardopRfHz,
                modeName: $"ARDOP {frame.Name}");

            // Only a frame that actually decoded says the station read the burst. A failed
            // decode is exactly the case the survey exists to catch, and telling it otherwise
            // would hide the one thing worth capturing in this band.
            if (frame.Ok)
            {
                survey?.NoteDecode(ardopSub, data, quality, ax25: false);
            }
        };
    }

    // Hold the packet modems off the air for the length of an ARQ session. Their frames are
    // queued, not discarded, until TransmitInhibitTimeout gives up on one - an AX.25 host will
    // have retried long before a Winlink session ends.
    M0LTE.Ardop.Arq.ArdopArqEngine ardopEngine = ardopTnc.Engine;
    channel.TransmitInhibit = () => ardopEngine.IsConnected || ardopEngine.IsPending;

    int ardopCommandPort = ardopModem.Port ?? 8515;
    ardopServer = new M0LTE.Ardop.Host.ArdopHostServer(
        ardopTnc, ardopCommandPort, listenAddress, ownsTnc: true);
    ardopServer.Start();
    Console.WriteLine(
        $"ardop host tcp: {(Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString())}:{ardopServer.LocalCommandPort} (data {ardopServer.LocalDataPort}, "
        + $"ardopcf-compatible virtual TNC, modem {ardopModem.SubChannel}{ardopShift.Describe()})");
}
await using var ardopLifetime = ardopServer;

Packet.SoundModem.Pocsag.PagingTcpServer? pagingServer = null;
if (benchTxTest is null && paging is not null)
{
    var polarity = paging.InvertPolarity
        ? M0LTE.Pocsag.PocsagPolarity.Inverted
        : M0LTE.Pocsag.PocsagPolarity.Normal;
    pagingServer = new Packet.SoundModem.Pocsag.PagingTcpServer(
        channel, paging.Port, paging.Baud, polarity, listenAddress);

    // A page the server said OK to and then could not send has no client left to tell -
    // the journal is the only place the loss can be recorded, exactly as for KISS frames.
    pagingServer.PageDropped += drop => Console.Error.WriteLine(
        $"page[{drop.Id}] to {drop.Ric} DROPPED: {drop.Reason}");
    pagingServer.AcceptFailed += why => Console.Error.WriteLine(
        $"paging: accept failed: {why} - listening continues");
    pagingServer.Start();
    Console.WriteLine($"paging tcp: {(Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString())}:{pagingServer.LocalPort} ({pagingServer.Mode}, DAPNET/POCSAG-compatible)");
}
await using var pagingLifetime = pagingServer;

// Audio + PTT: a FlexRadio DAX triplet (--device flex:…), an UberSDR web receiver's IQ stream
// (--device ubersdr:…, receive only), or an ALSA card. Each surfaces through the same
// IAudioInput/IAudioOutput/IPttControl the channel already speaks, so KISS packet, POCSAG
// paging and ARDOP all get every transport for free.
// Keyed off the modem entry, not the legacy --ardop flag: a station configuring ARDOP the
// documented way (a "mode": "ardop" modem entry) wants the deeper buffer just as much.
int flexPacketBuffer = ardopModem is null ? 3 : 6;
FlexRuntime? flex = null;
UberSdrAudioInput? uberSdr = null;
// Whether the UberSDR input, in either of its forms, has a session to be starved of. Null for
// every other device: their quiet is never deliberate.
Func<bool>? uberSdrSessionLive = null;
IPttControl ptt;
IAudioOutput playback;
IAudioInput input;
// Set only on the ALSA path: the sound card is the one device with xrun counters, and they are
// the difference between "the band is quiet" and "this machine will not schedule us".
AlsaAudioOutput? alsaOut = null;
AlsaAudioInput? alsaIn = null;
// The card's mixer, on the ALSA path only. Opened whether or not the configuration sets
// anything, so the start-up log records the level the station is actually listening at - but
// nothing is written to the card unless a key in "alsa"."mixer", or a change remembered in the
// state file from an earlier run, said so.
AlsaMixer? mixer = null;
MixerRuntime? mixerRuntime = null;
string mixerWhyNot = "this station has no sound card, so it has no mixer";

if (PipeAudio.IsPipe(device) && wavLoopPath is null)
{
    // Two FIFOs standing in for a sound card and a radio, so two daemons can be on the same air
    // with no hardware between them. See PipeAudio for what this deliberately does not model.
    try
    {
        (string inPipe, string outPipe, int pipeRate) = PipeAudio.Parse(device);
        if (pipeRate % DspRate != 0)
        {
            Console.Error.WriteLine(
                $"pipe rate {pipeRate} is not a multiple of the channel's {DspRate} Hz");
            return 2;
        }

        ptt = new NullPtt();
        var pipeOut = new PipeAudioOutput(outPipe, pipeRate);
        playback = pipeRate == DspRate
            ? pipeOut
            : new UpsamplingAudioOutput(pipeOut, DspRate);
        input = new PipeAudioInput(inPipe, pipeRate);
        Console.WriteLine($"audio: pipe in={inPipe} out={outPipe} {pipeRate} Hz -> {DspRate} Hz");
    }
    catch (Exception failure) when (failure is InvalidDataException or IOException
        or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"audio: {failure.Message}");
        return 2;
    }
}
else if (wavLoopPath is not null)
{
    // A recording standing in for the capture device: same decimation path, no TX side.
    var wavLoop = new WavLoopAudioInput(wavLoopPath);
    if (wavLoop.SampleRate % DspRate != 0)
    {
        Console.Error.WriteLine($"--wav-loop rate {wavLoop.SampleRate} is not a multiple of {DspRate}");
        return 2;
    }

    ptt = new NullPtt();
    playback = new NullAudioOutput(DspRate);
    input = wavLoop;
    Console.WriteLine($"audio: wav-loop {wavLoopPath} {wavLoop.SampleRate} Hz -> {DspRate} Hz");
}
else if (deviceIsUberSdr)
{
    string planSideband = bandPlan?.Sideband ?? sideband;
    var uberSdrTuning = new UberSdrTuning
    {
        // The receiver is tuned to the dial itself, so the suppressed carrier lands at DC in the
        // IQ and the demodulator's own NCO has nothing left to do.
        FrequencyHz = (int)Math.Round(receiveDialHz!.Value),
        Sideband = planSideband.Equals("lsb", StringComparison.OrdinalIgnoreCase)
            ? Sideband.Lower
            : Sideband.Upper,
        OutputRate = DspRate,
        Mode = uberSdrConfig?.Mode ?? "iq48",
        Password = uberSdrConfig?.Password,
        SsbLowHz = uberSdrConfig?.SsbLowHz ?? 150,
        SsbHighHz = uberSdrConfig?.SsbHighHz ?? 3450,
        StartupGuardMs = uberSdrConfig?.StartupGuardMs ?? 1000,
        Gain = (float)(uberSdrConfig?.Gain ?? 1.0),
    };

    string audioBanner =
        $"audio: {uberSdrEndpoint} {uberSdrTuning.Mode} IQ at {RfPlan.Mhz(receiveDialHz.Value)} -> "
        + $"{planSideband.ToUpperInvariant()} {uberSdrTuning.SsbLowHz:F0}-{uberSdrTuning.SsbHighHz:F0} Hz "
        + $"audio at {DspRate} Hz (RECEIVE ONLY";
    ConnectionResponse uberSdrConnection;
    string? uberSdrReceiver;

    if (uberSdrConfig?.OnDemand == true)
    {
        // A public monitor on somebody else's receiver: the session exists only while a browser
        // has the waterfall open, and is held for the linger after the last one leaves. The
        // pre-flight still runs here, so a wrong host or a refused IQ mode is still an error
        // at start-up; but a receiver that is merely down is not fatal - the page stays up and
        // says so, and the input keeps trying for as long as anyone is waiting.
        OnDemandUberSdrInput onDemand;
        try
        {
            // Its phase lines are this station's, so they go out through the station's journal
            // and pick up its tag when it has one.
            onDemand = await OnDemandUberSdrInput.OpenAsync(
                uberSdrEndpoint, uberSdrTuning, TimeSpan.FromSeconds(uberSdrConfig.LingerSeconds),
                stationJournal.ErrorSink, cancellation.Token);
        }
        catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                    or HttpRequestException or IOException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.UberSdr(device, configPath, e));
            return 1;
        }

        input = onDemand;
        uberSdrSessionLive = () => onDemand.SessionLive;
        uberSdrConnection = onDemand.Connection;
        uberSdrReceiver = onDemand.ReceiverDescription;
        stationJournal.Write($"{audioBanner}, on demand: connected while the waterfall has a viewer, "
            + $"held {uberSdrConfig.LingerSeconds} s after the last leaves)");

        // The page shows the input's own sentence for what it is doing, and credits the
        // receiver whether or not a session is up. The viewer count flows the other way.
        waterfallServer!.SetReceiver(uberSdrReceiver, uberSdrEndpoint.PublicUrl);
        waterfallServer.SetRadioStatus(onDemand.Status);
        onDemand.PhaseChanged += (_, sentence) => waterfallServer.SetRadioStatus(sentence);
        waterfallServer.ViewersChanged += onDemand.SetViewers;
    }
    else
    {
        try
        {
            uberSdr = await UberSdrAudioInput.OpenAsync(
                uberSdrEndpoint, uberSdrTuning, stationJournal.ErrorSink, cancellation.Token);
        }
        catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                    or HttpRequestException or IOException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.UberSdr(device, configPath, e));
            return 1;
        }

        input = uberSdr;
        uberSdrSessionLive = () => uberSdr.SessionLive;
        uberSdrConnection = uberSdr.Connection;
        uberSdrReceiver = uberSdr.ReceiverDescription;
        stationJournal.Write($"{audioBanner})");
        if (uberSdrReceiver is not null)
        {
            waterfallServer?.SetRadioStatus(uberSdrReceiver);
        }

        // A receiver that stays unreachable is not something to sit quietly on. Exit 1 so the
        // unit restarts and tries afresh, exactly as for a Flex whose session dies (exit 2 is
        // reserved for "your configuration is wrong", which restarting could never fix).
        uberSdr.Lost += reason =>
        {
            stationJournal.WriteError($"ubersdr: {reason}");
            radioLost = true;
            cancellation.Cancel();
        };
    }

    ptt = new NullPtt();
    playback = new NullAudioOutput(DspRate);
    if (uberSdrReceiver is not null)
    {
        stationJournal.Write($"ubersdr: {uberSdrReceiver}");
    }

    if (uberSdrConnection.RefusedForNow)
    {
        stationJournal.WriteError(
            "ubersdr: the receiver is refusing this address for now "
            + $"({uberSdrConnection.Reason ?? "daily listening allowance exhausted"}). The station "
            + "is up and will start hearing audio when the receiver lets us back in.");
    }
    else
    {
        stationJournal.Write(
            $"ubersdr: session limit {uberSdrConnection.MaxSessionTime} s - the stream is picked up "
            + "again each time the receiver ends one");
    }
}
else if (deviceIsFlex)
{
    try
    {
        flex = await FlexDevice.OpenAsync(device, DspRate, flexPacketBuffer, flexTuning, cancellation.Token);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        // The one device path that had no catch: a radio still booting at daemon start
        // escaped as a raw stack trace with an abort exit code, instead of the
        // DeviceDiagnostics message and the exit 1 (retry) contract the ALSA and UberSDR
        // paths honour. Broad on purpose - whatever the radio library throws, the answer
        // is the same: say what to check, and let the unit retry.
        Console.Error.WriteLine(DeviceDiagnostics.Flex(device, configPath, e));
        return 1;
    }

    ptt = flex.Ptt;
    playback = flex.Output;
    input = flex.Input;

    // Arbitrated keying: ordinary queued frames also defer BEFORE they are rendered while
    // another station transmits - the same polite hold ARDOP sessions get - rather than
    // discovering the busy radio inside Key(). Composed over whatever inhibit is already
    // installed (ARDOP's ARQ gate lands earlier), never instead of it. ARDOP's own bursts
    // bypass the inhibit by design and rely on the in-Key wait alone.
    if (flex.Ptt is M0LTE.Flex.FlexArbitratedPtt arbitratedPtt)
    {
        Func<bool>? priorInhibit = channel.TransmitInhibit;
        channel.TransmitInhibit = () =>
            (priorInhibit?.Invoke() ?? false) || arbitratedPtt.AnotherStationTransmitting;
        Console.WriteLine(
            "flex: arbitrated keying - every keyup waits out other stations, re-asserts the "
            + "transmit filter and the TX slice, and is confirmed against the interlock");
    }
    FlexDevice.FlexSpec flexSpec = FlexDevice.Parse(device);
    string flexModeDesc = flexSpec.Headless
        ? $"headless {flexTuning.Frequency} MHz {flexTuning.Antenna} {flexTuning.Mode}"
        : $"attach station '{flexSpec.Station}'";
    Console.WriteLine(
        $"audio: {device} DAX {input.SampleRate} Hz -> {DspRate} Hz "
        + $"(slice {flexSpec.SliceLetter}, dax {flexTuning.DaxChannel}, {flexModeDesc})");
    if (flex.Station.TuneWarning is string tuneWarning)
    {
        Console.Error.WriteLine($"flex: {tuneWarning}");
    }

    // Two headless instances that both take the default DAX channel displace each other, which
    // is exactly how this station lost its slice for six days (docs/flex-integration.md §12).
    // Said at bring-up, while it can still be acted on.
    if (flex.Station.DaxChannelWarning is string daxWarning)
    {
        Console.Error.WriteLine($"flex: {daxWarning}");
    }

    // The radio's global transmit filter, read back at bring-up (Flex 0.7.0) - it, not the
    // slice, limits transmitted DAX audio bandwidth, and it is whatever last touched the radio
    // (a 300 Hz CW filter would silently crush a 3 kHz mode). We deliberately never set it;
    // reporting it makes a stale value visible. Headless only - attach leaves it to SmartSDR.
    // A radio that reboots, or a network that blips, ends the session - and nothing used to
    // notice. The modem then sat with a dead socket: no audio, no waterfall, and nothing said
    // why. Stop instead, with exit 1 so the unit restarts and rediscovers the radio rather
    // than staying down (exit 2 is reserved for "your configuration is wrong", which a restart
    // could never fix).
    flex.Station.Client.Disconnected += () =>
    {
        Console.Error.WriteLine(
            "flex: the radio's session ended - rebooted, dropped off the network, or closed the "
            + "connection. Stopping so the service restarts and rediscovers it.");
        radioLost = true;
        cancellation.Cancel();
    };

    // Losing the SLICE is not the same as losing the session, and it used to be invisible. The
    // socket stays up, the modem keeps queueing, and every keyup is accepted by the radio and
    // does nothing - a station deaf and mute with a healthy-looking connection. Say so once,
    // clearly, and rebuild.
    flex.Station.SliceLost += check =>
        Console.Error.WriteLine($"flex: lost our slice - {check.Detail}");

    // State the starting point explicitly. The station reaches Healthy inside the bring-up
    // above, so the HealthChanged subscription below is attached after that first transition
    // has already fired and can only ever report LATER ones. Without this line the journal
    // never says that ownership was checked at all, which is exactly the reassurance a
    // six-day silent outage taught us to want.
    Console.WriteLine(
        $"flex: slice {flex.Station.SliceIndex} health {flex.Station.Health} at bring-up "
        + $"({flex.Station.VerifyOwnership().Detail switch
        {
            "" => "owned by this client",
            string detail => detail,
        }})");

    flex.Station.HealthChanged += report =>
    {
        switch (report.Health)
        {
            case M0LTE.Flex.FlexStationHealth.Healthy:
                Console.WriteLine($"flex: slice healthy - {report.Detail}");
                break;

            case M0LTE.Flex.FlexStationHealth.Contended:
                // Deliberately not an exit. Restarting would recreate the slice, which is the
                // same move the other client is making, and two daemons rebuilding at each
                // other churns the radio for both. Stay up, stay off the air, and be loud.
                Console.Error.WriteLine(
                    $"flex: STANDING DOWN - {report.Detail} This station is now off the air and "
                    + "will not retake the slice. Check what else is connected to the radio "
                    + "(SmartSDR, a capture tool, a second modem), stop it, and restart this "
                    + "service.");
                break;

            case M0LTE.Flex.FlexStationHealth.Disposed:
                // Shutdown. The daemon already says it is stopping; "Disposed - disposed"
                // adds nothing but a line to read past.
                break;

            case M0LTE.Flex.FlexStationHealth.Recovering:
            case M0LTE.Flex.FlexStationHealth.SliceLost:
            case M0LTE.Flex.FlexStationHealth.Unbound:
            default:
                Console.Error.WriteLine($"flex: {report.Health} - {report.Detail}");
                break;
        }
    };

    // Rebuild off the status thread: RecoverAsync serialises itself and returns immediately
    // when the slice is already ours, so a duplicate trigger is free.
    M0LTE.Flex.FlexStation flexStation = flex.Station;
    flexStation.SliceLost += lostCheck =>
    {
        _ = lostCheck;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    M0LTE.Flex.FlexRecoveryResult result =
                        await flexStation.RecoverAsync(cancellation.Token);
                    if (!result.Recovered)
                    {
                        Console.Error.WriteLine(
                            $"flex: could not rebuild the slice after {result.Attempts} "
                            + $"attempt(s) - {result.Detail}");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            },
            CancellationToken.None);
    };

    // The radio's frequency reference, into the waterfall's top bar and kept current. Only a
    // Flex reports one; a soundcard station shows nothing rather than an empty label.
    if (waterfallServer is not null)
    {
        void PublishReference(M0LTE.Flex.FlexReferenceStatus reference) =>
            waterfallServer.SetRadioStatus(reference.Describe());

        PublishReference(flex.Station.Client.Reference);
        flex.Station.Client.ReferenceChanged += PublishReference;
        Console.WriteLine($"flex: reference {flex.Station.Client.Reference.Describe()}");
    }

    // What the transmitter is actually doing, live, in the page's top bar. The meters are the
    // radio's own - forward power and SWR - so this reports the transmission rather than what we
    // asked for, which is the difference that matters when an antenna is wrong.
    if (waterfallServer is not null)
    {
        try
        {
            M0LTE.Flex.FlexMeters txMeters =
                await M0LTE.Flex.FlexMeters.SubscribeAsync(flex.Station.Client);
            flexMeters = txMeters;
            txMeters.Updated += reading =>
            {
                if (!reading.Descriptor.Name.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase))
                {
                    return;   // one update per keyed sample is plenty; SWR is read alongside it
                }

                double watts = M0LTE.Flex.FlexMeters.DbmToWatts(reading.Value);
                if (watts < TransmitReadoutFloorWatts)
                {
                    // Key-up: the display averages what it was given and holds that average,
                    // because a packet burst is over before an operator can read a live figure.
                    waterfallServer.SetTransmitReading(null, null);
                    return;
                }

                waterfallServer.SetTransmitReading(watts, txMeters.SwrFromPowers());
            };
        }
        catch (Exception e) when (e is M0LTE.Flex.FlexProtocolException or IOException)
        {
            // A station that cannot read its meters still transmits perfectly well.
            Console.Error.WriteLine($"flex: no transmit metering - {e.Message}");
        }
    }

    // Always reported, set or not: an inherited power shapes every transmission just as much as
    // a configured one, and it is the number the operator will be asked about on the air.
    if (flex.Station.RfPowerApplied is int rfPower)
    {
        double watts = rfPower / 100.0 * FlexDevice.PaWatts;
        string ceiling = flex.Station.MaxPowerLevel is int max
            ? $", limit {max / 100.0 * FlexDevice.PaWatts:0.#} W"
            : "";
        string source = flexTuning.TxPowerWatts is null ? " (radio's own setting)" : "";
        Console.WriteLine($"flex: transmit power {watts:0.#} W{ceiling}{source}");
    }

    if (flex.Station.TransmitFilter is (int txFilterLow, int txFilterHigh))
    {
        Console.WriteLine($"flex: transmit filter {txFilterLow}..{txFilterHigh} Hz (radio global - limits TX audio bandwidth)");

        // What the filter passes is checked against where the modems actually are, rather than
        // assumed: a modem outside it transmits a truncated signal, or nothing at all, and does
        // so silently. The high cut we ask for can still come back narrower - it is a radio-wide
        // setting and the radio has the last word - and in attach mode we never set it at all.
        foreach (TransmitFilterPlan.Band band in txBands
                     .Where(b => b.LowHz < txFilterLow || b.HighHz > txFilterHigh))
        {
            // Only the high cut is settable through the station API, so a modem under the low
            // edge is something only the operator can fix. Telling someone to move a modem that
            // has nowhere to go - the freedv-*/ms110d-* centres are pinned by their specs - is
            // worse than saying nothing.
            string remedy = band.LowHz < txFilterLow
                ? "The low cut is not settable from here; widen it on the radio."
                : ModemCatalog.AcceptsCentreFrequency(band.Mode)
                    ? "Widen the high cut on the radio, or move the modem down the passband."
                    : "Widen the high cut on the radio - this mode's centre is fixed by its spec.";
            Console.Error.WriteLine(
                $"flex: WARNING - modem {band.SubChannel} ({band.Mode}) occupies "
                + $"{band.LowHz:F0}-{band.HighHz:F0} Hz, outside the radio's "
                + $"{txFilterLow}..{txFilterHigh} Hz transmit filter - it will be clipped. "
                + remedy);
        }
    }

    if (flex.Station.ReceiveFilter is (int rxFilterLow, int rxFilterHigh))
    {
        Console.WriteLine(
            $"flex: slice receive filter {rxFilterLow}..{rxFilterHigh} Hz (what the modems can hear)");

        // Deaf rather than clipped, and just as quiet about it: a modem outside the slice's filter
        // decodes nothing at all and looks exactly like a dead band.
        foreach (TransmitFilterPlan.Band band in txBands
                     .Where(b => b.LowHz < rxFilterLow || b.HighHz > rxFilterHigh))
        {
            Console.Error.WriteLine(
                $"flex: WARNING - modem {band.SubChannel} ({band.Mode}) occupies "
                + $"{band.LowHz:F0}-{band.HighHz:F0} Hz, outside the slice's "
                + $"{rxFilterLow}..{rxFilterHigh} Hz receive filter - it will hear nothing there.");
        }
    }

    if (flex.Station.ReceiveFilterWarning is string receiveFilterWarning)
    {
        // The radio's ceiling on receive width is not measured, so this is how a radio that will
        // not go as wide as asked says so, rather than the modem quietly going deaf.
        Console.Error.WriteLine($"flex: WARNING - {receiveFilterWarning}");
    }
}
else
{
    ptt = new NullPtt();

    // Hardware the config names but the box does not have is the single most likely thing to
    // go wrong on a first install (the seeded config points at a CM108 on /dev/hidraw0). Say
    // which setting, which file, and how to list what is really there - but exit 1, not 2, so
    // the unit keeps retrying and comes up by itself if the device was only slow to appear.
    try
    {
        switch (pttConfig?.Type)
        {
            case null:
                break;
            case "serial":
                string serialLine = pttConfig.Line ?? "rts";
                ptt = new SerialPtt(pttConfig.Device, useRts: serialLine != "dtr", useDtr: serialLine == "dtr");
                Console.WriteLine($"ptt: serial {pttConfig.Device} ({serialLine})");
                break;
            case "cm108":
                int gpio = pttConfig.Gpio ?? 3;
                ptt = new Cm108Ptt(pttConfig.Device, gpio);
                Console.WriteLine($"ptt: cm108 {pttConfig.Device} (gpio {gpio})");
                break;
            default:
                Console.Error.WriteLine($"unknown ptt type '{pttConfig.Type}'");
                return 2;
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine(DeviceDiagnostics.Ptt(pttConfig!, configPath, e));
        return 1;
    }

    // The card's mixer, BEFORE the PCM is opened, and finished with before it is.
    //
    // The order is the point, and it was found the hard way on radio1 (2026-09-06). Opening a
    // capture stream does not start it: the kernel starts the endpoint on the first
    // snd_pcm_readi, and on a USB card that start is a URB submission which fails with -EIO if
    // the device is busy with a control transfer at that moment. Mixer traffic IS control
    // transfers. Doing it between the open and the first read therefore leaves a window in which
    // reading the card's own levels can stop the card from ever delivering audio, which showed up
    // as "receive feed dead: the input device failed (snd_pcm_readi: Input/output error)" about a
    // second after start-up, on 10 runs out of 13. Nothing here needs the PCM - reading a mixer
    // never did, which is why --mixer-show works on a running station - so the window is closed
    // by not being in it.
    //
    // Read even when the configuration asks for nothing, because a station's capture gain is the
    // difference between clean audio and clipped audio and the start-up log should say what it
    // is; written only where a key said so, so a file with no "alsa" section leaves every control
    // alone.
    string mixerCard = alsaConfig?.Mixer?.Card ?? AlsaMixer.CardFor(device);
    if (AlsaMixer.TryOpen(mixerCard, out AlsaMixer? openedMixer, out string mixerWhy))
    {
        mixer = openedMixer;

        // Guarded, not bare: these are top-level statements with nothing above them to catch
        // anything, and TryOpen only proves the ten entry points it uses itself. A libasound
        // missing one of the twenty the apply reaches would otherwise be a crash at every
        // start-up and a systemd restart loop, over a mixer. It costs the mixer instead.
        //
        // This is also where the state file is read and the precedence is decided: what
        // "alsa"."mixer" pins is applied and wins, what it says nothing about comes from a
        // change made on the page in some earlier run, and the rest is left as the card has it.
        // "." for a station configured entirely on the command line, which puts the state file
        // in the working directory. Nothing writes it on such a station anyway - the config API
        // refuses to be served without a --config file - but the read still has to have a path.
        mixerRuntime = MixerRuntime.Start(
            mixer!, alsaConfig?.Mixer, configPath ?? ".", device, Console.WriteLine,
            out string applyWhy);
        if (mixerRuntime is null)
        {
            mixerWhyNot = $"{mixerCard} could not be read or set: {applyWhy}";
            openedMixer!.Dispose();
            mixer = null;
        }
    }
    else
    {
        // Not a failure. A card with no mixer at all is a real thing (a bare I2S codec, a loopback
        // device), and so is a libasound with no mixer functions in it - neither is a reason for
        // a station to stop receiving.
        mixerWhyNot = $"{mixerCard} has no mixer: {mixerWhy}";
        Console.WriteLine(
            $"{MixerSetup.JournalPrefix}{mixerCard} has no mixer ({mixerWhy}); capture gain, AGC "
            + "and mic boost are left as the card has them");
    }

    try
    {
        // Transmit: modulate at the DSP rate; play at the card-native capture rate through the
        // image-rejecting upsampler (cards commonly refuse to open 12 kHz playback directly).
        var alsaPlayback = new AlsaAudioOutput(device, captureRate == DspRate ? DspRate : captureRate);
        alsaOut = alsaPlayback;
        playback = captureRate == DspRate
            ? alsaPlayback
            : new UpsamplingAudioOutput(alsaPlayback, DspRate);
        // Receive: capture at the card-native rate; ARDOP buffers more deeply (500 ms vs the
        // 120 ms default) to ride out device hiccups (snd-aloop re-locking mid-frame).
            var alsaInput = new AlsaAudioInput(device, captureRate, ardopModem is null ? 120_000 : 500_000);
        alsaIn = alsaInput;
        input = alsaInput;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine(DeviceDiagnostics.Audio(device, configPath, e));
        return 1;
    }

    Console.WriteLine($"audio: {device} capture {captureRate} Hz -> {DspRate} Hz");
}

using AlsaMixer? mixerLifetime = mixer;

// The operator page's mixer group and any script that wants the card's state come through here,
// under the same key as every other change - or under no key at all, where
// "waterfall"."enableAudioControls" says so. A station with no mixer says so rather than 404ing,
// so the page can tell "no mixer here" from "this daemon is too old to have the endpoint".
if (mixerRuntime is MixerRuntime liveMixer)
{
    runtimeApi?.ServeMixer(liveMixer);
}
else
{
    runtimeApi?.NoMixer(mixerWhyNot);
}

await using var flexLifetime = flex;

// A keyup that fails must say so: the loop survives it (faulting the queued frames), and
// this line is the only place the operator learns why nothing went out - "another station
// holds the PA" on an arbitrated radio, or a dead PTT lead anywhere else.
channel.PttFailed += failure => Console.Error.WriteLine($"ptt: {failure.Message}");

Task transmitter = channel.RunTransmitterAsync(playback, ptt, cancellation.Token);

// A transmitter that dies (the output device failed mid-keyup) must stop the daemon, not
// leave it running as a healthy-looking receive-only station for the rest of the process's
// life - which is what discarding the task's fault used to do, silently. Exit 1 so the
// unit restarts: a device that comes back is exactly what a restart fixes, and exit 2
// stays reserved for "your configuration is wrong".
_ = transmitter.ContinueWith(
    t =>
    {
        Console.Error.WriteLine(
            $"transmit: the audio output failed - {t.Exception!.GetBaseException().Message}. "
            + "Stopping so the service restarts.");
        radioLost = true;
        cancellation.Cancel();
    },
    CancellationToken.None,
    TaskContinuationOptions.OnlyOnFaulted,
    TaskScheduler.Default);

// ---------------------------------------------------------------- TX test
// The operator's transmitter test. Built here because this is the first point at which the
// answer to "can this station key at all" is settled: the PTT line has been opened or found not
// to exist, and the transmitter loop above is running. It is offered three ways - the operator
// page's control, POST /api/txtest, and --two-tone/--tone - and all three go through this one
// runner, so there is one set of rules and one cap rather than three.
string? txTestRefusal =
    !txTestConfig.Enabled
        ? "\"txTest\".\"enabled\" is false in this station's configuration"
    : channel.ReceiveOnlyReason is string txTestReceiveOnly
        ? txTestReceiveOnly
    : ptt is NullPtt
        // Refused rather than left to VOX, deliberately. A test transmission has to be keyed and
        // unkeyed by the station that made it; a VOX trip is the radio deciding, and "the daemon
        // put a carrier up and something else decided when to stop" is not a transmission anybody
        // should be able to start from a web page.
        ? "no \"ptt\" is configured, so this daemon does not key the radio"
    : null;

// Which sub-channel a test is filed under in the frame log and the frames panel. A label, not a
// choice: the transmit path is shared - one output device and one PTT line, whichever modem's
// frame is going out - so a test measures the same path whatever this says. It is the
// sub-channel a KISS frame on port 0 would reach: 0 where there is a modem there, and the lowest
// configured otherwise.
int txTestSubChannel = modems.Any(m => m.SubChannel == 0)
    ? 0
    : modems.Count > 0 ? modems.Min(m => m.SubChannel) : 0;

var txTestRunner = new TxTestRunner(new TxTestOptions
{
    Channel = channel,
    Journal = stationJournal,
    DefaultSeconds = txTestConfig.Seconds,
    MaxSeconds = txTestConfig.MaxSeconds,
    Amplitude = txTestConfig.Amplitude,
    Refusal = txTestRefusal,
    SubChannel = txTestSubChannel,
    Report = status => waterfallServer?.ReportTxTest(status),
    Recorded = record =>
    {
        // Written down where transmissions are written down. The frame log's rows are frames and
        // a tone burst is not one, so what the payload holds is the sentence describing what went
        // out - which is what the panel shows beside the row, and what a monitor site is sent for
        // a station that publishes to one. See CONFIG.md under "frameLog".
        frameLog?.RecordTransmitted(
            record.SubChannel,
            record.Payload,
            Packet.SoundModem.Waterfall.WaterfallWebServer.TestTransmissionMode,
            record.AudioHz,
            // No rf_hz: a test tone is not a modem with a planned RF centre, and a dial plus an
            // audio frequency is only the answer on one sideband of one kind of station.
            rfHz: null);
        waterfallServer?.ReportTestTransmission(
            record.SubChannel, record.Text, record.Payload.Length);

        // A test is a transmission, so it owes the same identification a frame does. The ident
        // clock is normally started by channel.FrameTransmitted, which a test never raises: it
        // goes out through the delegate overload and carries no sub-channel of its own. Without
        // this a station could key for tones all afternoon and never say who it was.
        if (identifiers.TryGetValue(record.SubChannel, out StationIdentifier? owedForTest))
        {
            owedForTest.NoteTransmission();
        }
    },
});

// Never on a public page: a test transmission is the operator's own act and the control belongs
// on the operator's own page, which is the only page this daemon serves that is not public.
if (waterfallServer is not null && waterfallConfig?.Public != true)
{
    waterfallServer.SetTxTest(txTestRunner.Control);
}

runtimeApi?.ServeTxTest(txTestRunner);
if (txTestRefusal is null)
{
    stationJournal.Write(
        $"tx test: ready - two-tone {Packet.SoundModem.Audio.TestTone.TwoToneLowHz:F0}+"
        + $"{Packet.SoundModem.Audio.TestTone.TwoToneHighHz:F0} Hz or a single tone, "
        + $"{txTestConfig.Seconds:0.#} s by default, capped at "
        + $"{Math.Clamp(txTestConfig.MaxSeconds, 1, TxTestRunner.CeilingSeconds):0.#} s");
}
else
{
    stationJournal.Write($"tx test: unavailable - {txTestRefusal}");
}

// Identification. Two halves: every frame a configured modem sends starts its clock, and a
// slow poll sends the ident once one falls due. Polling rather than a timer per modem because
// the decision is StationIdentifier's and depends on traffic as well as time - there is no
// instant to schedule in advance, only a condition to notice - and at a ten-minute interval a
// five-second granularity costs nothing.
if (identifiers.Count > 0)
{
    channel.FrameTransmitted += (sub, _) =>
    {
        if (identifiers.TryGetValue(sub, out StationIdentifier? owed))
        {
            owed.NoteTransmission();
        }
    };

    _ = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach ((int sub, StationIdentifier owed) in identifiers)
            {
                if (!owed.IdentificationDue)
                {
                    continue;
                }

                try
                {
                    // Queued like anything else, so it waits out a busy channel and a keyup in
                    // progress rather than transmitting over somebody. The TXDELAY budget is
                    // spent on silence: an SSB transmitter radiates nothing without audio, which
                    // is exactly what the PTT settling time wants.
                    await channel.EnqueueTransmit(txDelay =>
                    {
                        float[] tone = owed.Render();
                        int lead = (int)Math.Round(txDelay / 1000.0 * DspRate);
                        var audio = new float[lead + tone.Length];
                        tone.CopyTo(audio, lead);
                        return audio;
                    },
                    // This sub-channel's identifier is the transmitter, so the CW ident takes its
                    // own keyup rather than lengthening somebody else's - the station is deaf for
                    // whatever it appends itself to.
                    source: owed).ConfigureAwait(false);

                    // Stamped only on success: an ident the radio refused was not sent, and
                    // clearing the debt for it would mean the station quietly stopped identifying.
                    owed.NoteIdentified();
                    Console.WriteLine($"id[{sub}] {owed.Text} in CW");
                }
                catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
                {
                    Console.Error.WriteLine($"id[{sub}]: identification dropped - {refused.Message}");
                }
            }
        }
    });
}

// Dead-feed protection, two watches for two failure families, thresholds per device (the
// config's "deadFeed" block overrides; 0 = off). What each input actually does when its
// source dies - and therefore which of the two watches can see it - is read from the
// implementations and the two real incidents, and is written down on
// StationOptions.DeviceKind, beside the thresholds it decides.
//
// flex:mock counts as a bench device, not a Flex: its DAX-RX path deliberately delivers
// nothing between injected frames, which a starvation watch would read as a dead radio
// 30 s into every idle bench session.
DeadFeedDevice deadFeedDevice =
    wavLoopPath is not null ? DeadFeedDevice.WavLoop
    : deviceIsUberSdr ? DeadFeedDevice.UberSdr
    : deviceIsFlex ? (flex!.Mock is null ? DeadFeedDevice.Flex : DeadFeedDevice.WavLoop)
    : DeadFeedDevice.Alsa;

// The uplink to a public monitor site: this station's own display stream, offered outward over
// one socket the station dials out on. Nothing here is reachable without a "publish" block, and
// nothing about a station that has one depends on it - the uplink writes a journal line and
// retries for ever, and it never faults the station, sets the exit code or touches the radio
// (docs/uplink-plan.md, decision 8). It is the waterfall server's relay, so it is offered exactly
// what the page is already being shown.
Packet.SoundModem.Waterfall.UplinkClient? uplink = null;
// Not on a --two-tone run: a monitor site should not be dialled, credited with a station and
// then dropped again for the sake of a five-second bench test.
if (benchTxTest is null && publishConfig is not null)
{
    int publishRate = DaemonConfig.PublishedAudioRate(publishConfig, DspRate);

    // A modem above half the published rate is not in the published picture at all, and finding
    // that out by looking at the site would be a poor way to learn it.
    string outside = string.Join(", ", waterfallServer!.Bands
        .Where(b => b.HighHz > publishRate / 2.0)
        .Select(b => $"modem {b.SubChannel} ({b.Mode}, to {b.HighHz:F0} Hz)"));
    if (outside.Length > 0)
    {
        Console.Error.WriteLine(
            $"publish: WARNING - the published audio spans 0 to {publishRate / 2} Hz, so "
            + $"{outside} will not appear on the site. Raise \"publish\".\"audioRate\" to a "
            + $"divisor of {DspRate} that covers it, or accept the narrower picture.");
    }

    // 4.5's ADSL caveat, said as a fact about their line rather than as a warning about ours.
    if (publishRate >= 48000)
    {
        Console.Error.WriteLine(
            "publish: WARNING - 48000 Hz audio is about 770 kbit/s upstream while somebody is "
            + "watching. That is fine on FTTC or FTTP and most of an ADSL upload; there is no "
            + "codec, by decision, so the lever is \"publish\".\"audioRate\".");
    }

    uplink = new Packet.SoundModem.Waterfall.UplinkClient(
        waterfallServer,
        new Packet.SoundModem.Waterfall.UplinkSettings
        {
            Url = publishConfig.Url!,
            Token = publishConfig.Token!,
            Callsign = publishConfig.Callsign!,
            Operator = publishConfig.Operator,
            Location = publishConfig.Location,
            Radio = publishConfig.Radio,
            Site = publishConfig.Site,
            ChannelRate = DspRate,
            AudioRate = publishRate,
            FramesOnlyWhileWatched = publishConfig.Frames == "watched",
            // The dial and sideband the band plan settled on, so a relayed page draws the same RF
            // scale this station's own page draws rather than being told a second time.
            DialHz = waterfallConfig!.DialFrequencyHz != 0
                ? waterfallConfig.DialFrequencyHz
                : receiveDialHz ?? 0,
            Sideband = bandPlan?.Sideband ?? waterfallConfig.Sideband,
        },
        TimeProvider.System,
        stationJournal.ErrorSink);
    waterfallServer.Relay = uplink;
    uplink.Start();
}

// Disposal runs in reverse declaration order, so this stops after the station has stopped feeding
// it and before the waterfall server it publishes from goes, which is the order a goodbye needs.
await using var uplinkLifetime = uplink;

long frameLogDropsSeen = 0;
long captureDropsSeen = 0;

// The station: the input, the channel it feeds, the watches that decide the feed has died,
// and the loop that turns between them. One implementation for every device kind, so a
// second flavour of deployment cannot quietly grow a second opinion about a dead feed.
using var station = new Station(
    new StationOptions
    {
        Channel = channel,
        Input = input,
        DspRate = DspRate,
        Journal = stationJournal,
        DeviceKind = deadFeedDevice,
        DeadFeed = deadFeedConfig,
        BlockMilliseconds = ardopModem is null ? 100 : 20,
        SessionLive = uberSdrSessionLive,

        // A station that has deliberately given up its slice is silent on purpose, and restarting
        // it is the one response guaranteed to be wrong. Measured on the live 40 m station,
        // 2026-08-14: the contention policy stood down at 07:07:18 and the silence watch restarted
        // the process at 07:07:45, which took a fresh slice and resumed exactly the fight the
        // stand-down existed to end. Under real contention that is a loop - create, lose, stand
        // down, thirty seconds of silence, restart - and the dead-feed message already warned it
        // would be ("a deliberately muted DAX stream restart-loops this way"), without knowing that
        // the mute could be our own doing.
        //
        // The watch is for a feed that died WITHOUT explanation. This one has one, it is already in
        // the journal above, and the operator instruction there is to stop the other client and
        // restart the service by hand.
        SilenceExcuse = () => flex?.Station.Health == M0LTE.Flex.FlexStationHealth.Contended
            ? "receive feed silent because this station stood down from a contested slice, "
                + "which is deliberate - not restarting. Stop whatever else is claiming the "
                + "radio and restart this service."
            : null,

        // The sound card is the one device with xrun counters, and they are the difference between
        // "the band is quiet" and "this machine will not schedule us".
        XrunCounters = alsaIn is null && alsaOut is null
            ? null
            : () => (alsaIn?.Xruns ?? 0, alsaOut?.Xruns ?? 0),

        HealthChecks =
        [
            // A full disk left a station keeping an empty frame log for weeks with nothing anywhere
            // saying so: this counter was a dead one until the receive loop started reading it.
            () =>
            {
                if (frameLog is null)
                {
                    return null;
                }

                long logDrops = frameLog.Dropped;
                if (logDrops <= frameLogDropsSeen)
                {
                    return null;
                }

                string line =
                    $"frame log: {logDrops - frameLogDropsSeen} frames dropped unwritten "
                    + $"({logDrops} total) - the disk cannot keep up, is full, or is unwritable";
                frameLogDropsSeen = logDrops;
                return line;
            },

            () =>
            {
                if (survey is null)
                {
                    return null;
                }

                long captureDrops = survey.DroppedCaptures;
                if (captureDrops <= captureDropsSeen)
                {
                    return null;
                }

                string line =
                    $"survey: {captureDrops - captureDropsSeen} captures dropped unwritten "
                    + $"({captureDrops} total) - the disk cannot keep up, is full, or is unwritable";
                captureDropsSeen = captureDrops;
                return line;
            },
        ],
    },
    cancellation.Token);

// This process runs one station, so a station fault IS the daemon's fault: journal the
// sentence the station wrote, and take the proven restart contract every one of these
// detectors has always taken - orderly shutdown, exit 1, systemd rebuilds the device session
// from scratch (Restart=on-failure in the shipped unit; the capture campaign's unit is
// Restart=always). That is what each of these paths did for itself before there was a Station
// type. A host running fifty would update one row of a table instead, which is the whole
// reason the decision moved out here.
station.Faulted += fault =>
{
    stationJournal.WriteError(fault.Reason);

    if (fault.Stalled)
    {
        // The receive loop is wedged inside a blocked Read (the ALSA stall family), so the
        // orderly shutdown below can never run. Ending the process is the host's to do and
        // never the station's; systemd restarts either way.
        Environment.Exit(1);
    }

    radioLost = true;
    cancellation.Cancel();
};

// --two-tone / --tone: transmit once and go.
//
// The receive loop goes on a thread of its own here, which is what Station.Run's own docs say a
// host running it alongside anything else must do: it is synchronous and blocks until the station
// stops, because every input's Read is. The station has to be listening for the test to defer to
// a busy channel on the carrier sense a frame would - and calling Run in front of the test rather
// than beside it is a test that never transmits at all, the transmitter having already been shut
// down by the time it is asked. Measured on flex:mock before this was a thread: the burst sat on
// the queue until the wall-clock bound gave up on it.
if (benchTxTest is not null)
{
    var listening = new Thread(station.Run)
    {
        IsBackground = true,
        Name = "tx-test-station",
    };
    listening.Start();

    TxTestOutcome benchOutcome = await txTestRunner.RunAsync(benchTxTest);
    cancellation.Cancel();
    try
    {
        await transmitter;
    }
    catch (Exception)
    {
        // The transmitter ends by cancellation, which is how this path always ends it.
    }

    // The receive loop first, and BEFORE the devices are closed. It notices the cancellation on
    // its next block and returns; until it has, it is inside the input's Read, and closing an
    // ALSA capture handle from under snd_pcm_readi on another thread is not something alsa-lib
    // supports. A background thread, so an input whose Read is wedged cannot hold the process
    // open either way - but it gets its five seconds to come out on its own first.
    listening.Join(TimeSpan.FromSeconds(5));

    if (!deviceIsFlex)
    {
        (ptt as IDisposable)?.Dispose();
        (playback as IDisposable)?.Dispose();
        (input as IDisposable)?.Dispose();
    }

    // Exit 1 on a refusal, so a bench script can tell "it went out" from "it did not" without
    // reading the journal. Exit 2 stays what it has always been: your configuration is wrong.
    return benchOutcome.Ran ? 0 : 1;
}

station.Run();

try
{
    await transmitter;
}
catch (OperationCanceledException)
{
    // The normal shutdown path: the loop ends by cancellation.
}
catch (Exception)
{
    // Already journalled (and turned into exit 1) by the fault observer above.
}

if (!deviceIsFlex)
{
    (ptt as IDisposable)?.Dispose();
    (playback as IDisposable)?.Dispose();
    (input as IDisposable)?.Dispose();
}

return radioLost || restartRequested ? 1 : 0;

/// <summary>Runs an action on scope exit - for state captured after its `using` must be declared.</summary>
internal sealed class Disposer(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
