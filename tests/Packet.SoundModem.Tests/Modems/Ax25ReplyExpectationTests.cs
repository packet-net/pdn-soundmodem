using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Which frames get an answer, and - the part that keeps a modem a modem - which byte strings
/// this declines to have an opinion about at all.
/// </summary>
public class Ax25ReplyExpectationTests
{
    /// <summary>GB7OXF-2 &lt; GB7RDG-2, the RR poll this station kept sending unanswered.</summary>
    private const string RealPoll = "8E846E9EB08CE48E846EA4888E6551";

    /// <summary>GB7RDG's own ident: a UI frame to "ID", which nobody answers.</summary>
    private const string RealIdent =
        "928840404040E08E846EA4888E6103F0474237524447202D2052656164696E67202D2068747470"
        + "733A2F2F756B7061636B6574726164696F2E6E6574776F726B2F6E6F6465733A676237726467200D";

    private static byte[] Frame(byte control, params byte[] info)
    {
        byte[] frame = new byte[15 + info.Length];
        "GB7OXF".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 0);
        frame[6] = 0x64;
        "GB7RDG".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 7);
        frame[13] = 0x65; // last address
        frame[14] = control;
        info.CopyTo(frame, 15);
        return frame;
    }

    [Fact]
    public void The_Poll_This_Station_Kept_Losing_Expects_A_Reply() =>
        Ax25ReplyExpectation.ExpectsReply(Convert.FromHexString(RealPoll)).Should().BeTrue(
            "it is an RR with the poll bit set, which is a demand for a response - and holding "
            + "the channel for that response is the whole point");

    [Fact]
    public void The_Stations_Own_Ident_Expects_Nothing() =>
        Ax25ReplyExpectation.ExpectsReply(Convert.FromHexString(RealIdent)).Should().BeFalse(
            "a UI frame to ID is a broadcast; holding the channel quiet after every ident would "
            + "spend airtime waiting for something that is never coming");

    [Theory]
    [InlineData(0x00)] // I-frame, N(S)=0 N(R)=0, no poll
    [InlineData(0x10)] // I-frame with the poll bit
    [InlineData(0x22)] // I-frame, other sequence numbers
    public void An_Information_Frame_Always_Expects_An_Acknowledgement(byte control) =>
        Ax25ReplyExpectation.ExpectsReply(Frame(control, 0xF0, 0x41)).Should().BeTrue();

    [Theory]
    [InlineData(0x11)] // RR, final bit set: this IS an answer, not a question
    [InlineData(0x01)] // RR, no poll
    [InlineData(0x05)] // RNR, no poll
    public void A_Supervisory_Frame_Expects_A_Reply_Only_When_It_Polls(byte control) =>
        Ax25ReplyExpectation.ExpectsReply(Frame(control)).Should().Be((control & 0x10) != 0);

    [Theory]
    [InlineData(0x3F, true)]  // SABM, as it is always sent: a poll
    [InlineData(0x2F, true)]  // SABM with the poll bit clear - unusual, still draws a UA
    [InlineData(0x7F, true)]  // SABME
    [InlineData(0x6F, true)]  // SABME, poll clear
    [InlineData(0x53, true)]  // DISC
    [InlineData(0x43, true)]  // DISC, poll clear
    [InlineData(0x63, false)] // UA - the answer to SABM, not a question
    [InlineData(0x0F, false)] // DM
    [InlineData(0x03, false)] // UI
    public void Link_Setup_And_Teardown_Expect_An_Answer_And_Their_Answers_Do_Not(
        byte control, bool expected) =>
        Ax25ReplyExpectation.ExpectsReply(Frame(control)).Should().Be(expected);

    [Fact]
    public void A_Payload_That_Is_Not_Ax25_Gets_No_Opinion()
    {
        // The forwards-compatibility rule: this channel already carries IL2P payloads that are
        // not AX.25 and will carry more. Anything that does not read as an address field must
        // come back false, so the station keeps behaving exactly as it did before the hold
        // existed rather than holding for a reply the protocol may never send.
        Ax25ReplyExpectation.ExpectsReply("not an ax25 frame at all"u8).Should().BeFalse();
        Ax25ReplyExpectation.ExpectsReply(new byte[40]).Should().BeFalse("all zeroes is not a callsign");
        Ax25ReplyExpectation.ExpectsReply([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]).Should().BeFalse();
        Ax25ReplyExpectation.ExpectsReply([]).Should().BeFalse();
        Ax25ReplyExpectation.ExpectsReply(Convert.FromHexString(RealPoll)[..12]).Should().BeFalse("truncated");
    }

    [Fact]
    public void A_Lower_Case_Or_Punctuated_Field_Is_Not_An_Address()
    {
        // Real IL2P payloads that happen to be text: shifted ASCII of readable bytes lands in
        // exactly this range, so the check has to be tight or arbitrary data reads as a header.
        byte[] text = "hello world, this is a payload"u8.ToArray();
        Ax25ReplyExpectation.ExpectsReply(text).Should().BeFalse();
    }

    [Fact]
    public void A_Digipeated_Frame_Finds_Its_Control_Field_Past_The_Path()
    {
        byte[] frame = new byte[22];
        "GB7OXF".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 0);
        frame[6] = 0x64;
        "GB7RDG".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 7);
        frame[13] = 0x64; // not the last address: a digipeater follows
        "GB7BWR".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 14);
        frame[20] = 0x65; // last address
        frame[21] = 0x11; // RR with the poll bit

        Ax25ReplyExpectation.ExpectsReply(frame).Should().BeTrue(
            "the address field is self-delimiting, so a via path moves the control octet along");
    }
}
