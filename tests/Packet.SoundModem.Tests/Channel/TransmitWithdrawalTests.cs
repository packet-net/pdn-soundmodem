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
public class TransmitWithdrawalTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 5);
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly RecordingPtt _ptt = new();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Busy _band = new();
    private Task? _transmitter;

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            giveUp.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, giveUp.Token);
        }
    }

    [Fact]
    public async Task A_Withdrawn_Transmission_Never_Keys_The_Radio_Even_When_The_Channel_Clears()
    {
        using var withdraw = new CancellationTokenSource();
        var rendered = false;
        Task queued = _channel.EnqueueTransmit(
            _ =>
            {
                rendered = true;
                return Tone(SampleRate);
            },
            withdraw: withdraw.Token);

        // Queued and stuck behind carrier sense, which is where a test transmission waits.
        await Task.Delay(200);
        _ptt.Events.Should().BeEmpty("the band is busy, so nothing has keyed");

        await withdraw.CancelAsync();
        await FluentActions.Awaiting(() => queued.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<OperationCanceledException>(
                "the caller is told its transmission was taken back");

        // The whole point: the channel clears and nothing happens.
        _band.ChannelBusy = false;
        await Task.Delay(600);
        _ptt.Events.Should().BeEmpty("a withdrawn transmission never keys, then or later");
        _output.Snapshot().Should().BeEmpty();
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

        await Task.WhenAll(first, last).WaitAsync(TimeSpan.FromSeconds(10));
        sent.Should().Equal(["first", "last"]);
        doomed.IsCanceled.Should().BeTrue();

        // Waited for rather than asserted outright: a transmission's task completes when its
        // audio has left the device, and the unkey comes after the tail and the whole loop, so
        // reading the events straight after the await is racing them.
        await WaitUntilAsync(() => _ptt.Events.Count >= 2);
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
        await queued.WaitAsync(TimeSpan.FromSeconds(10));

        await withdraw.CancelAsync();
        queued.IsCompletedSuccessfully.Should().BeTrue();
        _output.Snapshot().Should().NotBeEmpty();
        await WaitUntilAsync(() => _ptt.Events.Count >= 2);
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
        await Task.Delay(150);
        await withdraw.CancelAsync();

        await FluentActions.Awaiting(() => queued.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<OperationCanceledException>();

        _channel.TransmitInhibit = null;
        _band.ChannelBusy = false;
        await Task.Delay(400);
        _ptt.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_Across_A_Take_Leaves_The_Channel_Answering()
    {
        // Six hundred cancellations racing the transmitter's dequeue, from four transmitters at
        // once, each one having to end as either sent or withdrawn - and the channel still
        // answering afterwards.
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
                        await queued.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await firing.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }));
        }

        await Task.WhenAll(racing).WaitAsync(TimeSpan.FromSeconds(25));

        // And the channel still answers. A wedged lock hangs this rather than failing it, which
        // is what the waits above are for.
        await _channel.EnqueueTransmit(_ => Tone(120)).WaitAsync(TimeSpan.FromSeconds(10));
        _ptt.Events.Should().NotBeEmpty("the transmitter is still running");
    }

    [Fact]
    public async Task A_Transmission_With_No_Token_Behaves_Exactly_As_It_Always_Did()
    {
        _band.ChannelBusy = false;

        await _channel.EnqueueTransmit(_ => Tone(SampleRate / 8)).WaitAsync(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => _ptt.Events.Count >= 2);
        _ptt.Events.Should().Equal(["key", "unkey"]);
        _output.Snapshot().Should().HaveCount(SampleRate / 8, "TXTAIL is off in this rig");
    }
}
