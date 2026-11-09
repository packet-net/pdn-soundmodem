using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Issue #790: once the daemon starts stopping, the process ends within 20 s whatever is still
/// running. GB7RDG sat off the air for 21 minutes after its Flex ended the session, still taking
/// KISS hosts and a transmit lease, until systemd SIGKILLed it.
/// </summary>
/// <remarks>
/// The daemon wires it exactly as these tests do: the host's token starts the clock
/// (<c>cancellation.Token.Register(shutdown.Begin)</c>), the radio loss cancels the token, and
/// the exit is <see cref="Environment.Exit"/>, here a list the test reads.
/// </remarks>
public class ShutdownDeadlineTests
{
    private sealed class Harness : IDisposable
    {
        internal Harness()
        {
            Deadline = new ShutdownDeadline(Journal.Add, Exits.Add, Time);
            Cancellation.Token.Register(Deadline.Begin);
        }

        internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 8, 11, 59, 13, TimeSpan.Zero));

        internal ShutdownDeadline Deadline { get; }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal List<string> Journal { get; } = [];

        internal List<int> Exits { get; } = [];

        /// <summary>What FlexStationDevice's Disconnected handler does, through DeviceOpenContext.RadioLost.</summary>
        internal void RadioSessionEnds() => Cancellation.Cancel();

        public void Dispose() => Cancellation.Dispose();
    }

    /// <summary>A long-running worker that never looks at its token: the hosted service that
    /// will not stop.</summary>
    private sealed class StubbornWorker : IAsyncDisposable
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => new(_never.Task);
    }

    [Fact]
    public void A_Radio_Loss_With_A_Worker_That_Will_Not_Stop_Exits_With_Code_1_At_The_Deadline()
    {
        using var harness = new Harness();
        IAsyncDisposable worker = harness.Deadline.Track("Flex radio session", new StubbornWorker());
        using (harness.Deadline.Track("KISS servers", new Disposer(() => { })))
        {
            harness.RadioSessionEnds();

            // The orderly shutdown starts closing, in the daemon's order, and sticks on the worker.
            Task closing = worker.DisposeAsync().AsTask();

            harness.Time.Advance(ShutdownDeadline.Limit - TimeSpan.FromSeconds(1));
            harness.Exits.Should().BeEmpty("the orderly shutdown still has its 20 s");

            harness.Time.Advance(TimeSpan.FromSeconds(1));

            harness.Exits.Should().Equal([1], "exit 1 is the code the unit's Restart=on-failure restarts");
            closing.IsCompleted.Should().BeFalse("the worker never stopped; the exit did not wait for it");
            harness.Journal.Should().ContainSingle().Which.Should().Be(
                "shutdown: not finished 20 s after it began (still open, in closing order: KISS servers, "
                + "Flex radio session); exiting with code 1 so the service restarts");
        }
    }

    [Fact]
    public void The_Line_Names_The_Part_It_Was_Stuck_On_First()
    {
        using var harness = new Harness();
        _ = harness.Deadline.Track("frame log", new StubbornWorker());
        IAsyncDisposable flex = harness.Deadline.Track("Flex radio session", new StubbornWorker());
        var transmitter = new TaskCompletionSource();
        harness.Deadline.Watch("transmitter", transmitter.Task);
        IDisposable receiveLoop = harness.Deadline.Running("receive loop");

        harness.RadioSessionEnds();
        receiveLoop.Dispose();
        transmitter.SetResult();
        _ = flex.DisposeAsync();

        harness.Time.Advance(ShutdownDeadline.Limit);

        harness.Journal.Should().ContainSingle().Which.Should().Contain(
            "still open, in closing order: Flex radio session, frame log)",
            "the receive loop and the transmitter had stopped; the Flex session is where it stuck");
    }

    [Fact]
    public void An_Orderly_Shutdown_That_Finishes_In_Time_Never_Exits()
    {
        using var harness = new Harness();
        using (harness.Deadline.Track("Flex radio session", new Disposer(() => { })))
        {
            harness.RadioSessionEnds();
        }

        // Disposed last of all in the daemon, once everything else has closed.
        harness.Deadline.Dispose();
        harness.Time.Advance(TimeSpan.FromMinutes(5));

        harness.Exits.Should().BeEmpty();
        harness.Journal.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_Is_Refused_And_No_Clock_Runs_Until_Stopping_Begins()
    {
        using var harness = new Harness();
        harness.Deadline.IsStopping.Should().BeFalse();
        harness.Deadline.RefusalReason.Should().BeNull();

        harness.Time.Advance(TimeSpan.FromHours(1));
        harness.Exits.Should().BeEmpty("a station running normally has no deadline");

        harness.RadioSessionEnds();
        harness.Deadline.IsStopping.Should().BeTrue();
        harness.Deadline.RefusalReason.Should().Be(ShutdownDeadline.Refusal);
    }

    [Fact]
    public void A_Second_Signal_Does_Not_Restart_The_Clock()
    {
        // systemctl restart's SIGTERM arriving after a radio loss used to do nothing at all; it
        // must not buy the hung shutdown more time either.
        using var harness = new Harness();
        _ = harness.Deadline.Track("Flex radio session", new StubbornWorker());

        harness.RadioSessionEnds();
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        harness.Deadline.Begin();
        harness.Time.Advance(TimeSpan.FromSeconds(5));

        harness.Exits.Should().Equal([1]);
    }

    [Fact]
    public void A_Part_The_Station_Does_Not_Have_Is_Not_Named()
    {
        using var harness = new Harness();
        _ = harness.Deadline.Track("rig", (IAsyncDisposable?)null);
        _ = harness.Deadline.Track(null, new Disposer(() => { }));

        harness.RadioSessionEnds();
        harness.Time.Advance(ShutdownDeadline.Limit);

        harness.Journal.Should().ContainSingle().Which.Should().Contain("every tracked part had closed");
        harness.Exits.Should().Equal([1]);
    }

    [Fact]
    public void The_Line_Is_Plain_Ascii()
    {
        using var harness = new Harness();
        _ = harness.Deadline.Track("Flex radio session", new StubbornWorker());
        harness.RadioSessionEnds();
        harness.Time.Advance(ShutdownDeadline.Limit);

        harness.Journal.Should().OnlyContain(l => l.All(c => c < 0x80));
        ShutdownDeadline.Refusal.All(c => c < 0x80).Should().BeTrue();
    }
}
