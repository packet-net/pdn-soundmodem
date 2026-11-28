using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The shared pump must never have two threads inside one channel's receive path. The bad interleaving is built, not hoped for: the pump thread is held inside the second rig's receive tap, while the
/// first rig has a keyup queued or running, until that keyup's own thread radiates into the same channel. If the pump feeds the second rig at such a time, the tap sees two threads at once.
/// </summary>
public class StationRigPumpTests
{
    private static void Reached(ManualResetEventSlim gate, string because)
    {
        if (!gate.Wait(Eventually.HangGuard))
        {
            throw new TimeoutException($"never happened: {because}");
        }
    }

    [Fact]
    public async Task The_pump_never_feeds_a_channel_while_the_other_rigs_keyup_radiates_into_it()
    {
        await using var a = new StationRig(seed: 11);
        await using var b = new StationRig(seed: 12);
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        b.Air.Timeline = timeline;
        a.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

        var radiating = new ThreadLocal<bool>();
        var keyupWriting = new ManualResetEventSlim();
        var overlapRecorded = new ManualResetEventSlim();
        int inside = 0;
        bool gating = true;
        bool overlap = false;
        a.Air.Radiated = chunk =>
        {
            radiating.Value = true;
            keyupWriting.Set();
            b.Channel.ProcessReceive(chunk);
            radiating.Value = false;
        };
        b.Air.Radiated = chunk => a.Channel.ProcessReceive(chunk);
        b.Channel.AddReceiveTap(_ =>
        {
            int now = Interlocked.Increment(ref inside);
            try
            {
                if (now > 1)
                {
                    // Two threads in this channel's receive path.
                    overlap = true;
                    gating = false;
                    overlapRecorded.Set();
                }
                else if (!radiating.Value && gating && (a.Channel.TransmitQueued || a.Air.Keyed))
                {
                    // The feeding thread, with the first rig's keyup queued or running: hold it here until that keyup radiates into this same channel.
                    Reached(keyupWriting, "the first rig's keyup to radiate");
                    Reached(overlapRecorded, "the keyup's thread to enter the channel while the feeding thread is in it");
                }
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        });
        a.StartTransmitter();
        b.StartTransmitter();

        a.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(() => b.Received.Length >= 1, "the datagram to reach the other station", a, b);

        overlap.Should().BeFalse("the pump waits out every keyup before it feeds another channel");
        b.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task The_pump_waits_for_a_finished_transmissions_signal_before_it_feeds_the_next_block()
    {
        // A transmission's task completes on the transmitter's thread, and the host posts the signal that it is over from a continuation on the thread pool. On a busy pool that continuation can sit
        // behind the pump, queued on the very thread the pump runs on, which feeds block after block without yielding while nothing is keyed. The host cannot hand over the station's next request until it has
        // that signal, so with both stations' signals stuck nothing more is ever keyed and the pump feeds out its ten minutes (#860).
        // Built here: the pump runs on a thread of its own, and the hosts' signals run on that thread only when the pump is waiting and nothing is keyed. The pump has to wait for them to get them.
        using var pump = new PumpThread();
        await using var a = new StationRig(seed: 11, signalScheduler: pump.WhenQuiet, cold: true);
        await using var b = new StationRig(seed: 12, signalScheduler: pump.WhenQuiet, cold: true);
        pump.Quiet = () => !a.Air.Keyed && !b.Air.Keyed && !a.Channel.ChannelBusy && !b.Channel.ChannelBusy;
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        b.Air.Timeline = timeline;
        a.Air.Radiated = chunk => b.Channel.ProcessReceive(chunk);
        b.Air.Radiated = chunk => a.Channel.ProcessReceive(chunk);
        a.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);
        b.Host.SetIdentity("M0XYZ").Should().Be(IdentityResult.Accepted);
        a.StartTransmitter();
        b.StartTransmitter();

        a.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        b.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("M0XYZ-1"), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        await pump.Run(() => StationRig.PumpAsync(
            () => a.Identified.Length >= 1 && b.Identified.Length >= 1 && a.MorseSent.Length >= 1 && b.MorseSent.Length >= 1,
            "each station to identify and be heard doing it",
            a,
            b));

        a.Identified.Select(i => i.Callsign).Should().Equal(["M0XYZ"]);
        b.Identified.Select(i => i.Callsign).Should().Equal(["G4ABC"]);
    }

    /// <summary>
    /// A thread of its own with a synchronization context, so that everything the pump awaits resumes on it, and a scheduler (<see cref="WhenQuiet"/>) whose work runs on it too, but only when the
    /// pump has nothing to do and <see cref="Quiet"/> holds: a continuation stuck behind a thread that is busy, until that thread waits for it.
    /// </summary>
    private sealed class PumpThread : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _pump = new();
        private readonly Queue<Action> _quiet = new();
        private bool _stopping;

        public PumpThread()
        {
            WhenQuiet = new QuietScheduler(this);
            var thread = new Thread(Loop) { IsBackground = true, Name = "station-rig pump" };
            thread.Start();
        }

        public TaskScheduler WhenQuiet { get; }

        public Func<bool> Quiet { get; set; } = () => true;

        public Task Run(Func<Task> body)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Add(_pump, async () =>
            {
                try
                {
                    await body();
                    done.SetResult();
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            });
            return done.Task;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopping = true;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        private void Add(Queue<Action> queue, Action work)
        {
            lock (_gate)
            {
                queue.Enqueue(work);
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            while (true)
            {
                Action work;
                lock (_gate)
                {
                    while (true)
                    {
                        if (_stopping)
                        {
                            return;
                        }

                        if (_pump.Count > 0)
                        {
                            work = _pump.Dequeue();
                            break;
                        }

                        if (_quiet.Count > 0 && Quiet())
                        {
                            work = _quiet.Dequeue();
                            break;
                        }

                        // Woken by new work; and by the end of a keyup, which always posts the pump's own continuation, so a change of Quiet is never missed.
                        System.Threading.Monitor.Wait(_gate);
                    }
                }

                work();
            }
        }

        private sealed class Context(PumpThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => owner.Add(owner._pump, () => d(state));

            public override SynchronizationContext CreateCopy() => this;
        }

        private sealed class QuietScheduler(PumpThread owner) : TaskScheduler
        {
            protected override void QueueTask(Task task) => owner.Add(owner._quiet, () => TryExecuteTask(task));

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

            protected override IEnumerable<Task> GetScheduledTasks() => [];
        }
    }
}
