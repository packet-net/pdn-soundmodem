using AwesomeAssertions;
using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>Reading an AX.25 header: the station a frame was heard from and is for next, whether
/// it is a UI frame for everyone, and whether it is expedited. Expected values are written out by
/// hand from the AX.25 rules, not taken from the code.</summary>
public class Ax25HeaderTests
{
    /// <summary>Address fields followed by the given control and payload bytes. A trailing
    /// <c>*</c> sets the H bit; the extension bit is set on the last address.</summary>
    private static byte[] Build(byte[] controlAndPayload, params string[] addresses)
    {
        var bytes = new List<byte>();
        for (int n = 0; n < addresses.Length; n++)
        {
            string address = addresses[n];
            bool repeated = address.EndsWith('*');
            address = address.TrimEnd('*');
            string[] parts = address.Split('-');
            foreach (char c in parts[0].PadRight(6))
            {
                bytes.Add((byte)(c << 1));
            }

            int ssid = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            byte last = (byte)(0x60 | (ssid << 1));
            if (repeated)
            {
                last |= 0x80;
            }

            if (n == addresses.Length - 1)
            {
                last |= 0x01;
            }

            bytes.Add(last);
        }

        bytes.AddRange(controlAndPayload);
        return [.. bytes];
    }

    private static byte[] WithControl(byte control, params string[] addresses) => Build([control], addresses);

    public static TheoryData<string[], string, string> Paths => new()
    {
        // addresses (destination, source, digipeaters), expected heard station, expected next hop
        { ["DEST", "SRC"], "SRC", "DEST" },
        { ["DEST", "SRC", "D1"], "SRC", "D1" },
        { ["DEST", "SRC", "D1*"], "D1", "DEST" },
        { ["DEST", "SRC", "D1*", "D2"], "D1", "D2" },
        { ["DEST", "SRC", "D1*", "D2*"], "D2", "DEST" },
        { ["DEST", "SRC", "D1", "D2*"], "D2", "D1" },
        { ["DEST", "SRC", "D1*", "D2*", "D3"], "D2", "D3" },
        { ["DEST", "SRC", "D1*", "D2*", "D3*", "D4*", "D5*", "D6*", "D7*", "D8*"], "D8", "DEST" },
        { ["DEST", "SRC", "D1*", "D2*", "D3*", "D4*", "D5*", "D6*", "D7*", "D8"], "D7", "D8" },
        { ["DEST", "SRC", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8"], "SRC", "D1" },
        { ["DEST-0", "SRC-0"], "SRC", "DEST" },
        { ["DEST-15", "SRC-15"], "SRC-15", "DEST-15" },
        { ["DEST", "SRC-1", "G4ABC-15*"], "G4ABC-15", "DEST" },
        { ["DEST", "M0LTE-7", "RELAY-3"], "M0LTE-7", "RELAY-3" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void The_Heard_Station_And_The_Next_Hop_Follow_The_H_Bits(string[] addresses, string heard, string next)
    {
        byte[] frame = WithControl(0x03, addresses);

        Ax25Header.HeardStation(frame).Should().Be(heard);
        Ax25Header.NextHop(frame).Should().Be(next);
    }

    [Fact]
    public void A_Lower_Case_Callsign_Is_Written_Upper_Case_And_SSID_Zero_Bare()
    {
        // The address field is shifted ASCII; a lower-case letter is not a callsign.
        byte[] upper = WithControl(0x03, "G4ABC-0", "M0LTE-1");
        Ax25Header.ReadStation(upper.AsSpan(0, 7)).Should().Be("G4ABC");
        Ax25Header.ReadStation(upper.AsSpan(7, 7)).Should().Be("M0LTE-1");

        byte[] lower = WithControl(0x03, "g4abc", "M0LTE");
        Ax25Header.ReadStation(lower.AsSpan(0, 7)).Should().BeNull();
        Ax25Header.HeardStation(lower).Should().BeNull();
    }

    [Fact]
    public void A_Header_That_Does_Not_Read_Gives_Nothing()
    {
        byte[] good = WithControl(0x03, "DEST", "SRC");

        // Truncated at every length short of address plus control.
        for (int length = 0; length < 15; length++)
        {
            Ax25Header.HeardStation(good.AsSpan(0, length)).Should().BeNull($"length {length}");
            Ax25Header.NextHop(good.AsSpan(0, length)).Should().BeNull($"length {length}");
            Ax25Header.IsExpedited(good.AsSpan(0, length)).Should().BeFalse($"length {length}");
            Ax25Header.IsUiWithNoDigipeaterPending(good.AsSpan(0, length)).Should().BeFalse($"length {length}");
        }

        // A bad address byte anywhere in the field.
        byte[] bad = (byte[])good.Clone();
        bad[9] = 0x01;
        Ax25Header.HeardStation(bad).Should().BeNull();
        Ax25Header.NextHop(bad).Should().BeNull();

        // No extension bit ever set: runs out of addresses.
        byte[] noEnd = (byte[])good.Clone();
        noEnd[13] &= 0xFE;
        Ax25Header.NextHop(noEnd).Should().BeNull();

        // Eleven addresses without an end.
        byte[] tooMany = Build([0x03], "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K");
        Ax25Header.NextHop(tooMany).Should().BeNull();
        Ax25Header.IsExpedited(tooMany).Should().BeFalse();
    }

    [Fact]
    public void A_Blank_Callsign_Is_Not_A_Station()
    {
        Ax25Header.NextHop(WithControl(0x03, "", "SRC")).Should().BeNull();
    }

    [Theory]
    [InlineData(0x03, true)]    // UI
    [InlineData(0x13, true)]    // UI with P/F
    [InlineData(0x0F, false)]   // DM
    [InlineData(0x01, false)]   // RR
    [InlineData(0x00, false)]   // I
    [InlineData(0x2F, false)]   // SABM
    public void Only_A_UI_Frame_Is_For_Everyone(int control, bool expected)
    {
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl((byte)control, "BEACON", "M0LTE")).Should().Be(expected);
    }

    [Fact]
    public void A_UI_Frame_Is_For_Everyone_Only_When_Every_Digipeater_Has_Repeated_It()
    {
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl(0x03, "ID", "M0LTE", "D1")).Should().BeFalse();
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl(0x03, "ID", "M0LTE", "D1*", "D2")).Should().BeFalse();
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl(0x03, "ID", "M0LTE", "D1", "D2*")).Should().BeFalse();
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl(0x03, "ID", "M0LTE", "D1*", "D2*")).Should().BeTrue();
        Ax25Header.IsUiWithNoDigipeaterPending(WithControl(0x03, "ID", "M0LTE")).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x01, true)]    // RR
    [InlineData(0x05, true)]    // RNR
    [InlineData(0x09, true)]    // REJ
    [InlineData(0x0D, true)]    // SREJ
    [InlineData(0xA1, true)]    // RR, N(R)=5
    [InlineData(0x11, true)]    // RR with P/F
    [InlineData(0xF5, true)]    // RNR, N(R)=7, P/F
    [InlineData(0x63, true)]    // UA
    [InlineData(0x73, true)]    // UA with P/F
    [InlineData(0x0F, true)]    // DM
    [InlineData(0x1F, true)]    // DM with P/F
    [InlineData(0x00, false)]   // I
    [InlineData(0xFE, false)]   // I, N(R)=7 N(S)=7, P
    [InlineData(0x03, false)]   // UI
    [InlineData(0x13, false)]   // UI with P/F
    [InlineData(0x2F, false)]   // SABM
    [InlineData(0x3F, false)]   // SABM with P/F
    [InlineData(0x6F, false)]   // SABME
    [InlineData(0x7F, false)]   // SABME with P/F
    [InlineData(0x43, false)]   // DISC
    [InlineData(0x53, false)]   // DISC with P/F
    [InlineData(0x87, false)]   // FRMR
    [InlineData(0x97, false)]   // FRMR with P/F
    [InlineData(0xAF, false)]   // XID
    [InlineData(0xBF, false)]   // XID with P/F
    [InlineData(0xE3, false)]   // TEST
    [InlineData(0xF3, false)]   // TEST with P/F
    public void Supervisory_Frames_And_UA_And_DM_Are_Expedited(int control, bool expected)
    {
        Ax25Header.IsExpedited(WithControl((byte)control, "G4ABC", "M0LTE")).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x0001, true)]    // RR at modulo 128: the first control byte ends 01
    [InlineData(0x0201, true)]
    [InlineData(0x0005, true)]    // RNR
    [InlineData(0x0009, true)]    // REJ
    [InlineData(0x000D, true)]    // SREJ
    [InlineData(0x0101, true)]    // RR with P/F (low bit of the second byte)
    [InlineData(0x0200, false)]   // I at modulo 128
    [InlineData(0x0000, false)]
    public void Modulo_128_Supervisory_Frames_Are_Expedited_By_Their_First_Control_Byte(int firstAndSecond, bool expected)
    {
        byte[] frame = Build([(byte)(firstAndSecond & 0xFF), (byte)(firstAndSecond >> 8)], "G4ABC", "M0LTE");

        Ax25Header.IsExpedited(frame).Should().Be(expected);
    }

    [Fact]
    public void The_Control_Byte_Is_Found_After_Digipeaters()
    {
        Ax25Header.IsExpedited(WithControl(0x63, "G4ABC", "M0LTE", "D1*", "D2*", "D3*")).Should().BeTrue();
        Ax25Header.IsExpedited(WithControl(0x00, "G4ABC", "M0LTE", "D1*", "D2*", "D3*")).Should().BeFalse();
    }

    [Fact]
    public void An_I_Frame_With_Payload_Looking_Like_A_Supervisory_Frame_Is_Not_Expedited()
    {
        // The payload byte 0x01 is not the control field.
        Ax25Header.IsExpedited(Build([0x00, 0xF0, 0x01, 0x63], "G4ABC", "M0LTE")).Should().BeFalse();
    }
}
