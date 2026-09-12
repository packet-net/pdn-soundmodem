using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class Ax25AddressParserTests
{
    /// <summary>
    /// A PD4R-12 beacon exactly as it came off the live 40 m channel, CRC valid: destination
    /// all spaces (40 40 40 40 40 40 E0), source PD4R-12, one digipeater (TEST), UI, and a
    /// frequencies text payload. 118 bytes. Shared with <c>FrameLogTests</c>, which pins the
    /// end-to-end consequence: the log used to record this frame with both callsigns null.
    /// </summary>
    internal const string Pd4rBeaconHex =
        "404040404040E0A08868A4404078A88AA6A840406103F020504434522D3132203C3C3C3C3C20717276206F6E20313434"
        + "2E3932352028666D20316B3229203134342E3737352028737362292031342E31303520287373622920372E3034353020"
        + "2873736229203433382E3137352028666D20396B3629";

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
        // Here BOTH fields are blank; with nobody readable there is nothing to attribute.
        var frame = new byte[16];
        Array.Fill(frame, (byte)(' ' << 1), 0, 14);
        frame[13] |= 1;

        Ax25AddressParser.TryParse(frame, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_blank_destination_does_not_cost_a_readable_source()
    {
        // Off-air evidence: PD4R-12's beacons decode and deliver, and the frame log recorded
        // source and destination both null - the all-or-nothing parse threw away a perfectly
        // readable sender over a destination field that carries no callsign at all.
        byte[] frame = Convert.FromHexString(Pd4rBeaconHex);

        Ax25AddressParser.TryParse(frame, out string source, out string destination)
            .Should().BeTrue("a readable source attributes the frame on its own");
        source.Should().Be("PD4R-12");
        destination.Should().Be("", "an all-spaces destination field carries no callsign");
    }

    /// <summary>
    /// What "blank" is in the bytes, pinned off the frame the tolerance exists for, because the
    /// rest of this file rests on it: an address field is blank when all six of its callsign
    /// bytes are shifted spaces, which is 0x40 (' ' &lt;&lt; 1). Anything else in those six bytes
    /// that will not read as a callsign is a field that did not survive, and the two are opposite
    /// verdicts.
    /// </summary>
    [Fact]
    public void A_blank_destination_is_six_shifted_spaces_and_nothing_else()
    {
        byte[] frame = Convert.FromHexString(Pd4rBeaconHex);

        frame.Take(6).Should().AllBeEquivalentTo((byte)0x40,
            "a space is 0x20, and an AX.25 address byte is its character shifted left one bit");
    }

    /// <summary>
    /// The frame that says why the destination half has to be read. Taken off the live 40 m
    /// channel on 2026-09-12 (frames.db id=125786 on GB7RDG's slot 3), where the real
    /// GB7BPQ&gt;GB7RDG-2 frame it was a copy of reads
    /// <c>8E846EA4888E64 8E846E84A0A2E1 31</c>. Chase decoding moved six bits, Reed-Solomon
    /// closed over the result, four bytes of the destination came back wrong - it decodes to
    /// <c>GB&amp;)&gt;W</c> - and the source half still spelled a callsign, so the row went onto
    /// the waterfall as GB7BPQ at a plausible SNR with no signal under it.
    /// </summary>
    [Fact]
    public void A_corrupt_destination_does_not_attribute_its_source()
    {
        byte[] frame = Convert.FromHexString("8E844C527CAE648E846E84A0A2E131");

        new string(frame.Take(6).Select(b => (char)(b >> 1)).ToArray())
            .Should().Be("GB&)>W", "this is the field as it came off the air");

        Ax25AddressParser.TryParse(frame, out string source, out string destination)
            .Should().BeFalse(
                "half the address field is provably wrong, so the other half is not evidence of "
                    + "who transmitted");
        source.Should().Be("");
        destination.Should().Be("");
    }

    /// <summary>
    /// The boundary between the two, in the one shape where every character is legal: a field
    /// that starts with a space and then carries callsign characters is not a callsign, and is
    /// not the blank field a beacon leaves either.
    /// </summary>
    [Fact]
    public void A_destination_that_starts_with_a_space_but_is_not_blank_does_not_attribute()
    {
        byte[] frame = Frame(" B7RDG", 0, "M0LTE", 0);

        Ax25AddressParser.TryParse(frame, out string source, out _).Should().BeFalse();
        source.Should().Be("");
    }

    [Fact]
    public void A_garbage_source_is_still_rejected_whatever_the_destination_says()
    {
        // Reading the fields independently must not loosen genuine validation: a source field
        // that is not a shifted callsign yields nothing, however clean the destination looks.
        byte[] frame = Frame("GB7RDG", 0, "M0LTE", 0);
        frame[8] = 0xFF;

        Ax25AddressParser.TryParse(frame, out string source, out _).Should().BeFalse();
        source.Should().Be("");
    }
}
