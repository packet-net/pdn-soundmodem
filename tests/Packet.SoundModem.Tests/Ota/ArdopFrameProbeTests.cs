using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Positive controls for the ARDOP sim probe (rx-roadmap workstream 0's ARDOP gap, campaign
/// phase A3): a probe whose zero cannot be distinguished from a broken chain measures
/// nothing. High-SNR AWGN must decode clean through the same RunPoint path the CLI and the
/// mask rows drive, and the mode string must resolve or reject loudly.
/// </summary>
public class ArdopFrameProbeTests
{
    [Fact]
    public void A_Clean_Channel_Decodes_Every_Frame()
    {
        SimPointResult result = SimBench.RunPoint(
            "ardop:4FSK.500.100", rate: null, SimLayer.Frame, SimChannelKind.Awgn,
            snrDb: 20, bursts: 5, frameBytes: 0, firstSeed: 1, workers: 2);

        result.Successes.Should().Be(5);
        result.Margin.Should().BeGreaterThan(90, "a clean channel should report high quality");
    }

    [Theory]
    [InlineData("ardop:4FSK.500.100", "4FSK.500.100.E", 64)]
    [InlineData("ardop:4PSK.200.100S", "4PSK.200.100S.E", 16)]
    [InlineData("ardop:16QAM.500.100", "16QAM.500.100.E", 256)]
    public void Mode_Strings_Resolve_To_The_Even_Frame_And_Its_Capacity(
        string mode, string frameName, int payloadBytes)
    {
        var probe = new ArdopFrameProbe(mode);

        probe.FrameName.Should().Be(frameName);
        probe.PayloadBytes.Should().Be(payloadBytes);
    }

    [Fact]
    public void An_Unknown_Frame_Name_Is_Rejected_Loudly()
    {
        Action resolve = () => _ = new ArdopFrameProbe("ardop:5FSK.9999");

        resolve.Should().Throw<ArgumentException>().WithMessage("*names no ARDOP data frame*");
    }
}
