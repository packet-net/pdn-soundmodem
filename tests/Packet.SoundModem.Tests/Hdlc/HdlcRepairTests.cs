using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Tests.Hdlc;

/// <summary>
/// The chase-repair engine of <see cref="HdlcDeframer"/> (see <see cref="HdlcRepairPolicy"/>):
/// confidence-ordered bit edits that recover frames whose FCS failed or whose bit alignment
/// slipped, and the gates that keep a chance FCS pass from being delivered as a frame.
/// </summary>
public class HdlcRepairTests
{
    /// <summary>Four opening flags, so the framed stream's body starts here.</summary>
    private const int BodyOffset = 4 * 8;

    /// <summary>
    /// Framed-stream positions of two source-address bits that are safe to damage: both sit
    /// in the header (no stuffing has been inserted before them, so framed and assembled bit
    /// indices differ by exactly <see cref="BodyOffset"/>), both flip 0 to 1, and neither
    /// creates a run of five ones - so each damage is a pure single-bit content error the
    /// repair engine sees as one weak assembled bit.
    /// </summary>
    private const int DamageA = BodyOffset + 84;  // source byte 3, bit 4
    private const int DamageB = BodyOffset + 82;  // source byte 3, bit 2

    /// <summary>A frame the repaired-delivery gates accept: real callsigns, UI control,
    /// PID 0xF0, and a comment-format payload the APRS skeleton checks pass through.</summary>
    private static byte[] RepairableFrame()
    {
        byte[] frame =
        [
            0x88, 0xA0, 0x9E, 0x8A, 0x40, 0x40, 0xE0,   // "DPOE" + two spaces, not last address
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0xEF,   // source with SSID 7, last address
            0x03, 0xF0,                                  // UI control, no layer 3
        ];
        return [.. frame, .. ">repair me,0123456789 abcdefghij"u8];
    }

    /// <summary>Feeds bits with per-bit soft magnitudes, collecting clean and repaired
    /// deliveries separately.</summary>
    private static (List<byte[]> Clean, List<(byte[] Frame, int Edited)> Repaired) Run(
        IEnumerable<(int Bit, float Soft)> stream, HdlcRepairPolicy? policy = null)
    {
        var clean = new List<byte[]>();
        var repaired = new List<(byte[], int)>();
        var deframer = new HdlcDeframer(clean.Add, repair: policy);
        deframer.FrameRepaired = (frame, edited) => repaired.Add((frame, edited));
        foreach ((int bit, float soft) in stream)
        {
            deframer.PushBit(bit, soft);
        }

        return (clean, repaired);
    }

    /// <summary>The frame's bits with uniform softs, then the listed framed-stream positions
    /// flipped and weakened: the shape the demodulator hands up when the eye collapsed at
    /// exactly those bits.</summary>
    private static List<(int Bit, float Soft)> DamagedStream(byte[] frame, params int[] damageAt)
    {
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 4, closingFlags: 2);
        var stream = bits.Select(b => ((int)b, 1.0f)).ToList();
        foreach (int index in damageAt)
        {
            stream[index] = (stream[index].Item1 ^ 1, 0.01f);
        }

        return stream;
    }

    [Fact]
    public void A_Single_Weak_Bit_Error_Is_Repaired_And_Reported_As_One_Edited_Bit()
    {
        byte[] frame = RepairableFrame();

        var (clean, repaired) = Run(DamagedStream(frame, DamageA), HdlcRepairPolicy.Corpus);

        clean.Should().BeEmpty();
        repaired.Should().ContainSingle();
        repaired[0].Frame.Should().Equal(frame);
        repaired[0].Edited.Should().Be(1);
    }

    [Fact]
    public void Two_Weak_Bit_Errors_Are_Repaired_By_The_Pair_Search()
    {
        byte[] frame = RepairableFrame();

        var (clean, repaired) = Run(DamagedStream(frame, DamageA, DamageB), HdlcRepairPolicy.Corpus);

        clean.Should().BeEmpty();
        repaired.Should().ContainSingle();
        repaired[0].Frame.Should().Equal(frame);
        repaired[0].Edited.Should().Be(2);
    }

    [Fact]
    public void A_Weak_Bit_Error_In_The_Address_Field_Is_Repaired()
    {
        // Both damage positions are header bits: the loose entry gate must let a frame whose
        // ADDRESS carries the error through to repair, and the strict exit gate must then
        // pass the restored original.
        byte[] frame = RepairableFrame();

        var (_, repaired) = Run(DamagedStream(frame, DamageB), HdlcRepairPolicy.Corpus);

        repaired.Should().ContainSingle();
        repaired[0].Frame.Should().Equal(frame);
    }

    [Fact]
    public void Without_A_Policy_A_Damaged_Frame_Is_Dropped_As_Before()
    {
        byte[] frame = RepairableFrame();

        var (clean, repaired) = Run(DamagedStream(frame, DamageA), policy: null);

        clean.Should().BeEmpty();
        repaired.Should().BeEmpty();
    }

    [Fact]
    public void A_Lost_Bit_Is_Reinserted_And_The_Frame_Realigned()
    {
        byte[] frame = RepairableFrame();
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 4, closingFlags: 2);

        // Drop one header bit: the closing flag finds six residual bits instead of seven,
        // and the softs around the gap collapse - which is what the realignment search
        // orders its insertions by.
        int dropAt = DamageA;
        var stream = new List<(int Bit, float Soft)>();
        for (int i = 0; i < bits.Length; i++)
        {
            if (i == dropAt)
            {
                continue;
            }

            stream.Add((bits[i], Math.Abs(i - dropAt) <= 2 ? 0.01f : 1.0f));
        }

        var (clean, repaired) = Run(stream, HdlcRepairPolicy.Corpus);

        clean.Should().BeEmpty();
        repaired.Should().ContainSingle();
        repaired[0].Frame.Should().Equal(frame);
        repaired[0].Edited.Should().Be(1);
    }

    [Fact]
    public void A_Gained_Bit_Is_Deleted_And_The_Frame_Realigned()
    {
        byte[] frame = RepairableFrame();
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 4, closingFlags: 2);

        // Insert one extra bit mid-header: zero residual bits at the closing flag, the
        // gained-bit signature. A 1 in a neighbourhood of short runs changes no stuffing.
        int insertAt = DamageA;
        var stream = new List<(int Bit, float Soft)>();
        for (int i = 0; i < bits.Length; i++)
        {
            if (i == insertAt)
            {
                stream.Add((1, 0.01f));
                stream.Add((bits[i], 0.01f));
            }
            else
            {
                stream.Add((bits[i], Math.Abs(i - insertAt) <= 2 ? 0.01f : 1.0f));
            }
        }

        var (clean, repaired) = Run(stream, HdlcRepairPolicy.Corpus);

        clean.Should().BeEmpty();
        repaired.Should().ContainSingle();
        repaired[0].Frame.Should().Equal(frame);
        repaired[0].Edited.Should().Be(1);
    }

    [Fact]
    public void The_Gates_Refuse_A_Repair_With_A_Corrupted_Position()
    {
        // A corrupted APRS position must not be deliverable as a repair whatever its FCS
        // says: this is the exact shape of the false passes measured on the WA8LMF corpus
        // before the gates existed.
        RepairedFrameGates.PayloadConsistent(MinimalUiFrame("!3349.03N/11802.82W_"u8)).Should().BeTrue();
        RepairedFrameGates.PayloadConsistent(MinimalUiFrame("!334X.03N/11802.82W_"u8)).Should().BeFalse();
        RepairedFrameGates.PayloadConsistent(MinimalUiFrame("!3349.03N/11802.8W#_"u8)).Should().BeFalse();
        RepairedFrameGates.PayloadConsistent(MinimalUiFrame("_11221922c051s000g005t052"u8)).Should().BeTrue();
        RepairedFrameGates.PayloadConsistent(MinimalUiFrame("_112219PYe146s000"u8)).Should().BeFalse();
    }

    [Fact]
    public void The_Gates_Refuse_NMEA_With_A_Bad_Checksum_And_Accept_A_Good_One()
    {
        RepairedFrameGates.PayloadConsistent(
            MinimalUiFrame("$GPRMC,014902,A,3347.6444,N,11805.4981,W,000.0,108.2,231105,013.4,E*6B"u8))
            .Should().BeTrue();
        RepairedFrameGates.PayloadConsistent(
            MinimalUiFrame("$GPRMC,014902,A,3347.6444,N,11805.4981,W,000.0,108.2,231105,013.4,E*6C"u8))
            .Should().BeFalse();
        RepairedFrameGates.PayloadConsistent(
            MinimalUiFrame("$GPRMC,014902,A,3347.6444,N,11805.4981,W,000.0,108.2,231105,013.4,E"u8))
            .Should().BeFalse();
    }

    [Fact]
    public void The_Gates_Refuse_A_Non_UI_Control_On_A_Repair()
    {
        // A search must never fabricate a link-state frame: only UI carries the monitor-grade
        // payloads a repaired delivery is for.
        RepairedFrameGates.Ax25Structure(MinimalFrame(0x2F, 0xF0, [])).Should().BeFalse();  // SABM
        RepairedFrameGates.Ax25Structure(MinimalFrame(0x63, 0xF0, [])).Should().BeFalse();  // UA
        RepairedFrameGates.Ax25Structure(MinimalFrame(0x03, 0xF0, [])).Should().BeTrue();   // UI
        RepairedFrameGates.Ax25Structure(MinimalFrame(0xEF, 0xF0, [])).Should().BeTrue();   // UI, poll/final
        RepairedFrameGates.Ax25Structure(MinimalFrame(0x03, 0x42, [])).Should().BeFalse();  // unknown PID
    }

    [Fact]
    public void The_Gates_Check_The_Mic_E_Destination_Digit_Encoding()
    {
        // A Mic-E payload's latitude lives in the destination field's characters, each in the
        // spec's 0-9 / A-L / P-Z ranges.
        RepairedFrameGates.PayloadConsistent(
            MinimalFrame(0x03, 0xF0, "`-(.l .K\\]\"8G}"u8.ToArray(), "S4PWPW"u8.ToArray())).Should().BeTrue();
        RepairedFrameGates.PayloadConsistent(
            MinimalFrame(0x03, 0xF0, "`-(.l .K\\]\"8G}"u8.ToArray(), "S4PW~W"u8.ToArray())).Should().BeFalse();
    }

    [Fact]
    public void A_Clean_Frame_Is_Never_Gated_However_Strange_Its_Bytes()
    {
        // The gates belong to the search, not to the wire: a frame whose FCS verifies as
        // received is delivered untouched, strange control byte and all - that is a real
        // transmission of something that is not AX.25-shaped, and the receiver's job is to
        // report it, not to judge it.
        byte[] odd = MinimalFrame(0x81, 0x00, [0xFF, 0x00, 0xA5]);

        var (clean, repaired) = Run(
            HdlcFramer.FrameBits(odd, openingFlags: 3, closingFlags: 2).Select(b => ((int)b, 1.0f)),
            HdlcRepairPolicy.Corpus);

        clean.Should().ContainSingle().Which.Should().Equal(odd);
        repaired.Should().BeEmpty();
    }

    private static byte[] MinimalUiFrame(ReadOnlySpan<byte> info) => MinimalFrame(0x03, 0xF0, info);

    /// <summary>Destination "DPOE" (any valid characters), source "KK4HEJ" with the address
    /// terminator, then the given control, PID and info.</summary>
    private static byte[] MinimalFrame(
        byte control, byte pid, ReadOnlySpan<byte> info, ReadOnlySpan<byte> destination = default)
    {
        byte[] frame = new byte[16 + info.Length];
        byte[] dest = destination.IsEmpty
            ? [0x88, 0xA0, 0x9E, 0x8A, 0x40, 0x40, 0xE0]
            : [.. destination[..6].ToArray().Select(c => (byte)(c << 1)), 0xE0];
        dest.CopyTo(frame, 0);
        byte[] src = [0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0xE1];
        src.CopyTo(frame, 7);
        frame[14] = control;
        frame[15] = pid;
        info.CopyTo(frame.AsSpan(16));
        return frame;
    }
}
