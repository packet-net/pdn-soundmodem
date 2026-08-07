using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The burst-verdict fold, deterministically: synthetic DCD envelopes at the replay's 100 ms
/// poll cadence become the rows the instrument writes. What the polls and counters really are
/// is <see cref="DcdBurstTracker"/>'s remarks; these tests pin the boundary rules - a dropout
/// does not split a transmission, a straggler frame still finds its burst, counter deltas
/// baseline at the burst open - because the retry-twin analysis downstream is only as honest
/// as those rules.
/// </summary>
public class DcdBurstTrackerTests
{
    private static readonly DateTimeOffset Base =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int poll) => Base + TimeSpan.FromMilliseconds(100 * poll);

    private static FrameQuality Quality(double? offsetHz = null) =>
        new("bpsk300-il2pc", 30, 0, true, FrequencyOffsetHz: offsetHz);

    /// <summary>Feeds polls 0..n-1 with the given DCD envelope and no offset or counters.</summary>
    private static DcdBurstTracker Track(params bool[] dcd)
    {
        var tracker = new DcdBurstTracker();
        for (int i = 0; i < dcd.Length; i++)
        {
            tracker.Observe(At(i), dcd[i], null, 0, 0);
        }

        return tracker;
    }

    private static bool[] Envelope(params (bool Dcd, int Polls)[] runs) =>
        [.. runs.SelectMany(r => Enumerable.Repeat(r.Dcd, r.Polls))];

    [Fact]
    public void A_Rise_And_Fall_Becomes_One_Row_With_The_Time_Dcd_Held()
    {
        DcdBurstTracker tracker = Track(Envelope((false, 5), (true, 20), (false, 15)));

        DcdBurst burst = tracker.Bursts.Should().ContainSingle().Which;
        burst.UtcStart.Should().Be(At(5), "the first asserted poll opens the burst");
        burst.UtcEnd.Should().Be(At(24), "the last asserted poll is the end, not the close decision");
        burst.DcdSeconds.Should().BeApproximately(2.0, 0.01, "twenty polls of 100 ms held");
        burst.Decoded.Should().BeFalse("no frame ever attached");
    }

    [Fact]
    public void A_Short_Dropout_Neither_Splits_The_Burst_Nor_Counts_As_Held_Time()
    {
        // A fade riding through PacketDcd's hysteresis: under the close window, so still one
        // transmission - splitting it would fabricate a retry twin downstream - but the down
        // time is not claimed as DCD time either.
        DcdBurstTracker tracker = Track(
            Envelope((false, 5), (true, 10), (false, 5), (true, 10), (false, 15)));

        DcdBurst burst = tracker.Bursts.Should().ContainSingle().Which;
        burst.UtcStart.Should().Be(At(5));
        burst.UtcEnd.Should().Be(At(29));
        burst.DcdSeconds.Should().BeApproximately(2.0, 0.01,
            "the half-second dropout is visible as dcd_seconds falling short of end - start");
    }

    [Fact]
    public void A_Gap_Past_The_Close_Window_Starts_A_New_Burst()
    {
        DcdBurstTracker tracker = Track(
            Envelope((false, 5), (true, 10), (false, 12), (true, 10), (false, 15)));

        tracker.Bursts.Should().HaveCount(2, "1.2 s of quiet is a burst boundary");
        tracker.Bursts[0].UtcEnd.Should().Be(At(14));
        tracker.Bursts[1].UtcStart.Should().Be(At(27));
    }

    [Fact]
    public void A_Frame_Attaches_To_The_Open_Burst_And_Its_Own_Offset_Wins()
    {
        var tracker = new DcdBurstTracker();
        for (int i = 0; i < 10; i++)
        {
            tracker.Observe(At(i), i >= 2, i >= 2 ? -12.5 : null, 0, 0);
        }

        tracker.OnFrame(At(9), Quality(offsetHz: 8.4));
        for (int i = 10; i < 25; i++)
        {
            tracker.Observe(At(i), false, null, 0, 0);
        }

        DcdBurst burst = tracker.Bursts.Should().ContainSingle().Which;
        burst.Decoded.Should().BeTrue();
        burst.Frames.Should().Be(1);
        burst.OffsetHz.Should().Be(8.4,
            "a decoded frame's own reading outranks the polled one");
    }

    [Fact]
    public void An_Undecoded_Burst_Keeps_The_Last_Polled_Offset()
    {
        var tracker = new DcdBurstTracker();
        double?[] offsets = [null, -10.0, -12.0, null];
        for (int i = 0; i < 4; i++)
        {
            tracker.Observe(At(i), true, offsets[i], 0, 0);
        }

        for (int i = 4; i < 20; i++)
        {
            tracker.Observe(At(i), false, null, 0, 0);
        }

        tracker.Bursts.Should().ContainSingle().Which.OffsetHz.Should().Be(-12.0,
            "the last measurement while DCD held describes the burst; a later null does not erase it");
    }

    [Fact]
    public void A_Straggler_Frame_Attaches_To_The_Burst_That_Just_Closed()
    {
        DcdBurstTracker tracker = Track(Envelope((true, 10), (false, 15)));

        tracker.OnFrame(At(24), Quality());

        tracker.Bursts.Should().ContainSingle().Which.Decoded.Should().BeTrue(
            "a frame released on the deframer reset lands within the attach window");
        tracker.OrphanFrames.Should().Be(0);
    }

    [Fact]
    public void A_Frame_With_No_Burst_Near_Is_An_Orphan()
    {
        DcdBurstTracker tracker = Track(Envelope((true, 10), (false, 15)));

        tracker.OnFrame(At(60), Quality());

        tracker.OrphanFrames.Should().Be(1,
            "a decode the DCD envelope missed is evidence against the calibration claim, "
            + "so it is counted rather than swallowed");
        tracker.Bursts.Should().ContainSingle().Which.Decoded.Should().BeFalse();
    }

    [Fact]
    public void Counter_Deltas_Are_Baselined_At_The_Burst_Open()
    {
        // The counters are cumulative across the whole pass; a burst's verdict is its delta.
        // The block that asserts DCD may already have ticked them, so the baseline is the poll
        // before the open, and ticks during the trailing quiet second still belong to the burst
        // (the deframer reset on the DCD falling edge is where a held failure surfaces).
        var tracker = new DcdBurstTracker();
        for (int i = 0; i < 5; i++)
        {
            tracker.Observe(At(i), false, null, 5, 1);
        }

        tracker.Observe(At(5), true, null, 7, 1);
        for (int i = 6; i < 10; i++)
        {
            tracker.Observe(At(i), true, null, 9, 1);
        }

        for (int i = 10; i < 25; i++)
        {
            tracker.Observe(At(i), false, null, 9, 2);
        }

        DcdBurst burst = tracker.Bursts.Should().ContainSingle().Which;
        burst.RsFailures.Should().Be(4, "seven minus the pre-burst five, plus two more mid-burst");
        burst.CrcFailures.Should().Be(1, "the tick during the trailing quiet second is the burst's");
    }

    [Fact]
    public void Flush_Closes_The_Open_Burst_With_The_Last_Counters()
    {
        var tracker = new DcdBurstTracker();
        for (int i = 0; i < 10; i++)
        {
            tracker.Observe(At(i), true, null, i < 8 ? 0 : 3, 0);
        }

        tracker.Bursts.Should().ContainSingle().Which.RsFailures.Should().Be(0,
            "the burst is still open, so its verdict is not in yet");
        tracker.Flush();

        DcdBurst burst = tracker.Bursts.Should().ContainSingle().Which;
        burst.UtcEnd.Should().Be(At(9));
        burst.RsFailures.Should().Be(3, "a pass that ends mid-burst still gets its verdict");
    }

    [Fact]
    public void A_Feed_Discontinuity_Does_Not_Masquerade_As_Held_Time()
    {
        // A capture gap between chunks jumps the poll clock; if DCD reads asserted on both
        // sides, the jump must not be claimed as minutes of DCD.
        var tracker = new DcdBurstTracker();
        tracker.Observe(At(0), true, null, 0, 0);
        tracker.Observe(At(1), true, null, 0, 0);
        tracker.Observe(At(1) + TimeSpan.FromMinutes(5), true, null, 0, 0);
        tracker.Flush();

        tracker.Bursts[^1].DcdSeconds.Should().BeLessThan(1.0,
            "five minutes of missing audio is not five minutes of carrier");
    }
}
