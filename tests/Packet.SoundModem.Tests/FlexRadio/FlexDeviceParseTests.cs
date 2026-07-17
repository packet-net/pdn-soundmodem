using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>Parsing of the <c>--device flex:&lt;radio&gt;[:slice]</c> string.</summary>
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
}
