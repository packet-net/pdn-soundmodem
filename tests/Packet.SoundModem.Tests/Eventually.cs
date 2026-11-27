namespace Packet.SoundModem.Tests;

/// <summary>
/// The one way a test waits for something that real background work will do: a hang guard that
/// turns "this never happens" into a failure that says what never happened.
/// </summary>
/// <remarks>
/// <para>The limit is not an assertion about speed. It is generous on purpose (a loaded build box
/// must never reach it) and it is the same for every caller; only the helper's own tests pass a
/// short one, in the overloads that take a limit.</para>
/// <para>Prefer a signal to a poll: when the code under test, or its test double, completes a
/// task, releases a semaphore or raises an event for the thing being waited on, wait on that
/// with <see cref="SignalAsync(Task, string)"/> and nothing is polled. The polling overloads are
/// for conditions nothing signals; their short real pause between looks is pacing of the test's
/// own looking, not a claim about how long anything takes.</para>
/// <para>Not for waiting for time to pass. A test that needs time to pass drives a fake clock or
/// the virtual air.</para>
/// <para>The condition must only be able to become true because of what is being waited for. A
/// count that is already at the target from earlier work (">= 10" when ten were already there)
/// ends the wait at once, and whatever the test then checks has not happened yet.</para>
/// </remarks>
internal static class Eventually
{
    /// <summary>The limit every caller gets; the repo's hang guards are all this long.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>The pause between looks. Pacing of the test's own looking only.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(5);

    /// <summary>Waits until <paramref name="condition"/> holds; fails with <paramref name="because"/> if it
    /// never does within <see cref="HangGuard"/>.</summary>
    public static Task UntilAsync(Func<bool> condition, string because) =>
        UntilAsync(condition, because, HangGuard);

    /// <summary>The same for a condition that is itself asynchronous.</summary>
    public static Task UntilAsync(Func<Task<bool>> condition, string because) =>
        UntilAsync(condition, because, HangGuard);

    /// <summary>Waits for a task the code under test (or its double) completes; fails with
    /// <paramref name="because"/> if it is not completed within <see cref="HangGuard"/>.</summary>
    public static Task SignalAsync(Task signal, string because) => SignalAsync(signal, because, HangGuard);

    /// <summary>The same for a task that carries a result, which is returned.</summary>
    public static Task<T> SignalAsync<T>(Task<T> signal, string because) => SignalAsync(signal, because, HangGuard);

    /// <summary>Waits for a task to finish, however it finishes (returned, faulted or cancelled), without
    /// throwing what it threw; fails with <paramref name="because"/> if it is still running after
    /// <see cref="HangGuard"/>. For a test that then looks at how it ended.</summary>
    public static Task CompletesAsync(Task signal, string because) => CompletesAsync(signal, because, HangGuard);

    /// <summary>The blocking form of <see cref="UntilAsync(Func{bool}, string)"/>, for a test body or a
    /// test double that is on a thread of its own.</summary>
    public static void Until(Func<bool> condition, string because)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= HangGuard)
            {
                throw Never(because, HangGuard);
            }

            Thread.Sleep(Pause);
        }
    }

    // The overloads with a limit are for the helper's own tests: only they need the limit to run out.

    internal static async Task UntilAsync(Func<bool> condition, string because, TimeSpan limit)
    {
        await UntilAsync(() => Task.FromResult(condition()), because, limit);
    }

    internal static async Task UntilAsync(Func<Task<bool>> condition, string because, TimeSpan limit)
    {
        using var guard = new CancellationTokenSource(limit);
        while (!await condition())
        {
            try
            {
                await Task.Delay(Pause, guard.Token);
            }
            catch (OperationCanceledException)
            {
                // One last look, so a condition that came true as the limit ran out is not missed.
                if (await condition())
                {
                    return;
                }

                throw Never(because, limit);
            }
        }
    }

    internal static async Task SignalAsync(Task signal, string because, TimeSpan limit)
    {
        try
        {
            await signal.WaitAsync(limit);
        }
        catch (TimeoutException) when (!signal.IsCompleted)
        {
            throw Never(because, limit);
        }
    }

    internal static async Task CompletesAsync(Task signal, string because, TimeSpan limit)
    {
        try
        {
            await signal.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw Never(because, limit);
        }
    }

    internal static async Task<T> SignalAsync<T>(Task<T> signal, string because, TimeSpan limit)
    {
        try
        {
            return await signal.WaitAsync(limit);
        }
        catch (TimeoutException) when (!signal.IsCompleted)
        {
            throw Never(because, limit);
        }
    }

    private static TimeoutException Never(string because, TimeSpan limit) =>
        new($"waited {limit.TotalSeconds:0.###} s for {because}, and it never happened");
}
