using TimeChannel = System.Threading.Channels.Channel;
using Microsoft.Extensions.Time.Testing;

namespace Packet.SoundModem.Tests;

/// <summary>
/// A fake clock that tells the test about every wait the code under test starts on it, so the test
/// moves the clock by exactly that wait once it exists, and by nothing else.
/// </summary>
/// <remarks>
/// <para>This replaces advancing the clock a step at a time from inside a poll, which moved it whether
/// or not anything was waiting and so made how far it had gone depend on how many looks the box got
/// through. A wait is announced after the fake clock has been given its timer, so a test that hears of
/// one and moves the clock at once cannot move it past a timer that does not exist yet.</para>
/// <para>Only one-shot timers are announced: a repeating one is a watchdog tick, not a wait the code is
/// parked on.</para>
/// </remarks>
internal sealed class AnnouncingClock : FakeTimeProvider
{
    private readonly System.Threading.Channels.Channel<TimeSpan> _waits = TimeChannel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        if (period == Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            _waits.Writer.TryWrite(dueTime);
        }

        return timer;
    }

    /// <summary>
    /// Moves the clock through each wait the code starts, one at a time and only once it has started it,
    /// until <paramref name="done"/> completes. If the code never starts another wait and
    /// <paramref name="done"/> never completes, the hang guard says what was awaited.
    /// </summary>
    public async Task AdvanceUntilAsync(Task done, string what)
    {
        while (!done.IsCompleted)
        {
            Task<TimeSpan> next = _waits.Reader.ReadAsync().AsTask();
            await Eventually.SignalAsync(Task.WhenAny(next, done), what);
            if (next.IsCompleted)
            {
                Advance(await next);
            }
        }

        await done;
    }
}
