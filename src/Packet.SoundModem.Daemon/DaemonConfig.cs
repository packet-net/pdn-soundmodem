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

    /// <summary>Centre/carrier frequency override in Hz (mode default when null).</summary>
    public double? Frequency { get; set; }
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

/// <summary>Channel-access tunables (KISS clients can override at runtime).</summary>
public sealed class CsmaConfig
{
    /// <summary>TXDELAY in milliseconds.</summary>
    public int TxDelayMilliseconds { get; set; } = 300;

    /// <summary>p-persistence 0–255.</summary>
    public int Persistence { get; set; } = 63;

    /// <summary>Slot time in milliseconds.</summary>
    public int SlotTimeMilliseconds { get; set; } = 100;

    /// <summary>TX tail in milliseconds.</summary>
    public int TxTailMilliseconds { get; set; } = 20;
}

/// <summary>pdn-soundmodem daemon configuration file (JSON, comments not allowed —
/// keep a commented example beside it).</summary>
public sealed class DaemonConfig
{
    /// <summary>ALSA device for capture and playback.</summary>
    public string Device { get; set; } = "default";

    /// <summary>Capture rate; card-native (48000) recommended — the daemon decimates.</summary>
    public int CaptureRate { get; set; } = 48000;

    /// <summary>KISS TCP listen port.</summary>
    public int KissPort { get; set; } = 8105;

    /// <summary>The logical modems sharing the audio channel.</summary>
    public List<ModemConfig> Modems { get; set; } = [];

    /// <summary>PTT control; null = VOX / none.</summary>
    public PttConfig? Ptt { get; set; }

    /// <summary>POCSAG paging endpoint; null = disabled.</summary>
    public PagingConfig? Paging { get; set; }

    /// <summary>ARDOP virtual TNC; null = disabled. Exclusive with Modems/Paging
    /// (the ARDOP channel is dedicated; docs/ardop-design.md §2.2).</summary>
    public ArdopConfig? Ardop { get; set; }

    /// <summary>Channel-access parameters.</summary>
    public CsmaConfig Csma { get; set; } = new();

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
            ?? throw new InvalidDataException("empty configuration");
        if (config.Ardop is not null && (config.Modems.Count > 0 || config.Paging is not null))
        {
            throw new InvalidDataException(
                "Ardop is exclusive with Modems/Paging — the ARDOP channel is dedicated");
        }

        if (config.Modems.Count == 0 && config.Ardop is null)
        {
            config.Modems.Add(new ModemConfig());
        }

        var duplicates = config.Modems.GroupBy(m => m.SubChannel).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidDataException($"duplicate sub-channel {duplicates[0].Key}");
        }

        return config;
    }
}
