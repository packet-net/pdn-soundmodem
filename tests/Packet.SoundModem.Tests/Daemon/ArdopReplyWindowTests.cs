using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// An ARDOP reply that cannot make its turnaround is taken back, not transmitted late. See
/// <c>ArdopReplyWindow</c> for why a late ARQ frame is worse than a missing one.
/// </summary>
public class ArdopReplyWindowTests
{
    private const int SampleRate = 12000;

    [Fact]
    public void A_Reply_That_Waits_Out_Its_Turnaround_Is_Withdrawn()
    {
        var time = new FakeTimeProvider();
        var window = new ArdopReplyWindow(time);

        CancellationToken turnaround = window.Open();
        turnaround.IsCancellationRequested.Should().BeFalse();

        time.Advance(ArdopReplyWindow.Deadline - TimeSpan.FromMilliseconds(1));
        turnaround.IsCancellationRequested.Should().BeFalse("the turnaround has not passed yet");

        time.Advance(TimeSpan.FromMilliseconds(1));
        turnaround.IsCancellationRequested.Should().BeTrue();

        window.NoteDropped();
        window.TakeDrop().Should().Be(ArdopReplyDrop.Deadline);
    }

    [Fact]
    public void A_Far_End_That_Transmits_Again_Closes_The_Gap_At_Once()
    {
        var time = new FakeTimeProvider();
        var window = new ArdopReplyWindow(time);

        CancellationToken turnaround = window.Open();
        window.FarEndTransmitted();

        turnaround.IsCancellationRequested.Should().BeTrue("the silence the reply belonged in is over");
        window.NoteDropped();
        window.TakeDrop().Should().Be(ArdopReplyDrop.FarEndTransmitted);
    }

    [Fact]
    public void A_Reply_That_Went_Out_Reports_No_Drop()
    {
        var window = new ArdopReplyWindow(new FakeTimeProvider());
        window.Open();
        window.Close();

        window.TakeDrop().Should().Be(ArdopReplyDrop.None);
    }

    [Fact]
    public void A_Drop_Is_Reported_Once()
    {
        var window = new ArdopReplyWindow(new FakeTimeProvider());
        window.Open();
        window.FarEndTransmitted();
        window.NoteDropped();
        window.Close();

        window.TakeDrop().Should().Be(ArdopReplyDrop.FarEndTransmitted);
        window.TakeDrop().Should().Be(ArdopReplyDrop.None, "the burst it belonged to is finished");
    }

    [Fact]
    public void A_Finished_Burst_Does_Not_Drag_The_Next_One_Down()
    {
        var time = new FakeTimeProvider();
        var window = new ArdopReplyWindow(time);

        window.Open();
        window.FarEndTransmitted();
        window.NoteDropped();
        window.Close();
        window.TakeDrop().Should().Be(ArdopReplyDrop.FarEndTransmitted);

        // The next reply is a fresh answer to whatever was just heard.
        CancellationToken next = window.Open();
        next.IsCancellationRequested.Should().BeFalse();
        window.TakeDrop().Should().Be(ArdopReplyDrop.None);
    }

    /// <summary>A modem whose carrier sense this test drives, standing in for a busy band.</summary>
    private sealed class Band : IModem
    {
        public string Mode => "afsk1200";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default!);
    }

    /// <summary>
    /// The whole point, against the real channel: a reply that misses its turnaround puts nothing
    /// on the air at all. The radio must not key for it.
    /// </summary>
    [Fact]
    public async Task A_Reply_That_Misses_Its_Turnaround_Never_Reaches_The_Air()
    {
        var time = new FakeTimeProvider();
        var window = new ArdopReplyWindow(time);
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, _ => new Band());
        channel.Csma.TxDelayMilliseconds = 10;
        channel.Csma.TxTailMilliseconds = 0;

        var output = new FakeAudioOutput(SampleRate);
        var ptt = new RecordingPtt();
        using var stop = new CancellationTokenSource();

        // No transmitter is running, so nothing can drain the queue - the station standing in for
        // one that is mid-keyup on a packet frame it cannot take back.
        CancellationToken turnaround = window.Open();
        Task reply = channel.EnqueueTransmit(
            _ => new float[SampleRate], rejected: null, ownsChannelTiming: true,
            source: new object(), withdraw: turnaround);

        time.Advance(ArdopReplyWindow.Deadline + TimeSpan.FromMilliseconds(1));

        Func<Task> awaiting = async () => await reply;
        await awaiting.Should().ThrowAsync<OperationCanceledException>(
            "a reply past its turnaround is taken back rather than queued for later");

        // And now that the channel is free, it still must not go out.
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stop.Token);
        await Task.Delay(200);
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().BeEmpty("the radio must not key for a burst that was withdrawn");
    }
}
