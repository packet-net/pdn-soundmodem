using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Daemon;

/// <summary>One receiver as the UberSDR directory describes it. Every field is optional.</summary>
/// <remarks>
/// The directory serves rather more than this - a PSK Reporter ranking, a CPU model, a rotator
/// azimuth - and several of those fields are absent on some instances, so nothing outside what
/// this monitor actually uses is modelled here and everything that is, is nullable. The names are
/// the directory's own, spelled out rather than derived by a naming policy, so a field that is
/// renamed upstream fails to bind visibly rather than quietly reading as zero.
/// </remarks>
internal sealed record UberSdrInstanceDto
{
    [JsonPropertyName("host")] public string? Host { get; init; }

    [JsonPropertyName("port")] public int Port { get; init; }

    /// <summary>
    /// Absent on three of the fifty instances captured on 2026-09-03 (pjmarsh.co.uk,
    /// sdr.meucorp.net, na5b.com), where it means plain HTTP rather than "unknown".
    /// </summary>
    [JsonPropertyName("tls")] public bool? Tls { get; init; }

    [JsonPropertyName("is_online")] public bool? IsOnline { get; init; }

    [JsonPropertyName("available_clients")] public int? AvailableClients { get; init; }

    [JsonPropertyName("max_clients")] public int? MaxClients { get; init; }

    [JsonPropertyName("public_iq_modes")] public List<string>? PublicIqModes { get; init; }

    [JsonPropertyName("antenna_connected")] public bool? AntennaConnected { get; init; }

    [JsonPropertyName("tuning_range")] public UberSdrTuningRangeDto? TuningRange { get; init; }

    [JsonPropertyName("callsign")] public string? Callsign { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("location")] public string? Location { get; init; }

    [JsonPropertyName("public_url")] public string? PublicUrl { get; init; }

    [JsonPropertyName("load_status")] public string? LoadStatus { get; init; }

    /// <summary>
    /// The receiver's own report of what it hears across HF, in dB. This is the figure worth
    /// showing; <c>noise_floor</c> next to it in the same object is a capability flag and not a
    /// level, which is a trap the 40 m plan fell into by calling this one "noise floor".
    /// </summary>
    [JsonPropertyName("snr_0_30_mhz")] public int? Snr0To30Mhz { get; init; }
}

/// <summary>What a receiver says it can tune to.</summary>
/// <param name="MinFrequency">Low end of the range, Hz.</param>
/// <param name="MaxFrequency">High end, Hz.</param>
/// <param name="Reported">
/// Whether the receiver measured this or the directory filled it in. False on six of the fifty
/// captured, where <c>samprate_source</c> is "fallback" and the range is a placeholder 10 kHz to
/// 30 MHz. A range that was never reported is not evidence of anything and does not exclude.
/// </param>
internal sealed record UberSdrTuningRangeDto(
    [property: JsonPropertyName("min_frequency")] double? MinFrequency,
    [property: JsonPropertyName("max_frequency")] double? MaxFrequency,
    [property: JsonPropertyName("reported")] bool? Reported);

/// <summary>The directory document.</summary>
internal sealed record UberSdrDirectoryDto
{
    [JsonPropertyName("instances")] public List<UberSdrInstanceDto>? Instances { get; init; }
}

/// <summary>
/// One receiver as this monitor sees it: what the directory said, plus the slug it is served
/// under and whether it can be picked.
/// </summary>
/// <param name="Slug">The path segment its page is served under, derived from
/// <paramref name="Host"/>; see <see cref="UberSdrDirectory.SlugFor"/>.</param>
/// <param name="Endpoint">Where to connect, straight from the directory's host, port and tls.</param>
/// <param name="Offered">
/// Whether a visitor can pick it. False on a receiver with no free slot, which is listed anyway
/// and shown as full: a visitor who is told nothing about a receiver that has simply run out of
/// room is left to wonder whether this site is broken.
/// </param>
/// <param name="Why">Why it cannot be picked, in a visitor's words. Null when it can.</param>
internal sealed record DirectoryReceiver(
    string Slug,
    UberSdrEndpoint Endpoint,
    string Callsign,
    string Name,
    string Location,
    string? PublicUrl,
    int? SnrDb,
    string? LoadStatus,
    int AvailableClients,
    int MaxClients,
    bool Offered,
    string? Why)
{
    /// <summary>The directory's host, which is what allow and deny match on and what the slug
    /// is derived from.</summary>
    internal string Host => Endpoint.Host;

    /// <summary>How the picker and the receiver's own status chip name it before any session has
    /// been opened: the directory's callsign and where it says it is.</summary>
    internal string Description =>
        Location.Length > 0 ? $"{Callsign}, {Location}" : Callsign;
}

/// <summary>
/// The list of receivers, and how old it is.
/// </summary>
/// <param name="Receivers">The last good list, filtered. Empty before the first successful fetch.</param>
/// <param name="ListFrom">When that list was fetched; null before the first successful fetch.</param>
/// <param name="Stale">Whether the most recent attempt failed, so the list is the last good one
/// rather than the current one.</param>
/// <param name="Problem">Why the most recent attempt failed, in one sentence. Null when it did not.</param>
internal sealed record DirectorySnapshot(
    IReadOnlyList<DirectoryReceiver> Receivers,
    DateTimeOffset? ListFrom,
    bool Stale,
    string? Problem)
{
    internal static DirectorySnapshot Cold { get; } = new([], null, false, null);
}

/// <summary>
/// The public UberSDR directory, fetched on a timer and remembered.
/// </summary>
/// <remarks>
/// <para><b>Tolerant of the directory being down.</b> A failed fetch keeps the previous list,
/// journals one line and marks the snapshot stale; the picker says how old the list is and
/// carries on. Nothing here can take a live session down, because a session is held by a station
/// and a station outlives whatever the directory last said about its receiver.</para>
/// <para><b>The filters decide two different things.</b> A receiver this monitor could not use -
/// offline, without the IQ mode it asks for, with no antenna, or tuned somewhere that cannot
/// cover the window the modems occupy - is not listed at all: a list of receivers that show the
/// 40 m packet window has no business holding one that cannot. A receiver with no free slot is
/// listed and shown as full, because that is a receiver a visitor may well come back to.</para>
/// <para><b>Allow and deny are matched on the host, and deny wins.</b> This is the mechanism by
/// which an operator who asks not to be listed is not listed, which is why it is not optional and
/// why it is tested.</para>
/// </remarks>
internal sealed class UberSdrDirectory : IDisposable
{
    // Directory-supplied hostnames are exactly the case the default infinite pooled-connection
    // lifetime gets wrong: a tunnel host moves and a process that has been up for a week keeps
    // dialling an address nobody is listening on. The same figure the UberSDR input's own client
    // now uses, and for the same reason.
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly UberSdrDirectoryOptions _options;
    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly StationJournal _journal;
    private readonly TimeProvider _time;

    // Slugs a station has already been built under. A URL a visitor has bookmarked must not stop
    // working because an unrelated instance appeared and collided with it, so a slug in here is
    // that host's for the life of the process and a newcomer takes the fallback instead.
    private readonly ConcurrentDictionary<string, string> _bound = new(StringComparer.Ordinal);

    // Read by every request the picker makes and written by the refresh timer. Replaced whole, so
    // a reader holds one consistent snapshot and never a half-updated list.
    private volatile DirectorySnapshot _snapshot = DirectorySnapshot.Cold;

    private ITimer? _refresh;

    internal UberSdrDirectory(
        UberSdrDirectoryOptions options,
        StationJournal journal,
        TimeProvider? time = null,
        Func<CancellationToken, Task<string>>? fetch = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _journal = journal;
        _time = time ?? TimeProvider.System;
        _fetch = fetch ?? (token => Http.GetStringAsync(options.Url, token));
    }

    /// <summary>The last good list, and how old it is.</summary>
    internal DirectorySnapshot Snapshot => _snapshot;

    /// <summary>Fetches once now, then again every <c>refreshMinutes</c>.</summary>
    /// <remarks>
    /// The first fetch is awaited so that a monitor which starts with a reachable directory has
    /// its list before it answers its first request. A first fetch that fails is not an error: the
    /// picker comes up saying it could not reach the directory, which is a better answer than a
    /// daemon that will not start.
    /// </remarks>
    internal async Task StartAsync(CancellationToken cancellation)
    {
        await RefreshAsync(cancellation).ConfigureAwait(false);
        if (_options.Refresh > TimeSpan.Zero)
        {
            _refresh = _time.CreateTimer(
                _ => _ = RefreshAsync(cancellation), null, _options.Refresh, _options.Refresh);
        }
    }

    /// <summary>Fetches the directory once and replaces the snapshot with what it said.</summary>
    internal async Task RefreshAsync(CancellationToken cancellation)
    {
        string body;
        try
        {
            body = await _fetch(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                                    or OperationCanceledException or InvalidOperationException
                                    or UriFormatException)
        {
            if (cancellation.IsCancellationRequested)
            {
                return;   // shutting down; a cancelled fetch is not an outage
            }

            Fail(Ascii(e.Message));
            return;
        }

        UberSdrDirectoryDto? document;
        try
        {
            document = JsonSerializer.Deserialize<UberSdrDirectoryDto>(body, Json);
        }
        catch (JsonException bad)
        {
            Fail($"the reply was not the JSON this expects ({Ascii(bad.Message)})");
            return;
        }

        if (document?.Instances is null)
        {
            Fail("the reply carried no \"instances\" list");
            return;
        }

        IReadOnlyList<DirectoryReceiver> receivers = Read(document.Instances);
        DirectorySnapshot previous = _snapshot;
        _snapshot = new DirectorySnapshot(receivers, _time.GetUtcNow(), Stale: false, Problem: null);

        if (previous.Stale)
        {
            _journal.Write(
                $"directory: {_options.Url} is answering again, {receivers.Count} receivers listed");
        }
        else if (previous.ListFrom is null)
        {
            _journal.Write(
                $"directory: {receivers.Count} of {document.Instances.Count} receivers listed from "
                + _options.Url);
        }
    }

    /// <summary>Remembers that a station has been built under <paramref name="slug"/>, so that
    /// nothing later takes it away from the host it names.</summary>
    internal void Bind(string slug, string host) => _bound[slug] = host;

    /// <summary>One line for a fetch that failed, and a snapshot that says the list is old.</summary>
    private void Fail(string reason)
    {
        DirectorySnapshot previous = _snapshot;
        _snapshot = previous with { Stale = true, Problem = reason };

        // Once per outage rather than every refresh: a directory that is down for a day would
        // otherwise write 288 identical lines, and the one that matters is the first.
        if (!previous.Stale)
        {
            _journal.WriteError(
                $"directory: cannot reach {_options.Url} - {reason}. "
                + (previous.ListFrom is { } from
                    ? $"Keeping the {previous.Receivers.Count} receivers listed at "
                        + $"{from.UtcDateTime:HH:mm} UTC; nothing watching a receiver is affected."
                    : "Nothing has been listed yet; the picker will fill in when it answers."));
        }
    }

    /// <summary>Filters, slugs and orders one fetch's instances.</summary>
    private IReadOnlyList<DirectoryReceiver> Read(List<UberSdrInstanceDto> instances)
    {
        // Ordinal by host, so that whatever order the directory happens to serve them in, two
        // instances competing for a slug are resolved the same way every time.
        var usable = new List<UberSdrInstanceDto>();
        foreach (UberSdrInstanceDto instance in instances
                     .Where(i => !string.IsNullOrWhiteSpace(i.Host))
                     .OrderBy(i => i.Host, StringComparer.Ordinal))
        {
            if (Excluded(instance) is null)
            {
                usable.Add(instance);
            }
        }

        Dictionary<string, string> slugs = AssignSlugs(usable);
        var receivers = new List<DirectoryReceiver>(usable.Count);
        foreach (UberSdrInstanceDto instance in usable)
        {
            string host = instance.Host!;
            if (!slugs.TryGetValue(host, out string? slug))
            {
                continue;   // two hosts that sanitise identically; already journalled
            }

            int available = instance.AvailableClients ?? 0;
            int max = instance.MaxClients ?? 0;
            bool offered = available > 0;
            receivers.Add(new DirectoryReceiver(
                slug,
                // The directory's own three fields, which is exactly what an endpoint is - no
                // device string is built here and none is re-parsed.
                new UberSdrEndpoint(host, instance.Port, instance.Tls ?? false),
                instance.Callsign?.Trim() ?? "",
                instance.Name?.Trim() ?? "",
                instance.Location?.Trim() ?? "",
                instance.PublicUrl,
                instance.Snr0To30Mhz,
                instance.LoadStatus,
                available,
                max,
                offered,
                offered ? null : "full"));
        }

        return receivers;
    }

    /// <summary>
    /// Why this instance is not listed, or null when it is. The plan's order: the operator's
    /// wishes first, then whether it is up, then whether it can do what this monitor needs.
    /// </summary>
    private string? Excluded(UberSdrInstanceDto instance)
    {
        string host = instance.Host!;

        // Deny always wins, and beats a host that is also allowed. An operator who has asked not
        // to be listed has asked once and should not have to ask again because somebody edited
        // the other list.
        if (_options.Deny.Contains(host))
        {
            return "denied";
        }

        if (_options.Allow.Count > 0 && !_options.Allow.Contains(host))
        {
            return "not in the allow list";
        }

        if (instance.IsOnline == false)
        {
            return "offline";
        }

        if (instance.PublicIqModes is { Count: > 0 } modes
            && !modes.Contains(_options.IqMode, StringComparer.OrdinalIgnoreCase))
        {
            return $"does not offer {_options.IqMode}";
        }

        if (instance.AntennaConnected == false)
        {
            return "no antenna connected";
        }

        // An unreported range is a placeholder the directory filled in, not a claim the receiver
        // made, so it passes: "probably fine" is the honest reading of a field whose own flag says
        // nobody measured it.
        if (instance.TuningRange is { Reported: true } range
            && (range.MinFrequency > _options.WindowLowHz || range.MaxFrequency < _options.WindowHighHz))
        {
            return "cannot tune the window";
        }

        return null;
    }

    /// <summary>
    /// Which slug each host is served under, with collisions broken the same way every time.
    /// </summary>
    /// <remarks>
    /// The short form is what a visitor sees in the address bar and is worth having. Where two
    /// hosts want the same one, the tie is broken by giving each the full sanitised host instead -
    /// except that a slug a station has already been built under stays with the host it names,
    /// because a bookmark that stops working when an unrelated instance appears is exactly what
    /// deriving the slug from the host was meant to prevent.
    /// </remarks>
    private Dictionary<string, string> AssignSlugs(List<UberSdrInstanceDto> instances)
    {
        var wanted = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (UberSdrInstanceDto instance in instances)
        {
            string slug = SlugFor(instance.Host!);
            if (slug.Length == 0)
            {
                // A host with no letters or digits in it at all. Nothing can be served under an
                // empty path segment, and this is not a receiver anybody could reach anyway.
                _journal.WriteError(
                    $"directory: \"{Ascii(instance.Host!)}\" gives no usable address for a page, "
                    + "so it is not listed");
                continue;
            }

            (wanted.TryGetValue(slug, out List<string>? hosts)
                ? hosts
                : wanted[slug] = []).Add(instance.Host!);
        }

        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string slug, List<string> hosts) in wanted)
        {
            // The station that already answers on this slug keeps it; everybody else in the
            // collision takes the fallback, whether or not one of them is bound.
            string? bound = _bound.TryGetValue(slug, out string? owner) && hosts.Contains(owner)
                ? owner
                : null;
            bool contested = hosts.Count > 1;

            foreach (string host in hosts)
            {
                string chosen = !contested || host == bound ? slug : FullSlugFor(host);
                if (taken.TryGetValue(chosen, out string? already))
                {
                    _journal.WriteError(
                        $"directory: {host} and {already} both want /r/{chosen}/, and neither has "
                        + "a longer form to fall back to. Listing the first and leaving the "
                        + "second out; tell the receivers' operators, because one of them has a "
                        + "hostname that cannot be told apart from the other's.");
                    continue;
                }

                taken[chosen] = host;
                assigned[host] = chosen;
            }

            if (contested)
            {
                _journal.Write(
                    $"directory: {hosts.Count} receivers want /r/{slug}/ ({string.Join(", ", hosts)}), "
                    + "so "
                    + (bound is null
                        ? "each is served under its full host instead"
                        : $"{bound} keeps it and the others are served under their full hosts"));
            }
        }

        return assigned;
    }

    /// <summary>
    /// The path segment a receiver's page is served under, derived from its host.
    /// </summary>
    /// <remarks>
    /// <para>Lower-case the host; strip a trailing <c>.tunnel.ubersdr.org</c> or
    /// <c>.instance.ubersdr.org</c>; replace every run of characters outside <c>[a-z0-9-]</c> with
    /// a single hyphen; trim leading and trailing hyphens. That gives <c>m9psy-1</c>,
    /// <c>rocksdr</c>, <c>g4eyr</c>, <c>reading-ubersdr-m0lte-uk</c>,
    /// <c>websdr-heppen-be</c>.</para>
    /// <para>The host, because it is the only field the directory guarantees unique and because a
    /// URL a visitor bookmarks must not change when an unrelated instance appears. The callsign
    /// reads better and is unique today, but the directory does not promise it. The host's first
    /// label looks tempting and is wrong: <c>websdr</c> appears twice and <c>ubersdr</c> four
    /// times in the capture. The slug is ugly for the instances not on an ubersdr.org tunnel; the
    /// picker shows the callsign and the location, so it is only ever seen in the address bar.</para>
    /// </remarks>
    internal static string SlugFor(string host)
    {
        string lowered = host.ToLowerInvariant();
        foreach (string suffix in (ReadOnlySpan<string>)[".tunnel.ubersdr.org", ".instance.ubersdr.org"])
        {
            if (lowered.EndsWith(suffix, StringComparison.Ordinal))
            {
                lowered = lowered[..^suffix.Length];
                break;
            }
        }

        return Sanitise(lowered);
    }

    /// <summary>The whole host, sanitised: what a slug falls back to when two of them collide.</summary>
    internal static string FullSlugFor(string host) => Sanitise(host.ToLowerInvariant());

    private static string Sanitise(string lowered)
    {
        var slug = new StringBuilder(lowered.Length);
        bool pendingHyphen = false;
        foreach (char c in lowered)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingHyphen = false;
                slug.Append(c);
            }
            else
            {
                // A run of anything else, hyphens included, collapses to one hyphen - and one
                // that is only written if something follows it, which is what trims the ends.
                pendingHyphen = true;
            }
        }

        return slug.ToString();
    }

    /// <summary>
    /// A string safe to put in the journal. Everything here came off the internet, and journald's
    /// pager under a C locale renders a byte above 0x7F as &lt;E2&gt;&lt;80&gt;&lt;94&gt;.
    /// </summary>
    internal static string Ascii(string text)
    {
        if (!text.Any(c => c > '~' || c < ' '))
        {
            return text;
        }

        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            clean.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return clean.ToString();
    }

    /// <inheritdoc />
    public void Dispose() => _refresh?.Dispose();
}

/// <summary>What the directory client needs to know.</summary>
internal sealed record UberSdrDirectoryOptions
{
    /// <summary>Where the list is fetched from.</summary>
    public required string Url { get; init; }

    /// <summary>How often it is fetched again. Zero fetches once, at start-up.</summary>
    public TimeSpan Refresh { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The IQ mode this monitor asks receivers for; one that does not offer it is not
    /// listed.</summary>
    public string IqMode { get; init; } = "iq48";

    /// <summary>Low edge of the RF window the configured modems occupy, Hz.</summary>
    public required double WindowLowHz { get; init; }

    /// <summary>High edge of that window, Hz.</summary>
    public required double WindowHighHz { get; init; }

    /// <summary>When non-empty, the only hosts listed.</summary>
    public HashSet<string> Allow { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts never listed, whatever else says otherwise.</summary>
    public HashSet<string> Deny { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
