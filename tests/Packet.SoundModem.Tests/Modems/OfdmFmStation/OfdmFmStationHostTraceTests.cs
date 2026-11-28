using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

public class OfdmFmStationHostTraceTests
{
    [Fact]
    public async Task The_stations_trace_lines_reach_a_subscriber_of_the_host()
    {
        await using var rig = new StationRig();
        var lines = new List<TraceEvent>();
        rig.Host.Trace += lines.Add;

        rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

        lines.Should().Contain(l => l.Text == "Callsign set to G4ABC" && l.Level == TraceLevel.Flow);
    }

    [Fact]
    public async Task The_level_passes_through_and_detail_adds_lines()
    {
        await using var rig = new StationRig();
        rig.Host.TraceLevel.Should().Be(TraceLevel.Flow);
        var lines = new List<TraceEvent>();
        rig.Host.Trace += lines.Add;
        rig.Host.SetIdentity("G4ABC");

        rig.Host.TraceLevel = TraceLevel.Detail;
        rig.Host.TraceLevel.Should().Be(TraceLevel.Detail);
        rig.Host.Send(new byte[10], LinkAddress.FromAscii("M0XYZ"), LinkAddress.FromAscii("G4ABC"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        lines.Should().Contain(l => l.Level == TraceLevel.Detail);
    }

    [Fact]
    public async Task An_unsubscribed_handler_gets_nothing_more()
    {
        await using var rig = new StationRig();
        var lines = new List<TraceEvent>();
        Action<TraceEvent> handler = lines.Add;
        rig.Host.Trace += handler;
        rig.Host.Trace -= handler;

        rig.Host.SetIdentity("G4ABC");

        lines.Should().BeEmpty();
    }
}
