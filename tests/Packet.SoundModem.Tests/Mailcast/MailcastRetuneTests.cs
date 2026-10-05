using System.Collections.Concurrent;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Tests.Channel;
using Packet.SoundModem.Tests.Rig;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// Retuning the rig around GB7RDG's slots, against <see cref="FakeRigctld"/>: when the windows
/// open and close, that every source of transmission the station has is held while the rig is on
/// the mailcast dial, and a station restarted in the middle of a window. The rig and the retuner
/// run on a fake clock; the channel's own waits are real but never what an assertion depends on.
/// </summary>
public sealed class MailcastRetuneTests : IAsyncDisposable
{
    // 11:58 UTC on 5 October: the noon slot is in daylight at IO91lk.
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 11, 58, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Slot = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly RigTuning Plan = new(7_049_450, "USB", 2400);

    private readonly FakeRigctld _fake = new(dialHz: 7_049_450, mode: "USB", passbandHz: 2400);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ConcurrentQueue<string> _journal = new();
    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-retune");
    private readonly List<RigControl> _rigs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _running = [];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (Task task in _running)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (RigControl rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        await _fake.DisposeAsync();
        _dir.Dispose();
    }

    private string RestorePath => Path.Combine(_dir.FullName, RigRestoreFile.NameFor(_fake.Endpoint));

    private async Task<RigControl> StartedRig()
    {
        var rig = new RigControl(new RigControlOptions
        {
            Endpoint = _fake.Endpoint,
            Time = _time,
            Plan = Plan,
            RestoreFile = RestorePath,
        });
        rig.Journal += _journal.Enqueue;
        rig.Problem += _journal.Enqueue;
        _rigs.Add(rig);
        (await rig.StartAsync(CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    private readonly List<MailcastRetuner> _retuners = [];

    private MailcastRetuner Retuner(RigControl rig, string? rigMode = null)
    {
        var config = new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Retune = true };
        var retuner = new MailcastRetuner(rig, config, rigMode, () => MailcastOnAir.DefaultTimetable, _time, _journal.Enqueue);
        _retuners.Add(retuner);
        _running.Add(Task.Run(() => retuner.RunAsync(_stop.Token)));
        return retuner;
    }

    /// <summary>
    /// Moves the fake clock on to <paramref name="until"/> in steps of at most 5 s, and never past
    /// a retuner's next timer until it has acted on that one and set the next: so a renewal always
    /// lands before the rig's own five-minute window could run out, however slowly the machine
    /// is running.
    /// </summary>
    private async Task AdvanceTo(DateTimeOffset until)
    {
        foreach (MailcastRetuner retuner in _retuners)
        {
            await Eventually(() => retuner.NextWake > DateTimeOffset.MinValue, "the retuner has set its first timer");
        }

        while (_time.GetUtcNow() < until)
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset next = now + TimeSpan.FromSeconds(5);
            foreach (MailcastRetuner retuner in _retuners)
            {
                if (retuner.NextWake > now && retuner.NextWake < next)
                {
                    next = retuner.NextWake;
                }
            }

            _time.SetUtcNow(next < until ? next : until);
            foreach (MailcastRetuner retuner in _retuners)
            {
                DateTimeOffset due = retuner.NextWake;
                if (due <= _time.GetUtcNow())
                {
                    await Eventually(() => retuner.NextWake != due, "the retuner acted on its timer");
                }
            }

            await Task.Delay(1);
        }
    }

    /// <summary>
    /// Moves the fake clock on 50 ms at a time until <paramref name="condition"/> holds, for the
    /// channel's own waits (its carrier-sense slots and inhibit polls run on the same clock).
    /// Bounded by a count of looks, not by a time.
    /// </summary>
    private async Task Pump(Func<bool> condition, string what)
    {
        for (int look = 0; look < 4000 && !condition(); look++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }

        condition().Should().BeTrue(what);
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
    public async Task The_Rig_Goes_To_The_Mailcast_Dial_A_Minute_Before_Each_Slot_And_Back_Twelve_Minutes_After()
    {
        RigControl rig = await StartedRig();
        MailcastRetuner retuner = Retuner(rig);

        await Eventually(() => retuner.State.StartsWith("waiting for the 12:00 UTC slot", StringComparison.Ordinal), "waiting for noon");
        _fake.DialHz.Should().Be(7_049_450);
        rig.HoldsTransmitter.Should().BeFalse();

        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => _fake.DialHz == 7_052_000 && retuner.Listening, "on the mailcast dial at 11:59");
        rig.HoldsTransmitter.Should().BeTrue();
        _fake.Mode.Should().Be("USB");
        File.Exists(RestorePath).Should().BeTrue("a station killed now puts the rig back at its next start");

        // Past the rig's own five-minute cap on a window: renewed, still there.
        await AdvanceTo(Slot + TimeSpan.FromMinutes(11));
        _fake.DialHz.Should().Be(7_052_000);
        retuner.Listening.Should().BeTrue();
        retuner.State.Should().Be("on 7.052 MHz for the 12:00 UTC slot until 12:12 UTC");

        await AdvanceTo(Slot + TimeSpan.FromMinutes(12));
        await Eventually(() => _fake.DialHz == 7_049_450 && !rig.HoldsTransmitter, "back on the station's own dial at 12:12");
        retuner.Listening.Should().BeFalse();
        File.Exists(RestorePath).Should().BeFalse();
        await Eventually(() => retuner.State.StartsWith("waiting for the 13:00 UTC slot", StringComparison.Ordinal), "waiting for the next");
        _journal.Should().Contain("mailcast: rig on 7.052 MHz USB for the 12:00 UTC slot until 12:12 UTC; nothing is transmitted until it is put back");
        _journal.Should().Contain(line => line.StartsWith("rig: put back to 7.049450 MHz USB (2400 Hz passband)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_Window_Opens_For_A_Slot_In_The_Dark()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 19, 0, 0, TimeSpan.Zero));
        RigControl rig = await StartedRig();
        MailcastRetuner retuner = Retuner(rig);

        // Sunset at IO91lk is about 17:35 UTC: the next slot is tomorrow morning's.
        await Eventually(() => retuner.State.StartsWith("waiting for the 09:00 UTC slot", StringComparison.Ordinal), "waiting for the morning");
        await AdvanceTo(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero));
        _fake.DialHz.Should().Be(7_049_450);
        rig.HoldsTransmitter.Should().BeFalse();
    }

    [Fact]
    public async Task A_Rig_Whose_Data_Jack_Needs_Pktusb_Is_Retuned_In_It()
    {
        RigControl rig = await StartedRig();
        MailcastRetuner retuner = Retuner(rig, rigMode: "PKTUSB");

        retuner.Tuning.Should().Be(new RigTuning(7_052_000, "PKTUSB", 3000));
        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => _fake.Mode == "PKTUSB" && _fake.DialHz == 7_052_000, "in PKTUSB");
    }

    [Fact]
    public async Task Every_Source_Of_Transmission_Is_Held_While_The_Rig_Is_On_The_Mailcast_Dial()
    {
        RigControl rig = await StartedRig();
        var channel = new SoundModemChannel(12000, _time, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        channel.Csma.Persistence = 255;
        channel.TransmitInhibitTimeout = TimeSpan.FromMinutes(30);
        var line = new WatchedLine(() => rig.HoldsTransmitter, () => _fake.DialHz);
        IPttControl ptt = rig.HoldTransmissions(channel, line);
        var output = new FakeAudioOutput(12000);
        _running.Add(channel.RunTransmitterAsync(output, ptt, _stop.Token));
        MailcastRetuner retuner = Retuner(rig);
        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => retuner.Listening && _fake.DialHz == 7_052_000, "the window is open");

        // A frame from a host on a KISS port.
        Task kiss = channel.EnqueueTransmit(0, MailcastSlotAudio.Ui("G8ABC", "GB7TST", "hello"u8));

        // A Morse ident, which is not even queued while the rig is retuned, and waits if it is.
        var identifier = new StationIdentifier("G8ABC", null, 1000, 25, TimeSpan.FromMinutes(10), 12000);
        identifier.NoteTransmission();
        IdentTransmission.ShouldSend(channel, identifier, held: rig.HoldsTransmitter).Should().BeFalse();
        Task ident = IdentTransmission.SendAsync(channel, identifier);

        // A service transmission (POCSAG paging goes this way).
        Task page = channel.EnqueueTransmit(rate => new float[1200]);

        // The operator's transmitter test.
        var tests = new TxTestRunner(new TxTestOptions
        {
            Channel = channel,
            Journal = new StationJournal("", _journal.Enqueue, _journal.Enqueue),
            ChannelWait = TimeSpan.FromMinutes(30),
            Time = _time,
        });
        Task<TxTestOutcome> test = tests.RunAsync(new Packet.SoundModem.Waterfall.TxTestRequest(true, 0, 0.5));

        // An ARDOP burst owns the channel's timing and passes the inhibit: refused at the key.
        Task ardop = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: true);
        await Pump(() => ardop.IsCompleted, "the ARDOP burst answered");
        Func<Task> ardopWait = () => ardop;
        (await ardopWait.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*tuned to 7.052000 MHz USB*for mailcast*");

        kiss.IsCompleted.Should().BeFalse("held, not sent and not dropped");
        ident.IsCompleted.Should().BeFalse();
        page.IsCompleted.Should().BeFalse();
        test.IsCompleted.Should().BeFalse();
        line.Keys.Should().Be(0);

        // The window ends and the rig goes back: everything held goes out, on the right frequency.
        await AdvanceTo(Slot + TimeSpan.FromMinutes(12));
        await Eventually(() => !rig.HoldsTransmitter, "put back");
        await Pump(() => kiss.IsCompleted && ident.IsCompleted && page.IsCompleted && test.IsCompleted, "everything held has gone");
        await Task.WhenAll(kiss, ident, page);
        (await test).Ran.Should().BeTrue();
        line.Keys.Should().BeGreaterThanOrEqualTo(4);
        line.KeyedWhileHeld.Should().Be(0, "nothing keyed while the rig was retuned or owed its restore");
        line.KeyedOffPlan.Should().Be(0, "nothing keyed with the rig anywhere but the station's own dial");
    }

    [Fact]
    public async Task The_Transmit_Lease_Holder_Is_Refused_At_The_Key_While_The_Rig_Is_Retuned()
    {
        RigControl rig = await StartedRig();
        var channel = new SoundModemChannel(12000, _time, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        channel.Csma.Persistence = 255;
        var line = new WatchedLine(() => rig.HoldsTransmitter, () => _fake.DialHz);
        _running.Add(channel.RunTransmitterAsync(new FakeAudioOutput(12000), rig.HoldTransmissions(channel, line), _stop.Token));
        MailcastRetuner retuner = Retuner(rig);
        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => retuner.Listening, "the window is open");

        // A lease outranks the inhibit, so its holder's frames get as far as the PTT, which refuses them.
        channel.TransmitLease.Take(0, TimeSpan.FromSeconds(60)).Granted.Should().BeTrue();
        Task leased = channel.EnqueueTransmit(0, MailcastSlotAudio.Ui("GB7RDG", "MCAST", "x"u8));
        await Pump(() => leased.IsCompleted, "the lease holder's frame answered");
        Func<Task> leasedWait = () => leased;

        (await leasedWait.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*for mailcast*");
        line.Keys.Should().Be(0);
        channel.TransmitLease.Release().Should().BeTrue();
    }

    [Fact]
    public async Task A_Station_Restarted_Mid_Window_Puts_The_Rig_Back_First_And_Then_Listens_Out_The_Slot()
    {
        // The last run was killed at 12:04, mid-window, with the rig on the mailcast dial.
        RigRestoreFile.Write(RestorePath, _fake.Endpoint, Plan);
        _fake.DialHz = 7_052_000;
        _fake.Mode = "USB";
        _fake.PassbandHz = 3000;
        _time.SetUtcNow(Slot + TimeSpan.FromMinutes(5));

        RigControl rig = await StartedRig();
        _journal.Should().Contain(line => line.Contains("the station stopped during a tuning window last time"));
        MailcastRetuner retuner = Retuner(rig);
        await Eventually(() => retuner.Listening && _fake.DialHz == 7_052_000, "back on the mailcast dial for the rest of the slot");

        // Put back to the plan first (F 7049450), then retuned for what is left of the slot.
        int restored = _fake.Sets.ToList().IndexOf("F 7049450");
        int retuned = _fake.Sets.ToList().LastIndexOf("F 7052000");
        restored.Should().BeGreaterThanOrEqualTo(0);
        retuned.Should().BeGreaterThan(restored);
        rig.HoldsTransmitter.Should().BeTrue();

        await AdvanceTo(Slot + TimeSpan.FromMinutes(12));
        await Eventually(() => _fake.DialHz == 7_049_450 && !rig.HoldsTransmitter, "put back at 12:12");
    }

    [Fact]
    public async Task A_Window_The_Api_Holds_Is_Not_Taken_Over_And_The_Slot_Is_Retried()
    {
        RigControl rig = await StartedRig();
        rig.Tune(new RigTuning(7_074_000, "USB", 0), TimeSpan.FromMinutes(2), "the API").Granted.Should().BeTrue();
        _time.SetUtcNow(Slot - TimeSpan.FromSeconds(50));
        MailcastRetuner retuner = Retuner(rig);

        await Eventually(() => retuner.State.Contains("could not be retuned", StringComparison.Ordinal), "refused while the API holds the rig");
        _fake.DialHz.Should().Be(7_074_000);
        _journal.Should().ContainSingle(line => line.StartsWith("mailcast: WARNING - cannot retune the rig for the 12:00 UTC slot yet", StringComparison.Ordinal));

        rig.Release("the API").Should().BeTrue();
        await AdvanceTo(_time.GetUtcNow() + MailcastRetuner.RetryEvery);
        await Eventually(() => retuner.Listening && _fake.DialHz == 7_052_000, "retuned once the API let go");
    }

    [Fact]
    public async Task The_Receiver_Only_Listens_While_The_Rig_Is_On_The_Mailcast_Dial()
    {
        RigControl rig = await StartedRig();
        MailcastRetuner retuner = Retuner(rig);
        var placement = new MailcastPlacement(true, 1800, 350, 3250);
        await using MailcastReceiver receiver = MailcastReceiver.Create(
            new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Retune = true },
            placement, MailcastSlotAudio.Rate, Path.Combine(_dir.FullName, "mailcast"), _time, _journal.Enqueue);
        receiver.Retuner = retuner;
        float[] audio = MailcastSlotAudio.Render(MailcastSlotAudio.Frames().Take(1).ToList());

        // Before the window the audio is the station's own frequency, whatever is on it.
        receiver.Process(audio);
        await receiver.Intake.DrainAsync(CancellationToken.None);
        receiver.Intake.FramesHeard.Should().Be(0);

        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => retuner.Listening, "the window is open");
        receiver.Process(audio);
        await receiver.Intake.DrainAsync(CancellationToken.None);
        receiver.Intake.FramesHeard.Should().Be(1);
        receiver.Status()["retune"]!["listening"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task One_Failure_Does_Not_End_Retuning()
    {
        RigControl rig = await StartedRig();
        int asked = 0;
        var config = new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Retune = true };
        var retuner = new MailcastRetuner(
            rig, config, null,
            () => ++asked == 1 ? throw new InvalidOperationException("the timetable could not be read") : MailcastOnAir.DefaultTimetable,
            _time, _journal.Enqueue);
        _retuners.Add(retuner);
        _running.Add(Task.Run(() => retuner.RunAsync(_stop.Token)));

        await Eventually(() => retuner.State.StartsWith("retuning failed", StringComparison.Ordinal), "the failure is shown");
        _journal.Should().ContainSingle(line => line == "mailcast: WARNING - the retuning loop failed (the timetable could not be read); trying again in 5 s");

        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Eventually(() => retuner.Listening && _fake.DialHz == 7_052_000, "retuning carried on");
    }

    /// <summary>A PTT line that notes, at each keyup, whether the rig said to hold and where it was.</summary>
    private sealed class WatchedLine(Func<bool> held, Func<long> dial) : IPttControl
    {
        private int _keys;
        private int _held;
        private int _offPlan;

        internal int Keys => Volatile.Read(ref _keys);

        internal int KeyedWhileHeld => Volatile.Read(ref _held);

        internal int KeyedOffPlan => Volatile.Read(ref _offPlan);

        public void Key()
        {
            Interlocked.Increment(ref _keys);
            if (held())
            {
                Interlocked.Increment(ref _held);
            }

            if (dial() != Plan.DialHz)
            {
                Interlocked.Increment(ref _offPlan);
            }
        }

        public void Unkey()
        {
        }
    }
}
