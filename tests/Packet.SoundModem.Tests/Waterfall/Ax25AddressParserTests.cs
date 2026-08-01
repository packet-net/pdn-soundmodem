using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class Ax25AddressParserTests
{
    private static byte[] Frame(string destination, int destinationSsid, string source, int sourceSsid)
    {
        var frame = new byte[16];
        Encode(destination, destinationSsid, last: false).CopyTo(frame, 0);
        Encode(source, sourceSsid, last: true).CopyTo(frame, 7);
        frame[14] = 0x03; // UI control
        frame[15] = 0xF0; // no layer 3
        return frame;
    }

    private static byte[] Encode(string callsign, int ssid, bool last)
    {
        var field = new byte[7];
        for (int n = 0; n < 6; n++)
        {
            field[n] = (byte)((n < callsign.Length ? callsign[n] : ' ') << 1);
        }

        field[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
        return field;
    }

    [Fact]
    public void Parses_source_and_destination_with_ssids()
    {
        Ax25AddressParser.TryParse(Frame("GB7RDG", 0, "M0LTE", 9), out string source, out string destination)
            .Should().BeTrue();

        source.Should().Be("M0LTE-9");
        destination.Should().Be("GB7RDG");
    }

    [Fact]
    public void Ssid_zero_omits_the_suffix()
    {
        Ax25AddressParser.TryParse(Frame("APRS", 0, "M0LTE", 0), out string source, out string destination)
            .Should().BeTrue();

        source.Should().Be("M0LTE");
        destination.Should().Be("APRS");
    }

    [Fact]
    public void Rejects_short_frames()
    {
        Ax25AddressParser.TryParse(new byte[10], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_bytes_that_are_not_a_shifted_address()
    {
        var junk = new byte[20];
        Array.Fill(junk, (byte)0xFF);

        Ax25AddressParser.TryParse(junk, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_empty_callsign()
    {
        // All-space address fields (shifted 0x40) are structurally valid but carry no call.
        var frame = new byte[16];
        Array.Fill(frame, (byte)(' ' << 1), 0, 14);
        frame[13] |= 1;

        Ax25AddressParser.TryParse(frame, out _, out _).Should().BeFalse();
    }
}
