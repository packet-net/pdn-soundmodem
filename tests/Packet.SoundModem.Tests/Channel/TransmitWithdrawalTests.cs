using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Taking a queued transmission back off the channel, so it never keys the radio.
/// </summary>
/// <remarks>
/// <para>Emptying a burst is not enough and never was: the transmitter keys PTT and only then
/// asks the item for its audio, so a caller that has changed its mind still gets a keyup, and on
/// a channel that stays busy for minutes it arrives long after the operator has walked away. The
/// operator's test transmission is the first caller that can change its mind, and this is what it
/// needs.</para>
/// <para>Nothing else uses it. The token defaults to one that cannot be cancelled, so every
/// existing path takes no registration and behaves exactly as it did.</para>
/// </remarks>
/// <remarks>
/// Every test runs on a <see cref="VirtualAir.Clock"/>. A transmission "stuck behind carrier sense"
/// is the transmitter parked on its slot delays while the test moves the clock; nothing waits on the
/// wall clock. Where a test shows that something did not happen later, it ends with a transmission
/// queued behind the withdrawn one and looks at what that one carried and what keyed the radio.
/// </remarks>
public class TransmitWithdrawalTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly VirtualAir.Clock _time = new();
    private readonly SoundModemChannel _channel;
    private readonly VirtualAir.PacedSink _output;
    private readonly SignallingPtt _ptt = new();

    // Cancelled by the test's disposal; the clock's own hang guards turn a stuck transmitter into a failure.
    private readonly CancellationTokenSource _stop = new();
    private readonly Busy _band = new();
    private Task? _transmitter;

    /// <summary>
    /// A PTT that records what reached it and lets a test wait for an event count on the simulated
    /// clock, so the end of a keyup is a signal rather than a guess.
    /// </summary>
    private sealed class SignallingPtt : IPttControl
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

        public string[] Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public void Key() => Add("key");

        public void Unkey() => Add("unkey");

        /// <summary>Completes once at least <paramref name="count"/> events have reached the PTT.</summary>
        public Task WhenAsync(int count)
        {
            lock (_gate)
            {
                if (_events.Count >= count)
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }

        private void Add(string what)
        {
            lock (_gate)
            {
                _events.Add(what);
                foreach ((int count, TaskCompletionSource reached) in _waiters)
                {
                    if (_events.Count >= count)
                    {
                        reached.TrySetResult();
                    }
                }
            }
        }
    }

    /// <summary>A modem whose carrier sense this test drives.</summary>
    private sealed class Busy : IModem
    {
        public string Mode => "afsk1200";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy { get; set; } = true;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default!);
    }

    public TransmitWithdrawalTests()
    {
        _channel = new SoundModemChannel(SampleRate, _time, randomSeed: 5);
        _output = new VirtualAir.PacedSink(SampleRate, _time, keepSamples: true);
    }

    public ValueTask InitializeAsync()
    {
        _channel.AddModem(0, _ => _band);
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 10;
        _channel.Csma.TxTailMilliseconds = 0;
        _transmitter = _channel.RunTransmitterAsync(_output, _ptt, _stop.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static float[] Tone(int samples)
    {
        var audio = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            audio[n] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / SampleRate);
        }

        return audio;
    }

    /// <summary>Only ever turns a hang into a failure: nothing passes or fails on how long it took.</summary>
    private static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_Withdrawn_Transmission_Never_Keys_The_Radio_Even_When_The_Channel_Clears()
    {
        using var withdraw = new CancellationTokenSource();
        var rendered = false;
        DateTimeOffset start = _time.GetUtcNow();
        Task queued = _channel.EnqueueTransmit(
            _ =>
            {
                rendered = true;
                return Tone(SampleRate);
            },
            withdraw: withdraw.Token);

        // Queued and stuck behind carrier sense, which is where a test transmission waits.
        await _time.AdvanceToUnlessAsync(start + TimeSpan.FromMilliseconds(200), queued);
        _ptt.Events.Should().BeEmpty("the band is busy, so nothing has keyed");

        await withdraw.CancelAsync();
        await FluentActions.Awaiting(() => queued.WaitAsync(HangAfter))
            .Should().ThrowAsync<OperationCanceledException>(
                "the caller is told its transmission was taken back");

        // The whole point: the channel clears and nothing happens. The way to see nothing happen
        // is to send something behind it: the next keyup is that one's alone, and only its audio
        // goes out.
        _band.ChannelBusy = false;
        Task behind = _channel.EnqueueTransmit(_ => Tone(600));
        await _time.RunUntilAsync(Task.WhenAll(behind, _ptt.WhenAsync(2)));
        _ptt.Events.Should().Equal(["key", "unkey"], "a withdrawn transmission never keys, then or later");
        _output.Samples().Should().HaveCount(600, "only the one sent behind it went out");
        rendered.Should().BeFalse("it was never even asked for its audio");
    }

    [Fact]
    public async Task Withdrawing_One_Leaves_The_Others_In_Order()
    {
        using var withdraw = new CancellationTokenSource();
        var source = new object();
        var sent = new List<string>();

        Task first = _channel.EnqueueTransmit(_ => { sent.Add("first"); return Tone(600); }, source: source);
        Task doomed = _channel.EnqueueTransmit(
            _ => { sent.Add("doomed"); return Tone(600); }, source: source, withdraw: withdraw.Token);
        Task last = _channel.EnqueueTransmit(_ => { sent.Add("last"); return Tone(600); }, source: source);

        await withdraw.CancelAsync();
        _band.ChannelBusy = false;

        // A transmission's task completes when its audio has left the device, and the unkey comes
        // after the tail and the whole loop, so the keyup's end is waited for as its own signal.
        await _time.RunUntilAsync(Task.WhenAll(first, last, _ptt.WhenAsync(2)));
        sent.Should().Equal(["first", "last"]);
        doomed.IsCanceled.Should().BeTrue();
        _ptt.Events.Should().Equal(["key", "unkey"], "the survivors still share one keyup");
    }

    [Fact]
    public async Task Withdrawing_A_Transmission_That_Is_Already_On_The_Air_Changes_Nothing()
    {
        // There is nothing to be done about one whose audio is already in the sound card, so the
        // transmission completes as what it was rather than pretending it did not happen. It is
        // the caller's business to render nothing if it no longer wants to be heard.
        using var withdraw = new CancellationTokenSource();
        _band.ChannelBusy = false;

        Task queued = _channel.EnqueueTransmit(_ => Tone(SampleRate / 4), withdraw: withdraw.Token);
        await _time.RunUntilAsync(queued);

        await withdraw.CancelAsync();
        queued.IsCompletedSuccessfully.Should().BeTrue();
        _output.Samples().Should().NotBeEmpty();
        await _time.RunUntilAsync(_ptt.WhenAsync(2));
        _ptt.Events.Should().Equal(["key", "unkey"]);
    }

    [Fact]
    public async Task A_Transmission_Withdrawn_Before_It_Was_Ever_Queued_Is_Cancelled()
    {
        // The other place a caller waits: an ARDOP session holding the channel, where the
        // transmission has not reached the queue at all yet.
        _channel.TransmitInhibit = () => true;
        using var withdraw = new CancellationTokenSource();

        Task queued = _channel.EnqueueTransmit(_ => Tone(600), withdraw: withdraw.Token);
        await _time.AdvanceToUnlessAsync(_time.GetUtcNow() + TimeSpan.FromMilliseconds(150), queued);
        await withdraw.CancelAsync();

        await FluentActions.Awaiting(() => queued.WaitAsync(HangAfter))
            .Should().ThrowAsync<OperationCanceledException>();

        // The hold lifts and the band clears: the next keyup is the one sent behind it, alone.
        _channel.TransmitInhibit = null;
        _band.ChannelBusy = false;
        Task behind = _channel.EnqueueTransmit(_ => Tone(600));
        await _time.RunUntilAsync(Task.WhenAll(behind, _ptt.WhenAsync(2)));
        _ptt.Events.Should().Equal(["key", "unkey"]);
        _output.Samples().Should().HaveCount(600);
    }

    [Fact]
    public async Task Cancelling_Across_A_Take_Leaves_The_Channel_Answering()
    {
        // Six hundred cancellations racing the transmitter's dequeue, from four transmitters at
        // once, each one having to end as either sent or withdrawn - and the channel still
        // answering afterwards.
        //
        // The race is between real threads, so this one keeps them: nothing in it waits for time to
        // pass (the band is clear and the roll always wins), and the virtual clock is moved by the
        // sound card alone. The limits are hang guards for a wedged lock, not assertions.
        //
        // What this does NOT do is force the interleaving that nearly shipped here: the
        // transmitter holds the channel's lock to dequeue an item and then lets go of its
        // withdrawal registration, and if that let-go WAITS for a callback that is already
        // running - and that callback's first act is to take the same lock - neither ever
        // returns. The lock is then held for the life of the process, the transmitter stops, and
        // every enqueue behind it blocks with no log line and nothing to restart it. The window
        // is a few instructions wide and this test passes with the faulty call in place, so what
        // actually prevents it is Unregister and the comment beside it in TakeFrom; this is the
        // coarser guard, and it is honest about being one.
        _band.ChannelBusy = false;

        var racing = new List<Task>();
        for (int thread = 0; thread < 4; thread++)
        {
            racing.Add(Task.Run(async () =>
            {
                var source = new object();
                for (int attempt = 0; attempt < 150; attempt++)
                {
                    using var withdraw = new CancellationTokenSource();
                    Task queued = _channel.EnqueueTransmit(
                        _ => Tone(60), source: source, withdraw: withdraw.Token);
                    Task firing = Task.Run(withdraw.Cancel);

                    // Either outcome is legal - taken and sent, or withdrawn first. What must
                    // never happen is neither.
                    try
                    {
                        await queued.WaitAsync(HangAfter);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await firing.WaitAsync(HangAfter);
                }
            }));
        }

        await Task.WhenAll(racing).WaitAsync(HangAfter);

        // And the channel still answers. A wedged lock hangs this rather than failing it, which
        // is what the waits above are for.
        await _time.RunUntilAsync(_channel.EnqueueTransmit(_ => Tone(120)));
        _ptt.Events.Should().NotBeEmpty("the transmitter is still running");
    }

    [Fact]
    public async Task A_Transmission_With_No_Token_Behaves_Exactly_As_It_Always_Did()
    {
        _band.ChannelBusy = false;

        Task sent = _channel.EnqueueTransmit(_ => Tone(SampleRate / 8));
        await _time.RunUntilAsync(Task.WhenAll(sent, _ptt.WhenAsync(2)));

        _ptt.Events.Should().Equal(["key", "unkey"]);
        _output.Samples().Should().HaveCount(SampleRate / 8, "TXTAIL is off in this rig");
    }
}
