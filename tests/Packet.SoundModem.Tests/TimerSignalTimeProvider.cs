using Microsoft.Extensions.Time.Testing;

namespace Packet.SoundModem.Tests;

/// <summary>
/// A fake clock that tells a test when the code under test has put a timer on it, so the test
/// moves the clock on only once the timer exists. A clock advanced before the timer is set never
/// fires it, and the test then waits for something that cannot come.
/// </summary>
internal sealed class TimerSignalTimeProvider : FakeTimeProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<TimeSpan, int> _created = [];
    private readonly List<(TimeSpan DueTime, int AtLeast, TaskCompletionSource Done)> _waiting = [];

    /// <summary>Completes once <paramref name="atLeast"/> timers with this first due time have been
    /// created on this clock, counting those created before the call.</summary>
    internal Task WhenCreatedAsync(TimeSpan dueTime, int atLeast = 1)
    {
        lock (_gate)
        {
            if (_created.GetValueOrDefault(dueTime) >= atLeast)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((dueTime, atLeast, done));
            return done.Task;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            int count = _created.GetValueOrDefault(dueTime) + 1;
            _created[dueTime] = count;
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].DueTime == dueTime && count >= _waiting[i].AtLeast)
                {
                    ready.Add(_waiting[i].Done);
                    _waiting.RemoveAt(i);
                }
            }
        }

        foreach (TaskCompletionSource done in ready)
        {
            done.TrySetResult();
        }

        return timer;
    }
}
