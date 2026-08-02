using System.Globalization;
using System.Net;
using System.Net.Sockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using M0LTE.Dsp;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
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
// Modes: afsk1200, bpsk300 (IL2P+CRC), bpsk300-nocrc, bpsk1200 — the BPSK modes are a
// differential frequency-diversity bank by default (parallel branches at stepped centres;
// bpsk300-multi/bpsk1200-multi are aliases; offsetPairs/offsetStepHz tune it, offsetPairs:0 =
// single modem; --psk-detector coherent forces coherent), qpsk2400, qpsk3600 (both IL2P+CRC),
// fsk9600 (classic G3RUH), fsk9600-il2p (IL2P+CRC), freedv-datac0/1/3/4/13/14 (FreeDV datac
// OFDM waveform; payloads carry the family-standard IL2P+CRC bit stream — a pdn convention,
// FreeDV defines no framing at the raw-data layer), ms110d-wn0/1/2/3/4/5/6/7/8/13
// (MIL-STD-188-110D App D 3 kHz serial-tone, 75–3200 bps; same IL2P+CRC payload
// convention; RX is autobaud — the wnN suffix selects the transmit waveform only).
// Multiple --modem options share the
// audio channel and are addressed by the KISS port nibble (QtSoundModem multiplex model).
//
// The optional N:MODE:FREQ third field sets the modem's audio centre in Hz (both TX and
// RX), QtSoundModem-style — e.g. --modem 0:bpsk300:1459 places 300 BPSK at 1459 Hz to
// meet a peer that sits off the usual centre. It applies to the AFSK tone-pair modes
// (afsk*, centre = mark/space midpoint, default 1700) and the BPSK/QPSK carrier modes
// (bpsk*/qpsk*, default 1500, 1650 for qpsk3600). The baseband FSK families (fsk*/c4fsk*)
// occupy DC-to-Nyquist and have no audio centre; the spec-fixed waveforms (freedv-*,
// ms110d-*) are pinned by their standards — a :FREQ on any of these is an error, not
// silently ignored.
// --wav decodes a file instead of live audio (testing/corpus runs) and exits.
// --wav-loop replays a file forever at wall-clock pace as if it were the capture device —
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
// at a ~1-2 dB noise cost — for short-preamble links). See issue #5.
//
// --paging starts the POCSAG paging endpoint (DAPNET/POCSAG-compatible waveform; local
// paging API, pdn). Pages are not AX.25 frames, so they get a line-based TCP service of
// their own instead of a KISS port — one UTF-8 command per line:
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
// (command port PORT, data port PORT+1 — Pat and other Winlink hosts connect
// unmodified). Per the dedicated-channel policy (docs/ardop-design.md §2.2) the ARDOP
// channel carries only ARDOP: --ardop is exclusive with --modem/--paging, the KISS
// server is not started, and CSMA persistence is forced to 255 — ARDOP runs its own
// channel discipline (ARQ timing budgets, negotiated leaders), which the daemon's
// p-persistence roll must never delay. PTT keying and sample-domain TX-complete are
// the channel's, exactly as for the packet modes.

// --device flex:<radio>[:slice][@station] uses a FlexRadio 6000-series over the LAN as the
// sound card + PTT: <radio> is `discover` (broadcast), an IP (host[:port]), a discovery spec
// (serial=…/name=…), or `mock` (an in-process fake, for offline testing). <slice> is a
// letter A–H (default A). The DAX transport is auto-picked from the DSP rate (24 kHz s16
// for the 12 kHz modes, 48 kHz float32 for the 48 kHz modes). The radio keys itself, so
// --ptt is rejected alongside flex:.
//
// Selection policy: with no @station the daemon OWNS the radio and brings it up HEADLESS
// (registers as a GUI client, creates its own slice — the "pdn at the radio, no SmartSDR"
// deployment; the default). --flex-freq/--flex-ant/--flex-mode set the created slice's
// working frequency (default 14.100000 MHz), antenna (ANT1) and mode (DIGU); the headless path
// also disables band persistence and explicitly tunes the slice, so it lands on the requested
// QRG regardless of the radio's last-used band. A trailing @station selects ATTACH mode:
// coexist with a running SmartSDR by binding that station's existing slice (the slice params
// are then ignored — SmartSDR configures it). --flex-daxch sets the DAX channel to claim
// (default 1) for BOTH paths; a headless client sharing a box with SmartSDR must pick a channel
// SmartSDR is not using (it grabs DAX 1). See docs/flex-integration.md §4/§8.

// 9600-family and freedv-* modems need 48 kHz DSP (the FreeDV engine is native 8 kHz, and
// 48000 = 6·8000 while 12000 has no integer ratio); everything else runs at 12 kHz.

string device = "default";
int captureRate = 48000;
int kissPort = 8105;
string bindAddress = "127.0.0.1";
string sideband = "usb";
bool sidebandWasStated = false;
double? dialFrequency = null;
// 300 ms is a RADIO allowance, not a modem requirement — the modems themselves acquire
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
// real carriers arrive off-frequency with short preambles — reversing the coherent default of
// #5), while QPSK stays Coherent (its V.26A interop was validated coherent, #5/#6).
PskDetector? pskDetectorOverride = null;
string? pagingSpec = null;
int? ardopPort = null;
var modemSpecs = new List<string>();
// Headless FlexRadio slice params (--device flex: with no @station). Null = unset here;
// resolved against the config's Flex section, then FlexTuning defaults. --flex-daxch applies
// to both paths (headless and attach) — the DAX channel to claim.
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
WaterfallConfig? waterfallConfig = null;

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
        Console.Error.WriteLine($"config: WARNING — {warning}");
    }

    device = config.Device;
    captureRate = config.CaptureRate;
    kissPort = config.KissPort;
    bindAddress = config.Bind;
    sideband = config.Sideband;
    sidebandWasStated = config.SidebandWasStated;
    dialFrequency = config.DialFrequency;
    modems = config.Modems;
    pttConfig = config.Ptt;
    paging = config.Paging;
    flexConfig = config.Flex;
    waterfallConfig = config.Waterfall;
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
};

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
                + "about the dial. Drop \"sideband\" — the slice mode already says which it is.");
            return 2;
        }

        sideband = impliedSideband;
    }
}

ModemConfig? ardopModem = modems.FirstOrDefault(m => DaemonConfig.IsArdop(m.Mode));
if (ardopModem is not null && ardopPort is not null)
{
    Console.Error.WriteLine(
        "ARDOP is configured twice — as a modem and with --ardop/\"ardop\". Keep the modem entry.");
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

// ARDOP's engine is native 12 kHz, so it cannot share a channel with the 48 kHz families.
int DspRate = modems.Any(m => ModemCatalog.DspRateFor(m.Mode) == 48000) ? 48000 : 12000;
if (ardopModem is not null && DspRate != M0LTE.Ardop.ArdopModulator.SampleRate)
{
    string wideband = string.Join(", ", modems
        .Where(m => ModemCatalog.DspRateFor(m.Mode) == 48000).Select(m => m.Mode).Distinct());
    Console.Error.WriteLine(
        $"ardop needs a {M0LTE.Ardop.ArdopModulator.SampleRate} Hz channel, but {wideband} "
        + "runs at 48000 Hz. ARDOP can share a channel with the 12 kHz modes (afsk*, bpsk*, "
        + "qpsk*) but not with the 9600/c4fsk/freedv/ms110d families.");
    return 2;
}

// A FlexRadio provides its own DAX sample clock (24/48 kHz auto-picked from the DSP rate),
// so --capture-rate (an ALSA concept) does not apply.
bool deviceIsFlex = FlexDevice.IsFlex(device);

if (!deviceIsFlex && captureRate % DspRate != 0)
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
    bandPlan = BandPlanner.Plan(modems, sideband, dialFrequency, DspRate);
}
catch (InvalidDataException planFailure)
{
    Console.Error.WriteLine($"band plan: {planFailure.Message}");
    return 2;
}

if (bandPlan is not null)
{
    bool flexWillTune = deviceIsFlex && FlexDevice.Parse(device).Headless;
    BandPlanner.Report(bandPlan, Console.Out, flexWillTune);
    foreach (string warning in bandPlan.Warnings)
    {
        Console.Error.WriteLine($"band plan: WARNING — {warning}");
    }

    // A Flex is its own dial: rather than telling the operator to set it, set it. Headless
    // only — in attach mode SmartSDR owns the slice and we would be fighting it. The transmit
    // filter is a global, persistent radio setting, so state its high cut from the plan or a
    // previous session's narrow filter silently truncates the top of the band.
    if (flexWillTune)
    {
        // Overwriting a stated slice frequency without a word would leave someone upgrading a
        // working Flex config with a number that has quietly stopped meaning anything.
        if (flexConfig?.Frequency is not null || flexFreq is not null)
        {
            Console.Error.WriteLine(
                $"flex: WARNING — the slice frequency you set ({flexFreq ?? flexConfig!.Frequency}) "
                + "is superseded by the band plan, which computed "
                + $"{RfPlan.Mhz(bandPlan.DialHz)}. Remove it, or remove the modems' "
                + "\"rfFrequency\" if you meant to place them by audio centre.");
        }

        int filterHigh = BandPlanner.TransmitFilterHighHz(bandPlan);
        flexTuning = flexTuning with
        {
            Frequency = (bandPlan.DialHz / 1_000_000).ToString("F6", CultureInfo.InvariantCulture),
            TransmitFilterHighHz = filterHigh,
        };
        Console.WriteLine(
            $"flex: setting the slice to {RfPlan.Mhz(bandPlan.DialHz)} and the transmit filter "
            + $"high cut to {filterHigh} Hz from the band plan");
    }
}

var channel = new SoundModemChannel(DspRate);
// Channel access (TXDELAY, P, SLOTTIME, TXTAIL) belongs to the host, which sets it over KISS
// at runtime — see KissTcpServer. The library's defaults stand until it does; there is
// deliberately no configuration-file equivalent. --txdelay remains as a bench override for
// runs with no host attached.
if (txDelay is int txDelayOverride)
{
    channel.Csma.TxDelayMilliseconds = txDelayOverride;
}

// Per-family PSK detector, for the informational print below: --psk-detector overrides both;
// unset, the catalogue's per-family defaults apply (BPSK differential, QPSK coherent).
PskDetector bpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("bpsk");
PskDetector qpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("qpsk");

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
        Console.Error.WriteLine(
            $"modem {subChannel}: mode '{mode}' has a fixed centre frequency — drop the " +
            "frequency override (only the afsk*/bpsk*/qpsk* modes accept one)");
        return 2;
    }

    channel.AddModem(subChannel, sink => ModemCatalog.Create(mode, DspRate, sink,
        new ModemOptions(
            CentreFrequencyHz: frequency,
            OffsetPairs: modemConfig.OffsetPairs,
            OffsetStepHz: modemConfig.OffsetStepHz,
            Detector: pskDetectorOverride)));
    Console.WriteLine($"modem {subChannel}: {mode}{(frequency is { } f ? $" @ {f} Hz" : "")}");
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

channel.FrameReceived += (subChannel, frame) =>
    Console.WriteLine($"rx[{subChannel}] {frame.Length} bytes");
channel.TransmitRejected += (subChannel, frame, reason) =>
    Console.Error.WriteLine($"tx[{subChannel}] dropped {frame.Length} bytes: {reason.Message}");

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
            // The band plan already knows the dial; the waterfall's RF scale should not have
            // to be told it a second time (and then disagree when one of them is edited).
            DialFrequencyHz = waterfallConfig.DialFrequencyHz != 0
                ? waterfallConfig.DialFrequencyHz
                : bandPlan?.DialHz ?? 0,
            Sideband = bandPlan?.Sideband ?? waterfallConfig.Sideband,
            LinesPerSecond = waterfallConfig.LinesPerSecond,
            FftSize = waterfallConfig.FftSize,
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

    Console.WriteLine($"waterfall: {waterfallServer.Url}");
}

await using var waterfallLifetime = waterfallServer;

// The Flex owns keying (the slice PTT is an API command), so a conflicting --ptt /
// configured PTT is rejected — matching how --device flex: implicitly keys the radio.
if (deviceIsFlex && (pttSpec is not null || pttConfig is not null))
{
    Console.Error.WriteLine(
        "--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)");
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

// One bind for every listener — KISS, per-modem ports, waterfall, paging and ARDOP.
System.Net.IPAddress listenAddress = DaemonConfig.ParseBind(bindAddress)!;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var kissServers = new List<KissTcpServer>();
// KISS serves the packet modems, so it starts whenever there are any — ARDOP sharing the
// channel is no longer a reason to withhold it. (It was, when an ARDOP channel carried nothing
// else; gating on the old top-level "ardop" setting would now silently leave the packet modems
// with no host interface at all.)
if (modems.Any(m => !DaemonConfig.IsArdop(m.Mode)))
{
    string shown = Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString();

    // The shared port: every modem, addressed by nibble (the QtSoundModem multiplex model).
    var shared = new KissTcpServer(channel, kissPort, listenAddress);
    shared.EmitQualityFrames = qualityFrames;
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
        dedicated.Start();
        kissServers.Add(dedicated);
        Console.WriteLine(
            $"kiss tcp: {shown}:{dedicated.LocalPort} (modem {modemConfig.SubChannel} "
            + $"{modemConfig.Mode} only, as nibble 0)");
    }

    if (!Equals(listenAddress, System.Net.IPAddress.Loopback))
    {
        Console.WriteLine(
            "kiss: WARNING — listening beyond loopback. KISS has no authentication: anything "
            + "that can reach these ports can transmit on your licence.");
    }
}

await using var kissLifetime = new KissServerSet(kissServers);

M0LTE.Ardop.Host.ArdopHostServer? ardopServer = null;
if (ardopModem is not null)
{
    // ARDOP runs its own channel discipline (ARQ timing budgets, negotiated leaders), so its
    // own bursts must never wait on a p-persistence roll — they go out through the channel's
    // inhibit-bypassing path. The packet modems keep normal CSMA among themselves; what keeps
    // them off an ARQ session is TransmitInhibit, set once the engine exists.
    // Bind the M0LTE.Ardop TNC to this daemon's channel: transmit bursts through the
    // channel-access path, receive audio via a channel tap (the old ForChannel glue,
    // now that the package is audio-device-agnostic).
    if (ardopModem.Frequency is double ardopCentre
        && ArdopChannelShift.Concern(ardopCentre, DspRate) is string ardopConcern)
    {
        Console.Error.WriteLine($"ardop: WARNING — {ardopConcern}");
    }

    var ardopShift = ArdopChannelShift.For(ardopModem.Frequency, DspRate);
    var ardopTnc = new M0LTE.Ardop.Host.ArdopHostTnc(captureDevice: device, playbackDevice: device)
    {
        Transmitter = audio => channel.EnqueueTransmit(
            _ =>
            {
                var floats = new float[audio.Length];
                for (int i = 0; i < audio.Length; i++)
                {
                    floats[i] = audio[i] / 32768f;
                }

                return ardopShift.Transmit(floats);
            },
            rejected: null,
            // ARDOP's own bursts are what the inhibit exists to protect; they never wait on it.
            bypassInhibit: true),
    };
    channel.AddReceiveTap(samples => ardopTnc.ProcessReceive(ardopShift.Receive(samples)));

    // Hold the packet modems off the air for the length of an ARQ session. Their frames are
    // queued, not discarded, until TransmitInhibitTimeout gives up on one — an AX.25 host will
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
    pagingServer.Start();
    Console.WriteLine($"paging tcp: {(Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString())}:{pagingServer.LocalPort} ({pagingServer.Mode}, DAPNET/POCSAG-compatible)");
}
await using var pagingLifetime = pagingServer;

// Audio + PTT: a FlexRadio DAX triplet (--device flex:…) or an ALSA card. The Flex
// surfaces its DAX stream through the same IAudioInput/IAudioOutput/IPttControl the channel
// already speaks, so KISS packet, POCSAG paging and ARDOP all get Flex support for free.
int flexPacketBuffer = ardopPort is null ? 3 : 6;
FlexRuntime? flex = null;
IPttControl ptt;
IAudioOutput playback;
IAudioInput input;

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
    Console.WriteLine($"audio: wav-loop {wavLoopPath} {wavLoop.SampleRate} Hz → {DspRate} Hz");
}
else if (deviceIsFlex)
{
    flex = await FlexDevice.OpenAsync(device, DspRate, flexPacketBuffer, flexTuning, cancellation.Token);
    ptt = flex.Ptt;
    playback = flex.Output;
    input = flex.Input;
    FlexDevice.FlexSpec flexSpec = FlexDevice.Parse(device);
    string flexModeDesc = flexSpec.Headless
        ? $"headless {flexTuning.Frequency} MHz {flexTuning.Antenna} {flexTuning.Mode}"
        : $"attach station '{flexSpec.Station}'";
    Console.WriteLine(
        $"audio: {device} DAX {input.SampleRate} Hz → {DspRate} Hz "
        + $"(slice {flexSpec.SliceLetter}, dax {flexTuning.DaxChannel}, {flexModeDesc})");
    if (flex.Station.TuneWarning is string tuneWarning)
    {
        Console.Error.WriteLine($"flex: {tuneWarning}");
    }

    // The radio's global transmit filter, read back at bring-up (Flex 0.7.0) — it, not the
    // slice, limits transmitted DAX audio bandwidth, and it is whatever last touched the radio
    // (a 300 Hz CW filter would silently crush a 3 kHz mode). We deliberately never set it;
    // reporting it makes a stale value visible. Headless only — attach leaves it to SmartSDR.
    if (flex.Station.TransmitFilter is (int txFilterLow, int txFilterHigh))
    {
        Console.WriteLine($"flex: transmit filter {txFilterLow}..{txFilterHigh} Hz (radio global — limits TX audio bandwidth)");

        // Only the high cut is settable through the station API, so the low cut is whatever the
        // radio was left on. Compare it against the plan rather than assume: a modem sitting
        // below the filter's low edge transmits nothing, and does so silently.
        if (bandPlan is not null)
        {
            var clipped = bandPlan.Modems
                .Where(m => m.AudioCentreHz - (m.Slot.BandwidthHz / 2) < txFilterLow
                         || m.AudioCentreHz + (m.Slot.BandwidthHz / 2) > txFilterHigh)
                .ToList();
            foreach (PlannedModem m in clipped)
            {
                Console.Error.WriteLine(
                    $"flex: WARNING — modem {m.Slot.SubChannel} ({m.Slot.Mode}) occupies "
                    + $"{m.AudioCentreHz - (m.Slot.BandwidthHz / 2):F0}-"
                    + $"{m.AudioCentreHz + (m.Slot.BandwidthHz / 2):F0} Hz, outside the radio's "
                    + $"{txFilterLow}..{txFilterHigh} Hz transmit filter — it will be clipped. "
                    + "The low cut is not settable from here; widen it on the radio.");
            }
        }
    }
}
else
{
    ptt = new NullPtt();

    // Hardware the config names but the box does not have is the single most likely thing to
    // go wrong on a first install (the seeded config points at a CM108 on /dev/hidraw0). Say
    // which setting, which file, and how to list what is really there — but exit 1, not 2, so
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
        playback = captureRate == DspRate
            ? new AlsaAudioOutput(device, DspRate)
            : new UpsamplingAudioOutput(new AlsaAudioOutput(device, captureRate), DspRate);
        // Receive: capture at the card-native rate; ARDOP buffers more deeply (500 ms vs the
        // 120 ms default) to ride out device hiccups (snd-aloop re-locking mid-frame).
        input = new AlsaAudioInput(device, captureRate, ardopPort is null ? 120_000 : 500_000);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine(DeviceDiagnostics.Audio(device, configPath, e));
        return 1;
    }

    Console.WriteLine($"audio: {device} capture {captureRate} Hz → {DspRate} Hz");
}

await using var flexLifetime = flex;

Task transmitter = channel.RunTransmitterAsync(playback, ptt, cancellation.Token);

// Decimate the source to the DSP rate. When it already runs at the DSP rate (a 48 kHz
// mode's full-bandwidth DAX, --capture-rate 12000, or a 12 kHz virtual card) there is
// nothing to decimate — a Decimator with factor 1 is invalid, so feed samples straight
// through.
int inputRate = input.SampleRate;
var rxDecimator = inputRate == DspRate ? null : new Decimator(inputRate, inputRate / DspRate);

// 100 ms RX blocks for the packet modes; 20 ms when ARDOP runs — its ARQ timing budgets
// (IRS ACK inside the ISS repeat window) want RX latency low.
int blockSamples = ardopPort is null ? inputRate / 10 : inputRate / 50;
var floatBuffer = new float[blockSamples];
var dspBuffer = new float[rxDecimator?.MaxOutput(blockSamples) ?? blockSamples];
while (!cancellation.IsCancellationRequested)
{
    int got = input.Read(floatBuffer);
    if (got == 0)
    {
        continue;
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

await transmitter.ContinueWith(_ => { }, TaskScheduler.Default);
if (!deviceIsFlex)
{
    (ptt as IDisposable)?.Dispose();
    (playback as IDisposable)?.Dispose();
    (input as IDisposable)?.Dispose();
}

return 0;
