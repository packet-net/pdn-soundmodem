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

        // Three receivers listed, one of them full, all three drawn.
        probe.Rows.Should().HaveCount(3);
        string dalgety = probe.Rows.Single(r => r.Contains("M9PSY-1"));
        dalgety.Should().Contain("href=\"/r/m9psy-1/\"", "the row links to that receiver's page")
            .And.Contain("Dalgety Bay, Scotland, UK")
            .And.Contain("RX888 with 40m Full Wave Loop")
            .And.Contain("31 dB", "the receiver's own signal figure")
            .And.Contain("19 of 20", "free slots of total")
            .And.Contain("https://m9psy-1.instance.ubersdr.org/",
                "a link to the receiver's own page: it is somebody else's receiver");

        // A receiver with no room is listed and says so, rather than vanishing and leaving the
        // visitor to wonder whether this site is broken.
        string full = probe.Rows.Single(r => r.Contains("G4EYR"));
        full.Should().Contain("full").And.NotContain("href=\"/r/g4eyr/\"");

        probe.Summary.Should().Contain("2 receivers").And.Contain("1 not available");
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
            3, "a directory that has gone away leaves the last list it gave, which is still true");
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
        int[] PollMs,
        bool Reloaded,
        string[] Thrown);

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
        private readonly CancellationTokenSource _stopping = new();
        private string? _failure;

        private Harness(int port, string? coldFailure)
        {
            _failure = coldFailure;
            Port = port;
            _host = new MonitorHost(new MonitorHostOptions
            {
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
                    ? Task.FromResult(DirectoryJson)
                    : Task.FromException<string>(new HttpRequestException(_failure)),
                OpenInput = (receiver, log, token) => throw new InvalidOperationException(
                    "no receiver is picked in these tests"),
            });
        }

        internal int Port { get; }

        internal static async Task<Harness> StartAsync(string? coldFailure = null)
        {
            var harness = new Harness(FreePort(), coldFailure);
            (await harness._host.StartAsync()).Should().Be(0);
            return harness;
        }

        internal void Fail(string reason) => _failure = reason;

        internal Task RefreshAsync() => _host.RefreshDirectoryAsync();

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            _stopping.Dispose();
        }

        /// <summary>Three receivers: two with room, one full.</summary>
        private const string DirectoryJson = """
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

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
