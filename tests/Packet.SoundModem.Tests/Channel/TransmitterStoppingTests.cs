using System.Diagnostics;
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

    /// <summary>A device that takes real time to play what it is given, as a sound card does,
    /// recording the keying beside it.</summary>
    private sealed class RealTimeRig : IAudioOutput, IPttControl
    {
        private readonly Lock _gate = new();

        public int SampleRate => TransmitterStoppingTests.SampleRate;

        public List<string> Events { get; } = [];

        public long Played;

        public TaskCompletionSource Keyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Unkeyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(ReadOnlySpan<float> samples)
        {
            Thread.Sleep(TimeSpan.FromSeconds(samples.Length / (double)SampleRate));
            Interlocked.Add(ref Played, samples.Length);
        }

        public void Drain()
        {
        }

        public void Key()
        {
            lock (_gate)
            {
                Events.Add("key");
            }

            Keyed.TrySetResult();
        }

        public void Unkey()
        {
            lock (_gate)
            {
                Events.Add("unkey");
            }

            Unkeyed.TrySetResult();
        }
    }

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
        var rig = new RealTimeRig();
        using var stopping = new CancellationTokenSource();
        // On a thread of its own: with work already queued the loop keys without yielding.
        Task transmitter = Task.Run(() => channel.RunTransmitterAsync(rig, rig, stopping.Token));

        // A 30 s burst: longer than the whole deadline.
        Task sent = channel.EnqueueTransmit(_ => new float[SampleRate * 30]);
        await rig.Keyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        var clock = Stopwatch.StartNew();
        await stopping.CancelAsync();
        await rig.Unkeyed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Stop();

        // Long audio goes to the device five seconds at a time, so the stop lands at the next
        // chunk boundary at the latest.
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the deadline is 20 s and the unkey must come well before it");
        rig.Events.Should().Equal(["key", "unkey"]);
        Interlocked.Read(ref rig.Played).Should().BeLessThan(SampleRate * 15, "the rest of the burst was not played");
        await sent.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>(
            "a burst cut short never went out whole, so it is not reported sent");
        await transmitter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Multi_Frame_Keyup_Takes_No_Further_Frame_Once_Stopping_Begins()
    {
        SoundModemChannel channel = Channel();
        var rig = new RealTimeRig();
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
        await rig.Keyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        await stopping.CancelAsync();
        await rig.Unkeyed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Events.Should().Equal(["key", "unkey"]);
        // Played rather than counted by task: identical delegate frames are merged by the queue
        // optimiser, which answers the merged ones when the first goes out.
        Interlocked.Read(ref rig.Played).Should().BeLessThan(
            SampleRate * 8 / 10 * 3, "the frames still queued were not played");
        await transmitter.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }
}
