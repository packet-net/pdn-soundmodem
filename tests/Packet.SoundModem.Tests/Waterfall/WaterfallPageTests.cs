using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The waterfall page's own JavaScript, run against a real <see cref="WaterfallWebServer"/>.
/// </summary>
/// <remarks>
/// <para>Everything else in this suite tests the server. That left a gap the size of the whole
/// browser: the Listen button once shipped completely non-functional while every server-side
/// assertion stayed green, because both defects lived in the page — a socket that wasn't in scope
/// where the click handler needed it, and a 16-bit view onto an odd byte offset, which is illegal
/// and throws. The bytes on the wire were correct throughout.</para>
/// <para>So the page script is executed here, as the browser executes it, by Node. Skipped when
/// Node isn't installed rather than failing: this is a real check where it can run, and not a
/// reason to block a build where it can't.</para>
/// </remarks>
public class WaterfallPageTests
{
    private const int SampleRate = 12000;
    private const double ToneHz = 1000;
    private const float ToneAmplitude = 0.25f;

    [Fact]
    public async Task Listen_Plays_The_Audio_The_Station_Is_Receiving()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var feeding = new CancellationTokenSource();
        Task tone = Task.Run(() => FeedTone(channel, feeding.Token));

        Probe probe = await RunProbeAsync(node, port);
        await feeding.CancelAsync();
        await tone;

        probe.Thrown.Should().BeEmpty("the page must not throw while playing audio");
        probe.Connected.Should().BeTrue("the page must reach the server before anything else can work");
        probe.ClickError.Should().BeNull();

        probe.Listening.Should().Be(true, "one click on Listen must start listening");
        probe.Label.Should().Be("Listening");

        // 40 ms blocks: a couple of seconds of listening is tens of them, not zero and not one.
        probe.BlocksPlayed.Should().BeGreaterThan(20, "audio must actually reach the speakers");

        // The decisive assertion. A block that arrives but is read at the wrong offset, or scaled
        // wrongly, still counts as "played" — only the amplitude proves the samples came through
        // intact, and it is the amplitude that was fed in.
        probe.PeakAmplitude.Should().BeApproximately(
            ToneAmplitude, 0.02, "the tone must arrive at the level it was transmitted at");

        probe.BlocksAfterStop.Should().Be(0, "a second click must stop the audio, not just the label");
        probe.StoppedLabel.Should().Be("Listen");
    }

    /// <summary>
    /// A station identification is tagged onto its burst and listed in the panel, and both say it
    /// is an ident rather than ordinary traffic on the same slot.
    /// </summary>
    /// <remarks>
    /// Page behaviour no server-side assertion can see: the server sends the same <c>frame</c>
    /// message either way and only the <c>id</c> flag differs. The ordinary frame is driven
    /// through the same two functions in the same run, so what an ident does is measured against
    /// what a normal frame does rather than against an assumption.
    /// </remarks>
    [Fact]
    public async Task An_Id_Beacon_Is_Tagged_And_Listed_As_An_Ident()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing frames");
        probe.Connected.Should().BeTrue("the page needs the config before it can tag anything");

        // The waterfall: both tag, and the ident's says so. A ghost has no band of its own, so
        // without the word an ident reads as a station sitting on the slot it identified beside.
        //
        // Two draws each, not one: the page writes every tag twice — once at its place in the
        // scroll ring and once a ring-height up — so a tag straddling the wrap seam is whole on
        // both sides of it. Asserted rather than deduplicated, because one draw would mean that
        // seam handling had been lost.
        probe.OrdinaryTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("M0LTE").And.NotContain("ID"));
        probe.IdentTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("KK4HEJ").And.Contain("ID"));

        // The panel, newest first — the probe logs an ordinary frame, then the ident, then two
        // more for the transmit check, so the ident sits directly above the ordinary one.
        probe.FrameRows.Should().HaveCount(4);
        probe.FrameRows[2].Should().Contain("KK4HEJ").And.Contain("IDENT");
        probe.FrameRows[2].Should().Contain(
            "class=\"id\"", "an ident must be badged, or it reads as ordinary traffic");
        probe.FrameRows[2].Should().Contain(">ID<");
        probe.FrameRows[3].Should().Contain("M0LTE");
        probe.FrameRows[3].Should().NotContain(
            "class=\"id\"", "only idents carry the badge");
    }

    /// <summary>
    /// A frame this station sent is listed in the panel and marked as ours, and — unlike
    /// everything heard — is not tagged onto the waterfall.
    /// </summary>
    /// <remarks>
    /// Both halves are page behaviour the server cannot see: it sends one <c>frame</c> message
    /// either way and only the <c>tx</c> flag differs. The received frame is driven through the
    /// same entry point in the same run, so "listed but not tagged" is measured against what a
    /// heard frame does rather than against an assumption. Not tagged because the burst is
    /// painted from a queue in real time while the event fires as soon as the audio device took
    /// the audio — the tag would land somewhere up the burst rather than on it.
    /// </remarks>
    [Fact]
    public async Task Our_Own_Transmission_Is_Listed_But_Not_Tagged()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing frames");
        probe.Connected.Should().BeTrue("the page needs the config before it can tag anything");

        // Two draws for a heard frame (the tag is written twice, either side of the scroll ring's
        // wrap seam), none at all for ours.
        probe.HeardTag.Should().HaveCount(2).And.AllSatisfy(tag => tag.Should().Contain("GB7RDG"));
        probe.TxTag.Should().BeEmpty("our own burst is drawn in its own style and needs no tag");

        // Newest first, so ours is row 0 and the frame heard just before it is row 1.
        probe.FrameRows[0].Should().Contain("M0LTE-9").And.Contain("GB7RDG").And.Contain(">TX<");
        probe.FrameRowClasses[0].Should().Be(
            "fr tx", "a transmission must be styled apart, or it reads as a station heard");
        probe.FrameRows[1].Should().NotContain(">TX<", "only our own transmissions are marked");
        probe.FrameRowClasses[1].Should().Be("fr");
    }

    private static void FeedTone(SoundModemChannel channel, CancellationToken token)
    {
        var block = new float[512];
        double phase = 0;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = ToneAmplitude * (float)Math.Sin(phase);
                phase += 2 * Math.PI * ToneHz / SampleRate;
            }

            channel.ProcessReceive(block);
            Thread.Sleep(block.Length * 1000 / SampleRate);
        }
    }

    private static async Task<Probe> RunProbeAsync(string node, int port)
    {
        string here = Path.GetDirectoryName(typeof(WaterfallPageTests).Assembly.Location)!;
        var start = new ProcessStartInfo(node)
        {
            ArgumentList = { Path.Combine(here, "Waterfall", "browser", "page-probe.mjs") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PAGE"] = PageOnDisk();
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
    /// The page the server serves is an embedded resource; the probe needs it as a file. Writing
    /// the embedded copy out — rather than reaching into the source tree — keeps this testing what
    /// ships, not what happens to be sitting next to it.
    /// </summary>
    private static string PageOnDisk()
    {
        using Stream? resource = typeof(WaterfallWebServer).Assembly
            .GetManifestResourceStream("Packet.SoundModem.Waterfall.wwwroot.waterfall.html");
        resource.Should().NotBeNull("the page ships embedded in the library");

        string path = Path.Combine(Path.GetTempPath(), $"pdnsm-page-{Environment.ProcessId}.html");
        using (FileStream file = File.Create(path))
        {
            resource!.CopyTo(file);
        }

        return path;
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

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed record Probe(
        bool Connected,
        string? ClickError,
        bool Listening,
        string Label,
        int BlocksPlayed,
        double PeakAmplitude,
        int BlocksAfterStop,
        string StoppedLabel,
        string[] OrdinaryTag,
        string[] IdentTag,
        string[] HeardTag,
        string[] TxTag,
        string[] FrameRows,
        string[] FrameRowClasses,
        string[] Thrown);
}
