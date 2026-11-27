using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

public class EventuallyTests
{
    // The helper's own tests are the one place a real short limit is acceptable: what is being proved
    // is that a limit which runs out fails with the caller's words, and a limit cannot run out
    // without time passing. Nothing here asserts how long anything took.
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_Condition_That_Already_Holds_Returns_Without_Looking_Again()
    {
        int looks = 0;

        await Eventually.UntilAsync(() => { looks++; return true; }, "nothing");

        looks.Should().Be(1);
    }

    [Fact]
    public async Task A_Condition_That_Comes_True_Returns_As_Soon_As_It_Does()
    {
        int looks = 0;

        await Eventually.UntilAsync(() => ++looks == 3, "the third look");

        looks.Should().Be(3, "it stops looking when the condition holds");
    }

    [Fact]
    public async Task An_Asynchronous_Condition_That_Comes_True_Returns_As_Soon_As_It_Does()
    {
        int looks = 0;

        await Eventually.UntilAsync(async () => { await Task.Yield(); return ++looks == 2; }, "the second look");

        looks.Should().Be(2);
    }

    [Fact]
    public async Task A_Condition_That_Never_Holds_Fails_Saying_What_Never_Happened()
    {
        Func<Task> wait = () => Eventually.UntilAsync(() => false, "the frame to arrive", Short);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*the frame to arrive*never happened*");
    }

    [Fact]
    public async Task An_Asynchronous_Condition_That_Never_Holds_Fails_Saying_What_Never_Happened()
    {
        Func<Task> wait = () => Eventually.UntilAsync(() => Task.FromResult(false), "the reply", Short);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*the reply*never happened*");
    }

    [Fact]
    public async Task A_Signal_Already_Given_Returns_Its_Result()
    {
        int got = await Eventually.SignalAsync(Task.FromResult(7), "a seven");

        got.Should().Be(7);
    }

    [Fact]
    public async Task A_Signal_Given_Later_Returns_Once_It_Is_Given()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task waiting = Eventually.SignalAsync(source.Task, "the event");
        waiting.IsCompleted.Should().BeFalse("nothing has been signalled yet");

        source.SetResult();

        await waiting;
    }

    [Fact]
    public async Task A_Signal_Never_Given_Fails_Saying_What_Never_Happened()
    {
        var never = new TaskCompletionSource();
        Func<Task> wait = () => Eventually.SignalAsync(never.Task, "the event", Short);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*the event*never happened*");
    }

    [Fact]
    public async Task A_Result_Signal_Never_Given_Fails_Saying_What_Never_Happened()
    {
        var never = new TaskCompletionSource<int>();
        Func<Task> wait = () => Eventually.SignalAsync(never.Task, "the number", Short);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*the number*never happened*");
    }

    [Fact]
    public async Task A_Task_That_Ends_Cancelled_Counts_As_Having_Finished()
    {
        using var source = new CancellationTokenSource();
        Task cancelled = Task.Delay(Timeout.Infinite, source.Token);
        await source.CancelAsync();

        await Eventually.CompletesAsync(cancelled, "the task to end");

        cancelled.IsCanceled.Should().BeTrue("how it ended is for the caller to look at");
    }

    [Fact]
    public async Task A_Task_That_Never_Finishes_Fails_Saying_What_Never_Happened()
    {
        var never = new TaskCompletionSource();
        Func<Task> wait = () => Eventually.CompletesAsync(never.Task, "the task to end", Short);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*the task to end*never happened*");
    }

    [Fact]
    public async Task A_Signal_That_Faults_Reports_The_Fault_Not_A_Timeout()
    {
        Func<Task> wait = () => Eventually.SignalAsync(Task.FromException(new InvalidOperationException("boom")), "x");

        await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void A_Blocking_Wait_Returns_As_Soon_As_The_Condition_Holds()
    {
        int looks = 0;

        Eventually.Until(() => ++looks == 2, "the second look");

        looks.Should().Be(2);
    }
}
