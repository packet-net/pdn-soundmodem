using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Daemon;

/// <summary>One logical modem on the shared audio channel.</summary>
public sealed class ModemConfig
{
    /// <summary>KISS sub-channel (port nibble), 0-15.</summary>
    public int SubChannel { get; set; }

    /// <summary>Mode name as accepted by --modem (afsk1200, afsk1200-multi, bpsk300,
    /// bpsk300-nocrc, qpsk2400, qpsk3600, fsk9600, fsk9600-il2p).</summary>
    public string Mode { get; set; } = "afsk1200";

    /// <summary>Audio centre/carrier frequency override in Hz, applied to both TX and RX
    /// (QtSoundModem-style per-modem tuning; mode default when null). Honoured by the AFSK
    /// tone-pair modes (afsk*, default 1700), the BPSK/QPSK carrier modes (bpsk*/qpsk*,
    /// default 1500; 1650 for qpsk3600), and the spec-fixed waveforms (freedv-*/ms110d-*,
    /// moved by frequency translation around their unchanged DSP - see
    /// <c>FrequencyShiftedModem</c>). The baseband FSK families (fsk*/c4fsk*) have no
    /// settable centre; setting one on those is rejected at start-up, not ignored.</summary>
    public double? Frequency { get; set; }

    /// <summary>Frequency-diversity banks (<c>bpsk*-multi</c>) only: extra decoder branches
    /// either side of centre (0 = a single centred modem). Null uses the mode default (4).
    /// More branches widen off-frequency coverage at a linear CPU cost. Ignored by non-bank
    /// modes.</summary>
    public int? OffsetPairs { get; set; }

    /// <summary>Frequency-diversity banks (<c>bpsk*-multi</c>) only: the Hz step between
    /// adjacent branches. Null uses the mode default (baud/40), sized to the single-branch
    /// offset tolerance. Coverage spans ±<see cref="OffsetPairs"/>·this. Ignored by non-bank
    /// modes.</summary>
    public double? OffsetStepHz { get; set; }

    /// <summary>
    /// IL2P+CRC modes only: also hand up frames that arrive as plain IL2P, with no trailing CRC.
    /// Off by default, because IL2P+CRC is the interop ground truth on the networks this serves
    /// and every mode's default behaviour is Nino's.
    /// </summary>
    /// <remarks>
    /// <para>For a neighbour that sends the CRC-less variant. A BPQ32 node on the live 40 m slot
    /// does exactly that: a station running <c>bpsk300-il2pc</c> logged its bursts as unreadable
    /// for weeks, and one of the survey captures decoded offline as
    /// <c>bpsk300-nocrc @ 2116 Hz -> GB7BPQ&gt;BEACON</c> - right frequency, right modulation,
    /// right baud, wrong IL2P variant.</para>
    /// <para>It is not free. A plain IL2P frame is checked by Reed-Solomon alone, and RS will
    /// occasionally turn noise into a plausible-looking frame, which is the whole reason the
    /// +CRC variant exists. Those frames are logged with <c>crc_valid</c> null rather than true.
    /// Setting it on a mode that does not run IL2P+CRC is refused at start-up rather than
    /// ignored.</para>
    /// </remarks>
    public bool AcceptPlainIl2p { get; set; }

    /// <summary>
    /// A TCP port dedicated to this modem alone; null (the default) means a packet modem is
    /// reachable only through the shared <see cref="DaemonConfig.KissPort"/> by its nibble.
    /// </summary>
    /// <remarks>
    /// <b>The protocol spoken here follows the mode.</b> A packet mode gets KISS: only this
    /// modem's frames, presented as nibble 0, and everything sent to it transmitted on this
    /// modem whatever nibble was used - which is what host software that hardcodes KISS channel
    /// 0 needs to reach a modem that is not sub-channel 0. <c>ardop</c> instead gets the
    /// ardopcf host interface, command on this port and data always on the next one up, because
    /// an ARQ session has no KISS representation.
    /// </remarks>
    public int? Port { get; set; }

    /// <summary>
    /// Where this modem sits on the band, in absolute Hz (7051600 for 7051.6 kHz). Mutually
    /// exclusive with <see cref="Frequency"/>: state a band plan in RF terms and the daemon
    /// works out the dial and every modem's audio centre from it, rather than you doing that
    /// arithmetic and it being silently wrong when the dial moves.
    /// </summary>
    public double? RfFrequency { get; set; }

    /// <summary>
    /// How much room to plan for this modem, in Hz; measured from the modem itself when unset.
    /// Meaningful mainly for <c>ardop</c>, which has no fixed width - its bandwidth is
    /// negotiated per session, so the planner assumes the widest (2000 Hz) unless told
    /// otherwise. Setting it also caps what ARDOP will negotiate (200/500/1000/2000).
    /// </summary>
    public double? Bandwidth { get; set; }

    /// <summary>
    /// Identify this modem in Morse. Omit (the default) and it never transmits an identification.
    /// </summary>
    /// <remarks>
    /// Per modem rather than per station, because a station identifies on the signal it is
    /// identifying: the modems on one channel can sit kilohertz apart, and one station-wide ident
    /// would land on a single audio frequency, say nothing about the others, and usually sit on
    /// top of one of them. The modes that already identify themselves in-band (an AX.25 node
    /// sending its own ID frames) need nothing here.
    /// </remarks>
    public IdentifyConfig? Identify { get; set; }

    /// <summary>Keys in this modem entry that the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Publishing what this station hears, for a monitoring system to collect - see
/// <see cref="DaemonConfig.Metrics"/>.
/// </summary>
/// <remarks>
/// <para>Served on the <see cref="WaterfallConfig.Port"/> listener, so a station that already
/// publishes a waterfall gains this on the same port rather than another one to open.</para>
/// <para><b>Pull, and deliberately ignorant.</b> Nothing here knows the address, protocol or
/// credentials of any monitoring system: the station serves what it knows and whoever is
/// interested comes and reads it. That is what keeps it generic rather than fitted to one
/// operator's stack.</para>
/// <para><b>Unauthenticated.</b> What is served is callsigns and signal reports, transmitted in
/// the clear on a shared channel - the same facts the waterfall page already shows anyone who
/// opens it. Off by default all the same, because publishing is the operator's decision.</para>
/// </remarks>
public sealed class MetricsConfig
{
    /// <summary>Whether to serve <c>/metrics</c> and <c>/metrics/frames</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Most stations kept. On reaching it the least recently heard is dropped.</summary>
    public int MaxStations { get; set; } = 256;

    /// <summary>How long a frame stays in the per-frame feed, in seconds. Must comfortably
    /// exceed the collector's scrape interval: scraping more slowly loses frames, scraping
    /// faster sees some twice, which is harmless.</summary>
    public double FrameWindowSeconds { get; set; } = 300;

    /// <summary>How long a station keeps its series after its last frame, in hours. A station
    /// that has stopped transmitting should stop being a series rather than hold its last
    /// reading for ever.</summary>
    public double StationIdleHours { get; set; } = 6;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Runtime configuration over HTTP - see <see cref="DaemonConfig.Api"/>.</summary>
/// <remarks>
/// <para>Served on the <see cref="WaterfallConfig.Port"/> listener under <c>/api/</c>, so a
/// station that already publishes a waterfall gains this on the same port rather than a second
/// one to open.</para>
/// <para><b>This can change frequency and transmit power</b>, which makes it a bigger gun than
/// the waterfall it shares a socket with. It does nothing at all until <see cref="Key"/> is set,
/// and every request must carry that key.</para>
/// </remarks>
public sealed class ApiConfig
{
    /// <summary>
    /// The shared secret every request must present, as <c>Authorization: Bearer KEY</c> or
    /// <c>X-API-Key: KEY</c>. No key, no API: there is no default and no unauthenticated mode.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Morse identification for one modem - see <see cref="ModemConfig.Identify"/>.</summary>
public sealed class IdentifyConfig
{
    /// <summary>The callsign to send. Required; there is no sensible default for a licence
    /// condition, and guessing one wrong is worse than refusing to start.</summary>
    public string? Callsign { get; set; }

    /// <summary>
    /// Minutes between identifications, counted from the last one sent. Default 10.
    /// </summary>
    /// <remarks>
    /// The clock only runs while the modem is transmitting: an ident falls due when this has
    /// elapsed <em>and</em> the modem has transmitted since it last identified. A station that
    /// has sent nothing owes nothing, so an idle modem never keys up to announce itself.
    /// </remarks>
    public double IntervalMinutes { get; set; } = 10;

    /// <summary>Sending speed in words per minute (PARIS). Default 20.</summary>
    public double Wpm { get; set; } = 20;

    /// <summary>
    /// The audio tone to key, in Hz. Defaults to <b>this modem's own audio centre</b>, so the
    /// ident goes out on the signal it identifies and follows the band plan without being kept
    /// in step by hand. Set it only to move the ident off its modem deliberately.
    /// </summary>
    /// <remarks>
    /// The default is the whole point of hanging this off a modem. A conventional 700 Hz ident
    /// tone is a trap on a band-planned station: with a dial chosen to centre the ensemble, 700 Hz
    /// is wherever the planner happened to put it, which on a real 40 m layout landed on top of a
    /// neighbouring modem's slot.
    /// </remarks>
    public double? ToneHz { get; set; }

    /// <summary>
    /// Where to identify in absolute Hz (7054000 for 7054.0 kHz), as an alternative to
    /// <see cref="ToneHz"/>. Only meaningful on a band-planned station, which is the only kind
    /// that knows its own dial. Mutually exclusive with <see cref="ToneHz"/>.
    /// </summary>
    public double? RfFrequency { get; set; }

    /// <summary>
    /// Send the mode name after the callsign, e.g. <c>M0LTE FREEDV-DATAC1</c>. Off by default.
    /// Worth turning on for an unusual waveform: it tells a listener who just heard something
    /// they could not read what it actually was.
    /// </summary>
    public bool IncludeMode { get; set; }

    /// <summary>
    /// Key-down peak amplitude. Default 0.8, which is what the modulators themselves use, so the
    /// ident presents the transmitter with the same drive the data does.
    /// </summary>
    public double Amplitude { get; set; } = 0.8;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// One modem plugin to load: an assembly outside this package, providing modes this package does
/// not contain.
/// </summary>
/// <remarks>
/// <para>A path, and nothing but a path. There is no plugins directory to scan, no probing beside
/// the executable and no environment variable, because a file appearing on disk must never change
/// what a station transmits. What this file says is what gets loaded, and the start-up log repeats
/// it.</para>
/// <para>See <c>docs/modem-binding.md</c>. The modes a plugin provides are named
/// <c>pluginId:mode</c> - <c>ofdm-fm:nb</c> - so a mode string always says plainly whether it came
/// from this package.</para>
/// </remarks>
public sealed class ModemPluginConfig
{
    /// <summary>Path to the plugin assembly. Relative paths are resolved against the daemon's
    /// working directory; write an absolute one in anything a service unit starts.</summary>
    public string Path { get; set; } = "";

    /// <summary>Keys in this entry that the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>PTT configuration.</summary>
public sealed class PttConfig
{
    /// <summary>"serial" or "cm108" (omit the whole section for VOX).</summary>
    public string Type { get; set; } = "serial";

    /// <summary>Device path (/dev/ttyUSB0, /dev/hidraw0).</summary>
    public string Device { get; set; } = "";

    /// <summary>Serial line: "rts" (default) or "dtr".</summary>
    public string? Line { get; set; }

    /// <summary>CM108 GPIO pin (default 3).</summary>
    public int? Gpio { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>POCSAG paging endpoint (DAPNET/POCSAG-compatible waveform; local paging
/// API, pdn) - see PagingTcpServer for the line grammar.</summary>
public sealed class PagingConfig
{
    /// <summary>Paging TCP listen port.</summary>
    public int Port { get; set; } = 8106;

    /// <summary>POCSAG bit rate: 512, 1200 (DAPNET, default) or 2400.</summary>
    public int Baud { get; set; } = 1200;

    /// <summary>Invert the TX baseband polarity (for radios whose data path inverts;
    /// the spec convention '0' = high frequency is the default).</summary>
    public bool InvertPolarity { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>ARDOP virtual TNC (ardopcf-compatible TCP host interface; Winlink/Pat).
/// Per the dedicated-channel policy the ARDOP channel hosts no packet modems or
/// paging - configuring this alongside Modems/Paging is rejected.</summary>
public sealed class ArdopConfig
{
    /// <summary>Host-interface command port (ardopcf convention 8515); the data port
    /// always listens on the next port up.</summary>
    public int Port { get; set; } = 8515;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Headless FlexRadio slice-creation params (used when Device is
/// <c>flex:&lt;radio&gt;</c> with no <c>@station</c> - the daemon owns the radio and creates
/// its own slice). Ignored in attach mode (a <c>@station</c> device string). Defaults match
/// docs/flex-integration.md §8.</summary>
public sealed class FlexConfig
{
    /// <summary>
    /// Slice frequency (MHz, six-decimal Flex form). Null takes the default, and a band plan
    /// supersedes it entirely - with <c>rfFrequency</c> modems the dial is computed, so setting
    /// this as well says two different things.
    /// </summary>
    public string? Frequency { get; set; }

    /// <summary>RX/TX antenna. Default "ANT1".</summary>
    public string Antenna { get; set; } = "ANT1";

    /// <summary>Slice demod mode. Default "DIGU".</summary>
    public string Mode { get; set; } = "DIGU";

    /// <summary>
    /// The DAX channel the client claims, on both the headless and attach paths. Unset, a
    /// headless client takes <see cref="DefaultHeadlessDaxChannel"/> so that it coexists with
    /// SmartSDR without being told to.
    /// </summary>
    /// <remarks>
    /// A running SmartSDR grabs DAX channel 1 and the two contend (live finding, 2026-07-17 -
    /// docs/flex-integration.md §8). Defaulting elsewhere means the order the two are started in
    /// stops mattering, which is the whole problem with picking 1 and hoping.
    /// </remarks>
    public string? DaxChannel { get; set; }

    /// <summary>Out of SmartSDR's way, and valid on every 6000-series model (a 6500 has four).</summary>
    /// <remarks>
    /// It dodges SmartSDR; it does <b>not</b> dodge a second pdn-soundmodem, and two headless
    /// instances that both take the default land on the same channel. That is how a receive-only
    /// capture instance displaced GB7RDG's 40 m modem on 2026-08-07 and took it off air for six
    /// days. If you run a second instance against one radio, give it its own
    /// <see cref="DaxChannel"/> and set <see cref="ReceiveOnly"/>.
    /// </remarks>
    public const string DefaultHeadlessDaxChannel = "2";

    /// <summary>
    /// Receive only: never write the radio's global transmit state, and never contend for a
    /// slice. Default false.
    /// </summary>
    /// <remarks>
    /// For a capture or monitoring instance sharing a radio with a transmitting station. The
    /// transmit audio source, the transmit filter and RF power are global and persistent rather
    /// than per-slice, so a monitor that runs the normal bring-up quietly changes what the
    /// station on the same radio puts on air, and the change outlives the monitor's process.
    /// Setting this also stops the instance rebuilding a slice it has lost, so it can never
    /// fight the station for one.
    /// </remarks>
    public bool ReceiveOnly { get; set; }

    /// <summary>
    /// Transmit power in watts. Null (the default) leaves whatever the radio is already set to.
    /// </summary>
    /// <remarks>
    /// <para>Watts rather than the radio's 0-100 percentage because that is what an operator
    /// means; the daemon converts using the PA size the radio reports (100 W on the 6000
    /// series, so the two happen to coincide there).</para>
    /// <para>The radio <b>rejects</b> a power above its Max Power Level rather than reducing to
    /// it, so a value above your ceiling fails at startup with a message naming both numbers -
    /// it does not quietly transmit at less than you asked for.</para>
    /// <para>Only the client that owns the transmit slice can set this, which in a headless
    /// station is the daemon itself: with pdn-soundmodem holding the slice, an external tool's
    /// request is accepted and discarded. That is why this lives in the config rather than being
    /// left to the rig.</para>
    /// </remarks>
    public double? TxPowerWatts { get; set; }

    /// <summary>
    /// Transmit-filter high cut in Hz. Null (the default) derives it from the modems - the
    /// highest one's upper edge plus a little margin. 0 leaves whatever the radio was already
    /// set to.
    /// </summary>
    /// <remarks>
    /// <para>The transmit filter is a global, persistent radio setting rather than a slice one,
    /// so an inherited one from a previous session silently truncates anything wider than it -
    /// which is why the daemon states it rather than hoping. The default 3000 Hz passes every
    /// audio-band packet mode but clips the top of <c>ms110d-*</c> (a 3 kHz waveform at an 1800 Hz
    /// centre reaches past 3.1 kHz).</para>
    /// <para>Only the high cut is settable through the station API; the low cut and the receive
    /// filter are not, so a modem below the radio's low cut is reported at start-up and has to be
    /// fixed at the rig.</para>
    /// </remarks>
    public int? TransmitFilterHighHz { get; set; }

    /// <summary>
    /// The station name this client registers with the radio (headless only, best-effort).
    /// Default "pdn-soundmodem", so per-station state (transmit power) and anyone else's
    /// diagnostics name this daemon instead of a generic "Flex" - which matters the moment a
    /// second transmitting client (a test instance, the sm-ota harness) shares the radio.
    /// </summary>
    public string StationName { get; set; } = "pdn-soundmodem";

    /// <summary>
    /// Key through the arbitrated PTT: every keyup waits for the radio to be quiet, re-asserts
    /// the transmit filter and the TX slice, and only believes a keyup the radio confirms -
    /// so a second transmitting client cannot be transmitted over, and cannot silently steal
    /// this daemon's TX slice between bursts. Default false until the shared-PA hardware
    /// probes pass (docs/flex-integration.md names them); the default path is exactly what it
    /// always was.
    /// </summary>
    public bool Arbitration { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Stream parameters used only when Device is <c>ubersdr:&lt;instance&gt;</c> - a receive-only
/// station listening to a public UberSDR web receiver's IQ. Ignored for every other device.
/// Where to tune is not here: that comes from the band plan, as it does on a Flex.
/// </summary>
public sealed class UberSdrConfig
{
    /// <summary>The receiver's IQ mode. <c>iq48</c> (48 kHz complex, ±24 kHz) is what every
    /// public instance offers; <c>iq96</c> where one allows it.</summary>
    public string Mode { get; set; } = "iq48";

    /// <summary>Password for a protected instance; null for a public one.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Edges of the SSB filter to synthesise, in Hz above the dial. Holding the complex baseband
    /// means the receive filter is ours to choose rather than a rig's, and the default (150-3450)
    /// clears the whole 300-2700 Hz band the planner will place modems in.
    /// </summary>
    public double? SsbLowHz { get; set; }

    /// <summary>Upper edge of the synthesised SSB filter, Hz above the dial. See
    /// <see cref="SsbLowHz"/>.</summary>
    public double? SsbHighHz { get; set; }

    /// <summary>Audio discarded after each connect, in ms. The instances ramp their level over
    /// the first ~1 s of a stream; the default second of guard keeps that out of the modems.</summary>
    public int? StartupGuardMs { get; set; }

    /// <summary>Linear gain on the demodulated audio; 1.0 (the default) is the receiver's own
    /// scaling. For bringing a quiet instance up to soundcard-like levels on the waterfall.</summary>
    public double? Gain { get; set; }

    /// <summary>
    /// Hold a session on the receiver only while somebody has the waterfall page open. For a
    /// public monitor: with nobody watching there is nothing to show, and the receiver's slot
    /// and this address's daily allowance are better left alone. Off (the default) connects at
    /// start-up and stays connected, as a station feeding a node must.
    /// </summary>
    /// <remarks>
    /// With this on, a receiver that cannot be reached is not fatal: the daemon keeps the page
    /// up, tells the viewer, and tries again on the usual ladder for as long as anyone waits.
    /// The pre-flight at start-up still runs, so a wrong host or a refused IQ mode is still a
    /// configuration error.
    /// </remarks>
    public bool OnDemand { get; set; }

    /// <summary>
    /// With <see cref="OnDemand"/>, how long the session is kept after the last viewer leaves,
    /// in seconds. Default 60: a page refresh, a tab switch or a flaky connection should not
    /// cost the receiver a tear-down and rebuild each time. 0 closes at once.
    /// </summary>
    public int LingerSeconds { get; set; } = 60;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Flavour B: one daemon fronting many UberSDR web receivers, with a page that lists them and a
/// visitor picking one. Null (the default) is flavour A, one receiver or one radio per process.
/// </summary>
/// <remarks>
/// <para>Mutually exclusive with <see cref="DaemonConfig.Device"/>. The two say incompatible
/// things about what this process is: a station has one input and can transmit through it, while
/// a monitor has one input per receiver and can transmit through none of them.</para>
/// <para>Receive only, and no host interfaces: a monitor configures no KISS, no PTT, no config
/// API, no survey and no paging, and none of them are reachable on its port. What it serves is
/// the picker, one page per receiver, and the JSON the picker polls.</para>
/// <para>See <c>docs/monitor-plan.md</c> and CONFIG.md's <c>monitor</c> section.</para>
/// </remarks>
public sealed class MonitorConfig
{
    /// <summary>
    /// This site's own address as the world reaches it, e.g.
    /// <c>"https://monitor.ukpacketradio.network"</c>. Empty (the default) works it out from each
    /// request's <c>Host</c> header instead.
    /// </summary>
    /// <remarks>
    /// <para>What it is for is the <c>welcome</c> a station gets on its uplink: the address of
    /// its own page here, so its journal reads "publish: live at
    /// https://monitor.ukpacketradio.network/r/gb7rdg-2/" rather than naming its slug. The
    /// header-derived answer is null behind a tunnel that rewrites <c>Host</c>, which is what
    /// this site itself runs behind, and the site owner is the one who knows the name the world
    /// uses.</para>
    /// <para>A scheme, a host and an optional port, and nothing after them, because the site is
    /// served from the root of its port. A trailing slash is accepted and normalised away while
    /// the file is read, so everything downstream sees one shape.</para>
    /// </remarks>
    public string PublicUrl { get; set; } = "";

    /// <summary>Where the list of receivers comes from: the UberSDR project's public
    /// directory, or anything that serves the same JSON. An absolute http or https URL.</summary>
    public string Directory { get; set; } = "https://instances.ubersdr.org/api/instances";

    /// <summary>How often the directory is fetched again, in minutes. 0 fetches once, at
    /// start-up, and never again.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>How long a receiver's session is held after the last viewer of that receiver
    /// leaves, in seconds. The same linger the single-receiver flavour uses, per receiver.</summary>
    public int LingerSeconds { get; set; } = 60;

    /// <summary>
    /// When non-empty, the only hosts offered - which is how a smoke test runs the picker
    /// against two receivers rather than fifty. Matched on the directory's <c>host</c>, case
    /// insensitively.
    /// </summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>
    /// Hosts never offered, whatever else says otherwise: <see cref="Deny"/> beats
    /// <see cref="Allow"/>. This is how an operator who asks not to be listed is not listed,
    /// so it is not a convenience and it is tested.
    /// </summary>
    public List<string> Deny { get; set; } = [];

    /// <summary>
    /// The modems every receiver is given, in the same schema as the top-level
    /// <see cref="DaemonConfig.Modems"/>. Inside this section rather than at the top level so
    /// that one file cannot half-describe a station in one flavour and a monitor in the other.
    /// </summary>
    public List<ModemConfig> Modems { get; set; } = [];

    /// <summary>
    /// Private stations this site accepts an uplink from, each with the callsign it must say it
    /// is, the slug its page is served under, and the SHA-256 of the token issued to it. Empty
    /// (the default) accepts none, and <c>/uplink</c> refuses every connection.
    /// </summary>
    /// <remarks>
    /// The other half of <c>publish</c>, which is what a station puts in its own config. See
    /// <c>docs/uplink-plan.md</c> and CONFIG.md's <c>monitor.uplinks</c>.
    /// </remarks>
    public List<UplinkConfig> Uplinks { get; set; } = [];

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// One private station this monitor will list: who it is, where its page goes, and the hash of
/// the token that proves it.
/// </summary>
/// <remarks>
/// <para>The site issues the token; a station cannot mint one and cannot ask for a slug. Both of
/// those are decisions somebody took, written down here, and the wire carries neither: a
/// connection presents a token and says which callsign it is, and everything else about how it
/// appears comes from this entry. See <c>docs/uplink-plan.md</c> 4.4.</para>
/// <para>Removing an entry needs a restart, which is accepted (uplink-plan section 8).</para>
/// </remarks>
public sealed class UplinkConfig
{
    /// <summary>
    /// The callsign this station must say it is in its <c>hello</c>, and the name its row on the
    /// picker carries. A station whose <c>publish.callsign</c> does not match is refused.
    /// </summary>
    public string Callsign { get; set; } = "";

    /// <summary>
    /// The path segment its page is served under, <c>/r/&lt;slug&gt;/</c>. Lower-case letters,
    /// digits and hyphens; normally the callsign lower-cased, so <c>GB7RDG-2</c> is
    /// <c>gb7rdg-2</c>. Written down rather than derived, so the URL a visitor bookmarks is a
    /// decision somebody took and not a function that might change.
    /// </summary>
    public string Slug { get; set; } = "";

    /// <summary>
    /// The SHA-256 of the token issued to this station, as 64 hex characters.
    /// <c>pdn-soundmodem --uplink-token</c> prints a token and this hash together.
    /// </summary>
    /// <remarks>
    /// Hashed at rest because the monitor only ever compares: it never needs the plaintext, and a
    /// config file that leaks does not hand out working uplinks. Plain SHA-256 with no salt and
    /// no work factor is deliberate rather than an oversight - the token is 256 bits from
    /// <c>RandomNumberGenerator</c>, so there is no dictionary to defend against, and a work
    /// factor would only cost this process time on every connection.
    /// </remarks>
    public string TokenSha256 { get; set; } = "";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Frame log: every frame heard, written to a SQLite file. Null = not kept.</summary>
public sealed class FrameLogConfig
{
    /// <summary>
    /// Where to keep it. The packaged service runs as an unprivileged user, so the default sits
    /// under its own state directory rather than somewhere it cannot write.
    /// </summary>
    public string Path { get; set; } = "/var/lib/pdn-soundmodem/frames.db";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Signal survey: watch the whole passband for transmissions this station cannot read, and keep
/// the ones worth looking at later. Null = not surveying.
/// </summary>
/// <remarks>
/// Off unless configured, and budgeted when it is: this writes audio to disk unattended for as
/// long as the station runs. See <see cref="Survey.SignalSurvey"/> for what counts as worth
/// keeping and why.
/// </remarks>
public sealed class SurveyConfig
{
    /// <summary>Where captures are written - a WAV and a JSON sidecar per burst. The packaged
    /// service runs unprivileged, so the default sits under its own state directory.</summary>
    public string Path { get; set; } = "/var/lib/pdn-soundmodem/survey";

    /// <summary>Byte budget for that directory. On reaching it the oldest captures are deleted
    /// to make room, so a station left collecting for a week keeps its recent past rather than
    /// stopping on day one and leaving an empty tail.</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Most captures in any rolling hour.</summary>
    public int MaxPerHour { get; set; } = 30;

    /// <summary>How long the same part of the spectrum is left alone after a capture. One station
    /// working an unclaimed frequency is one discovery, not two hundred.</summary>
    public double CooldownSeconds { get; set; } = 120;

    /// <summary>Audio kept either side of the burst.</summary>
    public double MarginSeconds { get; set; } = 1.0;

    /// <summary>Longest burst still plausibly a packet. This, not width, is what separates a
    /// voice over from a wideband data burst - they occupy much the same 2.4 kHz.</summary>
    public double MaxSeconds { get; set; } = 20;

    /// <summary>Weakest burst worth keeping, in dB over the noise floor.</summary>
    public double MinPeakSnrDb { get; set; } = 6;

    /// <summary>
    /// Which verdicts to write out: <c>unclaimed</c> (outside every configured band),
    /// <c>missed</c> (inside one, nothing decoded), <c>unattributed</c> (a frame decoded with no
    /// readable AX.25 addresses). Empty = the default three.
    /// </summary>
    public string[]? Capture { get; set; }

    /// <summary>
    /// Whether to read each capture back with every mode that could have carried it, and
    /// propose the modems that would have read the traffic. Off by default: it is DSP work
    /// beside a real-time receiver, and a station that wants its CPU for its modems should
    /// have it.
    /// </summary>
    /// <remarks>
    /// Bounded by construction rather than by a rate limit - see <c>ProspectorWorker</c> - at a
    /// twentieth of one core whatever the station hears. Proposals are read from
    /// <c>/api/proposals</c>, which needs <see cref="ApiConfig.Key"/> set; without one they
    /// still reach the journal as they are made.
    /// </remarks>
    public bool Propose { get; set; }

    /// <summary>
    /// Separate captures a proposal needs behind it - separate transmissions, on separate
    /// occasions, each carrying a frame whose own check sequence verified. One decode is a
    /// decode; a modem slot is a standing commitment.
    /// </summary>
    public int ProposeMinCaptures { get; set; } = 3;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Continuous raw capture of the channel's receive audio - chunked WAVs under a byte budget,
/// so the whole run can be re-scored offline against a later receiver; null = off. Unlike the
/// <see cref="SurveyConfig">survey</see>, which curates bursts, this keeps the unedited
/// stream - see <see cref="RawCaptureWriter"/> for why both exist.
/// </summary>
public sealed class RawCaptureConfig
{
    /// <summary>Where chunks are written. The packaged service runs unprivileged, so the
    /// default sits under its own state directory.</summary>
    public string Path { get; set; } = "/var/lib/pdn-soundmodem/raw";

    /// <summary>Byte budget for the directory; the oldest chunks are pruned to fit. The
    /// default keeps roughly two days at a 12 kHz DSP rate - size it to the disk and the
    /// campaign, not the other way round.</summary>
    public long MaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Audio minutes per chunk. Fifteen matches the GB7RDG benchmark methodology -
    /// long enough that a burst almost never straddles a boundary, short enough to copy
    /// around.</summary>
    public int ChunkMinutes { get; set; } = 15;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Which kind of audio input the station runs on, for resolving the dead-feed
/// defaults - each family has its own way of dying and its own legitimate quiet.</summary>
public enum DeadFeedDevice
{
    /// <summary>An ALSA sound card.</summary>
    Alsa,

    /// <summary>A FlexRadio DAX stream (<c>flex:</c> device).</summary>
    Flex,

    /// <summary>An UberSDR web receiver's IQ stream (<c>ubersdr:</c> device).</summary>
    UberSdr,

    /// <summary>A bench input with no radio behind it: a recording replayed as the capture
    /// device (<c>--wav-loop</c>), or the in-process mock radio (<c>flex:mock</c>), whose
    /// DAX-RX path deliberately delivers nothing between injected frames.</summary>
    WavLoop,

    /// <summary>
    /// A private station's audio arriving over an uplink socket, on a monitor
    /// (<c>monitor.uplinks</c>). Silence off, starvation on.
    /// </summary>
    /// <remarks>
    /// Silence is off because what arrives is 16-bit PCM of somebody else's band, and a quiet
    /// band quantised to 16 bits can genuinely be exact zeros for a while - so the watch that
    /// catches a Flex padding a dead stream would here be catching a quiet evening. Starvation is
    /// on and means something precise: this site asked for audio and the station's socket
    /// delivered none, which is a half-open connection rather than a quiet band, and the answer
    /// is to drop the socket so the station reconnects.
    /// </remarks>
    Uplink,
}

/// <summary>
/// Dead-feed protection thresholds; null (no section) = the per-device defaults. Two watches,
/// two failure families: <c>silenceSeconds</c> catches a feed that keeps delivering samples
/// which are all exactly zero (a dead Flex VITA stream pads silence at full rate - the
/// 2026-08-07 incident recorded 6.8 hours of it); <c>starvationSeconds</c> catches a feed
/// that stops delivering samples at all (a hung network stream, a stalled or unplugged
/// card). Either firing takes the proven recovery: an orderly shutdown with exit 1, so the
/// unit restarts and rebuilds the device session from scratch.
/// </summary>
public sealed class DeadFeedConfig
{
    /// <summary>
    /// Seconds of unbroken digital silence that declare the feed dead; 0 = off; null = the
    /// device default - 30 for flex and ubersdr (their streams always carry noise-floor
    /// energy when healthy, so half a minute of exact zeros is a dead feed with certainty),
    /// off for ALSA and --wav-loop.
    /// </summary>
    /// <remarks>
    /// Off for ALSA by deliberate default, not oversight: genuinely-silent wired inputs
    /// exist, and a disconnected cable must not restart-loop the service. Set it here if
    /// your input always carries audible noise floor and you want the same protection.
    /// </remarks>
    public double? SilenceSeconds { get; set; }

    /// <summary>
    /// Seconds without any samples delivered that declare the feed starved; 0 = off; null =
    /// the device default - 30 for flex, ubersdr and ALSA, off for --wav-loop (a recording
    /// paces itself and cannot starve).
    /// </summary>
    public double? StarvationSeconds { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>
    /// The thresholds to run <paramref name="device"/> with: the device family's defaults,
    /// overridden field-by-field where <paramref name="config"/> states a value. 0 in either
    /// place means that watch is off.
    /// </summary>
    public static (double SilenceSeconds, double StarvationSeconds) Resolve(
        DeadFeedConfig? config, DeadFeedDevice device)
    {
        (double silence, double starvation) = device switch
        {
            DeadFeedDevice.Flex or DeadFeedDevice.UberSdr => (30.0, 30.0),
            DeadFeedDevice.Alsa or DeadFeedDevice.Uplink => (0.0, 30.0),
            _ => (0.0, 0.0),
        };

        return (config?.SilenceSeconds ?? silence, config?.StarvationSeconds ?? starvation);
    }
}

/// <summary>Browser waterfall endpoint (spectrum + waterfall + per-frame burst
/// attribution); null = disabled. See WaterfallWebServer.</summary>
public sealed class WaterfallConfig
{
    /// <summary>HTTP listen port.</summary>
    public int Port { get; set; } = 8107;

    /// <summary>Rig dial frequency in Hz, the page's opening default (each browser can
    /// retune its own copy). 0 = unset: audio frequencies only until the operator enters
    /// one.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>"usb" (RF = dial + audio, default) or "lsb" (RF = dial − audio).</summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Waterfall line rate / display frame rate. Default 30.</summary>
    public int LinesPerSecond { get; set; } = 30;

    /// <summary>FFT length override; 0 = the rate default (2048 at 12 kHz, 8192 at 48 kHz).</summary>
    public int FftSize { get; set; }

    /// <summary>
    /// The page is for the public, not the operator: it takes the <see cref="Title"/> and
    /// <see cref="About"/> below, credits and links the receiver it listens through, and hides
    /// the KISS host badges, which name ports on a box a visitor cannot reach. Nothing else
    /// changes, and nothing is removed from the operator's page. Off by default.
    /// </summary>
    public bool Public { get; set; }

    /// <summary>Public page title, in the tab and at the top of the page. Null keeps the
    /// page's own.</summary>
    public string? Title { get; set; }

    /// <summary>One paragraph for the visitor on a public page: what the window is, and that it
    /// is receive only. Null shows none.</summary>
    public string? About { get; set; }

    /// <summary>
    /// Whether the file actually said <c>"port"</c>, as opposed to taking the default. A station
    /// on a LAN may reasonably let the default stand; a monitor is a public site, and coming up
    /// on a port nobody chose is not something to do quietly.
    /// </summary>
    [JsonIgnore]
    public bool PortWasStated { get; internal set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Publishing this station to a public monitor site: the station dials out, the site lists it
/// while the socket is up, and a visitor gets the page this station's own operator sees. Null
/// (the default) publishes nothing, and is what every station is until somebody adds this block.
/// </summary>
/// <remarks>
/// <para>Section 4.3 of <c>docs/uplink-plan.md</c>. Strictly one way: audio, frames and a status
/// sentence go up, a viewer count comes down, and there is nothing in the protocol that could
/// transmit, retune or reconfigure anything here. Leaving is deleting this block and restarting.
/// </para>
/// <para>Mutually exclusive with <see cref="DaemonConfig.Monitor"/>: a station publishes and a
/// monitor accepts, and one process is not both.</para>
/// </remarks>
public sealed class PublishConfig
{
    /// <summary>The site's uplink endpoint, an absolute <c>ws</c> or <c>wss</c> URL. Required.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The token the site owner issued this station. Required, and there is no default: issued
    /// once, pasted in once, not edited by hand, exactly as <c>api.key</c> is.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// This station's callsign, with an optional SSID. Required: a station on a public page that
    /// will not say who it is has no business being there, and the site checks it against the
    /// token.
    /// </summary>
    public string? Callsign { get; set; }

    /// <summary>Who runs it, for the credit line and the picker row. Optional.</summary>
    public string? Operator { get; set; }

    /// <summary>Roughly where it is, for the picker row. Optional.</summary>
    public string? Location { get; set; }

    /// <summary>The radio and antenna, for the credit line. Optional.</summary>
    public string? Radio { get; set; }

    /// <summary>The operator's own page, an absolute http or https URL. Optional.</summary>
    public string? Site { get; set; }

    /// <summary>
    /// What rate the audio is published at, in Hz. An integer divisor of the channel's DSP rate,
    /// 6000 to 48000; unset takes the DSP rate capped at 12000. The relayed picture spans 0 to
    /// half of this, so a modem above that edge is not published and start-up says so. It is also
    /// the only lever an operator on a thin upload has, there being no codec: 12000 costs about
    /// 194 kbit/s upstream while somebody is watching, 6000 about 98, and 48000 about 770.
    /// </summary>
    public int? AudioRate { get; set; }

    /// <summary>
    /// <c>"always"</c> (the default) publishes decoded frames whether or not anybody is watching,
    /// which is what makes a quiet band look alive to somebody arriving an hour later and costs
    /// well under a kilobit a second. <c>"watched"</c> holds them back until somebody is.
    /// </summary>
    public string Frames { get; set; } = "always";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>pdn-soundmodem daemon configuration file. JSON, with comments and trailing
/// commas accepted (see <see cref="Options"/>) and case-insensitive key matching - the
/// shipped soundmodem.example.json relies on that and annotates itself. Full reference:
/// CONFIG.md.</summary>
public sealed class DaemonConfig
{
    /// <summary>ALSA device for capture and playback.</summary>
    public string Device { get; set; } = "default";

    /// <summary>Capture rate; card-native (48000) recommended - the daemon decimates.</summary>
    public int CaptureRate { get; set; } = 48000;

    /// <summary>KISS TCP listen port - shared by every modem, addressed by sub-channel nibble.
    /// Individual modems can also get a port to themselves; see <see cref="ModemConfig.KissPort"/>.</summary>
    public int KissPort { get; set; } = 8105;

    /// <summary>
    /// Address every TCP listener binds to - KISS, the per-modem ports, the waterfall, paging
    /// and ARDOP alike; "*" or "0.0.0.0" for all interfaces. One setting rather than one per
    /// service: they are all on the same machine facing the same network.
    /// </summary>
    /// <remarks>
    /// Loopback by default because KISS has no authentication whatsoever - anything that can
    /// reach the port can transmit on your licence.
    /// </remarks>
    public string Bind { get; set; } = "127.0.0.1";

    /// <summary>
    /// Which sideband the radio is set to, for turning RF frequencies into audio ones: "usb"
    /// (RF = dial + audio, the data-mode norm) or "lsb" (RF = dial - audio).
    /// </summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>
    /// Whether the file actually said "sideband", as opposed to taking the default. On a Flex
    /// the slice mode states the sideband, so a value that was merely defaulted is silently
    /// corrected while one that was written down and contradicts the radio is an error.
    /// </summary>
    [JsonIgnore]
    public bool SidebandWasStated { get; private set; }

    /// <summary>
    /// Whether the file actually said "device", as opposed to taking the default. "device" and
    /// "monitor" are exclusive, and refusing a monitor config because <c>Device</c> holds the
    /// string "default" that nobody wrote would be refusing it for a value the operator never
    /// typed.
    /// </summary>
    [JsonIgnore]
    public bool DeviceWasStated { get; private set; }

    /// <summary>
    /// Pins the dial instead of letting the daemon choose one - for a net frequency, or to
    /// match another application. Only meaningful alongside <see cref="ModemConfig.RfFrequency"/>.
    /// Unset (the default) the daemon picks a dial that centres every modem in the passband and
    /// prints it; pinned, it is used as-is and merely checked, because you can see your radio
    /// and the passband it checks against is only nominal.
    /// </summary>
    public double? DialFrequency { get; set; }

    /// <summary>The logical modems sharing the audio channel.</summary>
    public List<ModemConfig> Modems { get; set; } = [];

    /// <summary>
    /// Modem plugins to load before the modems are built: assemblies outside this package that
    /// provide modes it does not contain. Empty by default, which is the usual case.
    /// </summary>
    /// <remarks>
    /// <b>This loads and runs code from outside the package</b>, which is why it is a list of
    /// explicit paths rather than any kind of discovery: nothing gets loaded that this file does
    /// not name. A plugin that fails to load is reported by name and the daemon carries on without
    /// its modes - but a modem configured to use one of those modes is then an unknown mode, and
    /// that does stop start-up.
    /// </remarks>
    public List<ModemPluginConfig> ModemPlugins { get; set; } = [];

    /// <summary>PTT control; null = VOX / none.</summary>
    public PttConfig? Ptt { get; set; }

    /// <summary>POCSAG paging endpoint; null = disabled.</summary>
    public PagingConfig? Paging { get; set; }

    /// <summary>ARDOP virtual TNC; null = disabled. Exclusive with Modems/Paging
    /// (the ARDOP channel is dedicated; docs/ardop-design.md §2.2).</summary>
    public ArdopConfig? Ardop { get; set; }

    /// <summary>Headless FlexRadio slice params (Device <c>flex:</c> with no <c>@station</c>);
    /// null = defaults. Ignored for ALSA devices and attach-mode Flex.</summary>
    public FlexConfig? Flex { get; set; }

    /// <summary>UberSDR stream params (Device <c>ubersdr:</c>); null = defaults. Ignored for
    /// every other device.</summary>
    public UberSdrConfig? UberSdr { get; set; }

    /// <summary>Browser waterfall endpoint; null = disabled.</summary>
    public WaterfallConfig? Waterfall { get; set; }

    /// <summary>Flavour B: many web receivers behind one page. Null = the single-station
    /// flavour, which is everything else in this file. Exclusive with <see cref="Device"/>.</summary>
    public MonitorConfig? Monitor { get; set; }

    /// <summary>Publishing this station to a public monitor site; null publishes nothing, which
    /// is the default. Exclusive with <see cref="Monitor"/>. See <see cref="PublishConfig"/>.</summary>
    public PublishConfig? Publish { get; set; }

    /// <summary>
    /// Change this station's configuration at runtime over HTTP. Omit it (the default) and there
    /// is no such surface at all.
    /// </summary>
    public ApiConfig? Api { get; set; }

    /// <summary>Frame log; null = frames are heard and not written down.</summary>
    public FrameLogConfig? FrameLog { get; set; }

    /// <summary>Signal survey; null = signals this station cannot read go unrecorded.</summary>
    public SurveyConfig? Survey { get; set; }

    /// <summary>Publishing what this station hears, for a monitoring system to collect;
    /// null publishes nothing. Served on the waterfall's listener.</summary>
    public MetricsConfig? Metrics { get; set; }

    /// <summary>Answering off-frequency stations on their frequency; null = measure only.</summary>
    public FrequencyMatchingConfig? FrequencyMatching { get; set; }

    /// <summary>Continuous raw receive-audio capture; null = off.</summary>
    public RawCaptureConfig? RawCapture { get; set; }

    /// <summary>Dead-feed protection thresholds; null = the per-device defaults
    /// (see <see cref="DeadFeedConfig"/>).</summary>
    public DeadFeedConfig? DeadFeed { get; set; }

    /// <summary>
    /// Whether to listen for the station identifications a NinoTNC sends alongside its PSK SSB
    /// data modes rather than within them - 300 AFSK AX.25, 200 Hz above the carrier. On by
    /// default: it costs one cheap demodulator per PSK modem, changes nothing a host sees, and
    /// turns a recurring unreadable burst in the middle of the channel into a callsign.
    /// </summary>
    /// <remarks>
    /// Applies only to the four modes the TNC behaves this way in (<c>bpsk300</c>, <c>qpsk600</c>,
    /// <c>bpsk1200</c>, <c>qpsk2400</c> and their aliases) - see
    /// <see cref="Modems.IdBeaconGhost"/>. A station running none of them is unaffected either way.
    /// </remarks>
    public bool IdBeacons { get; set; } = true;

    /// <summary>
    /// Settings present in the file that this version does not know. Kept so start-up can say
    /// so out loud: System.Text.Json drops unknown members silently, which turns a typo - or a
    /// setting that has since been withdrawn, like the old "csma" block - into a config that
    /// looks accepted and does something else.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>Non-fatal complaints raised while loading; the daemon prints them at start-up.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads and validates a configuration file.</summary>
    public static DaemonConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<DaemonConfig>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException(
                "the file contains only `null` - there is nothing to configure from. A minimal "
                + "working file is {\"device\": \"default\", \"modems\": [{\"subChannel\": 0, "
                + "\"mode\": \"afsk1200\"}]}");
        config.DeviceWasStated = StatesKey(path, "device");
        if (config.Waterfall is not null)
        {
            config.Waterfall.PortWasStated = StatesKey(path, "waterfall", "port");
        }

        // Before the flavour split, because "publish" is refused on both sides of it: a station
        // has to be told what is wrong with its block, and a monitor has to be told that a
        // monitor does not publish.
        if (config.Publish is not null)
        {
            ValidatePublish(config);
        }

        if (config.Monitor is not null)
        {
            ValidateMonitor(config);

            // Everything below this is about a station: an ARDOP TNC, a KISS port, a dial, a
            // PTT. A monitor has none of them, and letting the station checks run over an empty
            // "modems" list would answer a monitor file with a diagnostic about a modem the
            // operator never wrote. The monitor's own modems were checked above.
            config.ModemPlugins ??= [];
            RequireBind(config);
            config.Warnings = CollectWarnings(config);
            return config;
        }

        // ARDOP is no longer exclusive with the packet modems: it shares the channel, and the
        // daemon holds packet transmissions while an ARQ session is up (SoundModemChannel's
        // TransmitInhibit). What is not allowed is asking for it twice.
        int ardopModems = config.Modems.Count(m => IsArdop(m.Mode));
        if (ardopModems > 1)
        {
            throw new InvalidDataException(
                "two modems have \"mode\": \"ardop\". One ARDOP TNC per channel - it is a whole "
                + "virtual TNC with its own host interface, not a demodulator you can run twice.");
        }

        if (ardopModems > 0 && config.Ardop is not null)
        {
            throw new InvalidDataException(
                "ARDOP is configured twice - once as a modem entry and once in the top-level "
                + "\"ardop\" section. Keep the modem entry (it can also carry \"frequency\" and "
                + "\"port\") and delete the \"ardop\" section.");
        }

        if (config.Modems.Count == 0 && config.Ardop is null)
        {
            config.Modems.Add(new ModemConfig());
        }

        var duplicates = config.Modems.GroupBy(m => m.SubChannel).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidDataException(
                $"two modems share \"subChannel\": {duplicates[0].Key}. Each modem needs its own "
                + "KISS sub-channel (0-15) - renumber one of them.");
        }

        ModemConfig? bothWays = config.Modems.FirstOrDefault(
            m => m.RfFrequency is not null && m.Frequency is not null);
        if (bothWays is not null)
        {
            throw new InvalidDataException(
                $"modem {bothWays.SubChannel} sets both \"frequency\" ({bothWays.Frequency}) and "
                + $"\"rfFrequency\" ({bothWays.RfFrequency}). Those say the same thing two ways - "
                + "\"frequency\" is an audio offset, \"rfFrequency\" is a place on the band. Keep one.");
        }

        // A band plan is all-or-nothing: the dial is shared, so a modem pinned in audio terms
        // would drift across the band as the dial is chosen for the others.
        var rfAddressed = config.Modems.Where(m => m.RfFrequency is not null).ToList();
        if (rfAddressed.Count > 0 && rfAddressed.Count != config.Modems.Count)
        {
            string audioOnly = string.Join(", ", config.Modems
                .Where(m => m.RfFrequency is null)
                .Select(m => $"modem {m.SubChannel} ({m.Mode})"));
            throw new InvalidDataException(
                $"some modems have \"rfFrequency\" and some do not ({audioOnly}). Give every modem "
                + "an \"rfFrequency\" or none of them: the dial is shared, so one pinned to an audio "
                + "offset would sit at whatever RF the dial chosen for the others happens to put it.");
        }

        // 0 is the documented "leave the radio's own filter alone"; anything else has to be a
        // filter an SSB transmitter could plausibly be set to, or the operator has typed a
        // frequency (14100000) where a cut-off belongs and the radio would take it.
        if (config.Flex?.TransmitFilterHighHz is int filterHigh && filterHigh is not 0 and (< 500 or > 10_000))
        {
            throw new InvalidDataException(
                $"\"flex\".\"transmitFilterHighHz\" is {filterHigh}. That is an audio cut-off in Hz "
                + "(3000 is a radio's usual default, 3400 clears ms110d) - use 500-10000, 0 to "
                + "leave the radio's own filter alone, or remove it to have it set from the modems.");
        }

        // Negative seconds is a sign error or a misunderstood off-switch either way; 0 is the
        // documented "that watch is off", so say so rather than letting a -30 disable a watch
        // the operator believed they had tightened.
        foreach ((string name, double? seconds) in (ReadOnlySpan<(string, double?)>)
            [
                ("silenceSeconds", config.DeadFeed?.SilenceSeconds),
                ("starvationSeconds", config.DeadFeed?.StarvationSeconds),
            ])
        {
            if (seconds is < 0)
            {
                throw new InvalidDataException(
                    $"\"deadFeed\".\"{name}\" is {seconds}. That is how many seconds the watch "
                    + "waits before declaring the feed dead and restarting the service - use a "
                    + "positive number of seconds, 0 to turn that watch off, or remove it for "
                    + "the device's default.");
            }
        }

        // "modemPlugins": null deserialises to a null list, not to the property initialiser, and
        // every read below would then throw where an operator expects a message about their file.
        config.ModemPlugins ??= [];

        // An entry with no path is a half-written line, not a request to load nothing: it would
        // otherwise become a "no path given" load failure at start-up, which reads as the plugin
        // mechanism misbehaving rather than as the file being unfinished.
        if (config.ModemPlugins.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Path)) is not null)
        {
            throw new InvalidDataException(
                "a \"modemPlugins\" entry has no \"path\". Each entry names one assembly to load, "
                + "as {\"path\": \"/opt/pdn/plugins/M0LTE.OfdmFm.dll\"} - there is no directory to "
                + "scan and no default location, deliberately.");
        }

        RequireBind(config);

        config.SidebandWasStated = StatesKey(path, "sideband");
        ValidatePorts(config);
        config.Warnings = CollectWarnings(config);
        return config;
    }

    /// <summary>The bind setting, which every flavour has and every listener uses.</summary>
    private static void RequireBind(DaemonConfig config)
    {
        if (ParseBind(config.Bind) is null)
        {
            throw new InvalidDataException(
                $"\"bind\": \"{config.Bind}\" is not an IP address. Use \"127.0.0.1\" for "
                + "loopback only, \"*\" for every interface, or the address of one interface.");
        }
    }

    /// <summary>
    /// What a <c>monitor</c> section has to say before this process can be one, every failure a
    /// sentence naming the setting and what to do about it.
    /// </summary>
    /// <remarks>
    /// Refused here rather than at start-up so the operator gets one exit 2 with a reason and
    /// systemd's RestartPreventExitStatus=2 stops retrying, which is the contract every other
    /// configuration error in this file already keeps.
    /// </remarks>
    private static void ValidateMonitor(DaemonConfig config)
    {
        MonitorConfig monitor = config.Monitor!;

        if (config.DeviceWasStated)
        {
            throw new InvalidDataException(
                $"this file sets both \"device\" (\"{config.Device}\") and \"monitor\". They say "
                + "incompatible things about what this process is: \"device\" is one radio or one "
                + "receiver with a KISS port and a transmitter, \"monitor\" is many web receivers "
                + "behind one page with neither. Remove whichever one you did not mean.");
        }

        if (monitor.Modems.Count == 0)
        {
            throw new InvalidDataException(
                "\"monitor\".\"modems\" is empty. Every receiver gets the same modems, and a "
                + "monitor with none would connect to receivers and decode nothing. Give it the "
                + "band plan you want watched, e.g. [{\"subChannel\": 0, \"mode\": "
                + "\"afsk300-il2pc\", \"rfFrequency\": 7050300}].");
        }

        if (config.Waterfall is null)
        {
            throw new InvalidDataException(
                "\"monitor\" needs a \"waterfall\" section: the picker and every receiver's page "
                + "are served on its \"port\", and the page's viewers are what asks for a "
                + "receiver. Add {\"waterfall\": {\"port\": 8099, \"title\": \"...\"}}.");
        }

        if (!config.Waterfall.PortWasStated)
        {
            throw new InvalidDataException(
                "\"waterfall\" has no \"port\". A monitor serves its whole site on that one port "
                + $"and would otherwise come up on {config.Waterfall.Port}, which is the "
                + "single-station default and not a decision anybody made - and this is a site "
                + "meant to be reached from outside, usually through a tunnel pointed at a port "
                + "somebody chose. Set it, e.g. {\"waterfall\": {\"port\": 8099}}.");
        }

        // Forced rather than checked. A picker is a page for strangers by definition - it lists
        // other people's receivers and invites anyone to watch one - so an operator's console
        // dressing on it would be a setting that could only ever be wrong.
        config.Waterfall.Public = true;

        foreach ((string name, int seconds) in (ReadOnlySpan<(string, int)>)
            [
                ("refreshMinutes", monitor.RefreshMinutes),
                ("lingerSeconds", monitor.LingerSeconds),
            ])
        {
            if (seconds < 0)
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"{name}\" is {seconds}. That is a number of "
                    + (name == "refreshMinutes" ? "minutes" : "seconds")
                    + " to wait, so it cannot be negative - use 0 or a positive number.");
            }
        }

        if (!Uri.TryCreate(monitor.Directory, UriKind.Absolute, out Uri? directory)
            || (directory.Scheme != Uri.UriSchemeHttp && directory.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException(
                $"\"monitor\".\"directory\" is \"{monitor.Directory}\", which is not an absolute "
                + "http or https URL. It is where the list of receivers is fetched from; the "
                + "public one is https://instances.ubersdr.org/api/instances.");
        }

        if (monitor.PublicUrl is { Length: > 0 } publicUrl)
        {
            // Before anything is quoted back, and it is the one refusal here that does not
            // quote: a URL with a username and password in it would put the password in the
            // journal, which is the last place a message about a mistake should leave it.
            if (Uri.TryCreate(publicUrl, UriKind.Absolute, out Uri? site)
                && site.UserInfo.Length > 0)
            {
                throw new InvalidDataException(
                    "\"monitor\".\"publicUrl\" carries credentials, and this message does not "
                    + "repeat it back because that would write them to the journal. It is the "
                    + "address visitors reach this site at, which is a scheme, a host and an "
                    + "optional port and nothing else: nothing signs in to a public monitor "
                    + "page. Write it as \"https://monitor.ukpacketradio.network\".");
            }

            if (site is null
                || (site.Scheme != Uri.UriSchemeHttp && site.Scheme != Uri.UriSchemeHttps)
                || site.AbsolutePath != "/"
                || site.Query.Length > 0
                || site.Fragment.Length > 0)
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"publicUrl\" is {Quoted(publicUrl)}. It is this site's own "
                    + "address as the world reaches it, which is a scheme, a host and an optional "
                    + "port and nothing after them, e.g. "
                    + "\"https://monitor.ukpacketradio.network\" - with or without a trailing "
                    + "slash, both being the same address. A path, a query or a fragment is "
                    + "refused because the site is served from the root of its port and every "
                    + "link on its pages is written from there. Leave it out to work the address "
                    + "out from each request instead, which is right for a site nothing rewrites "
                    + "the Host header in front of.");
            }

            // Normalised as the file is read, so the one place that uses it can append
            // "/r/<slug>/" without wondering whether the operator wrote the slash. IdnHost rather
            // than Authority because this string goes into the journal, and a punycode host is
            // what the wire carries anyway. An IPv6 literal is taken from Host instead, which
            // keeps the square brackets: without them the colon before a port is the address's
            // own, and the URL means something else or nothing at all.
            string host = site.HostNameType == UriHostNameType.IPv6 ? site.Host : site.IdnHost;
            monitor.PublicUrl = $"{site.Scheme}://{host}"
                + (site.IsDefaultPort ? "" : $":{site.Port}");
        }

        foreach ((string list, List<string> hosts) in (ReadOnlySpan<(string, List<string>)>)
            [("allow", monitor.Allow), ("deny", monitor.Deny)])
        {
            foreach (string host in hosts)
            {
                if (!IsPlausibleHostname(host))
                {
                    throw new InvalidDataException(
                        $"\"monitor\".\"{list}\" contains \"{host}\", which is not a hostname. "
                        + "Each entry is a receiver's host exactly as the directory gives it, "
                        + "e.g. \"m9psy-1.instance.ubersdr.org\" - no scheme, no port, no path.");
                }
            }
        }

        ValidateUplinks(monitor);
    }

    /// <summary>
    /// What a <c>publish</c> section has to say before this station can appear on somebody else's
    /// website, every failure a sentence naming the setting and what to do about it.
    /// </summary>
    /// <remarks>
    /// <para>Section 4.3 of <c>docs/uplink-plan.md</c>. Refused here rather than at start-up for
    /// the reason every other check in this file is: one exit 2 with a reason, and systemd's
    /// <c>RestartPreventExitStatus=2</c> stops retrying instead of crash-looping on a typo.</para>
    /// <para>The one check that is not here is <c>audioRate</c> dividing the channel's DSP rate,
    /// which is <see cref="PublishRateProblem"/>: the DSP rate is settled from the modem set after
    /// any plugins have loaded, so this file cannot know it yet without guessing at a mode it has
    /// not been told about.</para>
    /// </remarks>
    private static void ValidatePublish(DaemonConfig config)
    {
        PublishConfig publish = config.Publish!;

        if (config.Monitor is not null)
        {
            throw new InvalidDataException(
                "this file sets both \"publish\" and \"monitor\". A station publishes itself to a "
                + "monitor site and a monitor accepts stations; one process is not both. Remove "
                + "whichever one you did not mean - a site lists a station through its own "
                + "\"monitor\".\"uplinks\", not through a \"publish\" block of its own.");
        }

        // Tom's decision of 2026-09-04, and the sentence says why rather than just refusing.
        if (UberSdrDevice.IsUberSdr(config.Device))
        {
            throw new InvalidDataException(
                $"\"publish\" on \"device\": \"{config.Device}\", which is somebody else's public "
                + "web receiver. A receiver like that is already on the monitor site in its own "
                + "right, so relaying it a second time through this daemon would show one "
                + "operator's antenna twice under two names and spend that receiver's daily "
                + "listening allowance on the site's behalf without the site knowing. Publish "
                + "from a station with a radio of its own; to have a say about which receivers "
                + "the site lists, use the site's own \"monitor\".\"allow\" and \"deny\".");
        }

        if (config.Waterfall is null)
        {
            throw new InvalidDataException(
                "\"publish\" needs a \"waterfall\" section: the uplink publishes what the "
                + "waterfall server already computes - the audio, the frames and the status "
                + "sentence - and without one there is nothing to publish. Add "
                + "{\"waterfall\": {\"port\": 8107}}, or remove \"publish\".");
        }

        if (!Uri.TryCreate(publish.Url, UriKind.Absolute, out Uri? url)
            || (url.Scheme != "ws" && url.Scheme != "wss"))
        {
            throw new InvalidDataException(
                $"\"publish\".\"url\" is {Quoted(publish.Url)}, which is not an absolute ws or wss "
                + "URL. It is the site's uplink endpoint, given to you with the token, e.g. "
                + "\"wss://monitor.ukpacketradio.network/uplink\".");
        }

        if (publish.Token is not { Length: >= MinimumTokenLength })
        {
            throw new InvalidDataException(
                "\"publish\".\"token\" is "
                + (string.IsNullOrEmpty(publish.Token) ? "missing" : "too short")
                + $" - it is the credential the site issued this station, at least "
                + $"{MinimumTokenLength} characters, and there is no default. Ask the site owner "
                + "for one, paste it in as it was given, and do not edit it by hand.");
        }

        if (!IsPlausibleCallsign(publish.Callsign))
        {
            throw new InvalidDataException(
                $"\"publish\".\"callsign\" is {Quoted(publish.Callsign)}, which is not a callsign "
                + "with an optional SSID (e.g. \"GB7RDG-2\"). A station on a public page has to "
                + "say whose it is, and the site checks this against the token it issued.");
        }

        if (publish.Site is { Length: > 0 }
            && (!Uri.TryCreate(publish.Site, UriKind.Absolute, out Uri? site)
                || (site.Scheme != Uri.UriSchemeHttp && site.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidDataException(
                $"\"publish\".\"site\" is {Quoted(publish.Site)}, which is not an absolute http or "
                + "https URL. It is linked from a public page in your name, so it goes through "
                + "the same check the site applies to every other URL it is handed - remove it, "
                + "or write it out in full as \"https://example.org/\".");
        }

        foreach ((string name, string? value, int limit) in (ReadOnlySpan<(string, string?, int)>)
            [
                ("callsign", publish.Callsign, 16),
                ("operator", publish.Operator, 40),
                ("location", publish.Location, 60),
                ("radio", publish.Radio, 60),
            ])
        {
            if (value is not null && value.Length > limit)
            {
                throw new InvalidDataException(
                    $"\"publish\".\"{name}\" is {value.Length} characters and the limit is "
                    + $"{limit}. Said here rather than cut in half on somebody else's website: "
                    + "shorten it to something that reads as a picker row.");
            }
        }

        if (publish.Frames is not ("always" or "watched"))
        {
            throw new InvalidDataException(
                $"\"publish\".\"frames\" is {Quoted(publish.Frames)}. It is \"always\" (the "
                + "default: decoded frames go up whether or not anybody is watching, which costs "
                + "well under a kilobit a second and is what makes a quiet band look alive) or "
                + "\"watched\".");
        }

        if (publish.AudioRate is { } rate && rate is < MinimumAudioRate or > MaximumAudioRate)
        {
            throw new InvalidDataException(
                $"\"publish\".\"audioRate\" is {rate} Hz, and the range is {MinimumAudioRate} to "
                + $"{MaximumAudioRate}. It is the rate this station's audio is published at, so "
                + "the relayed picture spans 0 to half of it; leave it out for the default of "
                + "12000, which is about 194 kbit/s upstream while somebody is watching.");
        }
    }

    /// <summary>Shortest token accepted. The site issues 43 url-safe base64 characters.</summary>
    private const int MinimumTokenLength = 32;

    /// <summary>Publishable audio rates, in Hz (4.3).</summary>
    private const int MinimumAudioRate = 6000;

    /// <summary>Publishable audio rates, in Hz (4.3).</summary>
    private const int MaximumAudioRate = 48000;

    /// <summary>The default published rate: the channel's own rate, capped (4.5, decision "no codec").</summary>
    internal const int DefaultAudioRate = 12000;

    /// <summary>
    /// What is wrong with <c>publish.audioRate</c> against the channel this station actually runs,
    /// or null if nothing is. Separate from <see cref="ValidatePublish"/> because the DSP rate is
    /// not settled until the modem set is known and any modem plugins have loaded, which is after
    /// this file has been read.
    /// </summary>
    /// <param name="publish">The section, already through <see cref="ValidatePublish"/>.</param>
    /// <param name="dspRate">The rate the station's audio channel runs at.</param>
    internal static string? PublishRateProblem(PublishConfig publish, int dspRate)
    {
        int rate = PublishedAudioRate(publish, dspRate);
        if (dspRate % rate == 0)
        {
            return null;
        }

        var divisors = new List<int>();
        for (int candidate = MinimumAudioRate; candidate <= Math.Min(dspRate, MaximumAudioRate); candidate++)
        {
            if (dspRate % candidate == 0)
            {
                divisors.Add(candidate);
            }
        }

        string problem =
            $"\"publish\".\"audioRate\" is {rate} Hz and this station's channel runs at "
            + $"{dspRate} Hz, which {rate} does not divide. The audio is decimated rather than "
            + "resampled, so it has to be an integer divisor";

        // No station runs a channel below 6000 Hz today, so the list is never empty in practice.
        // Ending the sentence "an integer divisor: ." if one ever did would not be honest.
        return divisors.Count > 0
            ? $"{problem}: {string.Join(", ", divisors)}."
            : $"{problem}, and this channel has none in {MinimumAudioRate} to {MaximumAudioRate}.";
    }

    /// <summary>
    /// A problem found after the file was read, in the same frame every refusal inside
    /// <see cref="Load"/> comes out in: the file, the sentence, and how to recover.
    /// </summary>
    /// <remarks>
    /// For the checks that cannot run while the file is being read because they need something
    /// settled later - <see cref="PublishRateProblem"/> is the only one today, and it needs the
    /// channel's DSP rate. An operator reading <c>journalctl</c> should not be able to tell which
    /// kind of check refused their station.
    /// </remarks>
    internal static string ConfigurationError(string path, string problem) =>
        Describe(path, problem);

    /// <summary>
    /// The rate this station publishes at: what the operator asked for, or the channel's own rate
    /// capped at <see cref="DefaultAudioRate"/> - so a 48 kHz station that says nothing gets a 0
    /// to 6 kHz picture at 194 kbit/s rather than 770.
    /// </summary>
    internal static int PublishedAudioRate(PublishConfig publish, int dspRate) =>
        publish.AudioRate ?? Math.Min(dspRate, DefaultAudioRate);

    /// <summary>
    /// What a <c>monitor.uplinks</c> entry has to say before this site will accept a station's
    /// connection on it. Every failure is an exit 2 naming the entry and what to do about it.
    /// </summary>
    /// <remarks>
    /// All three fields are the site owner's decisions rather than the station's, so all three
    /// are required and none is derived: a station cannot ask for a slug, cannot claim a callsign
    /// it was not issued a token for, and cannot mint a token at all. See
    /// <c>docs/uplink-plan.md</c> 4.4.
    /// </remarks>
    private static void ValidateUplinks(MonitorConfig monitor)
    {
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (UplinkConfig uplink in monitor.Uplinks)
        {
            string named = uplink.Callsign.Length > 0
                ? $"\"{uplink.Callsign}\""
                : uplink.Slug.Length > 0 ? $"the entry for \"{uplink.Slug}\"" : "an entry";

            if (!IsPlausibleCallsign(uplink.Callsign))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" has {named} for a \"callsign\". A station on a "
                    + "public page that will not say who it is has no business being there, and "
                    + "the callsign is what its token is issued against: one to six letters and "
                    + "digits with an optional -SSID, e.g. \"GB7RDG-2\".");
            }

            if (!IsUsableSlug(uplink.Slug))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} the slug \"{uplink.Slug}\", which "
                    + "cannot be a path segment. It is lower-case letters, digits and hyphens "
                    + "with no hyphen at either end - normally the callsign lower-cased, so "
                    + $"\"{uplink.Callsign}\" would be "
                    + $"\"{UberSdrDirectory.SlugForCallsign(uplink.Callsign)}\", and the page is "
                    + $"served at /r/{UberSdrDirectory.SlugForCallsign(uplink.Callsign)}/.");
            }

            if (!IsSha256Hex(uplink.TokenSha256))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} a \"tokenSha256\" that is not 64 hex "
                    + "characters. This site stores the HASH of the token it issued, never the "
                    + $"token: run \"pdn-soundmodem --uplink-token {uplink.Callsign}\", paste the "
                    + "hash here and give the token to the station's operator.");
            }

            if (slugs.TryGetValue(uplink.Slug, out string? already))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives both {already} and {named} the slug "
                    + $"\"{uplink.Slug}\". One page cannot be two stations: give each its own, "
                    + "normally its own callsign lower-cased.");
            }

            if (!callsigns.Add(uplink.Callsign))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" lists {named} twice. One entry per station: a "
                    + "second token for the same callsign would let two stations claim to be it, "
                    + "and only one of them could have the page.");
            }

            if (!hashes.Add(uplink.TokenSha256))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} a \"tokenSha256\" another entry "
                    + "already has. A token names one station, so the same one twice cannot say "
                    + "which of them is connecting: issue each a token of its own.");
            }

            slugs[uplink.Slug] = named;
        }
    }

    /// <summary>
    /// Whether a string could be a callsign with an optional SSID: up to six letters and digits,
    /// optionally <c>-0</c> to <c>-15</c>. The same shape <c>Ax25AddressParser</c> reads off the
    /// air, deliberately not a validity check against any licensing authority's real format -
    /// this is a label on a page, and the token is what says the station is who it claims.
    /// </summary>
    internal static bool IsPlausibleCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return false;
        }

        string[] parts = callsign.Split('-');
        if (parts.Length > 2 || parts[0].Length is 0 or > 6)
        {
            return false;
        }

        foreach (char c in parts[0])
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return parts.Length == 1
            || (parts[1].Length is 1 or 2 && int.TryParse(parts[1], out int ssid) && ssid is >= 0 and <= 15);
    }

    /// <summary>A value as it should read in a message: quoted, or the word for its absence.</summary>
    private static string Quoted(string? value) =>
        value is null ? "missing" : $"\"{value}\"";

    /// <summary>
    /// Whether a string can be the path segment a station's page is served under: exactly what
    /// <c>WaterfallWebServer.ValidatePathBase</c> accepts, checked here so that a typo is one
    /// sentence at start-up rather than an exception from inside a route registration.
    /// </summary>
    private static bool IsUsableSlug(string? slug) =>
        !string.IsNullOrEmpty(slug)
        && slug.Length <= 63
        && !slug.StartsWith('-')
        && !slug.EndsWith('-')
        && slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Whether a string is 64 hex characters, which is a SHA-256 written down.</summary>
    private static bool IsSha256Hex(string? hash) =>
        hash is { Length: 64 }
        && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>
    /// Whether a string could be a hostname: labels of letters, digits and hyphens separated by
    /// dots, no scheme, no port, no path. Deliberately not a DNS resolution - an allow list has
    /// to be writable for a receiver that is temporarily down - and deliberately not permissive,
    /// because "https://host/" in a deny list would silently match nothing and leave an operator
    /// believing they had been taken off the list.
    /// </summary>
    internal static bool IsPlausibleHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.StartsWith('.')
            || host.EndsWith('.'))
        {
            return false;
        }

        foreach (string label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Things worth saying out loud that are not worth refusing to start over.</summary>
    private static List<string> CollectWarnings(DaemonConfig config)
    {
        var warnings = new List<string>();

        // Every section that captures unknown keys is reported here. frameLog and survey used
        // to capture theirs and never surface them - the doc's "reported at start-up" promise
        // held for some sections and quietly not for others - and ptt/paging/ardop/flex did not
        // capture at all, so a typo there vanished into the deserialiser.
        void Unknown(string? section, Dictionary<string, JsonElement>? settings)
        {
            foreach (string key in settings?.Keys ?? Enumerable.Empty<string>())
            {
                string prefix = section is null ? "" : $"{section}: ";
                warnings.Add(
                    $"{prefix}\"{key}\" is not a setting this version knows, and is being "
                    + $"IGNORED. Check the spelling against {ConfigDocUrl}");
            }
        }

        Unknown(null, config.UnknownSettings);
        Unknown("waterfall", config.Waterfall?.UnknownSettings);
        Unknown("monitor", config.Monitor?.UnknownSettings);
        foreach (ModemConfig modem in config.Monitor?.Modems ?? [])
        {
            Unknown($"monitor modem {modem.SubChannel}", modem.UnknownSettings);
        }

        foreach (UplinkConfig station in config.Monitor?.Uplinks ?? [])
        {
            Unknown(
                $"monitor uplink {(station.Callsign.Length > 0 ? station.Callsign : station.Slug)}",
                station.UnknownSettings);
        }

        Unknown("api", config.Api?.UnknownSettings);
        Unknown("ubersdr", config.UberSdr?.UnknownSettings);
        Unknown("frameLog", config.FrameLog?.UnknownSettings);
        Unknown("survey", config.Survey?.UnknownSettings);
        Unknown("rawCapture", config.RawCapture?.UnknownSettings);
        Unknown("deadFeed", config.DeadFeed?.UnknownSettings);
        Unknown("ptt", config.Ptt?.UnknownSettings);
        Unknown("paging", config.Paging?.UnknownSettings);
        Unknown("ardop", config.Ardop?.UnknownSettings);
        Unknown("flex", config.Flex?.UnknownSettings);
        foreach (ModemConfig modem in config.Modems)
        {
            Unknown($"modem {modem.SubChannel}", modem.UnknownSettings);
            Unknown($"modem {modem.SubChannel} identify", modem.Identify?.UnknownSettings);
        }

        for (int i = 0; i < config.ModemPlugins.Count; i++)
        {
            Unknown($"modemPlugins[{i}]", config.ModemPlugins[i].UnknownSettings);
        }

        Unknown("publish", config.Publish?.UnknownSettings);

        // Plain ws off the machine is the shape of a smoke test and the shape of a mistake, and
        // only the operator knows which: the token and everything this station says about itself
        // would cross their network in clear. Warned rather than refused, deliberately (4.3).
        if (config.Publish?.Url is { Length: > 0 } published
            && Uri.TryCreate(published, UriKind.Absolute, out Uri? uplink)
            && uplink.Scheme == "ws"
            && !uplink.IsLoopback)
        {
            warnings.Add(
                $"publish: \"url\" is \"{published}\", which is unencrypted ws to {uplink.Host}. "
                + "The token and everything this station publishes cross the network in clear. "
                + "Use wss unless this is a test on your own wire.");
        }

        return warnings;
    }

    /// <summary>True for the mode name that is an ARDOP virtual TNC rather than a packet modem.</summary>
    internal static bool IsArdop(string? mode) =>
        string.Equals(mode, "ardop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects two services asking for the same TCP port. Left to the OS this surfaces as a
    /// bind failure from whichever listener happens to start second, naming neither setting.
    /// </summary>
    private static void ValidatePorts(DaemonConfig config)
    {
        var claimed = new Dictionary<int, string>();
        void Claim(int port, string what)
        {
            if (claimed.TryGetValue(port, out string? already))
            {
                throw new InvalidDataException(
                    $"{what} and {already} both want TCP port {port}. Give them different ports.");
            }

            claimed[port] = what;
        }

        if (config.Ardop is null)
        {
            Claim(config.KissPort, "\"kissPort\"");
        }

        foreach (ModemConfig modem in config.Modems.Where(m => m.Port is not null))
        {
            Claim(modem.Port!.Value, $"the \"port\" of modem {modem.SubChannel}");
            if (IsArdop(modem.Mode))
            {
                // ardopcf's convention, not ours to move: data is always command + 1.
                Claim(modem.Port!.Value + 1, $"the ARDOP data port of modem {modem.SubChannel}");
            }
        }

        if (config.Waterfall is not null)
        {
            Claim(config.Waterfall.Port, "the waterfall");
        }

        if (config.Paging is not null)
        {
            Claim(config.Paging.Port, "the paging endpoint");
        }

        if (config.Ardop is not null)
        {
            Claim(config.Ardop.Port, "the ARDOP command port");
            // ardopcf's convention, not ours to move: data is always command + 1.
            Claim(config.Ardop.Port + 1, "the ARDOP data port");
        }
    }

    /// <summary>
    /// Whether the file names a top-level key at all, as against taking its default. Read from
    /// the document rather than the object, because a defaulted value and a value written down
    /// that happens to equal the default are indistinguishable once deserialized.
    /// </summary>
    private static bool StatesKey(string path, params string[] keys)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            JsonElement at = document.RootElement;
            foreach (string key in keys)
            {
                if (at.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                JsonProperty found = at.EnumerateObject().FirstOrDefault(
                    p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (found.Value.ValueKind == JsonValueKind.Undefined)
                {
                    return false;
                }

                at = found.Value;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a bind setting; "*" means every interface. Null when it is not an address. Unset
    /// or blank stays on loopback - the safe reading, since the alternative would silently put
    /// an unauthenticated transmit interface on every interface because a value was empty.
    /// </summary>
    internal static System.Net.IPAddress? ParseBind(string? bind) =>
        string.IsNullOrWhiteSpace(bind) ? System.Net.IPAddress.Loopback
        : bind == "*" ? System.Net.IPAddress.Any
        : System.Net.IPAddress.TryParse(bind, out System.Net.IPAddress? parsed) ? parsed : null;

    /// <summary>
    /// Loads a configuration file, turning every failure into an operator-facing explanation
    /// instead of an exception. Returns null with <paramref name="error"/> set on failure.
    /// </summary>
    /// <remarks>
    /// This is what the daemon calls. A bad config is an operator typo, not a bug, and the
    /// person who has to act on it reads it in `journalctl` - a .NET stack trace there tells
    /// them nothing they can use, and buries the one line that names the problem.
    /// </remarks>
    public static DaemonConfig? TryLoad(string path, out string error)
    {
        try
        {
            error = "";
            // A truncated file is a likely half-finished edit, and "does not contain any JSON
            // tokens" is a poor way to be told the file is empty.
            if (File.Exists(path) && File.ReadAllText(path).AsSpan().IsWhiteSpace())
            {
                error = Describe(path, "the file is empty");
                return null;
            }

            return Load(path);
        }
        catch (FileNotFoundException)
        {
            error = Describe(path, $"no such file: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            error = Describe(path, $"no such directory: {Path.GetDirectoryName(path)}");
        }
        catch (UnauthorizedAccessException)
        {
            error = Describe(path, "permission denied reading the file");
        }
        catch (JsonException e)
        {
            // System.Text.Json counts lines from 0; humans and editors count from 1.
            string at = e.LineNumber is { } line
                ? $"line {line + 1}, position {(e.BytePositionInLine ?? 0) + 1}: "
                : "";
            string detail = e.Message.Split(" Path:")[0];
            error = Describe(path, $"not valid JSON - {at}{detail}");
        }
        catch (InvalidDataException e)
        {
            error = Describe(path, e.Message);
        }
        catch (IOException e)
        {
            error = Describe(path, $"could not be read: {e.Message}");
        }

        return null;
    }

    /// <summary>Formats a config failure as "what is wrong" followed by "what to do".</summary>
    private static string Describe(string path, string problem)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"configuration error in {path}");
        text.AppendLine($"  {problem}");
        text.AppendLine();
        // Commands are given bare, as root, rather than with a sudo prefix: Debian only
        // installs sudo when the root password is left blank at install time, so a "sudo …"
        // line is a command that does not exist on a good number of the machines this runs on.
        text.AppendLine("  The service will not start until this is fixed. As root, to start");
        text.AppendLine("  from a known-good file:");
        if (File.Exists(ExamplePath))
        {
            text.AppendLine($"    cp {ExamplePath} {path}");
        }
        else
        {
            text.AppendLine($"    copy the annotated example over {path}");
        }

        text.AppendLine("  Then edit it for your sound device and PTT, and:");
        text.AppendLine("    systemctl restart pdn-soundmodem");
        text.Append("  Every setting is documented at " + ConfigDocUrl);
        return text.ToString();
    }

    /// <summary>Where the .deb puts the annotated example config.</summary>
    internal const string ExamplePath = "/usr/share/pdn-soundmodem/soundmodem.example.json";

    internal const string ConfigDocUrl =
        "https://github.com/packet-net/pdn-soundmodem/blob/main/CONFIG.md";
}

/// <summary>
/// Answering a station on the frequency its receiver is actually listening on.
/// </summary>
/// <remarks>
/// <para>A rig's transmit and receive conversions share one master oscillator, so a station
/// heard 5 Hz high is also listening 5 Hz high. Shifting our reply by the offset measured on
/// its own frames puts our signal where its demodulator expects it. Our own reference error
/// cancels - it is in the measurement and in the transmission with the same sign, through the
/// same oscillator - so this needs no calibrated or GPS-locked reference, only one that does
/// not move appreciably between hearing them and answering.</para>
/// <para><b>The benefit is theirs, not ours.</b> This station finds them regardless: the 300
/// baud modes run offset-diversity decoder banks. A correspondent running a fixed-centre modem
/// with no such bank is the one that cannot hear us, which is why the correction is worth
/// making and why it is transmit-only.</para>
/// <para><b>On by default</b>, and bounded rather than timid: every shift is clamped to
/// <see cref="MaxTrimHz"/>, so the worst this can do to a signal is put it 50 Hz off the channel
/// centre, which is less than several of the stations on the band are off it already. Set
/// <c>enabled: false</c> to measure and report without touching the transmitter.</para>
/// </remarks>
public sealed class FrequencyMatchingConfig
{
    /// <summary>Actually shift the transmitter. True by default; false measures and reports only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Frames kept per station for the estimate. Default 8.</summary>
    public int Samples { get; set; } = DefaultSamples;

    /// <inheritdoc cref="Samples" />
    public const int DefaultSamples = 8;

    /// <summary>How old a frame may be and still count, in seconds. Default 600.</summary>
    public double MaxAgeSeconds { get; set; } = DefaultMaxAgeSeconds;

    /// <inheritdoc cref="MaxAgeSeconds" />
    public const double DefaultMaxAgeSeconds = 600;

    /// <summary>
    /// Frames required before the estimate is acted on. Default 3.
    /// </summary>
    /// <remarks>
    /// Low on purpose. The correction only has to hold for the exchange it is used in, so a
    /// handful of recent frames is the right evidence; a long run would average across drift and
    /// describe neither end of it.
    /// </remarks>
    public int MinSamples { get; set; } = 3;

    /// <summary>
    /// Largest spread across those frames, in Hz, that still counts as settled. Default 20.
    /// </summary>
    /// <remarks>
    /// This is what separates a rig that is merely off frequency from one that is wandering.
    /// Measured on the live 40 m station: GB7WEM-7 held 0.6 Hz of spread across 467 frames and
    /// GB7OXF-2 held 0.7, while GB7NOT ranged over 54 Hz. The first two are worth correcting
    /// for and the third is not.
    /// </remarks>
    public double MaxSpreadHz { get; set; } = 20;

    /// <summary>Largest shift that will ever be applied, in Hz. Default 50.</summary>
    /// <remarks>
    /// The safety cap, and the reason the rest of this can be on by default. It bounds the damage
    /// independently of every other guard: even two stations chasing each other with the detector
    /// switched off could not walk further than this off the channel centre, and 50 Hz is inside
    /// what the measured stations on the live 40 m port are already scattered across. The channel
    /// clamps again at its own hard ceiling, so a bug upstream cannot get past both.
    /// </remarks>
    public double MaxTrimHz { get; set; } = 50;

    /// <summary>
    /// Fraction of the measured offset applied to a station that has already moved under our
    /// correction once. Default 0.5. A station that has never moved gets the whole correction.
    /// </summary>
    /// <remarks>
    /// <para>Conditional, because damping only ever fixes a feedback loop and in the normal case
    /// there is no loop: our transmitter is not in the path by which we measure theirs, so
    /// correcting for a station that is not itself correcting is open-loop. Damping there
    /// stabilises nothing and simply leaves half the error uncorrected. Noise is handled by
    /// averaging the window and gating on its spread; a wild estimate is bounded by
    /// <see cref="MaxTrimHz"/>.</para>
    /// <para>It earns its keep only in a two-sided chase too small to trip
    /// <see cref="ChaseThresholdHz"/>, where two undamped stations alternate between aligned and
    /// fully offset every exchange and a steady small error is the kinder outcome. So it applies
    /// where there is evidence of a peer that reacts, and nowhere else.</para>
    /// </remarks>
    public double Damping { get; set; } = 0.5;

    /// <summary>
    /// How far a station's own frequency may move, in Hz, after we start answering it off-centre
    /// before we stop doing so. Default 10.
    /// </summary>
    /// <remarks>
    /// Our transmitter cannot change what we measure of theirs, so a station whose offset shifts
    /// once we begin correcting has moved itself: either it is correcting for us in turn, which
    /// leaves both of us worse off than if only one had, or its reference is drifting. Neither is
    /// worth chasing, so the correction latches off for that station and says why.
    /// </remarks>
    public double ChaseThresholdHz { get; set; } = 10;

    /// <summary>
    /// How long to leave a station alone after its frequency moved under our correction, in
    /// seconds. Default 1800 (30 minutes).
    /// </summary>
    /// <remarks>
    /// Backing off is not the same as giving up. A station that moves once has most likely just
    /// moved - a knocked dial, a rig warming up - and will sit happily at its new offset; writing
    /// it off forever would mean never correcting for it again because of something it did once.
    /// After the cooldown its new offset is measured and corrected for like anybody else's.
    /// </remarks>
    public double ChaseCooldownSeconds { get; set; } = 1800;

    /// <summary>
    /// How many times a station may move under our correction before we stop trying. Default 3;
    /// 0 retries indefinitely.
    /// </summary>
    /// <remarks>
    /// Repetition is what separates a rig that moved from a station correcting for us in turn. A
    /// moved rig stays put afterwards; a peer running this same algorithm moves again every time
    /// we correct, and two of those trade adjustments indefinitely without either landing on the
    /// right answer.
    /// </remarks>
    public int MaxChases { get; set; } = 3;

    /// <summary>
    /// Destinations never worth aiming at, because they are not one station.
    /// </summary>
    /// <remarks>
    /// A beacon or an ID is heard by everybody, and aiming it at one correspondent's oscillator
    /// aims it away from every other listener. In practice these exclude themselves - we never
    /// receive frames <em>from</em> "BEACON", so no estimate for it can exist - but saying so
    /// makes the intent legible rather than incidental.
    /// </remarks>
    public static readonly string[] BroadcastDestinations =
        ["ID", "BEACON", "CQ", "QST", "ALL", "NODES", "MAIL", "APRS", "TEST"];

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}
