using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// What a reading of a frame established, and therefore what a row may say about it.
/// </summary>
/// <remarks>
/// <para>
/// The table below is the measurement that produced the rule, taken off GB7RDG's own frame log
/// over eight days of its 40 m slot (docs/dev/false-decodes.md). The discriminator is payload
/// corroboration: that band repeats the same frames all day, so a payload seen exactly once in
/// 120,870 rows is very likely a payload nobody sent.
/// </para>
/// <para>
/// CRC ok 3.5% seen once, CRC ok and chased 1.5%, Reed-Solomon with a corroborating trailer 8.0%,
/// the same chased 16.0%, Reed-Solomon alone 10.4%, and <b>Reed-Solomon alone and chased 75.6%</b>.
/// So chase decoding is not the fault - behind a CRC it is the best-behaved class there is, better
/// than the no-chase baseline - and the class that is fabricating stations is chase with nothing
/// checking it.
/// </para>
/// </remarks>
public class DecodeStandingTests
{
    private static FrameQuality Reading(
        bool plainIl2p, int? trailerNearBits = null, int? chasedBits = null, bool? crcValid = null) =>
        new("bpsk300-il2pc", 32, CorrectedBytes: 1, crcValid,
            PlainIl2p: plainIl2p, TrailerNearBits: trailerNearBits, ChasedBits: chasedBits);

    [Fact]
    public void A_Verified_Frame_Says_Everything_It_Always_Said()
    {
        FrameQuality verified = Reading(plainIl2p: false, crcValid: true);

        verified.SnrWorthShowing.Should().BeTrue();
        verified.CallsignWorthShowing.Should().BeTrue();
    }

    [Fact]
    public void So_Does_A_Verified_Frame_The_Chase_Rescued()
    {
        // 1.5% seen once, against a 3.5% baseline: measured as better than no chase at all.
        // Capping corrected bytes or turning the chase off would have cost this class for
        // nothing.
        FrameQuality rescued = Reading(plainIl2p: false, crcValid: true, chasedBits: 7);

        rescued.SnrWorthShowing.Should().BeTrue();
        rescued.CallsignWorthShowing.Should().BeTrue();
    }

    [Fact]
    public void So_Does_A_Frame_Whose_Framing_Carries_No_Check_At_All()
    {
        // HDLC and FX.25: crc null because there was no CRC, not because none was checked. The
        // FCS passed, which is the whole guarantee that framing has.
        FrameQuality hdlc = Reading(plainIl2p: false);

        hdlc.SnrWorthShowing.Should().BeTrue();
        hdlc.CallsignWorthShowing.Should().BeTrue();
    }

    [Fact]
    public void A_Trailer_That_Corroborated_The_Frame_Counts_As_A_Check()
    {
        // Evidence of the same order as a passing CRC (Il2pReceiver.CorroborationMaxBits), and
        // the measurement agrees: 1.4% of GB7BPQ's corroborated rows were seen only once,
        // against 1.8% of its CRC-verified ones.
        FrameQuality corroborated = Reading(plainIl2p: true, trailerNearBits: 2, chasedBits: 4);

        corroborated.SnrWorthShowing.Should().BeTrue();
        corroborated.CallsignWorthShowing.Should().BeTrue();
    }

    [Fact]
    public void Reed_Solomon_Alone_Keeps_Its_Callsign_And_Loses_Its_Band_Snr()
    {
        // Nothing checked this reading, so the band figure beside it is not evidence that a
        // signal was there - but the bits were not moved to reach it either, and 90% of this
        // class on the live slot were payloads heard again. The name stands; the number does not.
        FrameQuality plain = Reading(plainIl2p: true);

        plain.SnrWorthShowing.Should().BeFalse();
        plain.CallsignWorthShowing.Should().BeTrue();
    }

    [Fact]
    public void Reed_Solomon_Alone_After_A_Chase_Names_Nobody()
    {
        // The measured class: three quarters of it never happened.
        FrameQuality fabricated = Reading(plainIl2p: true, chasedBits: 6);

        fabricated.SnrWorthShowing.Should().BeFalse();
        fabricated.CallsignWorthShowing.Should().BeFalse();
    }

    [Fact]
    public void The_Verdicts_Do_Not_Depend_On_What_The_Operator_Did_With_The_Frame()
    {
        // MonitorOnly is the operator's routing choice - whether an RS-only frame is handed to
        // the host - and it is identical across every branch of one bank. What a reading
        // established is a fact about the decode, so the same bits get the same verdict on a
        // -nocrc port as on an -il2pc one.
        FrameQuality withheld = Reading(plainIl2p: true, chasedBits: 6) with { MonitorOnly = true };
        FrameQuality delivered = Reading(plainIl2p: true, chasedBits: 6) with { MonitorOnly = false };

        withheld.CallsignWorthShowing.Should().Be(delivered.CallsignWorthShowing);
        withheld.SnrWorthShowing.Should().Be(delivered.SnrWorthShowing);
    }
}
