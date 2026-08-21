using AwesomeAssertions;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.Tests.MultiDecode;

/// <summary>
/// How pdn-decode renders a frame for a person to read.
/// </summary>
/// <remarks>
/// The header decode is the part worth pinning, and not because parsing AX.25 is hard. A tool
/// that prints confident-looking callsigns off bytes that were never an AX.25 frame is worse than
/// one that prints none: a Reed-Solomon-only decode of noise is a real thing this tool will
/// occasionally produce, and it must come out as a hex dump with no callsign line rather than as
/// a plausible station nobody transmitted.
/// </remarks>
public class FrameTextTests
{
    [Fact]
    public void A_Ui_Frame_Reads_As_Source_Destination_And_Payload()
    {
        byte[] frame = [.. Address("APRS", false), .. Address("M0LTE", true), 0x03, 0xF0, .. "hi"u8];

        FrameText.Ax25Header(frame).Should().Be("M0LTE>APRS  UI  pid=F0");
        FrameText.InfoField(frame).Should().Be("hi");
    }

    [Fact]
    public void A_Digipeated_Frame_Names_Its_Path_And_Marks_Who_Handled_It()
    {
        byte[] frame =
        [
            .. Address("APRS", false),
            .. Address("M0LTE", false, ssid: 7),
            .. Address("WIDE1", false, ssid: 1, repeated: true),
            .. Address("WIDE2", true, ssid: 1),
            0x03, 0xF0, .. "beacon"u8,
        ];

        FrameText.Ax25Header(frame).Should().Be("M0LTE-7>APRS via WIDE1-1*,WIDE2-1  UI  pid=F0");
        FrameText.InfoField(frame).Should().Be("beacon");
    }

    // The AX.25 2.2 control-byte encodings, with the P/F bit (0x10) in and out. Worth spelling
    // out one by one: the U-frame types are not contiguous, and the reader's instinct that UI is
    // 0x00 is wrong - 0x00 has bit 0 clear, which makes it an I frame.
    [Theory]
    [InlineData(0x03, "UI")]
    [InlineData(0x13, "UI P/F")]
    [InlineData(0x2F, "SABM")]
    [InlineData(0x3F, "SABM P/F")]
    [InlineData(0x6F, "SABME")]
    [InlineData(0x43, "DISC")]
    [InlineData(0x53, "DISC P/F")]
    [InlineData(0x63, "UA")]
    [InlineData(0x0F, "DM")]
    [InlineData(0x1F, "DM P/F")]
    [InlineData(0x87, "FRMR")]
    [InlineData(0xAF, "XID")]
    [InlineData(0xE3, "TEST")]
    [InlineData(0x01, "RR nr=0")]
    [InlineData(0x21, "RR nr=1")]
    [InlineData(0x05, "RNR nr=0")]
    [InlineData(0x09, "REJ nr=0")]
    [InlineData(0x0D, "SREJ nr=0")]
    public void Control_Bytes_Are_Named(byte control, string expected)
    {
        byte[] frame = [.. Address("TEST", false), .. Address("Q0AAA", true), control];

        FrameText.Ax25Header(frame).Should().EndWith(expected);
    }

    [Fact]
    public void An_I_Frame_Reports_Its_Sequence_Numbers()
    {
        // Modulo 8 is assumed and stated: a single frame carries nothing that distinguishes it
        // from a modulo 128 link, so the tool commits to the reading it can justify.
        byte[] frame = [.. Address("TEST", false), .. Address("Q0AAA", true), 0x42, 0xF0, .. "data"u8];

        FrameText.Ax25Header(frame).Should().Be("Q0AAA>TEST  I ns=1 nr=2  pid=F0");
        FrameText.InfoField(frame).Should().Be("data");
    }

    [Fact]
    public void Bytes_That_Are_Not_An_Ax25_Frame_Get_No_Header_At_All()
    {
        // Taken from a real run: c4fsk19200 read this out of a bpsk300 recording on Reed-Solomon
        // alone. It is 16 plausible-looking bytes and it is not a frame anyone sent.
        byte[] notAFrame =
        [
            0xaa, 0x64, 0x7e, 0x50, 0xa8, 0x6a, 0xe4, 0x7a,
            0x6e, 0x54, 0x7e, 0x50, 0xb8, 0x63, 0x13, 0xf0,
        ];

        FrameText.Ax25Header(notAFrame).Should().BeNull();
        FrameText.InfoField(notAFrame).Should().BeNull();
    }

    [Fact]
    public void A_Frame_Too_Short_To_Hold_Two_Addresses_Gets_No_Header()
    {
        FrameText.Ax25Header([1, 2, 3]).Should().BeNull();
        FrameText.Ax25Header([]).Should().BeNull();
    }

    [Fact]
    public void An_Address_Field_That_Never_Terminates_Is_Rejected()
    {
        // Every address says "more to follow" and the frame runs out. Insisting would walk off
        // the end or invent digipeaters.
        byte[] frame = [.. Address("TEST", false), .. Address("Q0AAA", false), 0x03, 0xF0];

        FrameText.Ax25Header(frame).Should().BeNull();
    }

    [Fact]
    public void The_Hex_Dump_Is_The_Canonical_Layout()
    {
        byte[] data = [.. "Hello, world!"u8, 0x00, 0x0d, 0x0a, 0xff];

        // The short last row pads to the full width so the ASCII gutters line up: one byte, then
        // the 15 missing slots (three columns each, four across the halfway gap) before the bar.
        FrameText.HexDump(data, indent: 2).Should().Be(
            "  0000  48 65 6c 6c 6f 2c 20 77  6f 72 6c 64 21 00 0d 0a  |Hello, world!...|\n"
            + "  0010  ff" + new string(' ', 46) + "  |.|\n");
    }

    [Fact]
    public void Non_Printable_Bytes_Become_Dots_Rather_Than_Terminal_Escapes()
    {
        // The gutter goes to a terminal whose locale is not ours to assume, and a decode artefact
        // in the high bytes must not be able to move the cursor or start an escape sequence.
        FrameText.Printable([0x1b, 0x5b, 0x32, 0x4a, 0x07, 0x80, 0xff]).Should().Be(".[2J...");
    }

    private static byte[] Address(string call, bool last, int ssid = 0, bool repeated = false)
    {
        var address = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            address[i] = (byte)((i < call.Length ? char.ToUpperInvariant(call[i]) : ' ') << 1);
        }

        address[6] = (byte)((repeated ? 0x80 : 0x00) | 0x60 | (ssid << 1) | (last ? 1 : 0));
        return address;
    }
}
