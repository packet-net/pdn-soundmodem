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
/// assertion stayed green, because both defects lived in the page - a socket that wasn't in scope
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
        // wrongly, still counts as "played" - only the amplitude proves the samples came through
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
        // Two draws each, not one: the page writes every tag twice - once at its place in the
        // scroll ring and once a ring-height up - so a tag straddling the wrap seam is whole on
        // both sides of it. Asserted rather than deduplicated, because one draw would mean that
        // seam handling had been lost.
        probe.OrdinaryTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("M0LTE").And.NotContain("ID"));
        probe.IdentTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("KK4HEJ").And.Contain("ID"));

        // The panel, newest first: the ident is logged after the ordinary frame, so it sits above.
        int ident = RowWith(probe.FrameRows, "KK4HEJ");
        int ordinary = RowWith(probe.FrameRows, "12.5 dB");
        ident.Should().BeLessThan(ordinary, "the panel is newest first");
        probe.FrameRows[ident].Should().Contain("IDENT")
            .And.Contain("class=\"id\"", "an ident must be badged, or it reads as ordinary traffic")
            .And.Contain(">ID<");
        probe.FrameRows[ordinary].Should().Contain("M0LTE")
            .And.NotContain("class=\"id\"", "only idents carry the badge");
    }

    /// <summary>
    /// A frame this station sent is listed in the panel and marked as ours, and - unlike
    /// everything heard - is not tagged onto the waterfall.
    /// </summary>
    /// <remarks>
    /// Both halves are page behaviour the server cannot see: it sends one <c>frame</c> message
    /// either way and only the <c>tx</c> flag differs. The received frame is driven through the
    /// same entry point in the same run, so "listed but not tagged" is measured against what a
    /// heard frame does rather than against an assumption. Not tagged because the burst is
    /// painted from a queue in real time while the event fires as soon as the audio device took
    /// the audio - the tag would land somewhere up the burst rather than on it.
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

        // Newest first: ours is logged after the frame heard just before it, so it sits above.
        int ours = RowWith(probe.FrameRows, ">TX<");
        int heard = RowWith(probe.FrameRows, "9.5 dB");
        ours.Should().BeLessThan(heard, "the panel is newest first");
        probe.FrameRows[ours].Should().Contain("M0LTE-9").And.Contain("GB7RDG");
        probe.FrameRowClasses[ours].Should().Be(
            "fr tx", "a transmission must be styled apart, or it reads as a station heard");
        probe.FrameRows[heard].Should().NotContain(">TX<", "only our own transmissions are marked");
        probe.FrameRowClasses[heard].Should().Be("fr");
    }

    /// <summary>
    /// The panel opens on the station's logged frames: listed, dimmed apart from live traffic,
    /// never tagged onto the waterfall, and stamped with when they were heard.
    /// </summary>
    /// <remarks>
    /// A panel that starts empty says nothing about a channel that has been busy all morning, and
    /// on a quiet band it is indistinguishable from a modem that is not working. All of this is
    /// page behaviour: the server sends one <c>history</c> message and everything below is what
    /// the page decides to do with it.
    /// </remarks>
    [Fact]
    public async Task The_Panel_Opens_On_The_Stations_Logged_Frames()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a backlog");

        probe.HistoryTag.Should().BeEmpty(
            "a logged frame was heard before the scroll on screen and belongs to no burst on it");

        // The probe drives the backlog last, over a panel that already holds four live rows -
        // which is the reconnect case, where the log by then holds those frames too. Rebuilt, not
        // stacked: three rows, not seven.
        probe.HistoryRows.Should().HaveCount(
            3, "a re-sent backlog rebuilds the panel rather than duplicating it");
        probe.HistoryRowClasses.Should().AllSatisfy(
            row => row.Should().Contain("hist"),
            "a backlog must not read as a channel that is busy right now");

        // Newest on top, as for live frames - the page prepends and the server sends oldest first.
        probe.HistoryRows[0].Should().Contain("GB7RDG-2").And.Contain("EI0RSI-1");
        probe.HistoryRows[1].Should().Contain("GB7BEX-15");

        // Stamped with when it was heard, not when it was shown. Today's frame gets a bare clock
        // time; one from a past year must carry a date too, or it reads as this morning. The
        // date's shape is the viewer's locale's business, so what is asserted is that it is not
        // a bare time - that being the thing that would mislead.
        probe.HistoryRows[0].Should().MatchRegex(@"<span class=""t"">\d\d:\d\d:\d\d</span>");
        probe.HistoryRows[1].Should().NotMatchRegex(@"<span class=""t"">\d\d:\d\d:\d\d</span>");
        probe.HistoryRows[1].Should().MatchRegex(@"<span class=""t"">.*\d\d:\d\d</span>");

        // And the rest of the row still reads as a frame.
        probe.HistoryRows[0].Should().Contain("31 B").And.Contain("+9 Hz");
        probe.HistoryRows[1].Should().Contain("fec 2").And.Contain("crc");
    }

    /// <summary>
    /// A transmission read back out of the log is shown as both: ours, and from before the page
    /// opened.
    /// </summary>
    /// <remarks>
    /// The station logs what it sends as well as what it hears, so a backlog row can carry both
    /// classes - and the two rules set the same <c>border-left</c>, with the history one declared
    /// second. Left to source order the grey would win and a logged transmission would arrive
    /// dimmed and unmarked down the edge, reading as a station heard. The colour asserted here is
    /// resolved out of the shipping stylesheet the way a browser resolves it, by specificity then
    /// source order, because the class list alone cannot say which rule won.
    /// </remarks>
    [Fact]
    public async Task A_Logged_Transmission_Is_Shown_As_Both_Ours_And_History()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a backlog");

        // Oldest last in a newest-first panel: the transmitted row is the one at the bottom.
        probe.HistoryRows[2].Should().Contain("M0LTE").And.Contain(">TX<",
            "a logged transmission carries the same badge a live one does");
        probe.HistoryRowClasses[2].Should().Be("fr tx hist");
        probe.HistoryRows[0].Should().NotContain(">TX<", "only our own frames are marked");

        probe.TxBorder.Should().Contain("#31d2f2", "cyan is what says a row is ours");
        probe.HistBorder.Should().NotContain("#31d2f2", "and grey is what says it is old");
        probe.TxHistBorder.Should().Contain(
            "#31d2f2", "a backlogged transmission must not lose the colour that says it is ours");
    }

    /// <summary>
    /// The survey on the page: a capture tagged where it happened and listed with its audio, and
    /// the status strip that says what a budget has been refusing.
    /// </summary>
    [Fact]
    public async Task A_Capture_Is_Tagged_Where_It_Happened_And_Listed_With_Its_Audio()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a capture");

        // Drawn twice, like every tag, so one straddling the scroll ring's wrap seam is whole on
        // both sides of it - and saying what it is and how wide, which is what a classifier wants
        // and what a list of filenames could never put on the frequency axis.
        probe.CaptureTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("unclaimed").And.Contain("400 Hz"));

        int capture = RowWith(probe.FrameRows, "unclaimed");
        probe.FrameRows[capture].Should()
            .Contain("/survey/20260804-151909-1144hz-unclaimed.wav")
            .And.Contain("/survey/20260804-151909-1144hz-unclaimed.json", "the sidecar too");
        probe.FrameRowClasses[capture].Should().Be("fr cap");

        // The refusals are the reason the strip exists - an operator has no other way to see them.
        probe.SurveyStatus.Should().Contain("7").And.Contain("2 skipped").And.Contain("12 MB");
    }

    /// <summary>
    /// A frame that decoded and would not yield callsigns explains itself in the panel - which is
    /// where an operator sees the word "unattributed" in the first place.
    /// </summary>
    [Fact]
    public async Task An_Unreadable_Frame_Shows_Its_Reason_And_Its_Bytes()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();

        string row = probe.FrameRows[RowWith(probe.FrameRows, "il2p Type1")];
        row.Should().Contain("il2p Type1").And.Contain("byte 0 of the destination callsign");
        row.Should().Contain("00010203DEADBEEF", "the bytes are what an operator copies out");

        // The reason quotes a character taken straight off the air. A payload that happens to
        // contain "<" is not markup, and a panel built with innerHTML has to say so.
        row.Should().NotContain("('<')", "the quoted character must arrive escaped");
        row.Should().Contain("&lt;");
    }

    /// <summary>
    /// A frame read as plain IL2P is badged in the panel, whether or not the modem was told to
    /// pass such frames to the host - because it is standing on Reed-Solomon alone either way.
    /// </summary>
    /// <remarks>
    /// <para>The row's mode column says <c>bpsk300-il2pc</c> and its decode had no CRC behind it at
    /// all, and the panel is the only place an operator sees both. Without the badge the two rows
    /// are indistinguishable from ordinary traffic, which is the state that let a CRC-less
    /// neighbour sit unread on the 40 m slot for weeks.</para>
    /// <para>The colours come out of the shipping stylesheet, resolved the way a browser resolves
    /// them (see <c>badgeBackground</c> in the probe), because the badge shares its box with the
    /// ident badge and the transmit rule also claims <c>.id</c> - so "does the warning colour
    /// actually win" is a real question and not one a stub could be trusted to answer.</para>
    /// </remarks>
    [Fact]
    public async Task A_Plain_Il2p_Frame_Is_Badged_As_Reed_Solomon_Only()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();

        string withheld = probe.FrameRows[RowWith(probe.FrameRows, "GB7BPQ")];
        string delivered = probe.FrameRows[RowWith(probe.FrameRows, "PD4R-11")];
        foreach (string row in (string[])[withheld, delivered])
        {
            row.Should().Contain("class=\"id rs\"", "the badge is about the decode, not the routing")
                .And.Contain(">RS ONLY<");
        }

        // The half only the tooltip can carry: what the operator's own configuration did with it.
        withheld.Should().Contain("NOT passed to the KISS host")
            .And.Contain("acceptPlainIl2p", "and how to change that");
        delivered.Should().Contain("Passed to the KISS host")
            .And.NotContain("NOT passed");

        // Ordinary traffic keeps the panel it had.
        probe.FrameRows[RowWith(probe.FrameRows, "12.5 dB")].Should().NotContain("RS ONLY");

        // And the stylesheet: the warning colour, distinct from the ident badge's, and surviving
        // on a row that also matches the transmit rule.
        probe.RsBadgeBackground.Should().Be("#f0b45a");
        probe.IdentBadgeBackground.Should().NotBe(
            probe.RsBadgeBackground, "an RS-only frame is not an ident and must not look like one");
        probe.RsBadgeOnTxRowBackground.Should().Be(
            "#f0b45a", "the warning must not be repainted by a rule about transmissions");
    }

    /// <summary>
    /// The transmit readout holds the last transmission's figures, and says - in words as well as
    /// in colour - whether what it is showing is live or held.
    /// </summary>
    /// <remarks>
    /// Page behaviour end to end: the server sends the same message shape either way and only
    /// <c>keyed</c> differs. The reason the readout exists at all is that a packet burst is a
    /// fraction of a second and the gaps between bursts are minutes, so the figures have to
    /// survive key-up - and the moment they do, the page has to say they are not live, or an
    /// operator reads a held 29 W as a transmitter that is still up.
    /// </remarks>
    [Fact]
    public async Task The_Transmit_Readout_Holds_The_Last_Burst_And_Says_That_It_Is_Held()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a transmit reading");

        // Keyed: the figures as the radio reports them, and a readout wearing the live class.
        probe.TxKeyed!.Hidden.Should().BeFalse("a reading makes the readout appear");
        probe.TxKeyed.When.Should().Be("Transmitting");
        probe.TxKeyed.Power.Should().Be("29.4");
        probe.TxKeyed.Swr.Should().Be("1.2");
        probe.TxKeyed.SwrHidden.Should().BeFalse();
        probe.TxKeyed.ClassName.Should().Contain("live");
        probe.TxKeyed.SwrClass.Should().NotContain("alarm");

        // The one reading an operator has to act on, in the state they are most likely to see it.
        probe.TxKeyedBadSwr!.SwrClass.Should().Contain(
            "alarm", "an SWR of 3.1 has to look different from an SWR of 1.2");

        // Key-up: the figures stay, and everything about the readout says they are from before.
        probe.TxHeld!.Hidden.Should().BeFalse("the readout survives key-up - that is the point");
        probe.TxHeld.Power.Should().Be("27.5", "the transmission's average is what is held");
        probe.TxHeld.Swr.Should().Be("1.4");
        probe.TxHeld.ClassName.Should().NotContain(
            "live", "a held reading must not be wearing the transmitting colour");
        probe.TxHeld.When.Should().StartWith("Last TX").And.Contain(
            ":", "a held reading says when it was taken, or it reads as one from just now");

        // A radio with no SWR to report shows no SWR, rather than a dash that reads as 1:1.
        probe.TxHeldNoSwr!.SwrHidden.Should().BeTrue();
        probe.TxHeldNoSwr.Power.Should().Be("27.5");

        // And the readout starts hidden, in a way that survives .ctl's own display rule - which
        // is more specific than the browser's rule for [hidden], so without a rule of its own the
        // header opens showing a transmit readout full of dashes.
        using var pageFile = new PageFile();
        string page = await File.ReadAllTextAsync(pageFile.FullName);
        page.Should().Contain("id=\"txReadout\" hidden")
            .And.Contain(".ctl[hidden] { display: none; }");
    }

    /// <summary>
    /// Each modem's label says whether the node software is attached to a KISS port that reaches
    /// it, and follows the client coming and going.
    /// </summary>
    /// <remarks>
    /// A host that quietly dropped its TCP session stops passing traffic, and from the modem's
    /// side that is indistinguishable from a band that went quiet - the one journal line that
    /// said so scrolled past hours ago. The badge is per modem rather than per port because a
    /// modem is reachable through both its own dedicated port and the multiplexed one, and what
    /// the operator is asking is "can anything get to this modem", not "which socket".
    /// </remarks>
    [Fact]
    public async Task A_Modem_Label_Says_Whether_A_Host_Is_Attached()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        // Set before the browser arrives, and never changed again: this is the state a page opened
        // at any moment other than a connect or a disconnect has to be given, and the whole reason
        // the server carries it rather than only broadcasting the events.
        server.SetHostPorts([new HostPortStatus(8105, null, 1), new HostPortStatus(8101, 0, 0)]);

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a host-port snapshot");

        probe.ChipsOnArrival.Should().ContainSingle();
        probe.ChipsOnArrival[0].Should().Contain(
            "1 host", "the handshake's snapshot has to reach the labels with nothing else happening");

        probe.ChipsAttached.Should().ContainSingle("the station has one modem");
        probe.ChipsAttached[0].Should().Contain("2 hosts")
            .And.Contain("class=\"host on\"", "attached is the state that reads as good")
            .And.Contain("8105 (all modems): 1 connected", "the tooltip says which port")
            .And.Contain("8101 (this modem): 1 connected");

        // And it follows them out again, which is the state worth noticing.
        probe.ChipsDetached.Should().ContainSingle();
        probe.ChipsDetached[0].Should().Contain("no host")
            .And.Contain("class=\"host\"", "nothing attached must not be wearing the good colour")
            .And.Contain("nothing connected");

        // The rest of the label is untouched: the badge is an addition, not a replacement.
        probe.ChipsDetached[0].Should().Contain("AFSK1200").And.Contain("1723 Hz");
    }

    /// <summary>
    /// The AX.25 links pane: frames grouped by the pair of stations exchanging them, each card
    /// saying in words what the last frame did, and a frame sent for the second time marked so
    /// that it cannot be missed.
    /// </summary>
    /// <remarks>
    /// The server sends one <c>links</c> message on connect and one <c>link</c> message per frame,
    /// and everything asserted here is what the page makes of them: which card a line lands on,
    /// what the card's header and figures say, and that the resend carries its tag. None of it
    /// is visible to a server-side test, and "a retry is obvious" was the request.
    /// </remarks>
    [Fact]
    public async Task The_Links_Pane_Groups_Frames_By_Link_And_Makes_A_Resend_Obvious()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePort();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while building link cards");
        probe.Connected.Should().BeTrue("the page needs the config to label a card with its modem");

        // The pane is closed until asked for, and the button says what is going on behind it:
        // one link up, and amber because that link is waiting on an answer.
        probe.LinksHiddenBefore.Should().BeTrue("the pane is on demand, not always there");
        probe.LinksHiddenAfter.Should().BeFalse();
        probe.LinksOnArrival.N.Should().Be("1", "one of the two pairs is a link that is up");
        probe.LinksOnArrival.Summary.Should().Contain("2 pairs heard").And.Contain("1 waiting on an answer");
        probe.LinksOnArrival.ButtonClass.Should().Contain("alert", "an unanswered poll is the thing worth looking up for");
        probe.LinksAfterEvent.ButtonClass.Should().NotContain("alert", "the poll has been answered");
        probe.LinksAfterEvent.ButtonClass.Should().Contain("on", "the pane is open");

        // One card per pair, the live one first regardless of the order they arrived in.
        probe.LinkCards.Should().HaveCount(2, "two pairs of stations were heard");
        LinkCard live = probe.LinkCards[0];
        LinkCard beacon = probe.LinkCards[1];
        live.ClassName.Should().Be("lk live", "a link that is up must be styled apart from a beacon");
        live.Head.Should().Contain("M0LTE-9").And.Contain("GB7RDG-2").And.Contain(">connected<")
            .And.Contain("AFSK1200", "the card says which modem the link is on");
        live.Stats.Should().Contain("1 resend", "the figures count what went wrong")
            .And.Contain("1 unacknowledged");
        live.ConcernHidden.Should().BeTrue("the concern went away with the answer");
        beacon.ClassName.Should().Contain("idle", "nothing has been heard on it for hours");
        beacon.Head.Should().Contain("GB7BEX").And.Contain(">no link<");
        beacon.Stats.Should().Contain("GB7BEX").And.NotContain(">ID<", "a beacon's addressee never sends anything");

        // The feed reads as a transcript: oldest first, worded, and the link coming up in green.
        live.Feed.Should().HaveCount(4);
        live.Feed[0].Html.Should().Contain("calls GB7RDG-2");
        live.Feed[1].ClassName.Should().Be("ln tx up", "the node's answer was ours, and brought the link up");
        live.Feed[2].Html.Should().Contain("hello &lt;node&gt;",
            "text off the air is quoted, and escaped, because a payload is not markup");

        // The decisive line: the resend, tagged as such, with what it carried under it.
        LinkFeedLine resend = live.Feed[3];
        resend.ClassName.Should().Be("ln tx");
        resend.Html.Should().Contain("resends #0")
            .And.Contain("class=\"tag resend\"", "a retry has to be obvious, not inferred from a sequence number")
            .And.Contain(">RESEND<")
            .And.Contain("Welcome to GB7RDG-2");

        // And a beacon through a digipeater says which one.
        beacon.Feed.Should().ContainSingle().Which.Html.Should().Contain("beacon").And.Contain("via MB7UXX*");
    }

    /// <summary>
    /// Finds a panel row by something only it contains. Rows were addressed by index, which made
    /// every test depend on how many other things the probe happened to drive first - adding one
    /// step broke two unrelated tests. Order still matters and is asserted where it means
    /// something (newest first), but identity is by content.
    /// </summary>
    private static int RowWith(string[] rows, string marker)
    {
        int at = Array.FindIndex(rows, row => row.Contains(marker, StringComparison.Ordinal));
        at.Should().BeGreaterThanOrEqualTo(0, "no panel row contains \"{0}\"", marker);
        return at;
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
    /// The page the server serves is an embedded resource; the probe needs it as a file. Writing
    /// the embedded copy out - rather than reaching into the source tree - keeps this testing what
    /// ships, not what happens to be sitting next to it.
    /// </summary>
    /// <remarks>
    /// Unique per instance, and deleted as soon as it has been used. The name used to be
    /// pdnsm-page-{pid}.html, which is two faults in one line. Nothing ever removed the files, so
    /// they piled up: 81 of them in /tmp on the dev box. And a leftover from a dead process of
    /// another Unix account, on a box that hosts more than one self-hosted runner, is a file this
    /// process cannot open, which surfaces as "Permission denied" on a path the test has every
    /// right to write. That is exactly how issue #349 blocked the v0.43.0 release from a
    /// neighbouring test. Nothing here is worth keeping after a failure either: these bytes are
    /// the shipped page, identical every run, and they are in the assembly.
    /// </remarks>
    private sealed class PageFile : IDisposable
    {
        public PageFile()
        {
            using Stream? resource = typeof(WaterfallWebServer).Assembly
                .GetManifestResourceStream("Packet.SoundModem.Waterfall.wwwroot.waterfall.html");
            resource.Should().NotBeNull("the page ships embedded in the library");

            FullName = Path.Combine(
                Path.GetTempPath(), $"pdnsm-page-{Guid.NewGuid():N}.html");
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

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The links button and the pane's summary line, as the page left them.</summary>
    private sealed record LinksButton(string N, string Summary, string ButtonClass);

    private sealed record LinkFeedLine(string ClassName, string Html);

    /// <summary>One card of the links pane: its parts as built, and its feed line by line.</summary>
    private sealed record LinkCard(
        string ClassName,
        string Head,
        string Stats,
        string Concern,
        bool ConcernHidden,
        LinkFeedLine[] Feed);

    /// <summary>One state of the header's transmit readout, as the page left it.</summary>
    private sealed record TxReadout(
        string ClassName,
        bool Hidden,
        string When,
        string Power,
        string Swr,
        bool SwrHidden,
        string SwrClass);

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
        string[] CaptureTag,
        string SurveyStatus,
        string[] HistoryTag,
        string[] HistoryRows,
        string[] HistoryRowClasses,
        string? TxHistBorder,
        string? TxBorder,
        string? HistBorder,
        string? RsBadgeBackground,
        string? IdentBadgeBackground,
        string? RsBadgeOnTxRowBackground,
        TxReadout? TxKeyed,
        TxReadout? TxKeyedBadSwr,
        TxReadout? TxHeld,
        TxReadout? TxHeldNoSwr,
        string[] ChipsOnArrival,
        string[] ChipsAttached,
        string[] ChipsDetached,
        bool LinksHiddenBefore,
        bool LinksHiddenAfter,
        LinksButton LinksOnArrival,
        LinksButton LinksAfterEvent,
        LinkCard[] LinkCards,
        string[] Thrown);
}
