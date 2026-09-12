using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// Explaining an unattributed frame instead of leaving somebody to reconstruct it from a payload
/// blob. Every case here is one the operator could actually be looking at: a real 118-byte
/// bpsk300 frame arrived CRC-valid on the live 40 m channel and would not yield callsigns, and
/// the only way to find out why was to open the frame log by hand.
/// </summary>
public class Ax25AttributionNoteTests
{
    // Hand-rolled rather than Ax25UiFrame.Build on purpose: these cases exercise the
    // attribution note's handling of truncated and PID-less shapes a well-formed builder
    // cannot produce.
    private static byte[] Frame(string destination, string source, int length = 20)
    {
        var frame = new byte[length];
        Write(frame, 0, destination, last: false);
        Write(frame, 7, source, last: true);
        if (length > 14)
        {
            frame[14] = 0x03;
        }

        return frame;

        static void Write(byte[] frame, int at, string call, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }

    [Fact]
    public void A_Frame_With_Readable_Callsigns_Has_Nothing_To_Explain()
    {
        Ax25AttributionNote.For(Frame("GB7RDG", "M0LTE")).Should().BeNull();
    }

    [Fact]
    public void A_Frame_Too_Short_To_Hold_An_Address_Field_Says_So_With_Its_Length()
    {
        string? note = Ax25AttributionNote.For(new byte[12]);

        note.Should().NotBeNull().And.Contain("12 bytes").And.Contain("shorter");
    }

    [Fact]
    public void A_Payload_That_Is_Not_An_Address_Field_Names_The_Byte_That_Gave_It_Away()
    {
        // The live case: 118 bytes, CRC-valid, and the first byte of what should be a shifted
        // destination callsign is not a callsign character at all.
        byte[] frame = [0x00, 0x01, 0x02, 0x03, .. new byte[114]];

        string? note = Ax25AttributionNote.For(frame);

        note.Should().NotBeNull()
            .And.Contain("byte 0")
            .And.Contain("destination")
            .And.Contain("0x00");
    }

    [Fact]
    public void A_Bad_Character_In_The_Source_Callsign_Names_That_Field_Instead()
    {
        byte[] frame = Frame("GB7RDG", "M0LTE");
        frame[8] = (byte)('/' << 1);   // a slash, which no AX.25 callsign carries

        string? note = Ax25AttributionNote.For(frame);

        note.Should().NotBeNull().And.Contain("byte 8").And.Contain("source").And.Contain("'/'");
    }

    [Fact]
    public void An_Ssid_Byte_Is_Not_Read_As_A_Callsign_Character()
    {
        // Byte 6 and byte 13 carry the SSID and the flags, not text - reporting them as bad
        // callsign characters would send somebody looking in the wrong place.
        byte[] frame = Frame("GB7RDG", "M0LTE");
        frame[6] = 0x7F;
        frame[13] = 0x61;

        Ax25AttributionNote.For(frame).Should().BeNull();
    }

    [Fact]
    public void An_Address_Field_Of_Spaces_Is_Reported_As_An_Empty_Callsign()
    {
        // Every character legal, no callsign present - one of the two ways the parse fails with
        // nothing specific to point at. Both fields are blank here, so the destination is the
        // blank field a beacon leaves rather than a corrupt one, and what is left to report is
        // the empty source. The other way is the test below it.
        byte[] frame = Frame("      ", "      ");

        Ax25AttributionNote.For(frame).Should().NotBeNull().And.Contain("empty source callsign");
    }

    [Fact]
    public void A_Blank_Destination_With_A_Readable_Source_Has_Nothing_To_Explain()
    {
        // The PD4R-12 beacon shape from the live 40 m channel: the destination field is all
        // spaces and the sender is perfectly readable. The frame is attributed to its source,
        // so it is not the survey's business and there is no note to write.
        Ax25AttributionNote.For(Frame("      ", "PD4R")).Should().BeNull();
    }

    [Fact]
    public void A_Destination_That_Is_Neither_A_Callsign_Nor_Blank_Says_Which_It_Is()
    {
        // Every character legal, and still not an address field: a destination that starts with a
        // space and then carries characters is not a callsign, and is not the blank field a
        // beacon leaves. The byte loop above has nothing to point at, so the fallback has to name
        // the field - saying "empty source callsign" here would send somebody to the wrong half.
        string? note = Ax25AttributionNote.For(Frame(" B7RDG", "M0LTE"));

        note.Should().NotBeNull().And.Contain("destination").And.Contain("neither");
    }

    /// <summary>
    /// The other way a row ends up with no callsign: the bytes read perfectly well, and nothing
    /// checked the bytes.
    /// </summary>
    /// <remarks>
    /// Without this the panel says "unattributed" about an address field that plainly is one, and
    /// the next person spends an evening looking for a parser bug. The note is what tells them the
    /// frame was read and the reading was not worth a name.
    /// </remarks>
    [Fact]
    public void A_Readable_Frame_Nothing_Checked_Says_Why_Its_Callsign_Is_Withheld()
    {
        byte[] frame = Frame("GB7RDG", "GB7BPQ");
        var chased = new FrameQuality(
            "bpsk300-il2pc", frame.Length, CorrectedBytes: 2, CrcValid: null,
            PlainIl2p: true, MonitorOnly: true, ChasedBits: 6);

        string? note = Ax25AttributionNote.For(frame, chased);

        note.Should().NotBeNull()
            .And.Contain("withheld")
            .And.Contain("Reed-Solomon")
            .And.Contain("6 bits");
    }

    [Fact]
    public void A_Readable_Frame_Something_Checked_Still_Has_Nothing_To_Explain()
    {
        byte[] frame = Frame("GB7RDG", "GB7BPQ");
        var verified = new FrameQuality(
            "bpsk300-il2pc", frame.Length, CorrectedBytes: 2, CrcValid: true, ChasedBits: 6);

        Ax25AttributionNote.For(frame, verified).Should().BeNull(
            "chase decoding behind a CRC is the best-behaved class measured, not a reason to "
                + "withhold anything");
    }

    [Fact]
    public void An_Unreadable_Frame_Still_Says_Why_It_Would_Not_Read()
    {
        // The quality overload must not swallow the original diagnosis: a frame whose bytes will
        // not read gets the byte that gave it away, whatever the decode was worth.
        byte[] frame = Frame("GB7RDG", "M0LTE");
        frame[8] = (byte)('/' << 1);
        var chased = new FrameQuality(
            "bpsk300-il2pc", frame.Length, CorrectedBytes: 2, CrcValid: null,
            PlainIl2p: true, MonitorOnly: true, ChasedBits: 6);

        Ax25AttributionNote.For(frame, chased).Should().NotBeNull()
            .And.Contain("byte 8").And.Contain("source");
    }
}
