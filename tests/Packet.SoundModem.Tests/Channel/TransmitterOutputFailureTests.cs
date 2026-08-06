using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmitter loop against an output device that fails, and the unkey handover. A
/// device failure used to fault the loop's task silently: the in-flight frame's enqueuer
/// waited forever, the queue was never read again, and the daemon ran on as a
/// healthy-looking receive-only station. The contract now: every queued frame gets a
/// definite answer, the rejection is announced, and the fault propagates to the task's
/// owner. And at end of transmission the demodulators are swept BEFORE the receive gate
/// reopens - the other order let the audio thread re-enter Process concurrently with
/// ResetCarrierState.
/// </summary>
public class TransmitterOutputFailureTests
{
    private const int SampleRate = 12000;

    private sealed class FailingAudioOutput(int sampleRate) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples) =>
            throw new IOException("the USB soundcard was unplugged");

        public void Drain()
        {
        }
    }

    /// <summary>A modem whose Modulate is trivial and whose reset calls are counted -
    /// the unkey handover's ordering witness.</summary>
    private sealed class CountingResetModem : IModem
    {
        public string Mode => "stub";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public int Resets;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[600];

        public void ResetCarrierState() => Interlocked.Increment(ref Resets);
    }

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[25];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(3).NextBytes(frame.AsSpan(16));
        return frame;
    }

    [Fact]
    public async Task A_Dead_Output_Faults_The_Frame_Announces_It_And_Propagates()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        var rejections = new List<Exception>();
        channel.TransmitRejected += (_, _, reason) => rejections.Add(reason);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(
            new FailingAudioOutput(SampleRate), new NullPtt(), cancellation.Token);

        // The device dies under the burst: the frame's task faults with the device's own
        // exception - a definite answer, not an ACKMODE host waiting forever.
        Task doomed = channel.EnqueueTransmit(0, SampleFrame());
        await FluentActions.Awaiting(() => doomed.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<IOException>().WithMessage("*unplugged*");

        // The drop is journalled like any other, and the loop's fault reaches its owner -
        // the daemon turns it into a logged exit rather than a silent transmit-dead station.
        rejections.Should().ContainSingle().Which.Should().BeOfType<IOException>();
        await FluentActions.Awaiting(() => transmitter.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task The_Unkey_Sweeps_Demodulators_Before_The_Receive_Gate_Reopens()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        CountingResetModem? created = null;
        channel.AddModem(0, _ => created = new CountingResetModem());
        CountingResetModem modem = created!;
        channel.Csma.Persistence = 255;

        // The witness: at the moment the channel announces the transmission over (which is
        // also when taps like the id-beacon ghosts reset), the modem sweep must already
        // have happened - both run while receive is still gated.
        int resetsWhenAnnounced = -1;
        channel.TransmittingChanged += transmitting =>
        {
            if (!transmitting)
            {
                resetsWhenAnnounced = Volatile.Read(ref modem.Resets);
            }
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(
            new FakeAudioOutput(SampleRate), new NullPtt(), cancellation.Token);
        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(10));

        using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (resetsWhenAnnounced < 0)
        {
            settle.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, settle.Token);
        }

        resetsWhenAnnounced.Should().BeGreaterThan(0,
            "the demodulator sweep must precede the end-of-transmission announcement");

        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => transmitter).Should().ThrowAsync<OperationCanceledException>();
    }
}
