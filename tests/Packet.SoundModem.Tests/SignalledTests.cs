using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

public class SignalledTests
{
    // As in EventuallyTests: the one place a real short limit is right, because what is proved is that
    // a limit which runs out fails in the caller's words. Nothing here asserts how long anything took.
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_Condition_Already_True_Returns_At_Once()
    {
        var list = new SignalledList<int>();
        list.Add(1);

        await list.WhenAsync(items => items.Count == 1, "one item");
    }

    [Fact]
    public async Task A_Change_After_The_Wait_Starts_Wakes_It()
    {
        var list = new SignalledList<string>();

        Task waiting = list.WhenAsync(items => items.Contains("b"), "b to arrive");
        waiting.IsCompleted.Should().BeFalse("nothing has been added yet");
        list.Add("a");
        waiting.IsCompleted.Should().BeFalse("a is not what it waits for");
        list.Add("b");

        await Eventually.SignalAsync(waiting, "the wait to end on the change");
    }

    [Fact]
    public async Task A_Change_Between_The_Look_And_The_Wait_Is_Not_Lost()
    {
        var list = new SignalledList<int>();
        int looks = 0;

        // The first look finds the condition false and, before the wait is entered, an item is added from
        // inside the look itself: the change has already happened when the wait begins, and the signal taken
        // before the look is what carries it.
        Task waiting = list.WhenAsync(
            items =>
            {
                if (Interlocked.Increment(ref looks) == 1)
                {
                    list.Add(7);
                    return false;
                }

                return items.Count == 1;
            },
            "the item added during the first look");

        await Eventually.SignalAsync(waiting, "the wait to see the change made during the look");
        looks.Should().Be(2);
    }

    [Fact]
    public async Task A_Hang_Fails_Saying_What_Was_Awaited_And_What_The_List_Held()
    {
        var list = new SignalledList<string>();
        list.Add("hello");
        list.Add("frame");

        Func<Task> wait = () => list.WhenAsync(items => items.Count == 3, "the third message", Short);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*the third message*never happened*2 item(s)*hello*frame*");
    }

    [Fact]
    public async Task A_Counter_Wakes_On_Each_Increment_And_Reports_Its_Value_On_A_Hang()
    {
        var counter = new SignalledCounter();

        Task waiting = counter.WhenAsync(v => v >= 2, "two");
        counter.Increment();
        waiting.IsCompleted.Should().BeFalse();
        counter.Increment();
        await Eventually.SignalAsync(waiting, "the counter to reach two");

        Func<Task> never = () => counter.WhenAsync(v => v >= 9, "nine", Short);
        (await never.Should().ThrowAsync<TimeoutException>()).WithMessage("*nine*never happened*the value 2*");
    }

    [Fact]
    public void Reads_Are_Snapshots_And_Work_Like_A_List()
    {
        var list = new SignalledList<int>();
        list.Add(1);
        list.Add(2);

        list.Count.Should().Be(2);
        list[1].Should().Be(2);
        list.Where(i => i > 1).Should().Equal(2);
        list.ToArray().Should().Equal(1, 2);
    }
}
