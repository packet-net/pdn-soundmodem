using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Keys in this modem entry that the daemon does not know; reported at start-up.</summary>
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
    public const string DefaultHeadlessDaxChannel = "2";

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
            DeadFeedDevice.Alsa => (0.0, 30.0),
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

    /// <summary>Frame log; null = frames are heard and not written down.</summary>
    public FrameLogConfig? FrameLog { get; set; }

    /// <summary>Signal survey; null = signals this station cannot read go unrecorded.</summary>
    public SurveyConfig? Survey { get; set; }

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

        if (ParseBind(config.Bind) is null)
        {
            throw new InvalidDataException(
                $"\"bind\": \"{config.Bind}\" is not an IP address. Use \"127.0.0.1\" for "
                + "loopback only, \"*\" for every interface, or the address of one interface.");
        }

        config.SidebandWasStated = StatesKey(path, "sideband");
        ValidatePorts(config);
        config.Warnings = CollectWarnings(config);
        return config;
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
        }

        for (int i = 0; i < config.ModemPlugins.Count; i++)
        {
            Unknown($"modemPlugins[{i}]", config.ModemPlugins[i].UnknownSettings);
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
    private static bool StatesKey(string path, string key)
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
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(
                    p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
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
