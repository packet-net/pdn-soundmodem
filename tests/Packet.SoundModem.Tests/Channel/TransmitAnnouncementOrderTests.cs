using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A keyup's frames are announced in the order they went out, one at a time, however late the
/// thread pool gets round to the first of them.
/// </summary>
/// <remarks>
/// <para>The station's frame log, console line and page all write their rows from
/// <see cref="SoundModemChannel.FrameTransmittedWithReport"/>, so the order of the announcements
/// is the order of the rows. Each frame's announcement runs on the thread pool after the
/// transmitter completes its task, and the frames of one keyup complete close together, so a
/// busy pool used to announce them in any order and at the same time: newest first, more often
/// than not, because a pool thread runs the work it queued itself newest first. That is what
/// <see cref="HeldForAttributionTests"/> and <see cref="TransmitWaitsTests"/> kept failing on
/// under load (#537), each frame's figures landing on another frame's row.</para>
/// <para>The test forces exactly that interleaving rather than hoping for load: the first frame's
/// handler is held until the whole keyup is over, which is the state a starved pool leaves the
/// others in, every one of them sent and its announcement free to run. No clock is moved: the
/// channel is clear and the roll always passes, so the keyup runs straight through.</para>
/// </remarks>
public class TransmitAnnouncementOrderTests
{
    private const int SampleRate = 12000;

    private sealed class Clear : IChannelBusySource
    {
        public bool? Busy => false;
    }

    private static byte[] Broadcast(byte marker)
    {
        byte[] frame = Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");
        frame[14] = 0x03; // UI, so no turnaround hold follows it
        return [.. frame, (byte)0xF0, marker];
    }

    private static SoundModemChannel Station()
    {
        var channel = new SoundModemChannel(
            SampleRate, new FakeTimeProvider(), randomSeed: 42, channelBusySource: new Clear());
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;   // no roll: the channel is clear, so nothing holds a frame
        channel.Csma.TxTailMilliseconds = 0;
        return channel;
    }

    /// <summary>A sound card whose stream is torn down under the first write.</summary>
    private sealed class TornDownOutput(int sampleRate) : M0LTE.Radio.Audio.IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples) =>
            throw new OperationCanceledException("the device's stream was torn down under the write");

        public void Drain()
        {
        }
    }

    [Fact]
    public async Task A_Keyups_Frames_Are_Announced_One_At_A_Time_In_The_Order_They_Went_Out()
    {
        SoundModemChannel channel = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        using var keyupOver = new ManualResetEventSlim();
        channel.TransmittingChanged += on =>
        {
            if (!on)
            {
                keyupOver.Set();
            }
        };

        var announced = new List<byte>();
        var inside = 0;
        var overlapped = false;
        channel.FrameTransmittedWithReport += (_, frame, _) =>
        {
            if (Interlocked.Increment(ref inside) > 1)
            {
                Volatile.Write(ref overlapped, true);
            }

            try
            {
                if (frame[^1] == 0x41)
                {
                    keyupOver.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue(
                        "the keyup ends whatever its handlers are doing");
                }

                lock (announced)
                {
                    announced.Add(frame[^1]);
                }
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        };

        Task a = channel.EnqueueTransmit(2, Broadcast(0x41));
        Task b = channel.EnqueueTransmit(2, Broadcast(0x42));
        Task c = channel.EnqueueTransmit(2, Broadcast(0x43));
        var output = new FakeAudioOutput(SampleRate);
        Task transmitter = channel.RunTransmitterAsync(output, new RecordingPtt(), cancellation.Token);
        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(60));

        lock (announced)
        {
            announced.Should().Equal([0x41, 0x42, 0x43], "a station's rows are in the order it transmitted");
        }

        Volatile.Read(ref overlapped).Should().BeFalse("one announcement at a time, never two handlers at once");

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A frame whose write is cut short by a cancellation gets a definite answer, and does not
    /// hold up the announcement of anything sent after it.
    /// </summary>
    /// <remarks>
    /// Every frame's announcement waits for the one before it, so a frame that reached the device
    /// and then never got an answer would hold every later caller for ever. A write can throw
    /// <see cref="OperationCanceledException"/> without this station shutting down - an output
    /// whose own stream is torn down, and <see cref="M0LTE.Radio.Audio.IAudioOutput.Write"/> takes
    /// no token to say whose cancellation it is - and the transmitter can then be started again
    /// on another output. The frame after that must still go out and be announced.
    /// </remarks>
    [Fact]
    public async Task A_Frame_Whose_Write_Is_Cancelled_Does_Not_Hold_Up_The_Ones_Behind_It()
    {
        SoundModemChannel channel = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var announced = new List<byte>();
        channel.FrameTransmittedWithReport += (_, frame, _) =>
        {
            lock (announced)
            {
                announced.Add(frame[^1]);
            }
        };

        Task cut = channel.EnqueueTransmit(2, Broadcast(0x41));
        Task first = channel.RunTransmitterAsync(new TornDownOutput(SampleRate), new RecordingPtt(), cancellation.Token);
        Func<Task> firstRun = () => first;
        await firstRun.Should().ThrowAsync<OperationCanceledException>(
            "a transmitter that hits a cancellation ends on it, as it always has");

        Func<Task> cutAnswer = () => cut;
        (await Task.WhenAny(cut, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None))).Should().BeSameAs(cut,
            "the frame that was cut short must get a definite answer rather than leave its caller waiting");
        await cutAnswer.Should().ThrowAsync<OperationCanceledException>("it never finished going out");

        Task next = channel.EnqueueTransmit(2, Broadcast(0x42));
        Task second = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), new RecordingPtt(), cancellation.Token);
        (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None))).Should().BeSameAs(next,
            "the next frame went out on a good output, and its announcement must not wait for the cut one's");
        await next;

        lock (announced)
        {
            announced.Should().Equal([0x42], "only the frame that went out is announced");
        }

        await cancellation.CancelAsync();
        try
        {
            await second;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The same for a packed burst: every frame in a burst whose write is cut short gets an
    /// answer, and a frame sent afterwards is still announced.
    /// </summary>
    [Fact]
    public async Task A_Packed_Burst_Whose_Write_Is_Cancelled_Does_Not_Hold_Up_The_Ones_Behind_It()
    {
        const int PackedRate = 9600;
        var channel = new SoundModemChannel(PackedRate, new FakeTimeProvider(), randomSeed: 42);
        channel.AddModem(0, sink => new Ms110dModem(PackedRate, sink)
        {
            Packing = new FramePacking(TimeSpan.FromSeconds(30), TimeSpan.Zero),
        });
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 100;
        channel.Csma.TxTailMilliseconds = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var announced = new List<byte>();
        channel.FrameTransmittedWithReport += (_, frame, _) =>
        {
            lock (announced)
            {
                announced.Add(frame[^1]);
            }
        };

        Task[] cut = [channel.EnqueueTransmit(0, Broadcast(0x41)), channel.EnqueueTransmit(0, Broadcast(0x42))];
        Task first = channel.RunTransmitterAsync(new TornDownOutput(PackedRate), new RecordingPtt(), cancellation.Token);
        Func<Task> firstRun = () => first;
        await firstRun.Should().ThrowAsync<OperationCanceledException>(
            "a transmitter that hits a cancellation ends on it, as it always has");

        Task answered = Task.WhenAll(cut.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        (await Task.WhenAny(answered, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None))).Should().BeSameAs(answered,
            "every frame of the burst that was cut short must get a definite answer");
        cut.Should().AllSatisfy(t => t.IsCanceled.Should().BeTrue("none of them finished going out"));

        Task next = channel.EnqueueTransmit(0, Broadcast(0x43));
        Task second = channel.RunTransmitterAsync(new FakeAudioOutput(PackedRate), new RecordingPtt(), cancellation.Token);
        (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None))).Should().BeSameAs(next,
            "the next frame went out on a good output, and its announcement must not wait for the cut ones'");
        await next;

        lock (announced)
        {
            announced.Should().Equal([0x43], "only the frame that went out is announced");
        }

        await cancellation.CancelAsync();
        try
        {
            await second;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
