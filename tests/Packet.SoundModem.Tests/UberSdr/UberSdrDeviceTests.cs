using AwesomeAssertions;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// Parsing of the <c>--device ubersdr:&lt;instance&gt;</c> string. An operator reaching for this
/// has an instance open in a browser, so the URL from the address bar has to work as readily as
/// the bare hostname - this is where that promise is kept.
/// </summary>
public sealed class UberSdrDeviceTests
{
    [Theory]
    [InlineData("ubersdr:m9psy-1.instance.ubersdr.org", "m9psy-1.instance.ubersdr.org", 443, true)]
    [InlineData("ubersdr:m9psy-1.instance.ubersdr.org:8443", "m9psy-1.instance.ubersdr.org", 8443, true)]
    [InlineData("ubersdr:https://m9psy-1.instance.ubersdr.org/", "m9psy-1.instance.ubersdr.org", 443, true)]
    [InlineData("ubersdr:https://m9psy-1.instance.ubersdr.org", "m9psy-1.instance.ubersdr.org", 443, true)]
    [InlineData("ubersdr:http://192.168.1.5:8080/", "192.168.1.5", 8080, false)]
    [InlineData("ubersdr:http://sdr.example", "sdr.example", 80, false)]
    [InlineData("UBERSDR:sdr.example", "sdr.example", 443, true)]
    [InlineData("ubersdr: sdr.example ", "sdr.example", 443, true)]
    public void Parse_reads_host_port_and_scheme(string device, string host, int port, bool ssl)
    {
        UberSdrEndpoint endpoint = UberSdrDevice.Parse(device);

        endpoint.Host.Should().Be(host);
        endpoint.Port.Should().Be(port);
        endpoint.Ssl.Should().Be(ssl);
    }

    [Theory]
    [InlineData("ubersdr:sdr.example", "https://sdr.example:443", "wss")]
    [InlineData("ubersdr:http://sdr.example:8080", "http://sdr.example:8080", "ws")]
    public void The_endpoint_knows_both_of_its_schemes(string device, string httpBase, string wsScheme)
    {
        UberSdrEndpoint endpoint = UberSdrDevice.Parse(device);

        endpoint.HttpBase.Should().Be(httpBase);
        endpoint.WebSocketScheme.Should().Be(wsScheme);
    }

    [Theory]
    [InlineData("ubersdr:sdr.example", "sdr.example")]
    [InlineData("ubersdr:sdr.example:8443", "sdr.example:8443")]
    [InlineData("ubersdr:http://sdr.example", "sdr.example")]
    public void An_endpoint_prints_the_way_an_operator_would_write_it(string device, string expected)
    {
        // It goes in the start-up banner and in every error message, so a default port dragged
        // along behind the hostname is noise on every line it appears in.
        UberSdrDevice.Parse(device).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("ubersdr:sdr.example", true)]
    [InlineData("UberSDR:sdr.example", true)]
    [InlineData("default", false)]
    [InlineData("plughw:1,0", false)]
    [InlineData("flex:discover", false)]
    public void IsUberSdr_detects_the_prefix(string device, bool expected)
    {
        UberSdrDevice.IsUberSdr(device).Should().Be(expected);
    }

    [Theory]
    [InlineData("ubersdr:")]
    [InlineData("ubersdr:   ")]
    [InlineData("ubersdr:sdr.example:notaport")]
    [InlineData("ubersdr:sdr.example:0")]
    [InlineData("ubersdr:ftp://sdr.example")]
    public void A_device_string_that_names_no_reachable_instance_is_an_operator_error(string device)
    {
        // InvalidDataException, not ArgumentException: the daemon treats these as configuration
        // problems and prints the message rather than a stack trace.
        Action parse = () => UberSdrDevice.Parse(device);

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parsing_something_that_is_not_a_ubersdr_device_is_a_programming_error()
    {
        Action parse = () => UberSdrDevice.Parse("plughw:1,0");

        parse.Should().Throw<ArgumentException>();
    }
}
