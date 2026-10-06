using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

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

    [Fact]
    public async Task A_Keyups_Frames_Are_Announced_One_At_A_Time_In_The_Order_They_Went_Out()
    {
        var channel = new SoundModemChannel(
            SampleRate, new FakeTimeProvider(), randomSeed: 42, channelBusySource: new Clear());
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;
        channel.Csma.TxTailMilliseconds = 0;
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
}
