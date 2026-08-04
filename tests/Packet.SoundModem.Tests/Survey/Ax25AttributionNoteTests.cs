using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// Explaining an unattributed frame instead of leaving somebody to reconstruct it from a payload
/// blob. Every case here is one the operator could actually be looking at: a real 118-byte
/// bpsk300 frame arrived CRC-valid on the live 40 m channel and would not yield callsigns, and
/// the only way to find out why was to open the frame log by hand.
/// </summary>
public class Ax25AttributionNoteTests
{
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
        // Byte 6 and byte 13 carry the SSID and the flags, not text — reporting them as bad
        // callsign characters would send somebody looking in the wrong place.
        byte[] frame = Frame("GB7RDG", "M0LTE");
        frame[6] = 0x7F;
        frame[13] = 0x61;

        Ax25AttributionNote.For(frame).Should().BeNull();
    }

    [Fact]
    public void An_Address_Field_Of_Spaces_Is_Reported_As_An_Empty_Callsign()
    {
        // Every character legal, no callsign present — the one remaining way the parse fails
        // with nothing specific to point at.
        byte[] frame = Frame("      ", "      ");

        Ax25AttributionNote.For(frame).Should().NotBeNull().And.Contain("empty callsign");
    }
}
