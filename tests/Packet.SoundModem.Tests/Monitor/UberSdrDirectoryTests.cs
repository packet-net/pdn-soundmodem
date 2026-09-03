using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// The UberSDR directory as this monitor reads it: which receivers are listed, which can be
/// picked, what each one's page is served under, and what happens when the directory is down.
/// </summary>
/// <remarks>
/// Everything here runs against <c>instances.json</c>, a real capture of
/// <c>https://instances.ubersdr.org/api/instances</c> taken on 2026-09-03 and kept exactly as it
/// was served. A hand-written fixture would agree with whatever the parser happened to expect;
/// this one carries the awkward cases the plan found by looking - three instances that omit
/// <c>tls</c> entirely, six whose tuning range says outright that nobody measured it, and three
/// with no antenna connected.
/// </remarks>
public class UberSdrDirectoryTests
{
    /// <summary>The 40 m packet window the live monitor watches, edge to edge in RF.</summary>
    private const double WindowLowHz = 7050136;
    private const double WindowHighHz = 7051776;

    private const string Dalgety = "m9psy-1.instance.ubersdr.org";
    private const string Reading = "reading-ubersdr.m0lte.uk";

    [Fact]
    public async Task The_Directory_Is_Parsed_From_A_Real_Capture()
    {
        var h = new Harness();

        await h.RefreshAsync(Capture());

        DirectorySnapshot snapshot = h.Directory.Snapshot;
        snapshot.Stale.Should().BeFalse();
        snapshot.ListFrom.Should().NotBeNull();

        // Fifty were served; three have no antenna connected and are no use to anybody, so
        // forty-seven are listed and every one of them can be picked.
        Instances().Should().HaveCount(50, "the capture is what it is");
        snapshot.Receivers.Should().HaveCount(47);
        snapshot.Receivers.Should().OnlyContain(r => r.Offered);

        DirectoryReceiver dalgety = snapshot.Receivers.Should().ContainSingle(r => r.Host == Dalgety).Subject;
        dalgety.Slug.Should().Be("m9psy-1");
        dalgety.Callsign.Should().Be("M9PSY-1");
        dalgety.Location.Should().Be("Dalgety Bay, Scotland, UK");
        dalgety.Endpoint.Should().Be(new Packet.SoundModem.UberSdr.UberSdrEndpoint(Dalgety, 443, true));
        dalgety.SnrDb.Should().NotBeNull("snr_0_30_mhz is the figure to show, not noise_floor");
        dalgety.MaxClients.Should().BeGreaterThan(0);
        dalgety.PublicUrl.Should().StartWith("https://");

        // The six whose range was never reported are all still here, which is the point of
        // treating an unreported range as "probably fine" rather than as a claim.
        snapshot.Receivers.Select(r => r.Host).Should().Contain("on8st.tunnel.ubersdr.org");
    }

    [Fact]
    public async Task An_Instance_With_No_Tls_Field_Is_Read_As_Plain_Http()
    {
        var h = new Harness();

        await h.RefreshAsync(Capture());

        // Three of the fifty omit "tls" altogether. Absent means plain HTTP, not "unknown" and
        // not "assume the safe thing": these instances answer on port 80 or 8080 and a client
        // that dialled https would simply never reach them.
        foreach (string host in (string[])["pjmarsh.co.uk", "sdr.meucorp.net", "na5b.com"])
        {
            Instance(host)!["tls"].Should().BeNull("the capture is what proves this case exists");
        }

        DirectoryReceiver plain = h.Directory.Snapshot.Receivers
            .Should().ContainSingle(r => r.Host == "pjmarsh.co.uk").Subject;
        plain.Endpoint.Ssl.Should().BeFalse();
        plain.Endpoint.HttpBase.Should().StartWith("http://");
    }

    [Fact]
    public async Task An_Offline_Instance_Is_Not_Offered()
    {
        var h = new Harness();

        await h.RefreshAsync(Edited(Dalgety, i => i["is_online"] = false));

        h.Directory.Snapshot.Receivers.Should().NotContain(r => r.Host == Dalgety);
    }

    [Fact]
    public async Task A_Full_Instance_Is_Listed_But_Not_Offered()
    {
        var h = new Harness();

        await h.RefreshAsync(Edited(Dalgety, i => i["available_clients"] = 0));

        // Listed, unlike everything else the filters catch. A receiver that has simply run out of
        // room is one a visitor may well come back to, and a picker that silently dropped it
        // would read as this site being broken rather than as that receiver being busy.
        DirectoryReceiver full = h.Directory.Snapshot.Receivers
            .Should().ContainSingle(r => r.Host == Dalgety).Subject;
        full.Offered.Should().BeFalse();
        full.Why.Should().Be("full");
        full.AvailableClients.Should().Be(0);
    }

    [Fact]
    public async Task An_Instance_Without_Our_Iq_Mode_Is_Not_Offered()
    {
        var h = new Harness();

        await h.RefreshAsync(Edited(Dalgety, i => i["public_iq_modes"] = new JsonArray("iq192")));

        h.Directory.Snapshot.Receivers.Should().NotContain(r => r.Host == Dalgety);
        h.Directory.Snapshot.Receivers.Should().Contain(
            r => r.Host == Reading, "one instance dropping out is not the others' problem");
    }

    [Fact]
    public async Task An_Instance_Whose_Range_Excludes_The_Window_Is_Not_Offered()
    {
        var h = new Harness();

        // A VHF-only receiver, reporting its range and meaning it.
        await h.RefreshAsync(Edited(Dalgety, i => i["tuning_range"] = new JsonObject
        {
            ["min_frequency"] = 50_000_000,
            ["max_frequency"] = 54_000_000,
            ["reported"] = true,
        }));

        h.Directory.Snapshot.Receivers.Should().NotContain(r => r.Host == Dalgety);
    }

    [Fact]
    public async Task An_Unreported_Tuning_Range_Does_Not_Exclude_An_Instance()
    {
        var h = new Harness();

        // The placeholder the directory fills in when a receiver has not said: 10 kHz to 30 MHz,
        // samprate_source "fallback". It happens to cover 40 m, so to prove the rule the range is
        // moved somewhere that does not - and the instance still has to be listed, because a range
        // whose own flag says nobody measured it is not evidence of anything.
        await h.RefreshAsync(Edited(Dalgety, i => i["tuning_range"] = new JsonObject
        {
            ["min_frequency"] = 50_000_000,
            ["max_frequency"] = 54_000_000,
            ["reported"] = false,
        }));

        h.Directory.Snapshot.Receivers.Should().Contain(r => r.Host == Dalgety);
    }

    [Fact]
    public async Task A_Denied_Host_Is_Never_Offered_Even_If_It_Is_Also_Allowed()
    {
        // Deny wins, and it wins over an allow list naming the same host. This is the mechanism by
        // which an operator who asks not to be listed is not listed, and it must not be possible
        // to defeat it by editing the other list.
        var h = new Harness(allow: [Dalgety, Reading], deny: ["M9PSY-1.Instance.UberSDR.org"]);

        await h.RefreshAsync(Capture());

        h.Directory.Snapshot.Receivers.Should().ContainSingle()
            .Which.Host.Should().Be(Reading, "case is not how a host is told apart");
    }

    [Fact]
    public async Task An_Allow_List_Excludes_Everything_It_Does_Not_Name()
    {
        var h = new Harness(allow: [Dalgety, Reading]);

        await h.RefreshAsync(Capture());

        h.Directory.Snapshot.Receivers.Select(r => r.Host)
            .Should().BeEquivalentTo([Dalgety, Reading]);
    }

    [Fact]
    public async Task Every_Host_In_The_Capture_Gets_Its_Own_Slug()
    {
        var h = new Harness();

        await h.RefreshAsync(Capture());

        IReadOnlyList<DirectoryReceiver> receivers = h.Directory.Snapshot.Receivers;
        receivers.Select(r => r.Slug).Should().OnlyHaveUniqueItems();
        receivers.Should().OnlyContain(r => r.Slug.Length > 0);

        // The rule, on the shapes the capture actually contains: an ubersdr.org tunnel, an
        // ubersdr.org instance, and the fifteen that are somebody's own domain.
        Slug(receivers, "rocksdr.tunnel.ubersdr.org").Should().Be("rocksdr");
        Slug(receivers, Dalgety).Should().Be("m9psy-1");
        Slug(receivers, Reading).Should().Be("reading-ubersdr-m0lte-uk");
        Slug(receivers, "websdr.heppen.be").Should().Be("websdr-heppen-be");
        Slug(receivers, "ubersdr.k3fef.com").Should().Be("ubersdr-k3fef-com");

        // Two hosts whose first label is the same and whose slug is not: the reason the slug is
        // the whole host rather than its first label.
        Slug(receivers, "ubersdr.k1ra.us").Should().Be("ubersdr-k1ra-us");
        h.Errors.Should().BeEmpty("nothing in the capture collides");
    }

    [Fact]
    public async Task A_Slug_Survives_Another_Instance_Appearing()
    {
        var h = new Harness();
        await h.RefreshAsync(Capture());
        h.Directory.Snapshot.Receivers.Single(r => r.Host == Dalgety).Slug.Should().Be("m9psy-1");

        // A station has been built, so somebody may have bookmarked /r/m9psy-1/.
        h.Directory.Bind("m9psy-1", Dalgety);

        // Now a second receiver appears whose host wants the same slug.
        const string Newcomer = "m9psy-1.tunnel.ubersdr.org";
        await h.RefreshAsync(Edited("m9psy.tunnel.ubersdr.org", i => i["host"] = Newcomer));

        IReadOnlyList<DirectoryReceiver> after = h.Directory.Snapshot.Receivers;
        after.Single(r => r.Host == Dalgety).Slug.Should().Be(
            "m9psy-1", "a URL a visitor bookmarked must not move because somebody else appeared");
        after.Single(r => r.Host == Newcomer).Slug.Should().Be("m9psy-1-tunnel-ubersdr-org");
        h.Lines.Should().Contain(l => l.Contains("both") || l.Contains("keeps it"),
            "a slug that had to be broken is worth a line in the journal");
    }

    [Fact]
    public async Task Two_Unbound_Hosts_That_Collide_Both_Take_Their_Full_Host()
    {
        // Nothing is bound yet, so there is no bookmark to protect and neither has a better claim
        // than the other: both fall back, as the plan says, rather than one winning on order.
        var h = new Harness();

        await h.RefreshAsync(Edited("m9psy.tunnel.ubersdr.org", i => i["host"] = "m9psy-1.tunnel.ubersdr.org"));

        IReadOnlyList<DirectoryReceiver> receivers = h.Directory.Snapshot.Receivers;
        receivers.Single(r => r.Host == Dalgety).Slug.Should().Be("m9psy-1-instance-ubersdr-org");
        receivers.Single(r => r.Host == "m9psy-1.tunnel.ubersdr.org").Slug
            .Should().Be("m9psy-1-tunnel-ubersdr-org");
        receivers.Select(r => r.Slug).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_Directory_Outage_Keeps_The_Last_Good_List_And_Says_So()
    {
        var h = new Harness();
        await h.RefreshAsync(Capture());
        int listed = h.Directory.Snapshot.Receivers.Count;

        h.Fail(new HttpRequestException("Connection refused (instances.ubersdr.org:443)"));
        await h.Directory.RefreshAsync(CancellationToken.None);

        DirectorySnapshot snapshot = h.Directory.Snapshot;
        snapshot.Receivers.Should().HaveCount(
            listed, "a directory going away must never take a live session down");
        snapshot.Stale.Should().BeTrue();
        snapshot.ListFrom.Should().NotBeNull("the picker says how old the list it is showing is");
        snapshot.Problem.Should().Contain("Connection refused");
        h.Errors.Should().ContainSingle().Which.Should()
            .Contain("cannot reach").And.Contain("Keeping the");

        // Once per outage, not once per refresh: a directory down for a day would otherwise write
        // 288 identical lines and bury the one that mattered.
        await h.Directory.RefreshAsync(CancellationToken.None);
        h.Errors.Should().ContainSingle();

        // And it says when it comes back, because that is the line an operator looks for.
        await h.RefreshAsync(Capture());
        h.Directory.Snapshot.Stale.Should().BeFalse();
        h.Lines.Should().Contain(l => l.Contains("answering again"));
    }

    [Fact]
    public async Task A_Cold_Start_With_No_Directory_Says_So_Rather_Than_Showing_Nothing()
    {
        var h = new Harness();
        h.Fail(new HttpRequestException("Name or service not known"));

        await h.Directory.StartAsync(CancellationToken.None);

        DirectorySnapshot snapshot = h.Directory.Snapshot;
        snapshot.Receivers.Should().BeEmpty();
        snapshot.ListFrom.Should().BeNull("there has never been a list");
        snapshot.Stale.Should().BeTrue();
        snapshot.Problem.Should().Contain("Name or service not known");
        h.Errors.Should().ContainSingle().Which.Should()
            .Contain("Nothing has been listed yet")
            .And.Contain("will fill in when it answers");
    }

    [Fact]
    public async Task A_Reply_That_Is_Not_The_Directory_Is_An_Outage_Rather_Than_A_Crash()
    {
        // A captive portal, a proxy error page, a redirect to a login: whatever it is, it is not
        // the directory, and the answer is the same as the directory being down.
        var h = new Harness();

        h.Body = "<html><body>502 Bad Gateway</body></html>";
        await h.Directory.RefreshAsync(CancellationToken.None);

        h.Directory.Snapshot.Stale.Should().BeTrue();
        h.Errors.Should().ContainSingle().Which.Should().Contain("cannot reach");
    }

    private static string Slug(IReadOnlyList<DirectoryReceiver> receivers, string host) =>
        receivers.Single(r => r.Host == host).Slug;

    /// <summary>The capture, verbatim.</summary>
    private static string Capture() => File.ReadAllText(FixturePath);

    /// <summary>The capture with one instance's field changed, for a case it does not contain.</summary>
    private static string Edited(string host, Action<JsonObject> edit)
    {
        JsonNode document = JsonNode.Parse(Capture())!;
        JsonObject instance = document["instances"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => (string?)i["host"] == host);
        edit(instance);
        return document.ToJsonString();
    }

    private static JsonArray Instances() =>
        JsonNode.Parse(Capture())!["instances"]!.AsArray();

    private static JsonObject? Instance(string host) => Instances()
        .Select(i => i!.AsObject())
        .SingleOrDefault(i => (string?)i["host"] == host);

    private static string FixturePath => Path.Combine(
        Path.GetDirectoryName(typeof(UberSdrDirectoryTests).Assembly.Location)!,
        "Monitor", "instances.json");

    /// <summary>A directory client with no network behind it and its journal in a list.</summary>
    private sealed class Harness
    {
        private Exception? _failure;

        internal Harness(IEnumerable<string>? allow = null, IEnumerable<string>? deny = null)
        {
            Directory = new UberSdrDirectory(
                new UberSdrDirectoryOptions
                {
                    Url = "https://instances.ubersdr.org/api/instances",
                    IqMode = "iq48",
                    WindowLowHz = WindowLowHz,
                    WindowHighHz = WindowHighHz,
                    Allow = new HashSet<string>(allow ?? [], StringComparer.OrdinalIgnoreCase),
                    Deny = new HashSet<string>(deny ?? [], StringComparer.OrdinalIgnoreCase),
                },
                new StationJournal("", Lines.Add, Errors.Add),
                new FakeTimeProvider(),
                _ => _failure is null
                    ? Task.FromResult(Body)
                    : Task.FromException<string>(_failure));
        }

        internal UberSdrDirectory Directory { get; }

        internal string Body { get; set; } = "";

        internal List<string> Lines { get; } = [];

        internal List<string> Errors { get; } = [];

        internal void Fail(Exception failure) => _failure = failure;

        internal Task RefreshAsync(string body)
        {
            _failure = null;
            Body = body;
            return Directory.RefreshAsync(CancellationToken.None);
        }
    }
}
