using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// Issue #790: M0LTE.Flex keys and unkeys with no timeout, so a radio that stops answering
/// mid-keyup used to hold the transmitter, and the station's shutdown, for ever.
/// </summary>
/// <remarks>
/// Every test runs on a fake clock (issue #854). The class under test measures its limit on that
/// clock, and its timed wait is a waiter of the test's own that tells the test each time a poll has
/// begun, so the test advances time only once the keyup is waiting for it. Nothing here sleeps, or
/// measures real time.
/// </remarks>
public sealed class BoundedFlexPttTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Stands in for the keyup's timed wait: signals that a poll has begun, then parks
    /// until the test releases it, so the test advances the fake clock only while the keyup is
    /// waiting and never waits in real time itself.</summary>
    private sealed class PollingWaiter
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _release = new(0);

        public FakeTimeProvider Clock { get; } = new();

        public bool Wait(Task running, TimeSpan poll)
        {
            _started.Release();
            _release.Wait(Token);
            return running.IsCompleted;
        }

        public BoundedFlexPtt.PollWait AsWait => Wait;

        /// <summary>Completes when the keyup has started its next poll, so it has looked at the
        /// session and the limit for every poll before it.</summary>
        public Task NextPoll() => _started.WaitAsync(Token);

        /// <summary>Waits for the next poll to begin, lets one poll interval pass on the clock,
        /// then ends the poll.</summary>
        public async Task PassOnePoll()
        {
            await NextPoll();
            Clock.Advance(Poll);
            _release.Release();
        }

        /// <summary>Ends the poll that has begun, after the test advanced the clock itself.</summary>
        public void EndPoll() => _release.Release();
    }

    /// <summary>A PTT whose radio never answers.</summary>
    private sealed class UnansweredPtt : IPttControl
    {
        private readonly ManualResetEventSlim _never = new();

        public void Key() => _never.Wait();

        public void Unkey() => _never.Wait();
    }

    private sealed class CountingPtt : IPttControl
    {
        public int Keys;

        public int Unkeys;

        public void Key() => Keys++;

        public void Unkey() => Unkeys++;
    }

    /// <summary>A PTT whose keyup is slow to answer, and which records what reached the radio.</summary>
    private sealed class SlowKeyPtt : IPttControl
    {
        public ManualResetEventSlim Answer { get; } = new();

        /// <summary>Set once the keyup has returned.</summary>
        public TaskCompletionSource KeyReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Released once for each unkey that reaches the radio.</summary>
        public SemaphoreSlim Unkeys { get; } = new(0);

        public void Key()
        {
            Answer.Wait();
            KeyReturned.SetResult();
        }

        public void Unkey() => Unkeys.Release();
    }

    [Fact]
    public async Task An_Unkey_The_Radio_Never_Answers_Gives_Up_When_The_Clock_Reaches_Its_Limit_And_Says_The_Radio_Is_Gone()
    {
        var clock = new PollingWaiter();
        var said = new List<string>();
        var ptt = new BoundedFlexPtt(
            new UnansweredPtt(), sessionAlive: () => true, keyLimit: TimeSpan.FromSeconds(30),
            unkeyLimit: TimeSpan.FromMilliseconds(300), time: clock.Clock, wait: clock.AsWait);
        ptt.RadioUnanswered += said.Add;

        Task unkeying = Task.Run(ptt.Unkey, Token);

        // 200 ms in, and the keyup has been round the poll again: it did not give up short of 300 ms.
        await clock.PassOnePoll();
        await clock.PassOnePoll();
        await clock.NextPoll();
        unkeying.IsCompleted.Should().BeFalse("300 ms have not passed on the clock");

        clock.Clock.Advance(Poll);
        clock.EndPoll();
        Func<Task> wait = () => unkeying.WaitAsync(Token);
        await wait.Should().ThrowAsync<TimeoutException>();
        said.Should().ContainSingle().Which.Should().Be(
            "flex: the radio did not answer the unkey in 0.3 s; treating it as gone");
    }

    [Fact]
    public async Task A_Keyup_Under_Way_When_The_Session_Ends_Gives_Up_Without_The_Clock_Reaching_Its_Limit()
    {
        var clock = new PollingWaiter();
        DateTimeOffset start = clock.Clock.GetUtcNow();
        bool alive = true;
        var said = new List<string>();
        var ptt = new BoundedFlexPtt(
            new UnansweredPtt(), () => Volatile.Read(ref alive), keyLimit: TimeSpan.FromMinutes(5), time: clock.Clock, wait: clock.AsWait);
        ptt.RadioUnanswered += said.Add;

        Task keying = Task.Run(ptt.Key, Token);
        await clock.PassOnePoll();
        await clock.NextPoll();
        keying.IsCompleted.Should().BeFalse("the session is up and the limit is minutes away");

        Volatile.Write(ref alive, false);
        clock.Clock.Advance(Poll);
        clock.EndPoll();

        Func<Task> wait = () => keying.WaitAsync(Token);
        await wait.Should().ThrowAsync<TimeoutException>("the keyup gave up rather than waiting on");
        (clock.Clock.GetUtcNow() - start).Should().Be(
            2 * Poll, "the keyup's own limit is minutes; the session ending is what ended it");
        said.Should().ContainSingle().Which.Should().Be("flex: the radio's session ended during the keyup");
    }

    [Fact]
    public async Task A_Keyup_With_The_Session_Still_Up_Gives_Up_When_The_Clock_Reaches_Its_Limit_And_Not_Before()
    {
        var clock = new PollingWaiter();
        var said = new List<string>();
        var ptt = new BoundedFlexPtt(
            new UnansweredPtt(), () => true, keyLimit: TimeSpan.FromSeconds(1), time: clock.Clock, wait: clock.AsWait);
        ptt.RadioUnanswered += said.Add;

        Task keying = Task.Run(ptt.Key, Token);
        for (int i = 0; i < 9; i++)
        {
            await clock.PassOnePoll();
        }

        await clock.NextPoll();
        keying.IsCompleted.Should().BeFalse("900 ms have passed and the limit is 1 s");

        clock.Clock.Advance(Poll);
        clock.EndPoll();
        Func<Task> wait = () => keying.WaitAsync(Token);
        await wait.Should().ThrowAsync<TimeoutException>();
        said.Should().ContainSingle().Which.Should().Be(
            "flex: the radio did not answer the keyup in 1 s; treating it as gone");
    }

    [Fact]
    public async Task A_Keyup_Given_Up_With_The_Session_Up_Is_Unkeyed_Now_And_Again_When_It_Finishes()
    {
        // The abandoned keyup can still send its xmit 1 after it is given up on; nothing else
        // would ever unkey it.
        var clock = new PollingWaiter();
        var inner = new SlowKeyPtt();
        var ptt = new BoundedFlexPtt(inner, () => true, keyLimit: TimeSpan.FromMilliseconds(300), time: clock.Clock, wait: clock.AsWait);

        Task keying = Task.Run(ptt.Key, Token);
        await clock.PassOnePoll();
        await clock.PassOnePoll();
        await clock.PassOnePoll();
        Func<Task> wait = () => keying.WaitAsync(Token);
        await wait.Should().ThrowAsync<TimeoutException>();

        await inner.Unkeys.WaitAsync(Token);
        inner.Unkeys.CurrentCount.Should().Be(0, "one unkey goes at once, for a claim already made");

        inner.Answer.Set();
        await inner.Unkeys.WaitAsync(Token);
    }

    [Fact]
    public async Task A_Keyup_Given_Up_Because_The_Session_Ended_Sends_No_Unkey()
    {
        var clock = new PollingWaiter();
        var inner = new SlowKeyPtt();
        var ptt = new BoundedFlexPtt(inner, () => false, keyLimit: TimeSpan.FromMinutes(1), time: clock.Clock, wait: clock.AsWait);

        Task keying = Task.Run(ptt.Key, Token);
        await clock.PassOnePoll();
        Func<Task> wait = () => keying.WaitAsync(Token);
        await wait.Should().ThrowAsync<TimeoutException>();

        inner.Answer.Set();
        await inner.KeyReturned.Task.WaitAsync(Token);

        inner.Unkeys.CurrentCount.Should().Be(0, "there is no session left to unkey through");
    }

    [Fact]
    public void A_Radio_That_Answers_Is_Keyed_And_Unkeyed_As_Before()
    {
        var inner = new CountingPtt();
        var ptt = new BoundedFlexPtt(
            inner, () => true, keyLimit: TimeSpan.FromSeconds(5), time: new FakeTimeProvider(),
            wait: (running, _) =>
            {
                running.Wait(Token);
                return true;
            });

        ptt.Key();
        ptt.Unkey();

        inner.Keys.Should().Be(1);
        inner.Unkeys.Should().Be(1);
    }
}
