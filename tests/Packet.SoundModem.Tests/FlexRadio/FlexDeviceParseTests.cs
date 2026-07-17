using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>Parsing of the <c>--device flex:&lt;radio&gt;[:slice][@station]</c> string,
/// including the headless (default) vs attach (<c>@station</c>) selection policy.</summary>
public sealed class FlexDeviceParseTests
{
    [Theory]
    [InlineData("flex:mock", "mock", "A")]
    [InlineData("flex:discover", "discover", "A")]
    [InlineData("flex:discover:B", "discover", "B")]
    [InlineData("flex:192.168.1.50", "192.168.1.50", "A")]
    [InlineData("flex:192.168.1.50:C", "192.168.1.50", "C")]
    [InlineData("flex:192.168.1.50:4992", "192.168.1.50:4992", "A")]
    [InlineData("flex:192.168.1.50:4992:D", "192.168.1.50:4992", "D")]
    [InlineData("flex:serial=1234-5678", "serial=1234-5678", "A")]
    [InlineData("flex:serial=1234-5678:B", "serial=1234-5678", "B")]
    [InlineData("flex:name=My_Radio:A", "name=My_Radio", "A")]
    [InlineData("flex:mock:b", "mock", "B")]
    public void Parse_splits_radio_and_slice(string device, string expectedRadio, string expectedSlice)
    {
        FlexDevice.FlexSpec spec = FlexDevice.Parse(device);
        spec.RadioSpec.Should().Be(expectedRadio);
        spec.SliceLetter.Should().Be(expectedSlice);
    }

    [Theory]
    [InlineData("flex:mock", true)]
    [InlineData("FLEX:mock", true)]
    [InlineData("default", false)]
    [InlineData("plughw:0,0", false)]
    public void IsFlex_detects_the_prefix(string device, bool expected)
    {
        FlexDevice.IsFlex(device).Should().Be(expected);
    }

    [Theory]
    [InlineData("flex:mock")]
    [InlineData("flex:discover:B")]
    [InlineData("flex:192.168.1.50:4992")]
    [InlineData("flex:serial=1234-5678")]
    public void Parse_defaults_to_headless_without_a_station(string device)
    {
        FlexDevice.FlexSpec spec = FlexDevice.Parse(device);
        spec.Headless.Should().BeTrue();
        spec.Station.Should().BeNull();
    }

    [Theory]
    // A trailing @station selects attach mode and names the SmartSDR station; the radio and
    // slice parse exactly as without it.
    [InlineData("flex:mock@Flex", "mock", "A", "Flex")]
    [InlineData("flex:mock:B@Flex", "mock", "B", "Flex")]
    [InlineData("flex:192.168.1.50@Station6500", "192.168.1.50", "A", "Station6500")]
    [InlineData("flex:192.168.1.50:C@Shack", "192.168.1.50", "C", "Shack")]
    [InlineData("flex:192.168.1.50:4992@Flex", "192.168.1.50:4992", "A", "Flex")]
    [InlineData("flex:192.168.1.50:4992:D@Flex", "192.168.1.50:4992", "D", "Flex")]
    [InlineData("flex:discover@Flex", "discover", "A", "Flex")]
    public void Parse_extracts_the_attach_station(
        string device, string expectedRadio, string expectedSlice, string expectedStation)
    {
        FlexDevice.FlexSpec spec = FlexDevice.Parse(device);
        spec.RadioSpec.Should().Be(expectedRadio);
        spec.SliceLetter.Should().Be(expectedSlice);
        spec.Station.Should().Be(expectedStation);
        spec.Headless.Should().BeFalse();
    }
}
