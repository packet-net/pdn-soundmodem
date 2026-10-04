using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmit lease: one sub-channel holds the transmitter, everyone else's transmissions are
/// refused (never queued), and a holder that stops renewing loses it on its own. Every clock here
/// is fake, so expiry is reached by advancing it rather than by waiting.
/// </summary>
public class TransmitLeaseTests
{
    private const int SampleRate = 12000;

    private static byte[] Frame(byte marker)
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame.AsSpan(16).Fill(marker);
        return frame;
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time) Station()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        return (channel, time);
    }

    [Fact]
    public void A_Lease_Is_Taken_Renewed_Refused_To_Others_And_Released()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        TransmitLease lease = channel.TransmitLease;
        var changes = new List<TransmitLeaseEvent>();
        lease.Changed += changes.Add;

        lease.Holder.Should().BeNull("nobody holds the transmitter until asked");
        TransmitLeaseGrant taken = lease.Take(3, TimeSpan.FromSeconds(60));
        taken.Should().Be(new TransmitLeaseGrant(true, 3, time.GetUtcNow().AddSeconds(60), Renewed: false));

        time.Advance(TimeSpan.FromSeconds(30));
        TransmitLeaseGrant renewed = lease.Take(3, TimeSpan.FromSeconds(60));
        renewed.Renewed.Should().BeTrue();
        lease.Expires.Should().Be(time.GetUtcNow().AddSeconds(60));

        TransmitLeaseGrant other = lease.Take(0, TimeSpan.FromSeconds(60));
        other.Granted.Should().BeFalse();
        other.SubChannel.Should().Be(3, "the answer names who holds it");

        lease.Release().Should().BeTrue();
        lease.Holder.Should().BeNull();
        lease.Release().Should().BeFalse("there is nothing left to give back");

        changes.Select(c => c.Change).Should().Equal(
            TransmitLeaseChange.Taken, TransmitLeaseChange.Renewed, TransmitLeaseChange.Released);
        changes[^1].HeldFor.Should().Be(TimeSpan.FromSeconds(30));
        changes[^1].Renewals.Should().Be(1);
    }

    [Fact]
    public void A_Lease_Asked_For_Longer_Than_The_Cap_Gets_The_Cap()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();

        TransmitLeaseGrant grant = channel.TransmitLease.Take(3, TimeSpan.FromHours(1));

        grant.Expires.Should().Be(time.GetUtcNow() + TransmitLease.MaxDuration);
        TransmitLease.MaxDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_Holder_That_Stops_Renewing_Loses_The_Lease_When_It_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        time.Advance(TimeSpan.FromSeconds(59));
        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().ContainSingle("nothing has happened yet but the take");

        time.Advance(TimeSpan.FromSeconds(1));

        // Reported by the lease's own timer as the clock passes the instant, not by somebody
        // happening to ask afterwards: that is what puts the line in the journal on time.
        changes.Should().HaveCount(2);
        changes[1].Change.Should().Be(TransmitLeaseChange.Expired);
        changes[1].SubChannel.Should().Be(3);
        changes[1].HeldFor.Should().Be(TimeSpan.FromSeconds(60));
        channel.TransmitLease.Holder.Should().BeNull();
        channel.TransmitLease.Admits(0).Should().BeTrue();
    }

    [Fact]
    public void A_Renewal_Moves_The_Expiry_So_The_Old_One_Does_Not_Fire()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        for (int i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        }

        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().NotContain(c => c.Change == TransmitLeaseChange.Expired);
    }

    [Fact]
    public void Frames_From_Other_Sub_Channels_Are_Refused_Not_Queued()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<(int Sub, string Why)>();
        channel.TransmitRejected += (sub, _, why) => refused.Add((sub, why.Message));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task again = channel.EnqueueTransmit(0, Frame(0x42));
        Task holders = channel.EnqueueTransmit(3, Frame(0x43));

        other.IsFaulted.Should().BeTrue("refused the moment it is handed over");
        again.IsFaulted.Should().BeTrue();
        holders.IsCompleted.Should().BeFalse("the holder's frame is queued for the transmitter");
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(r => r.Sub == 0);
        refused.Select(r => r.Why).Distinct().Should().ContainSingle(
            "one sentence every time, so the station's rate-limited DROPPED line folds them together")
            .Which.Should().Be("sub-channel 3 holds the transmit lease, so other transmissions are refused until it ends");

        TransmitLeaseEvent released = default;
        channel.TransmitLease.Changed += change => released = change;
        channel.TransmitLease.Release();
        released.Refused.Should().Be(2, "the release says how much was turned away");
    }

    [Fact]
    public async Task What_Others_Had_Already_Queued_Is_Refused_When_The_Lease_Is_Taken()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<int>();
        channel.TransmitRejected += (sub, _, _) => refused.Add(sub);

        Task queuedBefore = channel.EnqueueTransmit(0, Frame(0x41));
        Task holdersBefore = channel.EnqueueTransmit(3, Frame(0x42));
        Task service = channel.EnqueueTransmit(_ => new float[100]);
        queuedBefore.IsCompleted.Should().BeFalse("nothing is running the transmitter yet");

        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        // A sub-channel frame's task is the announcing wrapper's, which resumes on its own
        // continuation, so it is awaited rather than read; the refusal itself was synchronous.
        Func<Task> waiting = () => queuedBefore.WaitAsync(TimeSpan.FromSeconds(10));
        await waiting.Should().ThrowAsync<InvalidOperationException>(
            "a queue would release stale frames when the lease ends, so it is emptied instead");
        service.IsFaulted.Should().BeTrue("a transmission that names no sub-channel belongs to nobody");
        holdersBefore.IsCompleted.Should().BeFalse("the holder's own frame keeps its place");
        refused.Should().Equal(0);
    }

    [Fact]
    public void A_Service_Transmission_Attributed_To_The_Holder_Goes_Through()
    {
        // The holder's Morse ident: queued through the delegate overload under the identifier,
        // which the station attributes to its modem's sub-channel.
        (SoundModemChannel channel, _) = Station();
        object holdersIdent = new();
        object othersIdent = new();
        channel.TransmitLease.Attribute(holdersIdent, 3);
        channel.TransmitLease.Attribute(othersIdent, 0);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task mine = channel.EnqueueTransmit(_ => new float[100], source: holdersIdent);
        Task theirs = channel.EnqueueTransmit(_ => new float[100], source: othersIdent);

        mine.IsCompleted.Should().BeFalse("queued, waiting for the transmitter");
        theirs.IsFaulted.Should().BeTrue();
        channel.TransmitLease.Admits(holdersIdent).Should().BeTrue();
        channel.TransmitLease.Admits(othersIdent).Should().BeFalse();
    }

    [Fact]
    public void Normal_Service_Resumes_When_The_Lease_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.EnqueueTransmit(0, Frame(0x41)).IsFaulted.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(60));

        channel.EnqueueTransmit(0, Frame(0x42)).IsCompleted.Should().BeFalse(
            "queued again like any frame, once nobody holds the transmitter");
    }

    [Fact]
    public async Task The_Holders_Frames_Go_On_The_Air_While_Everyone_Else_Is_Refused()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var sent = new List<int>();
        channel.FrameTransmitted += (sub, _) => { lock (sent) { sent.Add(sub); } };
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task holders = channel.EnqueueTransmit(3, Frame(0x42));

        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);
        await holders.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        other.IsFaulted.Should().BeTrue();
        sent.Should().Equal(3);
        time.GetUtcNow().Should().Be(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
            "nothing here needed the clock to move");
    }

    /// <summary>A clock whose wall time can be stepped without its monotonic time moving.</summary>
    private sealed class SteppedWallClock(FakeTimeProvider inner, TimeSpan earlyBy = default) : TimeProvider
    {
        public TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Step;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime > earlyBy ? dueTime - earlyBy : dueTime, period);
    }

    [Fact]
    public void A_Wall_Clock_Step_Neither_Shortens_Nor_Lengthens_A_Lease()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var clock = new SteppedWallClock(fake);
        var channel = new SoundModemChannel(SampleRate, clock, randomSeed: 1);
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        clock.Step = TimeSpan.FromHours(1);
        channel.TransmitLease.Holder.Should().Be(3, "NTP stepping the clock forward is not the lease running out");

        clock.Step = TimeSpan.FromHours(-1);
        fake.Advance(TimeSpan.FromSeconds(60));
        channel.TransmitLease.Holder.Should().BeNull("nor is stepping it back a reason to keep it");
    }

    [Fact]
    public void A_Timer_That_Fires_Early_Rearms_So_The_Expiry_Is_Still_Reported()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var clock = new SteppedWallClock(fake, earlyBy: TimeSpan.FromSeconds(5));
        var channel = new SoundModemChannel(SampleRate, clock, randomSeed: 1);
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        var changes = new List<TransmitLeaseChange>();
        channel.TransmitLease.Changed += change => changes.Add(change.Change);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        fake.Advance(TimeSpan.FromSeconds(55));
        changes.Should().Equal([TransmitLeaseChange.Taken], "the timer fired five seconds early and found nothing due");

        fake.Advance(TimeSpan.FromSeconds(5));
        changes.Should().Equal(TransmitLeaseChange.Taken, TransmitLeaseChange.Expired);
    }

    [Fact]
    public void A_Release_Naming_Another_Sub_Channel_Frees_Nothing()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        channel.TransmitLease.Release(onlyIf: 0).Should().BeFalse("a late release must not free somebody else's lease");
        channel.TransmitLease.Holder.Should().Be(3);
        channel.TransmitLease.Release(onlyIf: 3).Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
    }

    /// <summary>A packing modem whose second render is held until the test lets it go.</summary>
    private sealed class HeldPacker : IModem, IFramePackingModem
    {
        private int _renders;

        public ManualResetEventSlim SecondRenderStarted { get; } = new();

        public ManualResetEventSlim ReleaseSecondRender { get; } = new();

        public string Mode => "held-packer";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public FramePacking? Packing { get; set; } =
            new(TimeSpan.FromSeconds(30), TimeSpan.Zero);

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[100];

        public void ResetCarrierState()
        {
        }

        public int FramesPerBurst(IReadOnlyList<byte[]> frames) => frames.Count;

        public float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds)
        {
            if (Interlocked.Increment(ref _renders) == 2)
            {
                SecondRenderStarted.Set();
                ReleaseSecondRender.Wait(TimeSpan.FromMinutes(2));
            }

            return new float[1000];
        }
    }

    /// <summary>Something on the channel the test can make busy, so the transmitter waits, and
    /// which says when carrier sense has asked it.</summary>
    private sealed class BusyModem : IModem
    {
        private volatile bool _busy = true;

        public ManualResetEventSlim Asked { get; } = new();
        public string Mode => "busy";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy
        {
            get
            {
                Asked.Set();
                return _busy;
            }
        }

        public void Clear() => _busy = false;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState()
        {
        }
    }

    [Fact]
    public async Task A_Lease_Taken_While_A_Burst_Is_In_Hand_Refuses_It_Before_The_Radio_Keys()
    {
        // The window the take-time sweep cannot see: the transmitter has won the channel, taken
        // the burst off the queue and is rendering it again (its TXDELAY changed while it waited),
        // and only then is the lease taken. The last check before the key must refuse it.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        var packer = new HeldPacker();
        var busy = new BusyModem();
        channel.AddModem(0, _ => packer);
        channel.AddModem(1, _ => busy);
        channel.Csma.Persistence = 255;
        channel.Csma.SlotTimeMilliseconds = 0; // carrier sense re-asks at once, with no clock in it

        var refused = new List<int>();
        channel.TransmitRejected += (sub, _, _) => { lock (refused) { refused.Add(sub); } };
        Task frame = channel.EnqueueTransmit(0, Frame(0x41));
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);

        // Carrier sense is only asked once the first render (TXDELAY 300) is done and the burst is
        // contending. While the busy channel holds it there, the host changes TXDELAY, so the
        // burst taken once the channel clears has to be rendered again - and that render is held.
        // The bounds below fail a broken run; they never pace a passing one.
        busy.Asked.Wait(TimeSpan.FromMinutes(1)).Should().BeTrue();
        channel.Csma.TxDelayMilliseconds = 120;
        busy.Clear();
        packer.SecondRenderStarted.Wait(TimeSpan.FromMinutes(1)).Should().BeTrue();

        channel.TransmitLease.Take(1, TimeSpan.FromSeconds(60));
        packer.ReleaseSecondRender.Set();

        Func<Task> waiting = () => frame.WaitAsync(TimeSpan.FromSeconds(30));
        await waiting.Should().ThrowAsync<InvalidOperationException>();
        ptt.Events.Should().BeEmpty("nothing keys for a burst the lease does not cover");
        refused.Should().Equal(0);

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
