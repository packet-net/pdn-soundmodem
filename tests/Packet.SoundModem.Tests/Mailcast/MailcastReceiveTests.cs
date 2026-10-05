using System.Collections.Concurrent;
using Packet.Mailcast;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// The built-in receiver end to end, without a radio: a slot rendered from Packet.Mailcast's own
/// scheduler and modulated with the MS110D modem, heard on a station's channel beside its own
/// modem, rebuilt in the store and forwarded into a fake FBB BBS. Every clock is a fake one; the
/// only real waiting is for the fake BBS's socket, bounded by a count of looks, not by a time.
/// </summary>
public sealed class MailcastReceiveTests : IAsyncDisposable
{
    private readonly ScratchDirectory _dir = new("pdnsm-mailcast");
    private readonly FakeTimeProvider _time = new(MailcastSlotAudio.Noon);
    private readonly ConcurrentQueue<string> _journal = new();
    private readonly FakeFbbBbs _bbs = new();
    private readonly List<MailcastReceiver> _receivers = [];
    private readonly CancellationTokenSource _stop = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (MailcastReceiver receiver in _receivers)
        {
            await receiver.DisposeAsync();
        }

        await _bbs.DisposeAsync();
        _dir.Dispose();
    }

    private MailcastConfig Config() => new()
    {
        Bbs = new MailcastBbsConfig { Host = "127.0.0.1", Port = _bbs.Port, Password = _bbs.Password },
    };

    private MailcastReceiver Receiver(double centreHz = 1800, TimeSpan? longestBurst = null)
    {
        var placement = new MailcastPlacement(false, centreHz, centreHz - 1450, centreHz + 1450);
        MailcastReceiver receiver = MailcastReceiver.Create(
            Config(), placement, MailcastSlotAudio.Rate, _dir.FullName, _time, _journal.Enqueue,
            longestBurst: longestBurst);
        _receivers.Add(receiver);
        return receiver;
    }

    /// <summary>Feeds audio in 100 ms blocks, moving the fake clock with it.</summary>
    private void Play(Action<ReadOnlySpan<float>> process, float[] audio)
    {
        const int Block = MailcastSlotAudio.Rate / 10;
        for (int at = 0; at < audio.Length; at += Block)
        {
            process(audio.AsSpan(at, Math.Min(Block, audio.Length - at)));
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (int look = 0; look < 4000 && !condition(); look++)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue(what);
    }

    [Fact]
    public async Task A_Slot_Heard_On_The_Stations_Channel_Is_Rebuilt_And_Forwarded_Into_The_Bbs()
    {
        IReadOnlyList<byte[]> frames = MailcastSlotAudio.Frames();
        float[] audio = MailcastSlotAudio.Render(frames);

        // The station's own modem on sub-channel 0, and the receiver beside it as a tap.
        var channel = new SoundModemChannel(MailcastSlotAudio.Rate);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", MailcastSlotAudio.Rate, sink));
        var toHosts = new ConcurrentQueue<byte[]>();
        channel.FrameReceived += (_, frame) => toHosts.Enqueue(frame);
        MailcastReceiver receiver = Receiver();
        receiver.Attach(channel);

        Play(channel.ProcessReceive, audio);
        await receiver.Intake.DrainAsync(CancellationToken.None);

        receiver.Intake.FramesHeard.Should().Be(frames.Count, "every burst decodes at 20 dB");
        toHosts.Should().BeEmpty("the mailcast modem is a tap with no KISS sub-channel, so nothing it hears reaches a host");
        receiver.Intake.Pending().Select(b => b.Bid).Should().BeEquivalentTo(["1001_GB7ABC", "2002_GB7XYZ"]);
        MailcastSlotSummary last = receiver.Slots.Last!;
        last.Slot.Should().Be(MailcastSlotAudio.Noon);
        last.Frames.Should().Be(frames.Count);
        last.Tone!.OffsetHz.Should().BeInRange(-1, 1);
        last.Tone.SnrDb.Should().BeGreaterThan(15);

        _ = receiver.RunAsync(_stop.Token);
        await Eventually(() => _bbs.Taken.Count == 2 && receiver.Intake.Pending().Count == 0, "both bulletins forwarded");

        _bbs.Logins.Should().AllSatisfy(login => login.Should().Equal("Q0CAST", "secret", "BBS"));
        _bbs.Taken.Select(t => t.Title).Should().BeEquivalentTo(["Net tonight", "For sale: a TS-50"]);
        _bbs.Taken.Single(t => t.Bid == "1001_GB7ABC").Body.Should().Contain("The net is at 1930 on 145.500 tonight.");
        receiver.Ledger.Delivered.Should().Be(2);
        File.ReadAllLines(Path.Combine(_dir.FullName, "deliveries.jsonl")).Should().HaveCount(2);
        _journal.Should().Contain(line => line.StartsWith("mailcast: tone ", StringComparison.Ordinal));
        _journal.Should().Contain(line => line.Contains("1001_GB7ABC accepted by the BBS"));
        _journal.Should().NotContain(line => line.Contains("secret"), "the password is never logged");
        _journal.Should().AllSatisfy(line => line.All(c => c is >= ' ' and <= '~').Should().BeTrue(line));

        string status = receiver.Status().ToJsonString();
        status.Should().NotContain("secret");
        status.Should().Contain("\"delivered\":2");
        status.Should().Contain("\"placement\":\"passband\"");
    }

    [Fact]
    public async Task A_Signal_Higher_In_A_Wide_Passband_Is_Heard_Where_The_Dial_Puts_It()
    {
        // A Flex slice on 7.0501 MHz puts the signal's centre at 3700 Hz.
        IReadOnlyList<byte[]> frames = MailcastSlotAudio.Frames(MailcastSlotAudio.Bulletins().Take(1).ToList());
        float[] audio = MailcastSlotAudio.Render(frames, centreHz: 3700, toneOffsetHz: 2.5);
        MailcastReceiver receiver = Receiver(centreHz: 3700);

        Play(receiver.Process, audio);
        await receiver.Intake.DrainAsync(CancellationToken.None);

        receiver.Intake.FramesHeard.Should().Be(frames.Count);
        receiver.Intake.Pending().Should().ContainSingle().Which.Bid.Should().Be("1001_GB7ABC");
        receiver.Slots.Last!.Tone!.OffsetHz.Should().BeInRange(1.5, 3.5);
    }

    [Fact]
    public async Task Rebuilt_Bulletins_Are_Kept_On_Disk_And_Delivered_After_A_Restart()
    {
        IReadOnlyList<byte[]> frames = MailcastSlotAudio.Frames();
        MailcastReceiver first = Receiver();
        foreach (byte[] frame in frames)
        {
            first.Intake.Offer(frame);
        }

        await first.Intake.DrainAsync(CancellationToken.None);
        first.Intake.Pending().Should().HaveCount(2);
        await first.DisposeAsync();
        _receivers.Remove(first);

        // The station restarts: the outbox is still there, and goes to the BBS at once.
        MailcastReceiver second = Receiver();
        second.Intake.Pending().Should().HaveCount(2);
        _ = second.RunAsync(_stop.Token);
        await Eventually(() => second.Intake.Pending().Count == 0, "the outbox delivered after the restart");
        _bbs.Taken.Should().HaveCount(2);
        _journal.Should().Contain("mailcast: 2 rebuilt bulletins waiting for the BBS from before");
    }

    [Fact]
    public async Task A_Bulletin_The_Bbs_Already_Has_Leaves_The_Outbox_And_Mail_It_Offers_Back_Is_Left_With_It()
    {
        _bbs.Known["1001_GB7ABC"] = true;
        _bbs.Queued.Add(new Packet.Fbb.FbbOutboundMessage
        {
            MessageType = 'P', From = "G8ABC", AtBbs = "GB7TST", To = "Q0CAST", Bid = "9_GB7TST", Title = "hello",
            Body = "R:261005/1100Z 9@GB7TST\r\n\r\nhi\r\n"u8.ToArray(),
        });
        MailcastReceiver receiver = Receiver();
        foreach (byte[] frame in MailcastSlotAudio.Frames())
        {
            receiver.Intake.Offer(frame);
        }

        await receiver.Intake.DrainAsync(CancellationToken.None);
        _ = receiver.RunAsync(_stop.Token);
        await Eventually(() => receiver.Intake.Pending().Count == 0 && !_bbs.ReverseAnswers.IsEmpty, "a session ran");

        _bbs.Taken.Select(t => t.Bid).Should().Equal("2002_GB7XYZ");
        _bbs.ReverseAnswers.Should().Equal([Packet.Fbb.FsAnswerKind.Defer], "the receiver never takes mail, and never says it has it");
        _journal.Should().Contain(line => line.Contains("1001_GB7ABC rejected by the BBS: it already has this BID"));
        _journal.Should().Contain(line => line.Contains("WARNING - the BBS tried to send the receiver's login 1 message(s)"));
    }

    [Fact]
    public async Task A_Bbs_That_Cannot_Be_Reached_Keeps_The_Bulletins_And_Says_Why()
    {
        var unreachable = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        unreachable.Start();
        int port = ((System.Net.IPEndPoint)unreachable.LocalEndpoint).Port;
        unreachable.Stop();
        var placement = new MailcastPlacement(false, 1800, 350, 3250);
        MailcastReceiver receiver = MailcastReceiver.Create(
            new MailcastConfig { Bbs = new MailcastBbsConfig { Port = port, Password = "secret" } },
            placement, MailcastSlotAudio.Rate, _dir.FullName, _time, _journal.Enqueue);
        _receivers.Add(receiver);
        foreach (byte[] frame in MailcastSlotAudio.Frames())
        {
            receiver.Intake.Offer(frame);
        }

        await receiver.Intake.DrainAsync(CancellationToken.None);
        _ = receiver.RunAsync(_stop.Token);
        await Eventually(() => receiver.Delivery.NextAttempt is not null, "the session failed and a retry is set");

        receiver.Intake.Pending().Should().HaveCount(2, "nothing leaves the outbox until the BBS has answered for it");
        receiver.Status()["bbs"]!["state"]!.GetValue<string>().Should().Be("failing");
        receiver.Delivery.NextAttempt.Should().Be(_time.GetUtcNow() + MailcastDelivery.Backoff[0]);
        _journal.Should().Contain(line => line.StartsWith("mailcast: bbs: ", StringComparison.Ordinal) && line.EndsWith("Trying again in 30 s.", StringComparison.Ordinal));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task An_Outbox_That_Cannot_Be_Written_Is_A_Failure_Waited_Out_Not_A_Tight_Loop()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "read-only folders are set with Unix modes");
        Assert.SkipWhen(Environment.UserName == "root", "root can delete from a read-only folder");
        MailcastReceiver receiver = Receiver();
        foreach (byte[] frame in MailcastSlotAudio.Frames())
        {
            receiver.Intake.Offer(frame);
        }

        await receiver.Intake.DrainAsync(CancellationToken.None);
        string outbox = Path.Combine(_dir.FullName, "store", "outbox");
        File.SetUnixFileMode(outbox, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _ = receiver.RunAsync(_stop.Token);
            await Eventually(() => receiver.Delivery.NextAttempt is not null, "the session ended and a retry is set");

            receiver.Delivery.NextAttempt.Should().Be(_time.GetUtcNow() + MailcastDelivery.Backoff[0]);
            receiver.Delivery.LastFailure.Should().Contain("out of the outbox");
            receiver.Intake.Pending().Should().HaveCount(2);
            _bbs.Logins.Should().ContainSingle("nothing is offered again until the wait is over");
            _journal.Should().Contain(line => line.StartsWith("mailcast: WARNING - cannot take ", StringComparison.Ordinal)
                && line.EndsWith("Trying again in 30 s.", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(outbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Once the store can be written again, the next session clears it: the BBS has them.
        _time.Advance(MailcastDelivery.Backoff[0]);
        await Eventually(() => receiver.Intake.Pending().Count == 0, "the outbox cleared after the wait");
        _bbs.Taken.Should().HaveCount(2, "the BBS answered FS - the second time, and took nothing twice");
    }

    [Fact]
    public async Task Other_Stations_Frames_Are_Not_Taken_For_Mailcast()
    {
        MailcastReceiver receiver = Receiver();
        byte[] payload = MailcastSlotAudio.Frames()[0][16..];

        receiver.Intake.Offer(MailcastSlotAudio.Ui("G8ABC", "MCAST", payload)).Should().BeFalse("only GB7RDG sends it");
        receiver.Intake.Offer(MailcastSlotAudio.Ui("GB7RDG", "CQ", payload)).Should().BeFalse();
        receiver.Intake.Offer(MailcastSlotAudio.Ui("GB7RDG", "MCAST", payload)).Should().BeTrue();
        await receiver.Intake.DrainAsync(CancellationToken.None);
        receiver.Intake.FramesHeard.Should().Be(1);
    }

    [Fact]
    public void A_Lock_That_Outlasts_Any_Burst_Is_Let_Go()
    {
        // A real burst, but a limit far shorter than it: the modem is locked on it for longer
        // than the limit, which is what a lock on noise after a weak preamble looks like.
        float[] audio = MailcastSlotAudio.Render(MailcastSlotAudio.Frames().Take(1).ToList(), snrDb: 30);
        MailcastReceiver receiver = Receiver(longestBurst: TimeSpan.FromMilliseconds(500));

        Play(receiver.Process, audio);

        receiver.LocksReleased.Should().BeGreaterThan(0);
        _journal.Should().Contain(line => line.Contains("longer than any GB7RDG sends") && line.Contains("listening afresh"));
    }

    [Fact]
    public void A_Tone_Away_From_A_Slots_Start_Is_Not_Taken_As_GB7RDGs()
    {
        var slots = new MailcastSlots(_time, _journal.Enqueue, null);
        _time.SetUtcNow(MailcastSlotAudio.Noon.AddMinutes(20));

        slots.OnTone(new MailcastTone(1801, 1, 12, TimeSpan.FromSeconds(10)));

        slots.Last.Should().BeNull();
        _journal.Should().Contain(line => line.Contains("not at a slot's start") && line.Contains("ignored"));
    }

    [Fact]
    public void The_Timetable_Is_Gb7rdgs_Until_Its_Directory_Says_Otherwise()
    {
        var slots = new MailcastSlots(_time, _journal.Enqueue, null);
        slots.Timetable.Should().Be(MailcastOnAir.DefaultTimetable);
        slots.FromDirectory.Should().BeFalse();
        MailcastSlots.Describe(slots.Timetable).Should().Be(
            "every hour on the hour, in daylight from 120 minutes after sunrise to 30 minutes before sunset at IO91lk");
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero));
        slots.Next.Should().Be(new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero));

        var every30 = new SlotTimetable(new TimeOnly(0, 15), 30, null);
        slots.Heard(every30);

        slots.Timetable.Should().Be(every30);
        slots.FromDirectory.Should().BeTrue();
        slots.Next.Should().Be(new DateTimeOffset(2026, 10, 5, 12, 45, 0, TimeSpan.Zero));
        _journal.Should().Contain("mailcast: GB7RDG's directory gives its slots as every 30 minutes from 00:15 UTC; using that");
    }
}
