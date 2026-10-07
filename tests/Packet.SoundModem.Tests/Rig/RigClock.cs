using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Tests.Rig;

/// <summary>
/// Moving a fake clock under <see cref="RigControl"/>s whose watch talks to a real socket.
/// </summary>
/// <remarks>
/// <para>The watch waits between looks at rigctld on the station's clock and then does real socket
/// work. A test that steps the fake clock on a timer of its own races that work: on a busy machine
/// the clock runs on by a minute while one poll is still waiting for its answer, which says a
/// dial change twice inside a minute, or leaves a restore's retry due at a time the test has
/// already stopped the clock short of. With a 100 ms delay put on every fake rigctld reply, three
/// tests in these classes failed that way, every run.</para>
/// <para>So the clock moves only while every running watch is waiting on it, never past the end of
/// one of those waits, and, once one has been reached, not again until that watch has acted on
/// it. The only wall clock here is a safety net well beyond the 30 s socket timeouts the tests
/// give the rig, which turns a hung watch into a named failure rather than deciding anything a
/// slow machine could.</para>
/// </remarks>
internal static class RigClock
{
    /// <summary>How long a watch may be away from the clock before it counts as hung.</summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>Waits until every running watch in <paramref name="rigs"/> is waiting on the clock.</summary>
    internal static Task UntilWaitingAsync(IEnumerable<RigControl> rigs) =>
        UntilAsync(
            () => rigs.All(rig => !rig.Watching || rig.NapUntil is not null),
            "every rig's watch is back waiting on the clock");

    /// <summary>
    /// The furthest the clock may go towards <paramref name="next"/>: no further than the end of
    /// any watch's wait still ahead of <paramref name="now"/>.
    /// </summary>
    internal static DateTimeOffset Limit(IEnumerable<RigControl> rigs, DateTimeOffset now, DateTimeOffset next)
    {
        foreach (RigControl rig in rigs)
        {
            if (rig.Watching && rig.NapUntil is DateTimeOffset until && until > now && until < next)
            {
                next = until;
            }
        }

        return next;
    }

    /// <summary>
    /// The waits that end at or before <paramref name="now"/>, each with its rig, taken before the
    /// clock is moved so that the wait seen is the one the move ends.
    /// </summary>
    internal static List<(RigControl Rig, DateTimeOffset Until)> Due(IEnumerable<RigControl> rigs, DateTimeOffset now)
    {
        var due = new List<(RigControl, DateTimeOffset)>();
        foreach (RigControl rig in rigs)
        {
            if (rig.Watching && rig.NapUntil is DateTimeOffset until && until <= now)
            {
                due.Add((rig, until));
            }
        }

        return due;
    }

    /// <summary>Waits until each watch in <paramref name="due"/> has left the wait it was in.</summary>
    internal static async Task UntilActedAsync(List<(RigControl Rig, DateTimeOffset Until)> due)
    {
        foreach ((RigControl rig, DateTimeOffset until) in due)
        {
            await UntilAsync(
                () => !rig.Watching || rig.NapUntil != until,
                "a rig's watch acted on the wait the clock ended");
        }
    }

    /// <summary>
    /// Moves <paramref name="time"/> on by at most <paramref name="step"/>, once every watch is
    /// waiting on it, stopping at the end of any watch's wait and returning once that watch has
    /// acted on it.
    /// </summary>
    internal static async Task StepAsync(FakeTimeProvider time, IReadOnlyCollection<RigControl> rigs, TimeSpan step)
    {
        await UntilWaitingAsync(rigs);
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset next = Limit(rigs, now, now + step);
        List<(RigControl, DateTimeOffset)> due = Due(rigs, next);
        time.SetUtcNow(next);
        await UntilActedAsync(due);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var patience = Stopwatch.StartNew();
        while (!condition())
        {
            if (patience.Elapsed > Patience)
            {
                condition().Should().BeTrue($"{what} within {Patience.TotalSeconds:0} s (a hang, not a slow machine)");
                return;
            }

            await Task.Delay(1);
        }
    }
}
