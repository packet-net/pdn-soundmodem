using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class Ax25TransmitOptimizationTests
{
    [Fact]
    public void An_Empty_Queue_Has_No_Survivors() =>
        Ax25TransmitOptimization.FindSurvivors([]).Should().BeEmpty();

    [Theory]
    [InlineData(0x01)] // RR
    [InlineData(0x00)] // I
    [InlineData(0x03)] // UI
    public void A_Single_Request_Represents_Itself(byte control) =>
        Ax25TransmitOptimization.FindSurvivors([Candidate(control)]).Should().Equal(0);

    [Fact]
    public void RR_Zero_One_Two_All_Point_Directly_To_The_Newest_Request()
    {
        Ax25TransmitOptimization.FindSurvivors([Frame(0x01), Frame(0x21), Frame(0x41)])
            .Should().Equal(2, 2, 2);
    }

    [Fact]
    public void RR_Sequence_Wrap_Keeps_The_Last_Request_Not_The_Largest_Number()
    {
        Ax25TransmitOptimization.FindSurvivors([Frame(0xC1), Frame(0xE1), Frame(0x01), Frame(0x21)])
            .Should().Equal(3, 3, 3, 3);
    }

    [Fact]
    public void RR_Sequence_Numbers_Need_Not_Increase_To_Keep_The_Last_Request()
    {
        Ax25TransmitOptimization.FindSurvivors([Frame(0x41), Frame(0x21), Frame(0x01)])
            .Should().Equal(2, 2, 2);
    }

    [Fact]
    public void Identical_RR_Requests_Still_Keep_The_Newest_Not_The_Earliest()
    {
        Ax25TransmitOptimization.FindSurvivors([Frame(0x21), Frame(0x21), Frame(0x21)])
            .Should().Equal(2, 2, 2);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public void Byte_Identical_Data_In_Different_Arrays_Keeps_The_Earliest_Request(byte control)
    {
        Ax25TransmitOptimization.FindSurvivors([
            Frame(control, 0xF0, 0x41), Frame(control, 0xF0, 0x41), Frame(control, 0xF0, 0x41)])
            .Should().Equal(0, 0, 0);
    }

    [Theory]
    [InlineData(0x02)] // N(S) changes, N(R) does not
    [InlineData(0x20)] // N(R) changes, N(S) does not
    public void I_Zero_One_Zero_Keeps_Both_Sequences_And_Maps_The_Repeat_To_Zero(byte changedControl)
    {
        Ax25TransmitOptimization.FindSurvivors([
            Frame(0x00, 0xF0, 0x41), Frame(changedControl, 0xF0, 0x41), Frame(0x00, 0xF0, 0x41)])
            .Should().Equal(0, 1, 0);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public void Data_Deduplication_Uses_The_Whole_PID_Payload_And_Length(byte control)
    {
        Ax25TransmitOptimization.FindSurvivors([
            Frame(control, 0xF0, 0x41, 0x42),
            Frame(control, 0xCC, 0x41, 0x42), // different PID, also non-polling if read as extended I
            Frame(control, 0xF0, 0x41, 0x43), // different last payload byte
            Frame(control, 0xF0, 0x41),       // shorter payload
            Frame(control, 0xF0, 0x41, 0x42, 0x43),
            Frame(control, 0xF0, 0x41, 0x42),
            Frame(control, 0xCC, 0x41, 0x42)])
            .Should().Equal(0, 1, 2, 3, 4, 0, 1);
    }

    [Fact]
    public void Every_Change_Between_I_UI_And_RR_Starts_A_New_Run()
    {
        // All six ordered category transitions occur here, with the same address throughout.
        Ax25TransmitOptimization.FindSurvivors([
            Candidate(0x00), Candidate(0x00),
            Candidate(0x03), Candidate(0x03),
            Frame(0x01), Frame(0x21),
            Candidate(0x00), Candidate(0x00),
            Frame(0x41), Frame(0x61),
            Candidate(0x03), Candidate(0x03),
            Candidate(0x00), Candidate(0x00)])
            .Should().Equal(0, 0, 2, 2, 5, 5, 6, 6, 9, 9, 10, 10, 12, 12);
    }

    [Theory]
    [InlineData(0x11, false)] // RR poll
    [InlineData(0x11, true)]  // RR final response
    [InlineData(0x31, false)] // RR, N(R)=1, poll
    [InlineData(0x10, false)] // I poll
    [InlineData(0x10, true)]  // I final response
    [InlineData(0x13, false)] // UI with P/F set
    [InlineData(0x13, true)]
    public void Poll_And_Final_Frames_Are_Preserved_And_Break_All_Runs(byte control, bool response)
    {
        byte[] boundary = control is 0x11 or 0x31 ? Frame(control) : Frame(control, 0xF0, 0x41);
        if (response)
        {
            boundary[6] = 0x64; // destination C clear
            boundary[13] = 0xE5; // source C set
        }

        ShouldBeBoundary(boundary);
    }

    [Theory]
    [InlineData(0x00, 0x01)] // extended I poll, standard I has no poll
    [InlineData(0x02, 0x03)] // extended N(S)=1, N(R)=1, poll
    [InlineData(0x00, 0xF1)] // an odd standard PID also reads as an extended poll
    [InlineData(0x10, 0x02)] // extended N(S)=8 with no poll, but standard I has P/F set
    public void An_I_Frame_Must_Be_Nonpolling_Under_Both_Control_Interpretations(
        byte firstControl, byte secondControl)
    {
        ShouldBeBoundary(Frame(firstControl, secondControl, 0xF0, 0x41));
    }

    [Fact]
    public void An_Extended_I_Frame_Nonpolling_Under_Both_Interpretations_Can_Be_Deduplicated()
    {
        // Extended N(S)=1, N(R)=1, P/F=0; the standard reading also has P/F=0.
        Ax25TransmitOptimization.FindSurvivors([
            Frame(0x02, 0x02, 0xF0, 0x41), Frame(0x02, 0x02, 0xF0, 0x41)])
            .Should().Equal(0, 0);
    }

    [Theory]
    [InlineData(0x05)] // RNR
    [InlineData(0x25)] // RNR, N(R)=1
    [InlineData(0x09)] // REJ
    [InlineData(0x29)] // REJ, N(R)=1
    [InlineData(0x0D)] // SREJ
    [InlineData(0x2D)] // SREJ, N(R)=1
    public void Non_RR_Supervisory_Frames_Are_Boundaries_Even_Without_Poll(byte control) =>
        ShouldBeBoundary(Frame(control));

    [Theory]
    [InlineData(0x01, 0x00)] // extended RR, N(R)=0
    [InlineData(0x01, 0x02)] // extended RR, N(R)=1
    [InlineData(0x01, 0xFE)] // extended RR, N(R)=127
    [InlineData(0x01, 0x01)] // extended RR poll, invisible in the first control octet
    [InlineData(0x01, 0xFF)] // extended RR, N(R)=127, poll
    [InlineData(0x05, 0x00)] // extended RNR
    [InlineData(0x09, 0x00)] // extended REJ
    [InlineData(0x0D, 0x00)] // extended SREJ
    public void Two_Octet_Supervisory_Control_Is_Never_Collapsed(byte control, byte secondControl) =>
        ShouldBeBoundary(Frame(control, secondControl));

    [Fact]
    public void An_RR_With_Unexpected_Trailing_Information_Is_A_Boundary() =>
        ShouldBeBoundary(Frame(0x01, 0xF0, 0x41));

    [Theory]
    [InlineData(0x2F)] // SABM
    [InlineData(0x3F)] // SABM poll
    [InlineData(0x6F)] // SABME
    [InlineData(0x7F)] // SABME poll
    [InlineData(0x43)] // DISC
    [InlineData(0x53)] // DISC poll
    [InlineData(0x63)] // UA
    [InlineData(0x73)] // UA final
    [InlineData(0x0F)] // DM
    [InlineData(0x1F)] // DM final
    public void Identical_Data_In_A_New_Session_Is_Not_Deduplicated_Across_Link_Management(byte control) =>
        ShouldBeBoundary(Frame(control));

    [Theory]
    [InlineData(0x87)] // FRMR, three diagnostic octets follow
    [InlineData(0xAF)] // XID
    [InlineData(0xE3)] // TEST
    [InlineData(0x23)] // other U control
    public void Other_Unnumbered_Frames_Are_Not_Data_Duplicates(byte control) =>
        ShouldBeBoundary(Frame(control, 0x00, 0x00, 0x01));

    [Fact]
    public void Null_Empty_And_Non_Ax25_Requests_Are_Preserved_As_Boundaries()
    {
        ShouldBeBoundary(null);
        ShouldBeBoundary([]);
        ShouldBeBoundary(new byte[40]);
        ShouldBeBoundary("not an ax25 frame at all"u8.ToArray());
        ShouldBeBoundary([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public void Every_Truncation_Of_The_Required_Address_Control_And_PID_Is_A_Boundary(byte control)
    {
        // RR needs an address and control; I and UI additionally need a PID, even with no payload.
        byte[] minimumFrame = control == 0x01 ? Frame(control) : Frame(control, 0xF0);
        for (int length = 0; length < minimumFrame.Length; length++)
        {
            ShouldBeBoundary(minimumFrame[..length]);
        }
    }

    [Theory]
    [InlineData(0, 0x8F)]  // destination character has the low bit set
    [InlineData(7, 0x8F)]  // source character has the low bit set
    [InlineData(0, 0xC2)]  // shifted lower-case 'a'
    [InlineData(7, 0x42)]  // shifted punctuation '!'
    [InlineData(6, 0xE5)]  // destination incorrectly terminates the address field
    [InlineData(13, 0x64)] // source promises a digipeater that is absent
    public void Malformed_Addresses_Do_Not_Qualify_As_Optimization_Candidates(int offset, byte value)
    {
        foreach (byte control in new byte[] { 0x01, 0x00, 0x03 })
        {
            byte[] frame = Candidate(control);
            frame[offset] = value;
            ShouldBeBoundary(frame);
        }
    }

    [Fact]
    public void A_Truncated_Digipeater_Path_Is_A_Boundary()
    {
        byte[] frame = Via(Candidate(0x03), ("GB7BWR", 0x62), ("GB7XYZ", 0x64));
        // The source says a path follows, and the first digipeater says another follows.
        for (int length = 14; length <= 28; length++)
        {
            ShouldBeBoundary(frame[..length]);
        }
    }

    [Theory]
    [InlineData(0, 0x90)]  // destination callsign
    [InlineData(7, 0x90)]  // source callsign
    [InlineData(6, 0xE6)]  // destination SSID, C bit unchanged
    [InlineData(13, 0x67)] // source SSID, C bit unchanged
    [InlineData(6, 0x64)]  // destination C bit only
    [InlineData(13, 0xE5)] // source C bit only
    public void The_Complete_Raw_Source_And_Destination_Define_A_Run(int offset, byte value)
    {
        foreach (byte control in new byte[] { 0x01, 0x00, 0x03 })
        {
            byte[] original = Candidate(control);
            byte[] changed = original.ToArray();
            changed[offset] = value;
            ShouldKeepSeparateRuns(original, changed, control == 0x01);
        }
    }

    [Fact]
    public void Command_And_Response_Addresses_Are_Different_Runs()
    {
        foreach (byte control in new byte[] { 0x01, 0x00, 0x03 })
        {
            byte[] command = Candidate(control);
            byte[] response = command.ToArray();
            response[6] = 0x64;
            response[13] = 0xE5;
            ShouldKeepSeparateRuns(command, response, control == 0x01);
        }
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public void Matching_Digipeater_Paths_Find_The_Control_After_All_Addresses(byte control)
    {
        byte[] first = Via(Candidate(control), ("GB7BWR", 0xE2), ("GB7XYZ", 0x64));
        byte[] second = Via(Candidate(control == 0x01 ? (byte)0x41 : control),
            ("GB7BWR", 0xE2), ("GB7XYZ", 0x64));
        Ax25TransmitOptimization.FindSurvivors([first, second])
            .Should().Equal(control == 0x01 ? new[] { 1, 1 } : new[] { 0, 0 });
    }

    [Theory]
    [InlineData("GB7ABC", 0x62, "GB7XYZ", 0x64)] // first callsign
    [InlineData("GB7BWR", 0x62, "GB7ABC", 0x64)] // second callsign
    [InlineData("GB7BWR", 0x66, "GB7XYZ", 0x64)] // first SSID
    [InlineData("GB7BWR", 0x62, "GB7XYZ", 0x66)] // second SSID
    [InlineData("GB7BWR", 0xE2, "GB7XYZ", 0x64)] // first H bit
    [InlineData("GB7BWR", 0x62, "GB7XYZ", 0xE4)] // second H bit
    [InlineData("GB7XYZ", 0x64, "GB7BWR", 0x62)] // path order
    public void Every_Digipeater_Callsign_SSID_H_Bit_And_Position_Is_Part_Of_The_Run(
        string firstCall, byte firstSsid, string secondCall, byte secondSsid)
    {
        foreach (byte control in new byte[] { 0x01, 0x00, 0x03 })
        {
            byte[] original = Via(Candidate(control), ("GB7BWR", 0x62), ("GB7XYZ", 0x64));
            byte[] changed = Via(Candidate(control), (firstCall, firstSsid), (secondCall, secondSsid));
            ShouldKeepSeparateRuns(original, changed, control == 0x01);
        }
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public void Adding_Or_Removing_A_Digipeater_Starts_A_New_Run(byte control)
    {
        byte[] direct = Candidate(control);
        byte[] oneHop = Via(direct, ("GB7BWR", 0x62));
        byte[] twoHops = Via(direct, ("GB7BWR", 0x62), ("GB7XYZ", 0x64));
        ShouldKeepSeparateRuns(direct, oneHop, control == 0x01);
        ShouldKeepSeparateRuns(oneHop, twoHops, control == 0x01);
        ShouldKeepSeparateRuns(twoHops, direct, control == 0x01);
    }

    private static void ShouldBeBoundary(byte[]? boundary)
    {
        // The boundary itself must survive twice, and optimization must resume on its far side.
        Ax25TransmitOptimization.FindSurvivors([
            Candidate(0x00), Candidate(0x00), boundary, boundary?.ToArray(),
            Candidate(0x00), Candidate(0x00)])
            .Should().Equal(0, 0, 2, 3, 4, 4);
        Ax25TransmitOptimization.FindSurvivors([
            Candidate(0x03), Candidate(0x03), boundary, boundary?.ToArray(),
            Candidate(0x03), Candidate(0x03)])
            .Should().Equal(0, 0, 2, 3, 4, 4);
        Ax25TransmitOptimization.FindSurvivors([
            Frame(0x01), Frame(0x21), boundary, boundary?.ToArray(), Frame(0x41), Frame(0x61)])
            .Should().Equal(1, 1, 2, 3, 5, 5);
    }

    private static void ShouldKeepSeparateRuns(byte[] original, byte[] changed, bool keepNewest)
    {
        Ax25TransmitOptimization.FindSurvivors([
            original, original.ToArray(), changed, changed.ToArray(), original.ToArray(), original.ToArray()])
            .Should().Equal(keepNewest ? new[] { 1, 1, 3, 3, 5, 5 } : new[] { 0, 0, 2, 2, 4, 4 });
    }

    private static byte[] Candidate(byte control) =>
        (control & 0x0F) == 0x01 ? Frame(control) : Frame(control, 0xF0, 0x41);

    private static byte[] Frame(byte control, params byte[] info)
    {
        // Standard AX.25 bytes without flags or FCS. F0's low bit also means no extended-I poll.
        byte[] frame = new byte[15 + info.Length];
        "GB7OXF".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 0);
        frame[6] = 0xE4; // destination SSID 2, command C bit set, another address follows
        "GB7RDG".Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, 7);
        frame[13] = 0x65; // source SSID 2, C bit clear, last address
        frame[14] = control;
        info.CopyTo(frame, 15);
        return frame;
    }

    private static byte[] Via(byte[] direct, params (string Callsign, byte Ssid)[] path)
    {
        byte[] frame = new byte[direct.Length + (7 * path.Length)];
        direct.AsSpan(0, 14).CopyTo(frame);
        frame[13] &= 0xFE;
        for (int i = 0; i < path.Length; i++)
        {
            int offset = 14 + (7 * i);
            path[i].Callsign.Select(c => (byte)(c << 1)).ToArray().CopyTo(frame, offset);
            frame[offset + 6] = (byte)((path[i].Ssid & 0xFE) | (i == path.Length - 1 ? 1 : 0));
        }

        direct.AsSpan(14).CopyTo(frame.AsSpan(14 + (7 * path.Length)));
        return frame;
    }
}
