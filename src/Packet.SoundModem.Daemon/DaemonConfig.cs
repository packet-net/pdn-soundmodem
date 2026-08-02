using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.SoundModem.Daemon;

/// <summary>One logical modem on the shared audio channel.</summary>
public sealed class ModemConfig
{
    /// <summary>KISS sub-channel (port nibble), 0–15.</summary>
    public int SubChannel { get; set; }

    /// <summary>Mode name as accepted by --modem (afsk1200, afsk1200-multi, bpsk300,
    /// bpsk300-nocrc, qpsk2400, qpsk3600, fsk9600, fsk9600-il2p).</summary>
    public string Mode { get; set; } = "afsk1200";

    /// <summary>Audio centre/carrier frequency override in Hz, applied to both TX and RX
    /// (QtSoundModem-style per-modem tuning; mode default when null). Honoured by the
    /// variable-centre modes only — the AFSK tone-pair modes (afsk*, default 1700) and the
    /// BPSK/QPSK carrier modes (bpsk*/qpsk*, default 1500; 1650 for qpsk3600). The baseband
    /// FSK families (fsk*/c4fsk*) and the spec-fixed waveforms (freedv-*/ms110d-*) have no
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
    /// A KISS TCP port dedicated to this modem alone; null (the default) means it is reachable
    /// only through the shared <see cref="DaemonConfig.KissPort"/> by its sub-channel nibble.
    /// </summary>
    /// <remarks>
    /// For host software that hardcodes KISS channel 0 and gives you no way to set the nibble —
    /// on the shared port such a host can only ever reach sub-channel 0, however many modems
    /// are configured. A dedicated port surfaces this modem's frames as nibble 0 and transmits
    /// everything it receives on this modem whatever nibble was used, so the host never has to
    /// know the multiplex exists. The shared port keeps working alongside it.
    /// </remarks>
    public int? KissPort { get; set; }
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
}

/// <summary>POCSAG paging endpoint (DAPNET/POCSAG-compatible waveform; local paging
/// API, pdn) — see PagingTcpServer for the line grammar.</summary>
public sealed class PagingConfig
{
    /// <summary>Paging TCP listen port.</summary>
    public int Port { get; set; } = 8106;

    /// <summary>POCSAG bit rate: 512, 1200 (DAPNET, default) or 2400.</summary>
    public int Baud { get; set; } = 1200;

    /// <summary>Invert the TX baseband polarity (for radios whose data path inverts;
    /// the spec convention '0' = high frequency is the default).</summary>
    public bool InvertPolarity { get; set; }
}

/// <summary>ARDOP virtual TNC (ardopcf-compatible TCP host interface; Winlink/Pat).
/// Per the dedicated-channel policy the ARDOP channel hosts no packet modems or
/// paging — configuring this alongside Modems/Paging is rejected.</summary>
public sealed class ArdopConfig
{
    /// <summary>Host-interface command port (ardopcf convention 8515); the data port
    /// always listens on the next port up.</summary>
    public int Port { get; set; } = 8515;
}

/// <summary>Headless FlexRadio slice-creation params (used when Device is
/// <c>flex:&lt;radio&gt;</c> with no <c>@station</c> — the daemon owns the radio and creates
/// its own slice). Ignored in attach mode (a <c>@station</c> device string). Defaults match
/// docs/flex-integration.md §8.</summary>
public sealed class FlexConfig
{
    /// <summary>Slice frequency (MHz, six-decimal Flex form). Default "14.100000".</summary>
    public string Frequency { get; set; } = "14.100000";

    /// <summary>RX/TX antenna. Default "ANT1".</summary>
    public string Antenna { get; set; } = "ANT1";

    /// <summary>Slice demod mode. Default "DIGU".</summary>
    public string Mode { get; set; } = "DIGU";

    /// <summary>The DAX channel the client claims (both headless and attach). Default "1". Set a
    /// different channel to coexist with a running SmartSDR (which grabs DAX 1) — see
    /// docs/flex-integration.md §8.</summary>
    public string DaxChannel { get; set; } = "1";
}

/// <summary>Browser waterfall endpoint (spectrum + waterfall + per-frame burst
/// attribution); null = disabled. See WaterfallWebServer.</summary>
public sealed class WaterfallConfig
{
    /// <summary>HTTP listen port.</summary>
    public int Port { get; set; } = 8107;

    /// <summary>Bind address; "*" listens on all interfaces (default loopback only).</summary>
    public string Bind { get; set; } = "127.0.0.1";

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
}

/// <summary>pdn-soundmodem daemon configuration file. JSON, with comments and trailing
/// commas accepted (see <see cref="Options"/>) and case-insensitive key matching — the
/// shipped soundmodem.example.json relies on that and annotates itself. Full reference:
/// CONFIG.md.</summary>
public sealed class DaemonConfig
{
    /// <summary>ALSA device for capture and playback.</summary>
    public string Device { get; set; } = "default";

    /// <summary>Capture rate; card-native (48000) recommended — the daemon decimates.</summary>
    public int CaptureRate { get; set; } = 48000;

    /// <summary>KISS TCP listen port — shared by every modem, addressed by sub-channel nibble.
    /// Individual modems can also get a port to themselves; see <see cref="ModemConfig.KissPort"/>.</summary>
    public int KissPort { get; set; } = 8105;

    /// <summary>
    /// Address the KISS listeners bind to; "*" for all interfaces. Loopback by default, because
    /// KISS has no authentication whatsoever — anything that can reach the port can transmit on
    /// your licence. Applies to the shared port and every per-modem port.
    /// </summary>
    public string KissBind { get; set; } = "127.0.0.1";

    /// <summary>The logical modems sharing the audio channel.</summary>
    public List<ModemConfig> Modems { get; set; } = [];

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

    /// <summary>Browser waterfall endpoint; null = disabled.</summary>
    public WaterfallConfig? Waterfall { get; set; }

    /// <summary>
    /// Settings present in the file that this version does not know. Kept so start-up can say
    /// so out loud: System.Text.Json drops unknown members silently, which turns a typo — or a
    /// setting that has since been withdrawn, like the old "csma" block — into a config that
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
                "the file contains only `null` — there is nothing to configure from. A minimal "
                + "working file is {\"device\": \"default\", \"modems\": [{\"subChannel\": 0, "
                + "\"mode\": \"afsk1200\"}]}");
        if (config.Ardop is not null && (config.Modems.Count > 0 || config.Paging is not null))
        {
            throw new InvalidDataException(
                "\"ardop\" cannot be combined with \"modems\" or \"paging\" — the ARDOP channel is "
                + "dedicated. Keep \"ardop\" and delete the others, or delete \"ardop\".");
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
                + "KISS sub-channel (0-15) — renumber one of them.");
        }

        if (ParseBind(config.KissBind) is null)
        {
            throw new InvalidDataException(
                $"\"kissBind\": \"{config.KissBind}\" is not an IP address. Use \"127.0.0.1\" for "
                + "loopback only, \"*\" for every interface, or the address of one interface.");
        }

        ValidatePorts(config);
        config.Warnings = CollectWarnings(config);
        return config;
    }

    /// <summary>Things worth saying out loud that are not worth refusing to start over.</summary>
    private static List<string> CollectWarnings(DaemonConfig config)
    {
        var warnings = new List<string>();
        foreach (string key in config.UnknownSettings?.Keys ?? Enumerable.Empty<string>())
        {
            if (key.Equals("csma", StringComparison.OrdinalIgnoreCase))
            {
                // Withdrawn deliberately: TXDELAY/P/SLOTTIME/TXTAIL belong to the host, which
                // sets them over KISS. Silently ignoring a tuned block would quietly restore
                // the defaults on somebody's working link.
                warnings.Add(
                    "\"csma\" is no longer a configuration setting and is being IGNORED. Channel "
                    + "access is the host's to set: send KISS TXDELAY (0x01), P (0x02), SLOTTIME "
                    + "(0x03) and TXTAIL (0x04) from your host software. Until it does, the "
                    + "defaults are 300 ms / 63 / 100 ms / 20 ms. `--txdelay MS` still overrides "
                    + "the TXDELAY default for bench use.");
            }
            else
            {
                warnings.Add(
                    $"\"{key}\" is not a setting this version knows, and is being IGNORED. Check "
                    + $"the spelling against {ConfigDocUrl}");
            }
        }

        return warnings;
    }

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

        foreach (ModemConfig modem in config.Modems.Where(m => m.KissPort is not null))
        {
            Claim(modem.KissPort!.Value, $"the \"kissPort\" of modem {modem.SubChannel}");
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
    /// Parses a bind setting; "*" means every interface. Null when it is not an address. Unset
    /// or blank stays on loopback — the safe reading, since the alternative would silently put
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
    /// person who has to act on it reads it in `journalctl` — a .NET stack trace there tells
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
            error = Describe(path, $"not valid JSON — {at}{detail}");
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
