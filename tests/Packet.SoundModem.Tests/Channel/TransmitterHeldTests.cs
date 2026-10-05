using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A keyup refused with <see cref="TransmitterHeldException"/> costs only the transmitter whose
/// keyup it was; any other failure from the PTT still costs everything queued.
/// </summary>
public class TransmitterHeldTests
{
    private const int SampleRate = 12000;

    /// <summary>Refuses keyups with the exception it is given: every one, or only the first.</summary>
    private sealed class RefusingPtt(Func<Exception> refusal) : IPttControl
    {
        internal bool RefuseOnce { get; init; }

        internal int Keys;

        private int _refused;

        public void Key()
        {
            if (!RefuseOnce || Interlocked.Increment(ref _refused) == 1)
            {
                throw refusal();
            }

            Interlocked.Increment(ref Keys);
        }

        public void Unkey()
        {
        }
    }

    private static (SoundModemChannel Channel, FakeAudioOutput Output) Station()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        return (channel, new FakeAudioOutput(SampleRate));
    }

    [Fact]
    public async Task A_Held_Keyup_Refuses_Only_That_Transmitter()
    {
        (SoundModemChannel channel, FakeAudioOutput output) = Station();
        var ptt = new RefusingPtt(() => new TransmitterHeldException("the rig is somewhere else")) { RefuseOnce = true };
        using var stop = new CancellationTokenSource();

        object a = new();
        object b = new();
        Task first = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: false, source: a);
        Task second = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: false, source: b);
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stop.Token);

        // Whichever keyup came first was refused, and only its transmitter's frame with it.
        Task settled = Task.WhenAll(
            first.ContinueWith(_ => { }, TaskScheduler.Default),
            second.ContinueWith(_ => { }, TaskScheduler.Default));
        await settled.WaitAsync(TimeSpan.FromSeconds(30));

        new[] { first, second }.Count(t => t.IsFaulted).Should().Be(1);
        new[] { first, second }.Single(t => t.IsFaulted).Exception!.InnerException
            .Should().BeOfType<TransmitterHeldException>();
        new[] { first, second }.Single(t => !t.IsFaulted).IsCompletedSuccessfully.Should().BeTrue();
        ptt.Keys.Should().Be(1);

        await stop.CancelAsync();
        Func<Task> ended = () => transmitter;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Any_Other_Key_Failure_Still_Fails_Everything_Queued()
    {
        (SoundModemChannel channel, FakeAudioOutput output) = Station();
        var ptt = new RefusingPtt(() => new IOException("the PTT lead is out"));
        using var stop = new CancellationTokenSource();

        object a = new();
        object b = new();
        Task first = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: false, source: a);
        Task second = channel.EnqueueTransmit(_ => new float[1200], rejected: null, ownsChannelTiming: false, source: b);
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stop.Token);

        Func<Task> both = () => Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        await both.Should().ThrowAsync<IOException>();
        first.IsFaulted.Should().BeTrue();
        second.IsFaulted.Should().BeTrue();
        ptt.Keys.Should().Be(0);

        await stop.CancelAsync();
        Func<Task> ended = () => transmitter;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }
}
