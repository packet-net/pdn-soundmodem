using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// After a frame that will be answered, the channel's OTHER modems stay off the air until the
/// answer has had its chance.
/// </summary>
/// <remarks>
/// This is the half p-persistence cannot do. At the instant the transmitter rolls, the reply has
/// not started, so carrier sense has nothing to detect and whether we key over it is down to the
/// roll. GB7RDG-2's log puts the peer's carrier 0.25 to 0.75 s after our PTT drops, which is
/// TXDELAY plus a few slots - the far end keying up and winning the channel, and both of those
/// are numbers this channel already holds.
/// </remarks>
public class TurnaroundHoldTests
{
    private const int SampleRate = 12000;

    private sealed class Sink(int sampleRate) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
        }

        public void Drain()
        {
        }
    }

    private static byte[] Poll() => Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");

    private static byte[] Broadcast()
    {
        byte[] frame = Poll();
        frame[14] = 0x03; // UI: nobody answers it
        return [.. frame, (byte)0xF0, (byte)0x41];
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time) Station()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;
        channel.QuietAfterTransmit = (_, frame) =>
            Ax25ReplyExpectation.ExpectsReply(frame) ? channel.TurnaroundHold : null;
        return (channel, time);
    }

    /// <summary>Runs the transmitter, letting the fake clock run so CSMA delays complete.</summary>
    private static async Task<Task> StartAsync(
        SoundModemChannel channel, FakeTimeProvider time, CancellationToken cancellation)
    {
        var ptt = new RecordingPtt();
        Task transmitter = channel.RunTransmitterAsync(new Sink(SampleRate), ptt, cancellation);
        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);
        await Task.Yield();
        return transmitter;
    }

    [Fact]
    public void The_Window_Is_Calculated_From_The_Channels_Own_Parameters()
    {
        (SoundModemChannel channel, _) = Station();
        channel.Csma.TxDelayMilliseconds = 300;
        channel.Csma.SlotTimeMilliseconds = 100;

        channel.TurnaroundHold.Should().Be(TimeSpan.FromMilliseconds(700),
            "the peer needs its own TXDELAY to key up, as long again for its rig and its decode, "
            + "and a slot of contention - all wall-clock parameters this channel already holds");

        // Not a fitted constant: change the channel's timing and the window follows.
        channel.Csma.TxDelayMilliseconds = 120;
        channel.Csma.SlotTimeMilliseconds = 50;
        channel.TurnaroundHold.Should().Be(TimeSpan.FromMilliseconds(290));

        // A host may set slot time to zero, and LinBPQ sets exactly that on the live station.
        // The window must not collapse with it, because with the backoff spinning this hold is
        // the only thing keeping the station off the air after its own transmission.
        channel.Csma.TxDelayMilliseconds = 300;
        channel.Csma.SlotTimeMilliseconds = 0;
        channel.TurnaroundHold.Should().Be(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task A_Frame_That_Expects_A_Reply_Holds_The_Other_Modem_Off()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task poll = channel.EnqueueTransmit(2, Poll());       // an RR poll: an answer is owed
        Task other = channel.EnqueueTransmit(0, Broadcast()); // the other link, queued behind it
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await poll.WaitAsync(TimeSpan.FromSeconds(15));
        // The polling modem is done; the other one must still be waiting out the turnaround.
        other.IsCompleted.Should().BeFalse(
            "the reply to the poll has not had its chance yet, and keying over it is exactly "
            + "what this station was doing 97 % of the time");

        await other.WaitAsync(TimeSpan.FromSeconds(15));
        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Broadcast_Holds_Nobody()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task ident = channel.EnqueueTransmit(2, Broadcast());
        Task other = channel.EnqueueTransmit(0, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await Task.WhenAll(ident, other).WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await Ignore(transmitter);
        // Reaching here at all is the assertion: neither frame expects an answer, so neither
        // spends the other link's airtime waiting for one.
    }

    [Fact]
    public async Task With_No_Hook_The_Channel_Behaves_As_It_Always_Did()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.QuietAfterTransmit = null; // a channel that says nothing about reply timing
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task poll = channel.EnqueueTransmit(2, Poll());
        Task other = channel.EnqueueTransmit(0, Poll());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await Task.WhenAll(poll, other).WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task Any_Number_Of_Transmitters_Share_The_Channel_Not_Just_Two()
    {
        // Nothing about this is a pair. The channel holds a queue per transmitter and serves them
        // round robin, so the hold generalises to however many share the half-duplex resource -
        // four packet modems, or three modems and a pager, or anything else that makes a receiver
        // go deaf by keying up.
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.AddModem(3, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 1120, 5));
        channel.Csma.Persistence = 255;
        channel.QuietAfterTransmit = (_, frame) =>
            Ax25ReplyExpectation.ExpectsReply(frame) ? channel.TurnaroundHold : null;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task poll = channel.EnqueueTransmit(2, Poll());
        Task second = channel.EnqueueTransmit(0, Broadcast());
        Task third = channel.EnqueueTransmit(3, Broadcast());
        Task service = channel.EnqueueTransmit(_ => new float[SampleRate / 20]);
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await poll.WaitAsync(TimeSpan.FromSeconds(20));
        second.IsCompleted.Should().BeFalse("every other transmitter waits out the turnaround");
        third.IsCompleted.Should().BeFalse();
        service.IsCompleted.Should().BeFalse("a service transmitter is one of them too");

        // And none of them is starved: once the window lapses unanswered they all get the air.
        await Task.WhenAll(second, third, service).WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Zero_Slot_Time_Does_Not_Turn_The_Hold_Into_A_Spin()
    {
        // A host may set slot time to zero and LinBPQ was sending exactly that to the live
        // station. The scheduler must wait out the hold rather than poll at slot intervals,
        // because a zero-length poll is a core spinning for the length of every hold. The
        // observable part is that the held frame still goes out; the wait being a single sleep
        // rather than a spin is what RemainingHold buys.
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.Csma.SlotTimeMilliseconds = 0;
        channel.TurnaroundHold.Should().Be(TimeSpan.FromMilliseconds(600),
            "the window must not collapse with the slot time");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task poll = channel.EnqueueTransmit(2, Poll());
        Task held = channel.EnqueueTransmit(0, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await poll.WaitAsync(TimeSpan.FromSeconds(15));
        held.IsCompleted.Should().BeFalse("the turnaround is still running");
        await held.WaitAsync(TimeSpan.FromSeconds(15)); // must release, not hang

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    private static async Task Ignore(Task transmitter)
    {
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A fake clock that says when something starts waiting on it.</summary>
    private sealed class WatchedClock(FakeTimeProvider inner) : TimeProvider
    {
        private TaskCompletionSource<TimeSpan> _waited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> NextWait => Volatile.Read(ref _waited).Task;

        public void Rearm() => Volatile.Write(ref _waited, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                Volatile.Read(ref _waited).TrySetResult(dueTime);
            }

            return timer;
        }
    }

    /// <summary>Records the length of everything the transmitter writes, in order.</summary>
    private sealed class LengthRecorder(int sampleRate) : IAudioOutput
    {
        public List<int> Lengths { get; } = [];

        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            lock (Lengths)
            {
                Lengths.Add(samples.Length);
            }
        }

        public void Drain()
        {
        }
    }

    [Fact]
    public async Task The_Holds_Owner_Does_Not_Jump_A_Frame_Already_Waiting_Out_Its_Hold()
    {
        // The order a node's two ports get the air in. During a hold the transmitter sleeps until
        // the hold ends, so a new frame from the link that owns the hold waits too, and when the
        // hold ends the frame that was already waiting goes first. On a frozen clock: nothing can
        // end the hold until the test advances it, so the order is the scheduler's alone.
        var fake = new FakeTimeProvider();
        var clock = new WatchedClock(fake);
        var channel = new SoundModemChannel(SampleRate, clock, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;
        channel.QuietAfterTransmit = (_, frame) =>
            Ax25ReplyExpectation.ExpectsReply(frame) ? channel.TurnaroundHold : null;

        var output = new LengthRecorder(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task firstPoll = channel.EnqueueTransmit(2, Poll());
        Task transmitter = channel.RunTransmitterAsync(output, new RecordingPtt(), cancellation.Token);
        await firstPoll.WaitAsync(TimeSpan.FromMinutes(1));

        // The other port's frame, much longer than a poll so its burst is easy to tell apart.
        clock.Rearm();
        byte[] waiting = [.. Broadcast(), .. new byte[60]];
        Task other = channel.EnqueueTransmit(0, waiting);
        TimeSpan hold = await clock.NextWait.WaitAsync(TimeSpan.FromMinutes(1));
        hold.Should().BeGreaterThan(TimeSpan.Zero, "the transmitter is now sleeping out the hold");

        // The owner's next poll, during the hold. It must stay queued: the clock is frozen, so
        // nothing correct can send it. The second here is a window for a wrong scheduler to show
        // itself in, not a pace - a correct one cannot fail this however loaded the box is.
        Task secondPoll = channel.EnqueueTransmit(2, Poll());
        await Task.WhenAny(secondPoll, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        secondPoll.IsCompleted.Should().BeFalse("a new frame from the hold's owner waits for the hold too");
        fake.Advance(channel.TurnaroundHold + TimeSpan.FromMilliseconds(100));
        await Task.WhenAll(other, secondPoll).WaitAsync(TimeSpan.FromMinutes(1));
        await cancellation.CancelAsync();
        await Ignore(transmitter);

        int tail = SampleRate * channel.Csma.TxTailMilliseconds / 1000;
        List<int> bursts;
        lock (output.Lengths)
        {
            bursts = output.Lengths.Where(length => length != tail).ToList();
        }

        bursts.Should().HaveCount(3);
        bursts[1].Should().BeGreaterThan(bursts[2],
            "the other port's frame was waiting first, so it goes first once the hold ends");
    }
}
