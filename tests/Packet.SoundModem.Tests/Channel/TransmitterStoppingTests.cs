using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Issue #790: a station that starts stopping in the middle of a long keyup ends it, unkeyed,
/// well inside the shutdown's 20 s deadline, rather than playing the rest of the burst.
/// </summary>
/// <remarks>
/// Keyups can run past 20 s (an MS110D burst, a 30 s test tone, a multi-frame HF keyup), and the
/// deadline ends the process with <see cref="Environment.Exit"/>, which unkeys nothing: a CM108
/// would stay keyed until something opened it again. So the transmitter itself stops at once and
/// unkeys normally, cutting a frame short if it has to.
/// </remarks>
public class TransmitterStoppingTests
{
    private const int SampleRate = 12000;

    private static SoundModemChannel Channel()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 3);
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 0;
        channel.Csma.TxTailMilliseconds = 0;
        return channel;
    }

    [Fact]
    public async Task A_Long_Burst_In_Progress_When_Stopping_Begins_Ends_With_An_Unkey_Well_Inside_The_Deadline()
    {
        SoundModemChannel channel = Channel();
        var rig = new GatedRig(SampleRate);
        using var stopping = new CancellationTokenSource();
        // On a thread of its own: with work already queued the loop keys without yielding.
        Task transmitter = Task.Run(() => channel.RunTransmitterAsync(rig, rig, stopping.Token));

        // A 30 s burst: longer than the whole deadline, so it goes to the device in 5 s chunks.
        Task sent = channel.EnqueueTransmit(_ => new float[SampleRate * 30]);
        await rig.Keyed.Task.WaitAsync(GatedRig.HangAfter);

        // The first chunk is at the device and "playing" (held at the gate). Stopping begins now.
        await rig.NextWriteTakenAsync();
        await stopping.CancelAsync();

        // Let everything through, so that any chunk the transmitter still meant to play would be
        // taken: the unkey must come after the chunk in hand and one short fade-out block only.
        rig.Admit(100);
        await rig.Unkeyed.Task.WaitAsync(GatedRig.HangAfter);

        rig.Events.Should().Equal(["key", "write", "write", "unkey"]);
        rig.Writes[0].Should().Be(SampleRate * 5, "long audio goes to the device five seconds at a time");
        rig.Writes[1].Should().BeLessThan(rig.Writes[0], "after the stop only a short fade-out block goes out, not the next chunk");
        await sent.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>(
            "a burst cut short never went out whole, so it is not reported sent");
        await transmitter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Multi_Frame_Keyup_Takes_No_Further_Frame_Once_Stopping_Begins()
    {
        SoundModemChannel channel = Channel();
        var rig = new GatedRig(SampleRate);
        using var stopping = new CancellationTokenSource();

        // Five 0.8 s frames from one transmitter, queued before it starts so they share a keyup.
        var frames = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            frames.Add(channel.EnqueueTransmit(_ => new float[SampleRate * 8 / 10], source: this));
            _ = frames[^1].ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }

        // On a thread of its own: with work already queued the loop keys without yielding.
        Task transmitter = Task.Run(() => channel.RunTransmitterAsync(rig, rig, stopping.Token));
        await rig.Keyed.Task.WaitAsync(GatedRig.HangAfter);

        // The first frame is let through; the second is at the device when stopping begins.
        await rig.NextWriteTakenAsync();
        rig.Admit(1);
        await rig.NextWriteTakenAsync();
        await stopping.CancelAsync();

        // Everything is let through from here on, so any frame the transmitter still took would
        // be written.
        rig.Admit(100);
        await rig.Unkeyed.Task.WaitAsync(GatedRig.HangAfter);

        // Counted by write rather than by task: identical delegate frames are merged by the
        // queue optimiser, which answers the merged ones when the first goes out.
        rig.Events.Should().Equal(["key", "write", "write", "unkey"], "no frame was taken once stopping began");
        await transmitter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }
}
