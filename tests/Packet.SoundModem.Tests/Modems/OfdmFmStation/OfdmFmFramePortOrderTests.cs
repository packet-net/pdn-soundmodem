using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// AX.25 needs its frames in order. The modem sends an expedited datagram ahead of ordinary ones, which would put an acknowledgement ahead of an I frame still queued to the same station: the receiver sees
/// its N(R) go backwards and resets the link (found by the AX.25 call over two stations, a banner and a request crossing on the air).
/// </summary>
public class OfdmFmFramePortOrderTests
{
    [Fact]
    public async Task An_acknowledgement_queued_behind_an_I_frame_to_the_same_station_goes_after_it()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        using var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        rig.StartTransmitter();

        Task iFrame = port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", ns: 0, nr: 0));
        Task ack = port.Transmit(FramePortFrames.Rr("M0XYZ-1", "G4ABC-1"));
        await rig.PumpAsync(() => iFrame.IsCompleted && ack.IsCompleted, "both frames to go");

        rig.Transmitted.Select(t => t.Tag.Value).Should().Equal([1L, 2L], "the I frame, then the acknowledgement that follows it");
    }

    [Fact]
    public async Task An_acknowledgement_to_another_station_still_goes_first()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        using var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        rig.StartTransmitter();

        Task iFrame = port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", ns: 0, nr: 0));
        Task ack = port.Transmit(FramePortFrames.Rr("M0ABC-1", "G4ABC-1"));
        await rig.PumpAsync(() => iFrame.IsCompleted && ack.IsCompleted, "both frames to go");

        rig.Transmitted.Select(t => t.Tag.Value).Should().Equal([2L, 1L], "nothing orders frames to different stations: the acknowledgement keeps its priority");
    }

    [Fact]
    public async Task Once_the_I_frame_has_gone_nothing_is_held_and_an_acknowledgement_keeps_its_priority()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        using var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        rig.StartTransmitter();

        Task iFrame = port.Transmit(FramePortFrames.I("M0XYZ-1", "G4ABC-1", ns: 0, nr: 0));
        Task ack = port.Transmit(FramePortFrames.Rr("M0XYZ-1", "G4ABC-1"));
        port.OrdinaryWaiting.Should().Be(2, "the I frame, and the acknowledgement held to its order");
        await rig.PumpAsync(() => iFrame.IsCompleted && ack.IsCompleted, "both frames to go");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        port.OrdinaryWaiting.Should().Be(0);
        port.Outstanding.Should().Be(0);
        port.Transmit(FramePortFrames.Rr("M0XYZ-1", "G4ABC-1")).IsCompleted.Should().BeFalse();
        port.OrdinaryWaiting.Should().Be(0, "an acknowledgement with nothing ahead of it is expedited, not counted");
    }
}
