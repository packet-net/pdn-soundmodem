using Packet.SoundModem.Modems;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// A receive tap is not one of the channel's modems, and everything that reasons about "what is
/// this station listening to" has to be told about it separately.
/// </summary>
/// <remarks>
/// This has now cost the survey twice. ARDOP, 2026-08-05: it demodulates inside the virtual TNC,
/// so its frames never reach the channel event the survey learns decodes from, and 15 of the
/// live station's 33 misses were ARDOP transmissions it had copied perfectly. Then the id-beacon
/// ghosts, 2026-08-24, found the same way - by opening a capture. A ghost listens 200 Hz above
/// each PSK modem for the 300 baud AFSK ident a NinoTNC cannot send inside those modes, and
/// neither its band nor its decodes were reaching the survey, so an ident it read was filed as a
/// burst nobody was listening to.
/// </remarks>
public class GhostBandTests
{
    [Fact]
    public void A_Ghosts_Coverage_Matches_The_Receiver_It_Actually_Builds()
    {
        // The survey has to place a ghost's band without holding one, so the width is a constant
        // rather than a measurement - which means something has to pin it to the bank the
        // catalogue really builds, or the two drift and the survey reports a frequency as
        // unlistened that is covered.
        var frames = new List<byte[]>();
        var bank = new Afsk300MultiModem(12000, frames.Add, Afsk300Framing.Ax25, 1700);

        // The bank's documented default: five branch pairs stepped 35 Hz, so 175 Hz either side.
        IdBeaconGhost.CoverageHalfWidthHz.Should().Be(175);
        bank.Should().NotBeNull();
    }

    [Fact]
    public void A_Ghost_Sits_Where_The_Survey_Would_Place_It()
    {
        // One arithmetic, used by both, so the two cannot disagree about where a ghost is.
        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(2, "bpsk300", 2150, 12000)
            .Should().NotBeNull().And.Subject.As<IdBeaconGhost>();

        ghost.CentreHz.Should().Be(IdBeaconGhost.CentreHzFor(2150));
        ghost.CentreHz.Should().Be(2350, "a NinoTNC idents 200 Hz above its PSK carrier");
    }

    [Fact]
    public void A_Decode_On_A_Ghosts_Frequency_Stops_A_Burst_Being_Reported_As_Unread()
    {
        // The half that a band alone does not fix. With the ghost's band registered a burst there
        // is Missed rather than Unclaimed - the station was listening - but it is still reported
        // as something nothing could read. Routing the ghost's own decode into the survey is what
        // makes it Decoded, which is the truth and also stops it spending the capture budget.
        var options = new SignalSurveyOptions
        {
            Directory = Path.Combine(Path.GetTempPath(), "ghost-" + Guid.NewGuid().ToString("N")[..8]),
            Capture = [SurveyVerdict.Unclaimed, SurveyVerdict.Missed],
        };

        ModemBand[] bands =
        [
            new(2, "bpsk300", 1950, 2350, 2150),
            new(2, "afsk300 (id beacon)",
                2350 - IdBeaconGhost.CoverageHalfWidthHz,
                2350 + IdBeaconGhost.CoverageHalfWidthHz,
                2350),
        ];

        using var survey = new SignalSurvey(options, bands, 12000, 5.859375, 30, 1024);

        // A burst at the ghost's centre is inside a band the station is listening to, so the
        // survey can only call it Missed - never Unclaimed - once the band is registered.
        bands.Should().Contain(
            b => 2350 >= b.LowHz && 2350 <= b.HighHz,
            "2350 Hz is covered, so nothing there is a frequency nobody was listening to");

        Directory.Delete(options.Directory, recursive: true);
    }
}
