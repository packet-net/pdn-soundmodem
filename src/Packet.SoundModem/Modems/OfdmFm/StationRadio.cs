using System.Text.Json;

namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// Station-level settings that are a property of the radio rather than of the waveform, read from
/// an untracked file beside the daemon.
/// </summary>
/// <remarks>
/// <para><b>Why a file and not <c>ModemOptions</c>.</b> A modem is deliberately blind:
/// "a modem sees audio in and frames out, and nothing else. It does not see the daemon, the KISS
/// server, the config or the radio". <c>ModemOptions</c> is a fixed record with no room for a
/// modem's own settings, so the only channel this modem has is a file it reads itself, which is
/// already how the waveform geometry arrives.</para>
/// <para>Absent file, absent section or absent port all mean the same thing: the feature is off and
/// nothing opens a serial port. A station opts in by writing the file.</para>
/// </remarks>
/// <param name="TaitPort">The radio's CCDI serial port, for example <c>/dev/ttyUSB0</c>. Null
/// leaves the feature off.</param>
/// <param name="TaitBaud">Port speed. The data port's own programming decides this; 28800 is what
/// these stations are set to.</param>
/// <param name="BusyAboveDbm">Optional: also call the channel busy when the radio's own RSSI reads
/// above this many dBm. Null uses the radio's DCD alone, which is event driven and costs no serial
/// traffic. Set it when the squelch is open (which a data station's usually is) and DCD therefore
/// never asserts, or to catch a carrier the squelch would not open for.</param>
/// <param name="PollMilliseconds">How often to read RSSI when <paramref name="BusyAboveDbm"/> is
/// set. Ignored otherwise.</param>
public sealed record StationRadio(
    string? TaitPort = null,
    int TaitBaud = 28800,
    double? BusyAboveDbm = null,
    int PollMilliseconds = 100)
{
    /// <summary>The file name looked for, beside the daemon and then upward.</summary>
    public const string FileName = "ofdm-fm.station.json";

    /// <summary>Whether this configuration asks for anything at all.</summary>
    public bool WantsCarrierSense => !string.IsNullOrWhiteSpace(TaitPort);

    /// <summary>
    /// Reads the station file, or returns null if there is none.
    /// </summary>
    /// <param name="path">File to read; defaults to <see cref="FileName"/> beside this assembly,
    /// then beside the host application, walking upward from each - the same search the geometry
    /// file gets, so the two live together.</param>
    public static StationRadio? Load(string? path = null)
    {
        string? file = path ?? Find(FileName);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(file);
            var doc = JsonSerializer.Deserialize<Wrapper>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return doc?.CarrierSense;
        }
        catch (JsonException e)
        {
            // A malformed station file must not take the daemon down on a feature nobody has to
            // use. Say so and carry on without it.
            Console.Error.WriteLine($"ofdm-fm: {FileName} is not valid JSON, ignoring it: {e.Message}");
            return null;
        }
    }

    private sealed record Wrapper(StationRadio? CarrierSense);

    internal static string? Find(string name)
    {
        string? beside = Path.GetDirectoryName(typeof(StationRadio).Assembly.Location);
        foreach (string? from in (ReadOnlySpan<string?>)[beside, AppContext.BaseDirectory])
        {
            if (string.IsNullOrEmpty(from))
            {
                continue;
            }

            var dir = new DirectoryInfo(from);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
