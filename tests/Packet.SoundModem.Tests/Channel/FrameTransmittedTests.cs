using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmit side's per-frame announcement.
/// </summary>
/// <remarks>
/// The channel has raised <see cref="SoundModemChannel.FrameReceived"/> since the beginning and, on
/// the transmit side, only <see cref="SoundModemChannel.TransmitRejected"/> - so a station's journal
/// recorded every frame it failed to send and none that it sent. The timing matters as much as the
/// event: a frame can wait behind CSMA or an ARQ session for seconds, and a line claiming a
/// transmission that has not happened yet is worse than no line.
/// </remarks>
public class FrameTransmittedTests
{
    private const int SampleRate = 12000;

    [Fact]
    public async Task A_Sent_Frame_Is_Announced_With_Its_Sub_Channel_And_Bytes()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(3, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;

        var announced = new List<(int Sub, byte[] Frame)>();
        channel.FrameTransmitted += (sub, frame) => announced.Add((sub, frame));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new FakeAudioOutput(SampleRate);
        Task transmitter = channel.RunTransmitterAsync(
            output, new M0LTE.Radio.Audio.NullPtt(), cancellation.Token);

        byte[] payload = [0x01, 0x02, 0x03];
        await channel.EnqueueTransmit(3, payload);

        announced.Should().ContainSingle();
        announced[0].Sub.Should().Be(3);
        announced[0].Frame.Should().BeEquivalentTo(payload);

        await cancellation.CancelAsync();
        try { await transmitter; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_Refused_Frame_Is_Not_Announced_As_Sent()
    {
        // A receive-only station refuses every frame. It must appear in the journal as a drop and
        // nowhere else - a frame belongs to exactly one of the two events.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7)
        {
            ReceiveOnlyReason = "this station receives only",
        };
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));

        var announced = new List<int>();
        var refused = new List<int>();
        channel.FrameTransmitted += (sub, _) => announced.Add(sub);
        channel.TransmitRejected += (sub, _, _) => refused.Add(sub);

        try { await channel.EnqueueTransmit(0, [0x01]); } catch (InvalidOperationException) { }

        refused.Should().ContainSingle();
        announced.Should().BeEmpty("a frame that never went out must not be logged as sent");
    }
}
