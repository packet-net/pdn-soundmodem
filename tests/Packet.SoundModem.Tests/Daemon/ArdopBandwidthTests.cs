using AwesomeAssertions;
using M0LTE.Ardop.Arq;
using M0LTE.Ardop.Host;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// An <c>ardop</c> modem entry's <c>"bandwidth"</c> is the TNC's ARQBW, not only the band
/// planner's reservation.
/// </summary>
/// <remarks>
/// <para>It was neither for a year: the key was consumed by the survey's band accounting and by
/// the transmit-filter plan, and nothing set <c>ArqBandwidth</c>, so a station configured for
/// 500 Hz answered <c>ARQBW 2000MAX</c> on its host port and would have accepted - and itself
/// requested - a 2000 Hz session in a 500 Hz coordinated slot. Read off the live GB7RDG station
/// on 2026-09-12 and filed as issue #459.</para>
/// <para>The chain these pin is config file to <see cref="ArdopChannelBridge.ArqBandwidthFor"/>
/// to a real <see cref="ArdopHostTnc"/>'s own <see cref="ArdopArqConfig.ArqBandwidth"/>. The one
/// link no test can run is the daemon's top-level statements calling it, so that is anchored on
/// the source text, the same way <c>StartUpRefusalTests</c> anchors the other start-up
/// decisions.</para>
/// </remarks>
public class ArdopBandwidthTests
{
    [Theory]
    [InlineData(200, ArdopBandwidth.B200Max)]
    [InlineData(500, ArdopBandwidth.B500Max)]
    [InlineData(1000, ArdopBandwidth.B1000Max)]
    [InlineData(2000, ArdopBandwidth.B2000Max)]
    public async Task A_Configured_Bandwidth_Reaches_The_Tncs_ArqBandwidth(
        double configured, ArdopBandwidth expected)
    {
        await using var tnc = new ArdopHostTnc(captureDevice: "null", playbackDevice: "null");
        tnc.Config.ArqBandwidth.Should().Be(
            ArdopBandwidth.B2000Max,
            "the package default is the widest, which is exactly what the station was stuck at");

        tnc.Config.ArqBandwidth = ArdopChannelBridge.ArqBandwidthFor(configured);

        tnc.Config.ArqBandwidth.Should().Be(expected);
        tnc.Config.ArqBandwidth.Hertz().Should().Be(
            (int)configured, "the width that reaches the TNC is the width that was configured");
        tnc.Config.ArqBandwidth.IsForced().Should().BeFalse(
            "MAX, not FORCED: a cap on what this station accepts and asks for, which still lets "
            + "a narrower peer settle lower");
    }

    [Fact]
    public async Task The_Per_Call_Override_Is_Left_Undefined_So_There_Is_One_Setting_To_Get_Right()
    {
        // CALLBW UNDEFINED means "use ARQBW" (ArdopArqConfig.cs:26-28). Setting both would be two
        // values that have to agree, and a station that changed one of them would be half
        // configured; a host that wants a narrower single call can still send its own CALLBW.
        await using var tnc = new ArdopHostTnc(captureDevice: "null", playbackDevice: "null");

        tnc.Config.ArqBandwidth = ArdopChannelBridge.ArqBandwidthFor(500);

        tnc.Config.CallBandwidth.Should().Be(
            ArdopBandwidth.Undefined,
            "the per-call override falls through to ARQBW; if the daemon is ever made to set it "
            + "too, that is a deliberate change and this test is where it is argued");
    }

    [Fact]
    public void An_Unset_Bandwidth_Keeps_The_Widest_Because_That_Is_What_The_Planner_Reserves()
    {
        // Deliberately unchanged behaviour. 2000 Hz is what the band planner already reserves for
        // an ardop entry that states nothing (ArdopChannelBridge.WidestBandwidthHz, used by
        // BandPlanner, TransmitFilterPlan and the survey), and it is ardopcf's own default, so
        // the TNC and the plan agree. Narrowing it would refuse sessions that stations have been
        // completing since before the key meant anything.
        ArdopChannelBridge.ArqBandwidthFor(null).Should().Be(ArdopBandwidth.B2000Max);
        ArdopChannelBridge.ArqBandwidthFor(null).Hertz().Should().Be(
            (int)ArdopChannelBridge.WidestBandwidthHz,
            "the cap and the reservation are the same number, or the plan describes a station "
            + "that does not exist");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(2400)]
    [InlineData(2000.5)]
    [InlineData(3000)]
    public void A_Width_Ardop_Cannot_Negotiate_Maps_To_Nothing(double bandwidthHz)
    {
        ArdopChannelBridge.TryArqBandwidth(bandwidthHz, out ArdopBandwidth arq).Should().BeFalse();
        arq.Should().Be(
            ArdopBandwidth.Undefined,
            "a failed parse must not leave a bandwidth behind that a caller might use");
    }

    [Fact]
    public void The_Room_Warning_Is_About_The_Width_This_Station_Will_Actually_Negotiate()
    {
        // 950 Hz is the live station's ARDOP centre. It leaves 1300 Hz inside the nominal
        // 300-2700 Hz passband, so a station that can still negotiate 2000 Hz is rightly told
        // the rig's filter would cut one; a station capped at 500 has no such session to cut,
        // and warning about it would send an operator after a fault that cannot happen.
        int rate = M0LTE.Ardop.ArdopModulator.SampleRate;

        ArdopChannelBridge.Concern(950, rate).Should().Contain(
            "up to 2000 Hz", "uncapped, the widest session is the one that would be clipped");
        ArdopChannelBridge.Concern(950, rate, 500).Should().BeNull(
            "1300 Hz of room is ample for the 500 Hz this station is capped at");
        ArdopChannelBridge.Concern(950, rate, 1000).Should().BeNull();
        ArdopChannelBridge.Concern(950, rate, 2000).Should().Contain("up to 2000 Hz");
    }

    [Fact]
    public void The_Daemon_Applies_The_Configured_Bandwidth_Before_It_Serves_The_Host_Port()
    {
        // Program.cs is a script that runs once and opens sound cards, so this one link is
        // anchored on its source. If any of these names change, update the test rather than
        // deleting it: what it is guarding is that the TNC is capped at all, and that it is
        // capped before a host can connect and start a session at the default.
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs"));

        int applied = source.IndexOf(
            "ardopTnc.Config.ArqBandwidth = ArdopChannelBridge.ArqBandwidthFor(ardopModem.Bandwidth)",
            StringComparison.Ordinal);
        int served = source.IndexOf("new M0LTE.Ardop.Host.ArdopHostServer(", StringComparison.Ordinal);

        applied.Should().BeGreaterThan(
            -1, "the configured bandwidth has to be written to the TNC's ARQBW at start-up");
        served.Should().BeGreaterThan(-1, "and the host server is still constructed here");
        applied.Should().BeLessThan(
            served, "the cap has to be in place before the host port accepts anyone");
        source.Should().NotContain(
            "Config.CallBandwidth",
            "CALLBW is deliberately left UNDEFINED so it falls through to ARQBW; see "
            + "The_Per_Call_Override_Is_Left_Undefined_So_There_Is_One_Setting_To_Get_Right");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
