using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A transmission that owns the channel's timing must not be able to end up behind one that
/// does not. Reproduces GB7RDG's late ARDOP ConAcks (see the notes on the fixture).
/// </summary>
public class OwnsTimingPriorityTests
{
    private const int SampleRate = 12000;

    private sealed class Band : IModem
    {
        public string Mode => "afsk1200";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
            new float[SampleRate / 10];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default!);
    }

    private static byte[] Frame()
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        return frame;
    }

    /// <summary>
    /// A packet frame is queued first and defers to carrier sense. An ARDOP burst then arrives
    /// with ownsChannelTiming, which is supposed to skip both the inhibit and the roll. It must
    /// go out while the band is still busy.
    /// </summary>
    [Fact]
    public async Task An_Owns_Timing_Burst_Goes_Out_While_A_Packet_Frame_Is_Deferring()
    {
        var band = new Band { ChannelBusy = true };
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, _ => band);
        channel.Csma.Persistence = 255;          // the roll never defers; only ChannelBusy does
        channel.Csma.TxDelayMilliseconds = 10;
        channel.Csma.TxTailMilliseconds = 0;
        channel.Csma.SlotTimeMilliseconds = 20;

        var output = new FakeAudioOutput(SampleRate);
        var ptt = new RecordingPtt();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stop.Token);

        // The packet frame: queued first, and it cannot go out while the band is busy.
        Task packet = channel.EnqueueTransmit(0, Frame());
        await Task.Delay(300);
        packet.IsCompleted.Should().BeFalse("the band is busy, so the packet frame must defer");

        // The ARDOP burst: owns the channel's timing, so carrier sense is not its gate.
        var ardopSource = new object();
        Task ardop = channel.EnqueueTransmit(
            _ => new float[SampleRate / 10],
            rejected: null,
            ownsChannelTiming: true,
            source: ardopSource);

        Task finished = await Task.WhenAny(ardop, Task.Delay(TimeSpan.FromSeconds(5)));

        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        finished.Should().BeSameAs(
            ardop,
            "a burst that owns the channel's timing must not wait on a packet frame's carrier sense");
    }

    /// <summary>Control: with the band clear, both go out. Proves the fixture can transmit.</summary>
    [Fact]
    public async Task Both_Go_Out_When_The_Band_Is_Clear()
    {
        var band = new Band { ChannelBusy = false };
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, _ => band);
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 10;
        channel.Csma.TxTailMilliseconds = 0;
        channel.Csma.SlotTimeMilliseconds = 20;

        var output = new FakeAudioOutput(SampleRate);
        var ptt = new RecordingPtt();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stop.Token);

        Task packet = channel.EnqueueTransmit(0, Frame());
        Task ardop = channel.EnqueueTransmit(
            _ => new float[SampleRate / 10], rejected: null, ownsChannelTiming: true, source: new object());

        await Task.WhenAll(packet, ardop).WaitAsync(TimeSpan.FromSeconds(10));

        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
