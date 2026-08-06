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
using Packet.SoundModem.Iq;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;
using Packet.SoundModem.Ms110d;

// pdn-soundmodem: headless soundcard packet modem daemon.
//
//   pdn-soundmodem [--config soundmodem.json]
//   pdn-soundmodem [--device default] [--capture-rate 48000] [--kiss 8105]
//                  [--bind 127.0.0.1|*]
//                  [--modem N:MODE[:FREQ]]... [--ptt serial:/dev/ttyUSB0[:rts|:dtr]]
//                  [--ptt cm108:/dev/hidraw0[:gpio]]
//                  [--txdelay MS] [--wav FILE] [--wav-loop FILE] [--quality-frames]
//                  [--psk-detector coherent|differential]
//                  [--paging PORT[:BAUD]]
//                  [--ardop PORT]
//                  [--waterfall PORT] [--dial HZ]
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
        case "--quality-frames": qualityFrames = true; break;
        case "--psk-detector": pskDetectorOverride = Enum.Parse<PskDetector>(Next(), ignoreCase: true); break;
        case "--paging": pagingSpec = Next(); break;
        case "--ardop": ardopPort = int.Parse(Next()); break;
        case "--flex-freq": flexFreq = Next(); break;
        case "--flex-ant": flexAnt = Next(); break;
        case "--flex-mode": flexMode = Next(); break;
        case "--flex-daxch": flexDaxCh = Next(); break;
        case "--help":
            Console.WriteLine("see source header for usage");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

var modems = new List<ModemConfig>();
PttConfig? pttConfig = null;
PagingConfig? paging = null;
FlexConfig? flexConfig = null;
UberSdrConfig? uberSdrConfig = null;
WaterfallConfig? waterfallConfig = null;
SurveyConfig? surveyConfig = null;
bool idBeacons = true;

if (configPath is not null)
{
    // A bad config is an operator typo, not a bug: explain it and exit 2. The unit's
    // RestartPreventExitStatus=2 stops systemd retrying, so the journal carries one
    // readable explanation instead of a stack trace every RestartSec.
    DaemonConfig? config = DaemonConfig.TryLoad(configPath, out string configError);
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
    paging = config.Paging;
    flexConfig = config.Flex;
    uberSdrConfig = config.UberSdr;
    waterfallConfig = config.Waterfall;
    surveyConfig = config.Survey;
    idBeacons = config.IdBeacons;
    ardopPort ??= config.Ardop?.Port;
    Console.WriteLine($"config: {configPath}");
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
    modems.Add(new ModemConfig
    {
        SubChannel = int.Parse(specParts[0]),
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

// ARDOP's engine is native 12 kHz; on a 48 kHz channel (any fsk9600/c4fsk/freedv/ms110d
// modem present) ArdopChannelBridge decimates its receive audio down and upsamples its
// bursts back up, so it shares the channel either way.
int DspRate = modems.Any(m => ModemCatalog.DspRateFor(m.Mode) == 48000) ? 48000 : 12000;

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

// Every frame the station hears, written down. Subscribed to the same event the KISS servers
// and the waterfall use, so it records what was actually decoded rather than a second opinion.
FrameLog? frameLog = null;
if (frameLogConfig is not null)
{
    try
    {
        frameLog = FrameLog.Open(frameLogConfig.Path);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
    {
        Console.Error.WriteLine(
            $"cannot open the frame log at {frameLogConfig.Path}\n"
            + $"  {e.Message}\n"
            + "  Set by \"frameLog\".\"path\". The service user must be able to write to its\n"
            + "  directory; remove the \"frameLog\" section to run without one.");
        return 2;
    }

    channel.FrameReceivedWithQuality += (sub, frame, quality) =>
    {
        (double? audio, double? rf) = frameLogRfByModem.TryGetValue(sub, out var placement)
            ? placement
            : (null, null);
        frameLog.Record(sub, frame, quality, audio, rf);
    };
    Console.WriteLine($"frame log: {frameLog.Path}");
}

await using var frameLogLifetime = frameLog;

// The PSK detector, for the informational print below: --psk-detector overrides it; unset,
// the catalogue default applies, which is differential for every PSK family (see
// ModemCatalog.DefaultDetectorFor for the bench evidence - an earlier comment here still
// said "QPSK coherent" long after the catalogue changed its mind, which is the kind of
// drift asking the catalogue directly avoids).
PskDetector bpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("bpsk300");
PskDetector qpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("qpsk2400");

foreach (ModemConfig modemConfig in modems)
{
    int subChannel = modemConfig.SubChannel;
    string mode = modemConfig.Mode;
    double? frequency = modemConfig.Frequency;
    if (DaemonConfig.IsArdop(mode))
    {
        // Not a demodulator: a whole virtual TNC with its own host protocol. Wired below,
        // against the same channel, as a receive tap plus a priority transmitter.
        continue;
    }

    if (!ModemCatalog.IsKnown(mode))
    {
        // Checked here rather than left to ModemCatalog.Create's throw: 38 mode names is
        // plenty to mistype, and "unknown mode 'fsk9600il2p'" with a stack trace under it
        // does not tell you that the name you wanted was one hyphen away.
        Console.Error.WriteLine($"modem {subChannel}: unknown mode '{mode}'");
        string[] near = ModemCatalog.NearestModes(mode);
        if (near.Length > 0)
        {
            Console.Error.WriteLine($"  did you mean: {string.Join(", ", near)}");
        }

        Console.Error.WriteLine(
            $"  the {ModemCatalog.KnownModes.Count} valid mode names are listed at "
            + "https://github.com/packet-net/pdn-soundmodem/blob/main/docs/modes.md");
        return 2;
    }

    if (frequency is not null && !ModemCatalog.AcceptsCentreFrequency(mode))
    {
        // The same wording as ModemCatalog.Create's own refusal: this message had drifted
        // to claim only afsk*/bpsk*/qpsk* accept a frequency, a mode family after the
        // spec-fixed ms110d-*/freedv-* ones learned to take one by decoration.
        Console.Error.WriteLine(
            $"modem {subChannel}: mode '{mode}' occupies the audio band from DC upwards and " +
            "has no centre frequency to move - drop the frequency override (the " +
            "afsk*/bpsk*/qpsk* and spec-fixed ms110d-*/freedv-* modes accept one)");
        return 2;
    }

    // Refused rather than ignored, same as the frequency override above: an operator who wrote
    // this down believes their modem changed behaviour, and on a mode with no second plain
    // reading to release there is nothing for it to change. Named modes rather than a pattern,
    // because fsk9600-il2p and fsk4800-il2p do run the CRC despite their names.
    if (modemConfig.AcceptPlainIl2p && !ModemCatalog.RunsIl2pCrc(mode))
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: mode '{mode}' does not run IL2P+CRC, so it has no separate "
            + "plain-IL2P reading to release - drop \"acceptPlainIl2p\"");
        Console.Error.WriteLine(
            "  it applies to the IL2P+CRC modes: afsk300-il2pc, afsk1200-il2p, bpsk*, qpsk*, "
            + "fsk9600-il2p, fsk4800-il2p, c4fsk*, freedv-*, ms110d-*");
        return 2;
    }

    channel.AddModem(subChannel, sink => ModemCatalog.Create(mode, DspRate, sink,
        new ModemOptions(
            CentreFrequencyHz: frequency,
            OffsetPairs: modemConfig.OffsetPairs,
            OffsetStepHz: modemConfig.OffsetStepHz,
            Detector: pskDetectorOverride,
            AcceptPlainIl2p: modemConfig.AcceptPlainIl2p)));
    Console.WriteLine($"modem {subChannel}: {mode}{(frequency is { } f ? $" @ {f} Hz" : "")}");
    if (modemConfig.AcceptPlainIl2p)
    {
        // Said out loud because it removes the one check that separates a frame from noise the
        // FEC happened to like, and a host has no way to know: such a frame arrives as an
        // ordinary KISS data frame like any other. Nothing is printed in the default case, where
        // the same frames are read and shown and simply not handed on, because that costs the
        // host nothing and would be a line on every IL2P+CRC modem on the station.
        Console.WriteLine(
            "  passing plain IL2P (no trailing CRC) to the host: those frames are checked by "
            + "Reed-Solomon alone");
    }
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

// What the station is doing, one line per frame, in the journal. FrameReceivedWithQuality
// rather than FrameReceived: the mode, the CRC verdict, the FEC corrections and the frequency
// offset are all already measured per frame, and they are what turn "something decoded" into
// something an operator can act on.
channel.FrameReceivedWithQuality += (subChannel, frame, quality) =>
    Console.WriteLine(ActivityLog.Received(subChannel, frame, quality));
channel.FrameTransmitted += (subChannel, frame) =>
{
    Console.WriteLine(ActivityLog.Transmitted(
        subChannel, modeBySubChannel.GetValueOrDefault(subChannel, "?"), frame));

    // And into the station's journal, alongside what it heard: a log that records every frame
    // received and none sent is half a record. Raised after the audio has gone to the device, so
    // a logged row is a frame that actually went on air. Same placement lookup as the receive
    // side, so audio_hz/rf_hz mean the same thing in both directions.
    //
    // The mode is the modem's own report of itself - what the waterfall stamps on its TX rows,
    // and what the receive side already writes into this column - rather than the configured
    // name the console line uses. It costs nothing and it keeps one modem's traffic under one
    // spelling, so "everything on bpsk300-il2pc today" is one query that includes our own.
    (double? audio, double? rf) = frameLogRfByModem.TryGetValue(subChannel, out var placement)
        ? placement
        : (null, null);
    frameLog?.RecordTransmitted(
        subChannel,
        frame,
        channel.Modems.TryGetValue(subChannel, out IModem? sender)
            ? sender.Mode
            : modeBySubChannel.GetValueOrDefault(subChannel, "?"),
        audio,
        rf);
};
channel.TransmitRejected += (subChannel, frame, reason) =>
    Console.Error.WriteLine(ActivityLog.Dropped(subChannel, frame, reason));

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
if (waterfallConfig is not null)
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
            // What each modem is meant to occupy. The centre so the label reads as the
            // operator placed it rather than as the probe measured it, and a width for ARDOP,
            // which is a receive tap rather than an IModem and so cannot be probed at all.
            DeclaredBands = [.. modems.Select(m => new DeclaredBand(
                m.SubChannel,
                DaemonConfig.IsArdop(m.Mode) ? "ardop" : m.Mode,
                m.Frequency ?? (DaemonConfig.IsArdop(m.Mode) ? ArdopChannelBridge.NativeCentreHz : 0),
                DaemonConfig.IsArdop(m.Mode)
                    ? m.Bandwidth ?? ArdopChannelBridge.WidestBandwidthHz
                    : null))],
            // The decoded-frames panel opens on what the station has already written down, so a
            // browser arriving mid-afternoon is shown the channel rather than an empty list. Null
            // when there is no frame log: nothing to show, and nothing pretending otherwise.
            FrameHistory = frameLog is null ? null : frameLog.Recent,
        },
        // One bind for every listener; the waterfall no longer carries its own.
        bindAddress);
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

using var surveyLifetime = new Disposer(() => survey?.Dispose());

// Ghost demodulators for the station identifications a NinoTNC sends alongside its PSK SSB data
// modes rather than within them (300 AFSK AX.25, 200 Hz above the carrier - see IdBeaconGhost).
// Without one, an ident is a recurring burst in the middle of the channel that every station can
// see and none can read. Attached here because a ghost reports to the waterfall and the frame
// log, and both have to exist first; the console line stands alone when neither is configured.
//
// Receive taps, not modems: no KISS sub-channel (a host asking for packet data should not have to
// filter idents out of it), no contribution to carrier sense, and nothing drawn on the waterfall -
// the ident rides on a modem that already has a band there.
if (idBeacons)
{
    var ghostCentres = new HashSet<long>();
    foreach (ModemConfig modemConfig in modems)
    {
        // Two PSK modems tuned to the same centre would hear the same TNC's ident twice and list
        // it twice. A band plan gives them different centres, so this is insurance rather than
        // the normal case - but the duplicate would be indistinguishable from two real beacons.
        double centre = IdBeaconGhost.CentreHzFor(modemConfig.Frequency);
        if (!IdBeaconGhost.AppliesTo(modemConfig.Mode) || !ghostCentres.Add((long)Math.Round(centre)))
        {
            continue;
        }

        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(
            modemConfig.SubChannel, modemConfig.Mode, modemConfig.Frequency, DspRate)!;

        // The ident's RF frequency follows its audio offset from the modem it accompanies, so a
        // band-planned station gets a real frequency in the log rather than a blank column.
        double? ghostRfHz = modemConfig.RfFrequency + IdBeaconGhost.BeaconOffsetHz;
        ghost.BeaconHeard += (frame, quality) =>
        {
            Ax25AddressParser.TryParse(frame, out string source, out string destination);

            // Alongside the rx[N] lines, and distinct from them: this is a station saying who it
            // is on a channel where its data is unreadable to us, which is worth its own word.
            Console.WriteLine(
                $"id[{ghost.SubChannel}] {(source.Length > 0 ? source : "?")}"
                + $">{(destination.Length > 0 ? destination : "?")} {frame.Length} bytes");

            waterfallServer?.ReportIdBeacon(
                ghost.SubChannel,
                quality.Mode,
                string.IsNullOrWhiteSpace(source) ? null : source,
                string.IsNullOrWhiteSpace(destination) ? null : destination,
                frame.Length,
                // How far the identifying station's dial sits from ours, measured off its carrier.
                quality.FrequencyOffsetHz);

            frameLog?.Record(
                ghost.SubChannel, frame, quality, ghost.CentreHz, ghostRfHz,
                modeName: $"ID beacon ({ModeNames.Display(quality.Mode)})");
        };

        channel.AddReceiveTap(ghost.Process);

        // A tap is not one of the channel's modems, so the unkey sweep that resets them misses
        // this: without it the demodulator carries its pre-keyup carrier state across the silence.
        channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                ghost.ResetCarrierState();
            }
        };

        Console.WriteLine(
            $"modem {modemConfig.SubChannel}: id beacons - listening in "
            + $"{ghost.Mode} @ {ghost.CentreHz:0.#} Hz");
    }
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

using var cancellation = new CancellationTokenSource();
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

// Who is attached to a KISS port, in the journal. A host that quietly drops its TCP session
// stops passing traffic, and from the modem's side that is indistinguishable from a quiet band -
// so the attach and the loss both get a line, and the loss carries its reason where it had one.
void WatchClients(KissTcpServer server)
{
    server.ClientConnected += e =>
        Console.WriteLine(ActivityLog.ClientConnected(server.LocalPort, server.DedicatedSubChannel, e));
    server.ClientDisconnected += e =>
        Console.WriteLine(ActivityLog.ClientDisconnected(server.LocalPort, server.DedicatedSubChannel, e));
    server.AcceptFailed += why => Console.Error.WriteLine(
        $"kiss[{server.LocalPort}] accept failed: {why} - listening continues");

    // What a host changed with SETHW, or why it was refused - RAM-only state with no other
    // record, so the journal is where an operator learns which waveform their modem is on.
    server.HardwareCommand += e => Console.WriteLine(e.Applied
        ? $"modem {e.SubChannel}: SETHW -> {e.Description}"
        : $"modem {e.SubChannel}: SETHW ignored - {e.Description}");
}

var kissServers = new List<KissTcpServer>();
// KISS serves the packet modems, so it starts whenever there are any - ARDOP sharing the
// channel is no longer a reason to withhold it. (It was, when an ARDOP channel carried nothing
// else; gating on the old top-level "ardop" setting would now silently leave the packet modems
// with no host interface at all.)
if (modems.Any(m => !DaemonConfig.IsArdop(m.Mode)))
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
if (ardopModem is not null)
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
                    ownsChannelTiming: true).ConfigureAwait(false);
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
if (paging is not null)
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
IPttControl ptt;
IAudioOutput playback;
IAudioInput input;
// Set only on the ALSA path: the sound card is the one device with xrun counters, and they are
// the difference between "the band is quiet" and "this machine will not schedule us".
const int XrunPollMs = 10_000;
AlsaAudioOutput? alsaOut = null;
AlsaAudioInput? alsaIn = null;

if (wavLoopPath is not null)
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

    try
    {
        uberSdr = await UberSdrAudioInput.OpenAsync(
            uberSdrEndpoint, uberSdrTuning, Console.Error.WriteLine, cancellation.Token);
    }
    catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                or HttpRequestException or IOException)
    {
        Console.Error.WriteLine(DeviceDiagnostics.UberSdr(device, configPath, e));
        return 1;
    }

    ptt = new NullPtt();
    playback = new NullAudioOutput(DspRate);
    input = uberSdr;
    Console.WriteLine(
        $"audio: {uberSdrEndpoint} {uberSdrTuning.Mode} IQ at {RfPlan.Mhz(receiveDialHz.Value)} -> "
        + $"{planSideband.ToUpperInvariant()} {uberSdrTuning.SsbLowHz:F0}-{uberSdrTuning.SsbHighHz:F0} Hz "
        + $"audio at {DspRate} Hz (RECEIVE ONLY)");
    if (uberSdr.ReceiverDescription is string receiver)
    {
        Console.WriteLine($"ubersdr: {receiver}");
        waterfallServer?.SetRadioStatus(receiver);
    }

    if (uberSdr.Connection.RefusedForNow)
    {
        Console.Error.WriteLine(
            "ubersdr: the receiver is refusing this address for now "
            + $"({uberSdr.Connection.Reason ?? "daily listening allowance exhausted"}). The station "
            + "is up and will start hearing audio when the receiver lets us back in.");
    }
    else
    {
        Console.WriteLine(
            $"ubersdr: session limit {uberSdr.Connection.MaxSessionTime} s - the stream is picked up "
            + "again each time the receiver ends one");
    }

    // A receiver that stays unreachable is not something to sit quietly on. Exit 1 so the unit
    // restarts and tries afresh, exactly as for a Flex whose session dies (exit 2 is reserved
    // for "your configuration is wrong", which restarting could never fix).
    uberSdr.Lost += reason =>
    {
        Console.Error.WriteLine($"ubersdr: {reason}");
        radioLost = true;
        cancellation.Cancel();
    };
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
                    waterfallServer.SetTransmitStatus(null);
                    return;
                }

                // Rounded, because the readout updates many times a second and a digit that
                // never settles is harder to read than one that does.
                double? swr = txMeters.SwrFromPowers();
                string reading_ = swr is double s && !double.IsInfinity(s)
                    ? $"TX {watts:F1} W, SWR {s:F1}"
                    : $"TX {watts:F1} W";
                waterfallServer.SetTransmitStatus(reading_);
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

// Decimate the source to the DSP rate. When it already runs at the DSP rate (a 48 kHz
// mode's full-bandwidth DAX, --capture-rate 12000, or a 12 kHz virtual card) there is
// nothing to decimate - a Decimator with factor 1 is invalid, so feed samples straight
// through.
int inputRate = input.SampleRate;
var rxDecimator = inputRate == DspRate ? null : new Decimator(inputRate, inputRate / DspRate);

// 100 ms RX blocks for the packet modes; 20 ms when ARDOP runs - its ARQ timing budgets
// (IRS ACK inside the ISS repeat window) want RX latency low.
int blockSamples = ardopModem is null ? inputRate / 10 : inputRate / 50;
var floatBuffer = new float[blockSamples];
var dspBuffer = new float[rxDecimator?.MaxOutput(blockSamples) ?? blockSamples];
var xrunWatch = new XrunWatch();
long nextHealthPoll = Environment.TickCount64 + XrunPollMs;
long frameLogDropsSeen = 0;
long captureDropsSeen = 0;
while (!cancellation.IsCancellationRequested)
{
    int got = input.Read(floatBuffer);
    if (got == 0)
    {
        continue;
    }

    // Polled from the receive loop rather than a timer: this loop only turns when audio is
    // flowing, which is exactly when an xrun means something, and it costs a few field
    // reads. The dropped-write counters ride the same poll - they were dead counters
    // before it: a full disk left a station keeping an empty frame log for weeks with
    // nothing anywhere saying so.
    if (Environment.TickCount64 >= nextHealthPoll)
    {
        nextHealthPoll = Environment.TickCount64 + XrunPollMs;
        if ((alsaIn is not null || alsaOut is not null)
            && xrunWatch.Poll(alsaIn?.Xruns ?? 0, alsaOut?.Xruns ?? 0) is string lost)
        {
            Console.Error.WriteLine(lost);
        }

        if (frameLog is not null && frameLog.Dropped is long logDrops && logDrops > frameLogDropsSeen)
        {
            Console.Error.WriteLine(
                $"frame log: {logDrops - frameLogDropsSeen} frames dropped unwritten "
                + $"({logDrops} total) - the disk cannot keep up, is full, or is unwritable");
            frameLogDropsSeen = logDrops;
        }

        if (survey is not null && survey.DroppedCaptures is long captureDrops
            && captureDrops > captureDropsSeen)
        {
            Console.Error.WriteLine(
                $"survey: {captureDrops - captureDropsSeen} captures dropped unwritten "
                + $"({captureDrops} total) - the disk cannot keep up, is full, or is unwritable");
            captureDropsSeen = captureDrops;
        }
    }

    if (rxDecimator is null)
    {
        channel.ProcessReceive(floatBuffer.AsSpan(0, got));
    }
    else
    {
        int produced = rxDecimator.Process(floatBuffer.AsSpan(0, got), dspBuffer);
        channel.ProcessReceive(dspBuffer.AsSpan(0, produced));
    }
}

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

return radioLost ? 1 : 0;

/// <summary>Runs an action on scope exit - for state captured after its `using` must be declared.</summary>
internal sealed class Disposer(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
