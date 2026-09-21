using Packet.SoundModem.CarrierSense;
namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// Carrier sense on an FM path, where a signal makes the receiver quieter rather than louder.
/// </summary>
/// <remarks>
/// The levels here are not invented. They are what a raw capture from radio1 measured on
/// 2026-09-18 while radio2 keyed up: -15.9 dBFS of open-squelch noise, -25 dBFS while the far end
/// modulated, -55 dBFS through its silent lead-in, and below -80 dBFS where receive was gated
/// during this station's own transmission. The whole point of the class is the third of those,
/// because a lead-in is what a sync correlator cannot see and what two stations collide inside.
/// </remarks>
public class FmQuietingBusyDetectorTests
{
    private const int Rate = 48000;
    private const double IdleDbfs = -15.9;
    private const double ModulatedDbfs = -25.0;
    private const double UnmodulatedCarrierDbfs = -55.0;
    private const double GatedDbfs = -86.0;

    /// <summary>Feeds noise at a level, and returns the detector for inspection.</summary>
    private static void Feed(FmQuietingBusyDetector detector, double dbfs, double seconds, int seed)
    {
        var random = new Random(seed);
        double amplitude = Math.Pow(10, dbfs / 20.0);
        int samples = (int)(Rate * seconds);
        for (int i = 0; i < samples; i++)
        {
            // Uniform noise scaled to the requested rms. Uniform on [-a, a] has rms a/sqrt(3).
            double value = ((random.NextDouble() * 2) - 1) * amplitude * Math.Sqrt(3);
            detector.Process((float)value);
        }
    }

    [Fact]
    public void Open_Squelch_Noise_On_Its_Own_Is_Not_Busy()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 1);

        detector.Busy.Should().BeFalse("band noise is the idle state, not a signal");
        detector.Engaged.Should().BeTrue("a floor at about -16 dBFS is open-squelch noise");
        detector.FloorDbfs.Should().BeApproximately(IdleDbfs, 1.5);
    }

    [Fact]
    public void An_Unmodulated_Carrier_Is_Busy_Although_It_Carries_Nothing_To_Correlate()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 2);
        detector.Busy.Should().BeFalse();

        // The case this class exists for: TXDELAY, where the channel is held and a sync-based
        // detect has nothing to find.
        Feed(detector, UnmodulatedCarrierDbfs, 0.05, seed: 3);

        detector.Busy.Should().BeTrue("a carrier 39 dB below the noise it replaced is the clearest "
            + "signal on this path, and it starts with the carrier rather than the modulation");
    }

    [Fact]
    public void A_Modulating_Far_End_Is_Busy()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 4);
        Feed(detector, ModulatedDbfs, 0.05, seed: 5);

        detector.Busy.Should().BeTrue("9 dB of quieting is shallower than a bare carrier and still "
            + "well clear of the 6 dB threshold");
    }

    [Fact]
    public void Busy_Is_Asserted_Well_Inside_A_Lead_In()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 6);

        // 40 ms is the shortest TXDELAY the modem can express, so a detector that needed longer
        // than this would leave the collision window open however good it was otherwise.
        Feed(detector, UnmodulatedCarrierDbfs, 0.040, seed: 7);

        detector.Busy.Should().BeTrue("a detector that needed longer than the shortest lead-in "
            + "would leave the window it exists to close");
    }

    [Fact]
    public void Busy_Releases_Once_The_Noise_Comes_Back()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 8);
        Feed(detector, ModulatedDbfs, 0.5, seed: 9);
        detector.Busy.Should().BeTrue();

        Feed(detector, IdleDbfs, 0.3, seed: 10);

        detector.Busy.Should().BeFalse("the channel is free the moment the far end stops");
    }

    [Fact]
    public void Busy_Holds_Through_A_Long_Transmission()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 11);

        // A 1900-byte frame at the slowest rate measured on this bench is about four seconds.
        // A floor that sagged during it would walk down to meet the signal and release part way
        // through, handing the channel to a station that would then transmit over the top.
        Feed(detector, ModulatedDbfs, 8.0, seed: 12);

        detector.Busy.Should().BeTrue("the floor is frozen while busy, so a long burst cannot "
            + "release it by outlasting the estimator");
    }

    [Fact]
    public void Gated_Audio_During_Our_Own_Transmission_Is_Not_A_Carrier()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 13);
        Feed(detector, GatedDbfs, 0.5, seed: 14);

        detector.Busy.Should().BeFalse("digital silence is a dead or gated input, and calling it "
            + "busy would wedge a transmitter behind its own muting");
    }

    [Fact]
    public void A_Squelched_Receiver_Leaves_The_Detector_Stood_Down()
    {
        var detector = new FmQuietingBusyDetector(Rate);

        // No open-squelch noise to measure against: everything is quiet, so "below the floor"
        // means nothing and the sync detect is the only honest answer.
        Feed(detector, -60.0, 3.0, seed: 15);

        detector.Engaged.Should().BeFalse("a floor that quiet is not open-squelch noise");
        detector.Busy.Should().BeFalse();
    }

    [Fact]
    public void Nothing_Is_Asserted_Before_The_Floor_Is_Established()
    {
        var detector = new FmQuietingBusyDetector(Rate);

        // Audio that starts quiet, before any idle noise has been seen to set a floor against.
        Feed(detector, UnmodulatedCarrierDbfs, 0.5, seed: 16);

        detector.Busy.Should().BeFalse("with no floor yet there is nothing to be below, and a "
            + "station that came up asserting busy would never take the channel");
    }

    [Fact]
    public void A_Noise_Floor_That_Falls_Does_Not_Wedge_The_Detector_Busy_Forever()
    {
        var detector = new FmQuietingBusyDetector(Rate);
        Feed(detector, IdleDbfs, 3.0, seed: 19);
        detector.Busy.Should().BeFalse();

        // The receiver's own noise gets quieter and stays quieter: a squelch closing, an operator
        // turning the volume down, an AGC settling, a band that goes quiet overnight. Nothing here
        // is a carrier, but everything here is "below the floor", and the floor is frozen while
        // busy - so without a backstop the station decides the channel is occupied and never
        // transmits again.
        Feed(detector, IdleDbfs - 14, 60.0, seed: 20);

        detector.Busy.Should().BeFalse("a station that stops transmitting until somebody notices "
            + "is a worse outcome than one that never detects anything");
    }

    [Fact]
    public void A_Station_That_Starts_Up_Mid_Transmission_Recovers_Once_The_Channel_Clears()
    {
        var detector = new FmQuietingBusyDetector(Rate);

        // Worst realistic start-up: the daemon comes up while the far end is already talking.
        Feed(detector, ModulatedDbfs, 2.0, seed: 17);
        Feed(detector, IdleDbfs, 3.0, seed: 18);

        detector.Engaged.Should().BeTrue();
        detector.Busy.Should().BeFalse();
        detector.FloorDbfs.Should().BeApproximately(IdleDbfs, 1.5,
            "the floor follows the loud state quickly, so a bad start corrects itself");
    }
}
