using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Each transmission's held time is its OWN wait, and a keyup carrying several frames reports a
/// different one for each of them.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> GB7RDG-2's frame log rows 191408-191410 on 2026-09-21 look
/// impossible at first reading: three I-frames of one link, N(S) 5, 6 and 7, out in order from
/// one FIFO queue, reporting 1476, 5028 and 8281 ms held, so that <c>heard_at - held_ms</c> puts
/// the FIRST of them in the queue 299 ms AFTER the other two. A FIFO queue cannot emit a frame
/// first that was queued last, so either the queue or the figure had to be wrong.</para>
/// <para><b>Neither is.</b> <c>heard_at</c> on a transmitted row is stamped when the frame's own
/// write returns, not when the transmitter picked the frame up - the row is written from
/// <see cref="SoundModemChannel.FrameTransmittedWithReport"/>, which is raised after the burst has
/// gone to the device. So <c>heard_at - held_ms</c> is not the moment the frame was queued: it is
/// that moment plus the frame's own airtime. The first frame of a keyup carries the full TXDELAY
/// preamble and every frame behind it carries the 30 ms token preamble
/// (<see cref="SoundModemChannel.RunTransmitterAsync"/>), so the first frame's implied queue time
/// lands one TXDELAY later than its keyup-mates' and the three rows separate into two clusters
/// exactly TXDELAY apart. On GB7RDG that difference measured 296.99 ms with a standard deviation
/// of 0.87 ms over four keyups, and the follower-to-follower relation held to 0.58 ms over
/// fourteen pairs - a transmit-path constant, not a queue that reorders.</para>
/// <para>So these tests pin the two halves of that: each frame reports its own wait
/// (<see cref="Each_Frame_Reports_Its_Own_Wait_Across_Several_Keyups"/>), and the arithmetic trap
/// is reproduced deterministically so the next reader who subtracts those two columns finds out
/// here rather than on the air
/// (<see cref="One_Keyups_Frames_Report_Their_Place_In_It"/>).</para>
/// <para>Everything runs on a <see cref="FakeTimeProvider"/>. The only thing the wall clock is
/// used for is turning the crank - the pump below advances the fake clock in 10 ms steps and the
/// paced sink waits for it - and no assertion here reads it.</para>
/// </remarks>
public class HeldForAttributionTests
{
    private const int SampleRate = 12000;

    /// <summary>
    /// The token preamble a frame gets when it is not the first of its keyup - the figure
    /// <see cref="SoundModemChannel.RunTransmitterAsync"/> passes to the modem.
    /// </summary>
    private const int TokenPreambleMs = 30;

    private static readonly TimeSpan Tolerance = VirtualAir.Tolerance;

    /// <summary>Carrier sense an operator of this test holds the switch for.</summary>
    private sealed class Switch : IChannelBusySource
    {
        public bool? Busy { get; set; }
    }

    /// <summary>A UI frame: nothing expects an answer, so no turnaround hold confuses the sums.</summary>
    private static byte[] Broadcast()
    {
        byte[] frame = Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");
        frame[14] = 0x03;
        return [.. frame, (byte)0xF0, (byte)0x41];
    }

    /// <summary>
    /// How long one frame's burst actually is, asked of the same modem the station transmits with.
    /// </summary>
    /// <remarks>
    /// Measured rather than predicted, so these tests assert against the modem's own arithmetic
    /// and not against a duration copied out of it into a comment. <paramref name="txDelayMs"/> is
    /// what the transmitter passes: the station's TXDELAY for the first frame of a keyup, and a
    /// token 30 ms for every frame behind it under the same PTT.
    /// </remarks>
    private static TimeSpan Burst(byte[] frame, int txDelayMs)
    {
        var modem = new BpskMultiModem(SampleRate, _ => { }, crc: true, 2150, baud: 300, offsetPairs: 4);
        return TimeSpan.FromSeconds(modem.Modulate(frame, txDelayMs).Length / (double)SampleRate);
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time, Switch Busy) Station()
    {
        var time = new FakeTimeProvider();
        var busy = new Switch { Busy = false };
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42, channelBusySource: busy);
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;   // no roll: the switch is the only thing that can hold a frame
        channel.Csma.SlotTimeMilliseconds = 10;
        channel.Csma.TxDelayMilliseconds = 300;
        channel.Csma.TxTailMilliseconds = 0;
        return (channel, time, busy);
    }

    private static async Task<(Task Transmitter, VirtualAir.PacedSink Sink)> StartAsync(
        SoundModemChannel channel, FakeTimeProvider time, CancellationToken cancellation)
    {
        var sink = new VirtualAir.PacedSink(SampleRate, time);
        Task transmitter = channel.RunTransmitterAsync(sink, new RecordingPtt(), cancellation);
        VirtualAir.Pump(time, cancellation);
        await Task.Yield();
        return (transmitter, sink);
    }

    /// <summary>
    /// Several frames queued at different moments, gone out over several keyups: every one of them
    /// reports the wait it actually had, not the wait of whichever frame it shared a queue with.
    /// </summary>
    [Fact]
    public async Task Each_Frame_Reports_Its_Own_Wait_Across_Several_Keyups()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch busy) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var held = new List<TimeSpan>();
        channel.FrameTransmittedWithReport += (_, _, report) => held.Add(report.HeldFor);

        // Whether the station is keyed up, so the second keyup below can be made to be one.
        var keyed = false;
        channel.TransmittingChanged += on => Volatile.Write(ref keyed, on);

        // Every instant below is READ off the fake clock rather than assumed. The crank runs on
        // the thread pool and cannot be made to stop exactly on a mark, so a test that took its
        // own targets for the truth would fail by however far the crank overshot - which is a
        // property of the box it ran on and nothing to do with the figures under test.
        busy.Busy = true;
        DateTimeOffset firstQueued = time.GetUtcNow();
        Task first = channel.EnqueueTransmit(2, Broadcast());
        (Task transmitter, VirtualAir.PacedSink sink) = await StartAsync(channel, time, cancellation.Token);

        // A second frame a second into the first one's wait. Same source, so one queue: it is
        // behind the first frame and it will share the first frame's keyup.
        await VirtualAir.AdvanceToAsync(time, firstQueued + TimeSpan.FromSeconds(1));
        DateTimeOffset secondQueued = time.GetUtcNow();
        Task second = channel.EnqueueTransmit(2, Broadcast());

        // Two seconds after the first was queued, the channel opens.
        await VirtualAir.AdvanceToAsync(time, firstQueued + TimeSpan.FromSeconds(2));
        DateTimeOffset opened = time.GetUtcNow();
        busy.Busy = false;
        await first.WaitAsync(TimeSpan.FromSeconds(60));
        await second.WaitAsync(TimeSpan.FromSeconds(60));

        // Wait for the first keyup to END before handing over the next frame. A frame that
        // arrives while the drain loop is still running joins THAT keyup and is picked up the
        // instant it is queued, which is a keyup of three rather than the two-then-one this is
        // about. The enqueue task of the last frame completes inside the keyup, one write before
        // the tail and the unkey, so awaiting it is not the same as waiting for the keyup.
        while (Volatile.Read(ref keyed))
        {
            await Task.Delay(1, CancellationToken.None);
        }

        // Now a second keyup, after a fresh wait of its own, to prove the ledger is per frame and
        // not something cumulative that keeps counting from the first one.
        busy.Busy = true;
        DateTimeOffset thirdQueued = time.GetUtcNow();
        Task third = channel.EnqueueTransmit(2, Broadcast());
        await VirtualAir.AdvanceToAsync(time, thirdQueued + TimeSpan.FromMilliseconds(1500));
        DateTimeOffset openedAgain = time.GetUtcNow();
        busy.Busy = false;
        await third.WaitAsync(TimeSpan.FromSeconds(60));

        held.Should().HaveCount(3);
        TimeSpan opening = Burst(Broadcast(), channel.Csma.TxDelayMilliseconds);

        // Frame 1 waited from being handed over to the switch coming back up.
        held[0].Should().BeCloseTo(opened - firstQueued, Tolerance,
            "it was handed over while the switch was down and picked up when it came back up");

        // Frame 2 was queued a second later, so its own wait is a second shorter - plus frame 1's
        // airtime, because it went out behind frame 1 under the same PTT.
        held[1].Should().BeCloseTo(opened - secondQueued + opening, Tolerance,
            "what was left of the switch being down, and then frame 1's whole burst");

        // The two held times are measured from different moments, so the ORDER of the two frames
        // is in the pickups rather than in the figures: add each frame's wait back to the moment
        // it was handed over and frame 2 comes off the queue one burst after frame 1. A shorter
        // held time on a later frame is normal and says nothing about the queue.
        DateTimeOffset firstPickup = firstQueued + held[0];
        DateTimeOffset secondPickup = secondQueued + held[1];
        (secondPickup - firstPickup).Should().BeCloseTo(opening, Tolerance,
            "one queue, drained in order, with frame 1's burst between the two pickups");

        // Frame 3's keyup is its own: four seconds, and nothing carried over from the first two.
        held[2].Should().BeCloseTo(openedAgain - thirdQueued, Tolerance,
            "a held time is a property of one frame, not a running total for the link");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// Three frames handed over together go out in one keyup, and each reports its place in it -
    /// which is what makes <c>heard_at - held_ms</c> read as though the first was queued last.
    /// </summary>
    /// <remarks>
    /// This is GB7RDG-2's rows 191408-191410 reproduced on a fake clock. The frames are queued at
    /// one instant, as a MAXFRAME=3 window is; the channel is held for a moment and then opens;
    /// all three go out under one PTT. The assertions are the two halves of the reading that made
    /// the live rows look impossible: each held time is its own wait plus its keyup-mates'
    /// airtime (right), and each row's <c>heard_at</c> is stamped a whole burst after the pickup,
    /// so subtracting the held time from it lands one burst late - a different distance for the
    /// first frame of the keyup, which is the only one carrying a full TXDELAY.
    /// </remarks>
    [Fact]
    public async Task One_Keyups_Frames_Report_Their_Place_In_It()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch busy) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var held = new List<TimeSpan>();
        channel.FrameTransmittedWithReport += (_, _, report) => held.Add(report.HeldFor);

        busy.Busy = true;
        DateTimeOffset queued = time.GetUtcNow();
        Task a = channel.EnqueueTransmit(2, Broadcast());
        Task b = channel.EnqueueTransmit(2, Broadcast());
        Task c = channel.EnqueueTransmit(2, Broadcast());
        (Task transmitter, VirtualAir.PacedSink sink) = await StartAsync(channel, time, cancellation.Token);

        await VirtualAir.AdvanceToAsync(time, queued + TimeSpan.FromMilliseconds(800));
        DateTimeOffset opened = time.GetUtcNow();
        busy.Busy = false;
        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(60));

        held.Should().HaveCount(3);
        TimeSpan opening = Burst(Broadcast(), channel.Csma.TxDelayMilliseconds);
        TimeSpan follower = Burst(Broadcast(), TokenPreambleMs);
        TimeSpan shut = opened - queued;

        held[0].Should().BeCloseTo(shut, Tolerance,
            "the first frame of the window waited for the channel and nothing else");
        held[1].Should().BeCloseTo(shut + opening, Tolerance,
            "frame 2 waited the same stretch and then frame 1's whole burst, under the same PTT");
        held[2].Should().BeCloseTo(shut + opening + follower, Tolerance,
            "and frame 3 waited both of the bursts in front of it");

        // The steps between them are the bursts themselves, and the opening burst is longer than
        // the ones behind it by the preamble - the station's TXDELAY against the floor a token
        // preamble falls back to (BpskModem.Modulate keeps at least 24 bits, 80 ms at 300 Bd, so
        // the difference is 220 ms here rather than the 270 ms a naive 300-minus-30 would give).
        TimeSpan firstBurst = held[1] - held[0];
        TimeSpan secondBurst = held[2] - held[1];
        (firstBurst - secondBurst).Should().BeCloseTo(opening - follower, Tolerance,
            "the opening burst of a keyup carries the preamble and the ones behind it do not");

        // And here is the trap, reproduced. heard_at is stamped a whole burst after the pickup, so
        // heard_at - held_ms is the queue instant PLUS that frame's own airtime, and the first
        // frame of the keyup is the only one carrying a full TXDELAY. The logged instants are the
        // ones the sink recorded as each burst finished going to the device, which is the instant
        // a station stamps: see PacedSink.Written for why they are not read from the event.
        IReadOnlyList<DateTimeOffset> logged = sink.Written;
        logged.Should().HaveCount(3);
        DateTimeOffset impliedA = logged[0] - held[0];
        DateTimeOffset impliedB = logged[1] - held[1];
        DateTimeOffset impliedC = logged[2] - held[2];

        impliedB.Should().BeCloseTo(impliedC, Tolerance,
            "frames 2 and 3 are the same length and both carry the token preamble, so the same "
            + "wrong number comes out of both - which is why the live rows clustered");
        (impliedA - impliedB).Should().BeCloseTo(opening - follower, Tolerance,
            "the first frame of a keyup looks handed over one preamble after its own keyup-mates, "
            + "and on GB7RDG that gap measured 296.99 ms with a standard deviation of 0.87 ms "
            + "across four keyups - a preamble, not a queue that reorders");

        // The frames really did go out in the order they were queued, whatever the subtraction says.
        impliedA.Should().BeAfter(impliedB,
            "this is the impossible-looking row: subtract held_ms from heard_at and the frame that "
            + "went out FIRST appears to have been handed over LAST");
        held[0].Should().BeLessThan(held[1], "while the frames themselves went out 1, 2, 3");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    private static async Task Ignore(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is OperationCanceledException or ArgumentException or InvalidOperationException)
        {
        }
    }
}
