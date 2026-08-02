using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A channel whose device cannot transmit — the <c>ubersdr:</c> web-receiver station. The point
/// of saying so on the channel rather than at each caller is that KISS, paging and ARDOP then
/// all get the same answer, immediately, with the same explanation.
/// </summary>
public class ReceiveOnlyTests
{
    private const int SampleRate = 12000;
    private const string Reason = "this station receives only: it listens to a web receiver.";

    private static SoundModemChannel ReceiveOnlyChannel()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7) { ReceiveOnlyReason = Reason };
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        return channel;
    }

    [Fact]
    public async Task A_frame_is_refused_with_the_reason_rather_than_queued()
    {
        SoundModemChannel channel = ReceiveOnlyChannel();
        Exception? reported = null;
        channel.TransmitRejected += (_, _, reason) => reported = reason;

        Func<Task> transmit = () => channel.EnqueueTransmit(0, [0x01, 0x02, 0x03]);

        await transmit.Should().ThrowAsync<InvalidOperationException>().WithMessage(Reason);
        reported.Should().NotBeNull();
        reported!.Message.Should().Be(Reason);
    }

    [Fact]
    public async Task The_refusal_is_immediate_and_not_a_thirty_second_wait()
    {
        // TransmitInhibit holds a frame until the channel frees up, and gives up after 30 s.
        // Nothing here ever frees up, so queueing would turn "cannot" into a long wait ending in
        // the wrong explanation.
        SoundModemChannel channel = ReceiveOnlyChannel();
        channel.TransmitInhibitTimeout = TimeSpan.FromMinutes(5);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> transmit = () => channel.EnqueueTransmit(0, [0x01]);
        await transmit.Should().ThrowAsync<InvalidOperationException>();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Owning_the_channel_timing_does_not_buy_a_transmitter()
    {
        // ARDOP bypasses the inhibit and the persistence roll because it is running the channel's
        // timing. Running the timing of a channel that cannot be keyed changes nothing.
        SoundModemChannel channel = ReceiveOnlyChannel();

        Func<Task> transmit = () => channel.EnqueueTransmit(
            _ => new float[100], rejected: null, ownsChannelTiming: true);

        await transmit.Should().ThrowAsync<InvalidOperationException>().WithMessage(Reason);
    }

    [Fact]
    public void Receiving_is_untouched()
    {
        SoundModemChannel channel = ReceiveOnlyChannel();
        int blocks = 0;
        channel.AddReceiveTap(_ => blocks++);

        channel.ProcessReceive(new float[SampleRate / 10]);

        blocks.Should().Be(1, "a station that cannot transmit is still a station that hears");
    }

    [Fact]
    public async Task Without_the_reason_set_a_transmission_is_queued_as_usual()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new FakeAudioOutput(SampleRate);
        Task transmitter = channel.RunTransmitterAsync(
            output, new M0LTE.Radio.Audio.NullPtt(), cancellation.Token);

        await channel.EnqueueTransmit(0, [0x01, 0x02, 0x03]);

        output.WrittenCount.Should().BeGreaterThan(0);
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
