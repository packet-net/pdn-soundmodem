using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Keeping the packet modems off the air while something else owns the channel's timing - an
/// ARDOP ARQ session, whose turnarounds an AX.25 frame landing mid-sequence would break. This
/// is what replaced the old rule that an ARDOP channel could carry nothing else.
/// </summary>
/// <remarks>
/// Every test runs on a <see cref="VirtualAir.Clock"/>: a held frame "waits" by the clock being moved
/// while the transmitter is parked on one of its own slot or poll delays, so what is asserted is what
/// the channel did and in what order, and how much simulated time it took where that is the point.
/// </remarks>
public class TransmitInhibitTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly VirtualAir.Clock _time = new();
    private readonly SoundModemChannel _channel;
    private readonly VirtualAir.PacedSink _output;

    // Cancelled by the test's disposal; the clock's own hang guards turn a stuck transmitter into a failure.
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _transmitter;
    private volatile bool _inhibited;

    public TransmitInhibitTests()
    {
        _channel = new SoundModemChannel(SampleRate, _time, randomSeed: 5);
        _output = new VirtualAir.PacedSink(SampleRate, _time);
    }

    public ValueTask InitializeAsync()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        _channel.TransmitInhibit = () => _inhibited;
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private static byte[] Frame()
    {
        byte[] frame = new byte[20];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        return frame;
    }

    [Fact]
    public async Task A_Packet_Frame_Waits_While_The_Channel_Is_Held_And_Goes_Out_After()
    {
        _inhibited = true;

        Task sent = _channel.EnqueueTransmit(0, Frame());

        // Half a second of simulated time, a poll at a time, with the transmitter parked at each one.
        await _time.AdvanceToUnlessAsync(_time.GetUtcNow() + TimeSpan.FromMilliseconds(500), sent);

        sent.IsCompleted.Should().BeFalse("the channel is held");
        _output.Written.Should().BeEmpty("nothing may reach the air during a held session");

        _inhibited = false;
        await _time.RunUntilAsync(sent);

        _output.Written.Should().NotBeEmpty("and it must go out once the hold lifts");
    }

    [Fact]
    public async Task The_Holder_Itself_Is_Never_Blocked_By_Its_Own_Hold()
    {
        // ARDOP's bursts are what the inhibit protects; if they queued behind it, an ARQ
        // session could never transmit and the whole arrangement would deadlock.
        _inhibited = true;

        Task sent = _channel.EnqueueTransmit(
            _ => new float[SampleRate / 10], rejected: null, ownsChannelTiming: true);

        await _time.RunUntilAsync(sent);
        _output.Written.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_Frame_Held_Too_Long_Is_Rejected_Rather_Than_Escaping_Late()
    {
        // An AX.25 host will have retried long before a Winlink session ends; a frame that
        // eventually escapes minutes late is a duplicate, not a delivery.
        _channel.TransmitInhibitTimeout = TimeSpan.FromMilliseconds(300);
        _inhibited = true;
        Exception? reported = null;
        _channel.TransmitRejected += (_, _, reason) => reported = reason;

        // The 300 ms is simulated: the held frame's expiry is a timer on the clock, which the clock
        // reaches by stepping through the transmitter's inhibit polls.
        Func<Task> send = () => _time.RunUntilAsync(_channel.EnqueueTransmit(0, Frame()));

        await send.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("holding the channel"));
        reported.Should().NotBeNull("the rejection must be observable, not just thrown");
        _output.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Held_Frame_That_Expires_Leaves_The_Transmitter_Waiting_For_Work_Not_Polling()
    {
        // The expiry removes the frame while the transmitter is in its inhibit poll. The
        // transmitter used to stay in that loop with nothing queued, polling every millisecond, and
        // on the clock that is a delay armed again inside every step, so the step loop never
        // yields its thread. The frame's answer reaches the test from a continuation the thread
        // pool has to run, and when that continuation is queued on the very thread doing the
        // stepping, which is what a busy pool does, it never runs: the clock was driven ten
        // virtual minutes past a frame that had been rejected at 300 ms (#860). Here that
        // continuation is put on the stepping thread on purpose, every time.
        //
        // A station of its own, so that its transmitter can be started on the stepping thread
        // with the frame already queued: it then runs straight to its first inhibit poll and is
        // parked on it before that call returns, and from there every delay fires on this thread
        // and every next one is armed inside the step that fired it. Nothing is left to the pool.
        var time = new VirtualAir.Clock();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 5);
        var output = new VirtualAir.PacedSink(SampleRate, time);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.TransmitInhibit = () => true;
        channel.TransmitInhibitTimeout = TimeSpan.FromMilliseconds(300);
        using var stop = new CancellationTokenSource();
        using var stepper = new OneThreadScheduler();
        Task transmitter = Task.CompletedTask;

        Task run = stepper.Start(() =>
        {
            Task frame = channel.EnqueueTransmit(0, Frame());
            transmitter = channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
            Task answered = frame
                .ContinueWith(t => t, CancellationToken.None, TaskContinuationOptions.None, stepper)
                .Unwrap();
            return time.RunUntilAsync(answered);
        });

        try
        {
            Func<Task> outcome = () => run;
            await outcome.Should().ThrowAsync<InvalidOperationException>()
                .Where(e => e.Message.Contains("holding the channel"));
            output.Written.Should().BeEmpty();
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await transmitter;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// One thread of its own that first makes one call, outside any task so that continuations may
    /// run inline on it as they would on a pool thread, and then runs whatever was scheduled on it
    /// meanwhile, in order.
    /// </summary>
    private sealed class OneThreadScheduler : TaskScheduler, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Task> _work = [];

        public Task Start(Func<Task> first)
        {
            var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    started.SetResult(first());
                }
                catch (Exception e)
                {
                    started.SetException(e);
                }

                foreach (Task task in _work.GetConsumingEnumerable())
                {
                    TryExecuteTask(task);
                }
            })
            {
                IsBackground = true,
                Name = "virtual-clock stepper",
            };
            thread.Start();
            return started.Task.Unwrap();
        }

        public void Dispose() => _work.CompleteAdding();

        protected override void QueueTask(Task task) => _work.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [.. _work];
    }

    [Fact]
    public async Task A_Timing_Owner_Skips_The_Persistence_Roll_As_Well_As_The_Inhibit()
    {
        // ARDOP runs its own channel discipline against ARQ turnaround budgets, and the busy it
        // would defer to is partly its own signal - a shifted centre sits inside a packet
        // modem's passband and asserts that modem's busy detector. Deferring to yourself is
        // the bug this guards.
        _channel.Csma.Persistence = 0;          // never win the roll
        _channel.Csma.SlotTimeMilliseconds = 5_000;

        DateTimeOffset queued = _time.GetUtcNow();
        Task owned = _channel.EnqueueTransmit(
            _ => new float[SampleRate / 10], rejected: null, ownsChannelTiming: true);

        await _time.RunUntilAsync(owned);
        _output.Written.Should().NotBeEmpty();

        // One lost roll costs a whole slot, so under a slot of simulated time means no roll was waited on.
        (_time.GetUtcNow() - queued).Should().BeLessThan(
            TimeSpan.FromMilliseconds(_channel.Csma.SlotTimeMilliseconds), "it must not have waited on p at all");
    }

    [Fact]
    public async Task A_Shared_Transmission_Still_Takes_The_Roll()
    {
        _channel.Csma.Persistence = 0;
        _channel.Csma.SlotTimeMilliseconds = 5_000;

        Task shared = _channel.EnqueueTransmit(0, Frame());

        // The roll is lost and the next one is a slot (5 s) away, so 600 ms in nothing has gone out.
        await _time.AdvanceToUnlessAsync(_time.GetUtcNow() + TimeSpan.FromMilliseconds(600), shared);

        shared.IsCompleted.Should().BeFalse("CSMA still applies to everything that shares the channel");
        _output.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task With_Nothing_Holding_The_Channel_Frames_Are_Not_Delayed()
    {
        _inhibited = false;

        await _time.RunUntilAsync(_channel.EnqueueTransmit(0, Frame()));

        _output.Written.Should().NotBeEmpty();
    }
}
