using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmitter loop against a PTT that fails. Before this existed the loop had no catch
/// and the daemon swallowed the task's fault, so one throwing Key() - a contended shared
/// radio, an unplugged serial lead - silently killed transmit for the rest of the process
/// with no log line. The contract now: the failure is announced, every queued frame gets a
/// definite answer, and the loop survives to try the next keyup.
/// </summary>
public class TransmitterPttFailureTests
{
    private const int SampleRate = 12000;

    private sealed class FlakyPtt : IPttControl
    {
        public bool FailKey { get; set; }

        public bool FailUnkey { get; set; }

        public int Keys { get; private set; }

        public int Unkeys { get; private set; }

        public void Key()
        {
            Keys++;
            if (FailKey)
            {
                throw new InvalidOperationException("another station holds the PA");
            }
        }

        public void Unkey()
        {
            Unkeys++;
            if (FailUnkey)
            {
                throw new IOException("the radio session died");
            }
        }
    }

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[25];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(3).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellation.Token);
        }
    }

    [Fact]
    public async Task A_Failed_Keyup_Faults_The_Queue_Announces_And_The_Loop_Survives()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        var output = new FakeAudioOutput(SampleRate);
        var ptt = new FlakyPtt { FailKey = true };
        var failures = new List<Exception>();
        channel.PttFailed += failures.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);

        // The keyup fails: the frame's task faults with the PTT's own exception - a definite
        // answer, exactly what an ACKMODE host needs to decide the frame is lost.
        Task doomed = channel.EnqueueTransmit(0, SampleFrame());
        await FluentActions.Awaiting(() => doomed.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*holds the PA*");
        failures.Should().ContainSingle();
        output.WrittenCount.Should().Be(0, "nothing may go out through a failed keyup");
        ptt.Unkeys.Should().Be(0, "there is nothing to unkey after a failed keyup");

        // The PTT recovers; the loop must still be alive and the next frame transmits.
        ptt.FailKey = false;
        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(10));
        output.WrittenCount.Should().BeGreaterThan(0, "the loop survived the failure");
        transmitter.IsCompleted.Should().BeFalse("the loop is still serving");

        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => transmitter).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Failed_Unkey_Does_Not_Mask_The_Bursts_Result()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        var output = new FakeAudioOutput(SampleRate);
        var ptt = new FlakyPtt { FailUnkey = true };
        var failures = new List<Exception>();
        channel.PttFailed += failures.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);

        // The burst itself succeeded; the unkey failing afterwards is the radio's problem to
        // announce, not a reason to fault a frame whose audio fully left the device.
        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(10));
        output.WrittenCount.Should().BeGreaterThan(0);
        await WaitUntilAsync(() => failures.Count == 1);
        failures.Single().Should().BeOfType<IOException>();
        transmitter.IsCompleted.Should().BeFalse("an unkey failure must not kill the loop");

        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => transmitter).Should().ThrowAsync<OperationCanceledException>();
    }
}
