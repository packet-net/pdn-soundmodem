using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Ota;

/// <summary>One burst a pass intends to transmit.</summary>
/// <param name="WaveformNumber">MS110D waveform.</param>
/// <param name="Interleaver">Short or Long.</param>
/// <param name="Blocks">Interleaver blocks in the burst.</param>
/// <param name="Seed">Payload seed — and, for an injected channel, the channel realisation too,
/// so this row alone reproduces the exact transmission.</param>
/// <param name="SnrDb">SNR the rig was asked to inject, or null for no injection.</param>
/// <param name="Channel">Injected channel geometry, when <paramref name="SnrDb"/> is set.</param>
/// <param name="ConstraintLength">7 or 9.</param>
/// <param name="PreambleSuperframes">Preamble length.</param>
public sealed record CampaignBurst(
    int WaveformNumber,
    Ms110dInterleaverKind Interleaver = Ms110dInterleaverKind.Short,
    int Blocks = 1,
    int Seed = 1,
    double? SnrDb = null,
    LadderChannel Channel = LadderChannel.Awgn,
    int ConstraintLength = 7,
    int PreambleSuperframes = 3)
{
    /// <summary>The transmit settings this row describes.</summary>
    public Ms110dTxSettings Settings() => new()
    {
        WaveformNumber = WaveformNumber,
        Interleaver = Interleaver,
        ConstraintLength = ConstraintLength,
        PreambleSuperframes = PreambleSuperframes,
    };

    /// <summary>Regenerates the bits this row stands for.</summary>
    public Ms110dReferenceBits Reference() => new(
        Settings(),
        Ms110dReferenceBits.PayloadBitsForBlocks(WaveformNumber, Interleaver, Blocks),
        Seed);

    /// <summary>The ladder point this row stands for, when a channel was injected.</summary>
    public LadderPoint Point() => new(
        WaveformNumber, Interleaver, SnrDb ?? double.PositiveInfinity, Channel, Seed, Blocks);
}

/// <summary>What a pass intends to do. The request, not the record — see <see cref="CampaignManifest"/>.</summary>
/// <param name="Name">Short label; ends up in filenames and in the evidence log.</param>
/// <param name="OffsetHz">Carrier offset from the waveform centre.</param>
/// <param name="Bursts">In transmit order.</param>
/// <param name="Notes">Why this pass exists — free text, for whoever reads it later.</param>
public sealed record CampaignSchedule(
    string Name,
    double OffsetHz,
    IReadOnlyList<CampaignBurst> Bursts,
    string? Notes = null);

/// <summary>
/// What a pass actually did: the schedule it came from, plus every fact needed to interpret the
/// scores months later.
/// </summary>
/// <remarks>
/// <para>The fields that are easy to omit and impossible to reconstruct are the point of this
/// type. <b>The modem revision</b> above all: the demodulator changes daily, so a BER without one
/// is a number with no meaning attached. <b>The capture's SHA-256</b>, because "the capture" is
/// not a stable reference once there are forty of them. <b>The dial correction</b>, which is a
/// per-session measurement rather than a constant. <b>The pass gain and RF power</b>, without
/// which no level can be compared to any other. And <b>the supply</b>, because it has already
/// moved both the 100 Hz floor and the frequency reference.</para>
/// <para>Everything here is recorded automatically except the supply note, which no machine on
/// this bench can see.</para>
/// </remarks>
/// <param name="Schedule">What was asked for.</param>
/// <param name="ModemRevision">Repository revision of the binary that ran, <c>-dirty</c> suffixed
/// when the tree had uncommitted changes.</param>
/// <param name="WrittenUtc">When this manifest was written.</param>
/// <param name="Radio">Radio address, or "none" for a rehearsal.</param>
/// <param name="FrequencyMHz">Waveform slice centre.</param>
/// <param name="RfPower">Radio power setting, or null when nothing was transmitted.</param>
/// <param name="PassGain">The single IQ gain applied across the pass.</param>
/// <param name="DialCorrectionHz">The session's measured dial correction.</param>
/// <param name="CapturePath">Capture file, if one was recorded.</param>
/// <param name="CaptureSha256">Its hash.</param>
/// <param name="CaptureSample0Utc">Timestamp of the capture's first sample — the timebase every
/// burst position is measured against.</param>
/// <param name="ReceiverHost">Which receiver.</param>
/// <param name="BurstStartSeconds">Where each burst's modulated section starts in the capture,
/// derived from actual key-up times rather than from the plan.</param>
/// <param name="SupplyNote">Battery, filtered mains, whatever — the operator's word for it.</param>
public sealed record CampaignManifest(
    CampaignSchedule Schedule,
    string ModemRevision,
    DateTimeOffset WrittenUtc,
    string Radio,
    string FrequencyMHz,
    int? RfPower,
    double PassGain,
    double DialCorrectionHz,
    string? CapturePath = null,
    string? CaptureSha256 = null,
    DateTimeOffset? CaptureSample0Utc = null,
    string? ReceiverHost = null,
    IReadOnlyList<double>? BurstStartSeconds = null,
    string? SupplyNote = null);

/// <summary>Reading and writing the two documents a pass is made of.</summary>
public static class CampaignFiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));

    public static T Load<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty or not the expected shape");

    /// <summary>
    /// Loads either document and returns the schedule with the burst positions, if the file was
    /// a manifest and therefore knows them.
    /// </summary>
    /// <remarks><b>The shape is decided by inspection, not by a failed parse.</b>
    /// <c>System.Text.Json</c> does not object to missing members, so deserialising a bare
    /// schedule into a <see cref="CampaignManifest"/> succeeds and hands back one whose
    /// <c>Schedule</c> is null — a try/catch fallback would never fire and the caller would get a
    /// null reference or, worse, an empty pass scored as though nothing had been sent.</remarks>
    public static (CampaignSchedule Schedule, IReadOnlyList<double>? StartSeconds, string Revision)
        LoadScheduleOrManifest(string path)
    {
        string text = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(text);
        bool isManifest = document.RootElement.ValueKind == JsonValueKind.Object
                          && document.RootElement.TryGetProperty(
                              nameof(CampaignManifest.Schedule), out JsonElement inner)
                          && inner.ValueKind == JsonValueKind.Object;

        if (!isManifest)
        {
            CampaignSchedule bare = JsonSerializer.Deserialize<CampaignSchedule>(text, Json)
                ?? throw new InvalidDataException($"{path} is not a schedule or a manifest");
            return (Validate(bare, path), null, "—");
        }

        CampaignManifest manifest = JsonSerializer.Deserialize<CampaignManifest>(text, Json)
            ?? throw new InvalidDataException($"{path} is not a manifest");
        return (Validate(manifest.Schedule, path), manifest.BurstStartSeconds,
                manifest.ModemRevision ?? "—");
    }

    private static CampaignSchedule Validate(CampaignSchedule? schedule, string path)
    {
        if (schedule is null || schedule.Bursts is null || schedule.Bursts.Count == 0)
        {
            throw new InvalidDataException($"{path} contains no bursts");
        }

        return schedule;
    }

    /// <summary>The schedule as the scorer wants it, with expected times if they are known.</summary>
    public static List<ScheduledBurst> ToScorerSchedule(
        this CampaignSchedule schedule, IReadOnlyList<double>? startSeconds = null)
    {
        var bursts = new List<ScheduledBurst>(schedule.Bursts.Count);
        for (int k = 0; k < schedule.Bursts.Count; k++)
        {
            double at = startSeconds is not null && k < startSeconds.Count ? startSeconds[k] : -1;
            bursts.Add(new ScheduledBurst(schedule.Bursts[k].Reference(), at));
        }

        return bursts;
    }

    /// <summary>
    /// Repository revision the running harness was built from, <c>-dirty</c> suffixed when the
    /// tree had uncommitted changes. This is the demodulator's revision too: the modem is a
    /// project reference, so building this tool rebuilds it from the same tree.
    /// </summary>
    /// <remarks>
    /// <para>Stamped at build time rather than read from git at run time, so it describes the
    /// code that actually ran rather than whatever the working tree says now — the two diverge
    /// the moment anyone edits a file mid-pass, which during development is most of the time.
    /// </para>
    /// <para>Read from <em>this</em> assembly, not the entry assembly. Under a test runner the
    /// entry assembly is the test host, which carries somebody else's version — the first
    /// version of this returned a 40-character hash from the runner and would have written that
    /// into manifests wherever the harness was hosted rather than run directly.</para>
    /// </remarks>
    public static string ModemRevision()
    {
        string? informational = typeof(CampaignFiles).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = informational?.IndexOf('+') ?? -1;
        return plus >= 0 ? informational![(plus + 1)..] : "unknown";
    }

    /// <summary>SHA-256 of a capture, streamed — these files are hundreds of megabytes.</summary>
    public static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
