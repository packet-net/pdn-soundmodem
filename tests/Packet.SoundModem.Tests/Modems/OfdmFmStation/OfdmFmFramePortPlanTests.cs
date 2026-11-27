using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>Which link addresses and which urgency a frame is sent with. Pure: the frames are built by the packet.net library, the expected answers are written out from the rules.</summary>
public class OfdmFmFramePortPlanTests
{
    private static void Expect(byte[] frame, string? to, string from, SendPriority priority)
    {
        (LinkAddress To, LinkAddress From, SendPriority Priority)? plan = OfdmFmFramePort.Plan(frame);

        plan.Should().NotBeNull();
        if (to is null)
        {
            plan!.Value.To.IsEveryone.Should().BeTrue("the frame is for everyone");
        }
        else
        {
            plan!.Value.To.Should().Be(LinkAddress.FromAscii(to));
        }

        plan.Value.From.Should().Be(LinkAddress.FromAscii(from));
        plan.Value.Priority.Should().Be(priority);
    }

    [Fact]
    public void An_I_frame_goes_to_its_destination_as_its_source_and_is_ordinary()
    {
        Expect(FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0), "M0XYZ-1", "G4ABC-1", SendPriority.Ordinary);
    }

    [Fact]
    public void A_bare_callsign_is_written_without_an_SSID()
    {
        Expect(FramePortFrames.I("M0XYZ-0", "G4ABC", 0, 0), "M0XYZ", "G4ABC", SendPriority.Ordinary);
    }

    [Fact]
    public void Through_two_digipeaters_the_next_hop_and_the_transmitting_station_follow_who_has_repeated()
    {
        string[] via = ["DIGI1-2", "DIGI2"];
        byte[] none = FramePortFrames.I("M0XYZ-1", "G4ABC-1", 0, 0, via: via);

        Expect(none, "DIGI1-2", "G4ABC-1", SendPriority.Ordinary);
        Expect(FramePortFrames.Repeated(none, 1), "DIGI2", "DIGI1-2", SendPriority.Ordinary);
        Expect(FramePortFrames.Repeated(none, 2), "M0XYZ-1", "DIGI2", SendPriority.Ordinary);
    }

    [Fact]
    public void A_UI_beacon_with_no_digipeater_goes_to_everyone_whatever_its_destination()
    {
        Expect(FramePortFrames.Ui("BEACON", "G4ABC-1"), null, "G4ABC-1", SendPriority.Ordinary);
        Expect(FramePortFrames.Ui("M0XYZ-1", "G4ABC-1"), null, "G4ABC-1", SendPriority.Ordinary);
    }

    [Fact]
    public void A_UI_frame_whose_digipeaters_have_all_repeated_goes_to_everyone_as_the_last_of_them()
    {
        byte[] ui = FramePortFrames.Ui("BEACON", "G4ABC-1", via: ["DIGI1", "DIGI2"]);

        Expect(FramePortFrames.Repeated(ui, 2), null, "DIGI2", SendPriority.Ordinary);
    }

    [Fact]
    public void A_UI_frame_with_a_digipeater_pending_goes_to_that_digipeater()
    {
        byte[] ui = FramePortFrames.Ui("BEACON", "G4ABC-1", via: ["DIGI1", "DIGI2"]);

        Expect(ui, "DIGI1", "G4ABC-1", SendPriority.Ordinary);
        Expect(FramePortFrames.Repeated(ui, 1), "DIGI2", "DIGI1", SendPriority.Ordinary);
    }

    public static TheoryData<string, byte[], SendPriority> Urgency => new()
    {
        { "RR", FramePortFrames.Rr("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "RNR", FramePortFrames.Rnr("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "REJ", FramePortFrames.Rej("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "SREJ", FramePortFrames.Srej("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "UA", FramePortFrames.Ua("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "DM", FramePortFrames.Dm("M0XYZ-1", "G4ABC-1"), SendPriority.Expedited },
        { "SABM", FramePortFrames.Sabm("M0XYZ-1", "G4ABC-1"), SendPriority.Ordinary },
        { "DISC", FramePortFrames.Disc("M0XYZ-1", "G4ABC-1"), SendPriority.Ordinary },
        { "I", FramePortFrames.I("M0XYZ-1", "G4ABC-1", 1, 2), SendPriority.Ordinary },
        { "UI", FramePortFrames.Ui("M0XYZ-1", "G4ABC-1"), SendPriority.Ordinary },
    };

    [Theory]
    [MemberData(nameof(Urgency))]
    public void Acknowledgements_and_responses_are_expedited_and_the_rest_are_ordinary(string name, byte[] frame, SendPriority priority)
    {
        OfdmFmFramePort.Plan(frame)!.Value.Priority.Should().Be(priority, name);
    }

    [Fact]
    public void A_frame_that_does_not_read_has_no_plan()
    {
        OfdmFmFramePort.Plan([1, 2, 3]).Should().BeNull();
        OfdmFmFramePort.Plan([]).Should().BeNull();
    }
}
