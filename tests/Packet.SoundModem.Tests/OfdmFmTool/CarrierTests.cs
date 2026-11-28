using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.OfdmFmTool;
using Packet.SoundModem.Tests.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.OfdmFmTool;

/// <summary>The radio's carrier indication through the tool: the option, its two lines, and what the station does with it. A fake source stands in for the radio; no port is opened.</summary>
public class CarrierTests
{
    private static readonly LinkAddress Me = LinkAddress.FromAscii("G4ABC-1");

    [Fact]
    public void The_option_is_read_on_every_command()
    {
        string[] common = ["--device", "d", "--ptt", "serial:x", "--call", "G4ABC-1", "--carrier", "tait:/dev/serial/by-id/usb-Tait-if00"];

        CommandLine.Parse(["listen", "--device", "d", "--call", "G4ABC-1", "--carrier", "tait:/dev/ttyUSB0"]).Options!.Carrier.Should().Be("tait:/dev/ttyUSB0");
        CommandLine.Parse([.. new[] { "send" }, .. common, "--to", "M0XYZ-1", "hello"]).Options!.Carrier.Should().Be("tait:/dev/serial/by-id/usb-Tait-if00");
        CommandLine.Parse([.. new[] { "ping" }, .. common, "--to", "M0XYZ-1"]).Options!.Carrier.Should().NotBeNull();
        CommandLine.Parse([.. new[] { "answer" }, .. common]).Options!.Carrier.Should().NotBeNull();
        CommandLine.Parse([.. new[] { "call" }, .. common, "--to", "M0XYZ-1"]).Options!.Carrier.Should().NotBeNull();
        CommandLine.Parse(["listen", "--device", "d", "--call", "G4ABC-1"]).Options!.Carrier.Should().BeNull("without the option nothing changes");
    }

    [Theory]
    [InlineData("tait:")]
    [InlineData("tait")]
    [InlineData("flex:radio")]
    [InlineData("/dev/ttyUSB0")]
    [InlineData("tait:/dev/tty USB0")]
    [InlineData("")]
    public void A_bad_spec_is_one_plain_line(string spec)
    {
        ParseResult result = CommandLine.Parse(["listen", "--device", "d", "--call", "G4ABC-1", "--carrier", spec]);

        result.Options.Should().BeNull();
        result.Error.Should().Be("--carrier must be tait:DEVICE, such as tait:/dev/ttyUSB0");
    }

    [Fact]
    public void The_spec_gives_the_control_port()
    {
        CarrierSpec.Parse("tait:/dev/ttyUSB0", out string port).Should().BeNull();
        port.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void The_tool_says_when_the_radio_first_answers_when_it_stops_and_when_it_is_back_and_says_nothing_of_present_and_gone()
    {
        var radio = new FakeRadio();
        var lines = new List<string>();
        var carrier = new LoggedCarrier(radio, "the radio on /dev/ttyUSB0", lines.Add);

        _ = carrier.Busy;
        lines.Should().BeEmpty("a radio that has not spoken yet is not news");
        radio.Set(false);
        carrier.Busy.Should().BeFalse();
        radio.Set(true);
        carrier.Busy.Should().BeTrue();
        radio.Set(false);
        _ = carrier.Busy;
        radio.Set(null);
        carrier.Busy.Should().BeNull();
        _ = carrier.Busy;
        radio.Set(true);
        carrier.Busy.Should().BeTrue();

        lines.Should().Equal(
            "Carrier indication from the radio on /dev/ttyUSB0 available",
            "Carrier indication from the radio on /dev/ttyUSB0 lost: the station goes by what it hears",
            "Carrier indication from the radio on /dev/ttyUSB0 back");
    }

    [Fact]
    public async Task Carrier_present_holds_the_stations_transmission_and_absent_releases_it()
    {
        var radio = new FakeRadio();
        var lines = new List<string>();
        radio.Set(true);
        await using var rig = new StationRig(radio: new LoggedCarrier(radio, "the radio", lines.Add));
        rig.Host.SetIdentity("G4ABC");
        rig.StartTransmitter();
        rig.WaitForTransmitter = false;
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await rig.FeedAsync(60 * 50);
        rig.Air.KeyAttempts.Should().Be(0, "a minute of carrier from the radio and nothing was keyed");

        radio.Set(false);
        rig.WaitForTransmitter = true;
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the radio said the carrier was gone");
        lines.Should().Equal("Carrier indication from the radio available");
    }

    [Fact]
    public async Task A_source_that_is_lost_falls_back_to_detection_and_the_tool_says_so()
    {
        var radio = new FakeRadio();
        var lines = new List<string>();
        radio.Set(true);
        await using var rig = new StationRig(radio: new LoggedCarrier(radio, "the radio", lines.Add));
        rig.Host.SetIdentity("G4ABC");
        rig.StartTransmitter();
        rig.WaitForTransmitter = false;
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        await rig.FeedAsync(30 * 50);
        rig.Air.KeyAttempts.Should().Be(0);

        radio.Set(null);
        rig.WaitForTransmitter = true;
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the radio stopped answering");

        lines.Should().Equal("Carrier indication from the radio available", "Carrier indication from the radio lost: the station goes by what it hears");
    }

    [Fact]
    public async Task The_stations_trace_says_present_gone_and_not_available_at_the_detail_level()
    {
        var radio = new FakeRadio();
        await using var rig = new StationRig(radio: new LoggedCarrier(radio, "the radio", _ => { }));
        rig.WaitForTransmitter = false;
        var trace = new List<TraceEvent>();
        rig.Host.Trace += trace.Add;
        rig.Host.TraceLevel = TraceLevel.Detail;
        rig.Host.SetIdentity("G4ABC");

        radio.Set(true);
        await rig.FeedAsync(2);
        radio.Set(false);
        await rig.FeedAsync(2);
        radio.Set(null);
        await rig.FeedAsync(2);

        trace.Select(t => t.Text).Where(t => t.StartsWith("Carrier from the radio", StringComparison.Ordinal)).Should().Equal(
            "Carrier from the radio: present", "Carrier from the radio: gone", "Carrier from the radio: not available");
    }
}
