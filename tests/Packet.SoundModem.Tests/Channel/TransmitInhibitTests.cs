using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Keeping the packet modems off the air while something else owns the channel's timing — an
/// ARDOP ARQ session, whose turnarounds an AX.25 frame landing mid-sequence would break. This
/// is what replaced the old rule that an ARDOP channel could carry nothing else.
/// </summary>
public class TransmitInhibitTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 5);
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));
    private Task? _transmitter;
    private volatile bool _inhibited;

    public ValueTask InitializeAsync()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        _channel.TransmitInhibit = () => _inhibited;
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private static byte[] Frame()
    {
        byte[] frame = new byte[20];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        return frame;
    }

    [Fact]
    public async Task A_Packet_Frame_Waits_While_The_Channel_Is_Held_And_Goes_Out_After()
    {
        _inhibited = true;

        Task sent = _channel.EnqueueTransmit(0, Frame());
        await Task.Delay(500);

        sent.IsCompleted.Should().BeFalse("the channel is held");
        _output.WrittenCount.Should().Be(0, "nothing may reach the air during a held session");

        _inhibited = false;
        await sent.WaitAsync(TimeSpan.FromSeconds(10));

        _output.WrittenCount.Should().BeGreaterThan(0, "and it must go out once the hold lifts");
    }

    [Fact]
    public async Task The_Holder_Itself_Is_Never_Blocked_By_Its_Own_Hold()
    {
        // ARDOP's bursts are what the inhibit protects; if they queued behind it, an ARQ
        // session could never transmit and the whole arrangement would deadlock.
        _inhibited = true;

        Task sent = _channel.EnqueueTransmit(
            _ => new float[SampleRate / 10], rejected: null, bypassInhibit: true);

        await sent.WaitAsync(TimeSpan.FromSeconds(10));
        _output.WrittenCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_Frame_Held_Too_Long_Is_Rejected_Rather_Than_Escaping_Late()
    {
        // An AX.25 host will have retried long before a Winlink session ends; a frame that
        // eventually escapes minutes late is a duplicate, not a delivery.
        _channel.TransmitInhibitTimeout = TimeSpan.FromMilliseconds(300);
        _inhibited = true;
        Exception? reported = null;
        _channel.TransmitRejected += (_, _, reason) => reported = reason;

        Func<Task> send = () => _channel.EnqueueTransmit(0, Frame());

        await send.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("holding the channel"));
        reported.Should().NotBeNull("the rejection must be observable, not just thrown");
        _output.WrittenCount.Should().Be(0);
    }

    [Fact]
    public async Task With_Nothing_Holding_The_Channel_Frames_Are_Not_Delayed()
    {
        _inhibited = false;

        await _channel.EnqueueTransmit(0, Frame()).WaitAsync(TimeSpan.FromSeconds(10));

        _output.WrittenCount.Should().BeGreaterThan(0);
    }
}
