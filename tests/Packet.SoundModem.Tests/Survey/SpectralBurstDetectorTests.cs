using Packet.SoundModem.Dsp;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// Finding transmissions nobody was configured to decode, from the spectrum lines the display
/// already computes. These tests build lines by hand rather than from audio, so what is under
/// test is the burst logic and not an FFT.
/// </summary>
public class SpectralBurstDetectorTests
{
    private const double BinWidthHz = 5.859375;   // 12 kHz / 2048
    private const int LinesPerSecond = 30;
    private const int LineLength = 1024;

    /// <summary>A line of flat noise, with an optional signal painted over part of it.</summary>
    private static byte[] Line(double floorDb = -70, double? lowHz = null, double? highHz = null, double signalDb = -50)
    {
        var line = new byte[LineLength];
        byte ToByte(double db) => (byte)Math.Clamp(
            (db - WaterfallSource.FloorDb) * (255 / -WaterfallSource.FloorDb), 0, 255);

        line.AsSpan().Fill(ToByte(floorDb));
        if (lowHz is double low && highHz is double high)
        {
            int lowBin = (int)(low / BinWidthHz);
            int highBin = (int)(high / BinWidthHz);
            line.AsSpan(lowBin, highBin - lowBin).Fill(ToByte(signalDb));
        }

        return line;
    }

    private static (List<SurveyBurst> Bursts, SpectralBurstDetector Detector) Detector(
        double minWidthHz = 150, double maxSeconds = 30)
    {
        var bursts = new List<SurveyBurst>();
        var detector = new SpectralBurstDetector(
            BinWidthHz, LinesPerSecond, LineLength, bursts.Add,
            minWidthHz: minWidthHz, maxSeconds: maxSeconds);
        return (bursts, detector);
    }

    /// <summary>Feeds enough quiet lines that a noise floor has been seeded. Three seconds
    /// rather than the two the seed takes, so a test is not sitting on the boundary.</summary>
    private static long Warm(SpectralBurstDetector detector, long from = 0, int lines = 90)
    {
        byte[] quiet = Line();
        for (int i = 0; i < lines; i++)
        {
            detector.AddLine(from + i, quiet);
        }

        return from + lines;
    }

    [Fact]
    public void Nothing_Is_Reported_Until_A_Noise_Floor_Has_Been_Banked()
    {
        // A burst measured against a floor we do not have is not a measurement. The honest
        // answer during warm-up is silence.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();

        detector.Ready.Should().BeFalse();
        byte[] loud = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 10; i++)
        {
            detector.AddLine(i, loud);
        }

        bursts.Should().BeEmpty();
    }

    [Fact]
    public void A_Burst_Is_Reported_Once_It_Ends_With_Where_It_Sat_And_How_Long()
    {
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        // 400 Hz wide at 1500 Hz centre for 2 s - a 300 baud packet's shape.
        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 60; i++)
        {
            detector.AddLine(line++, signal);
        }

        bursts.Should().BeEmpty("a burst still in progress has no measured extent yet");

        byte[] quiet = Line();
        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        SurveyBurst burst = bursts.Should().ContainSingle().Subject;
        burst.CentreHz.Should().BeApproximately(1500, 20);
        burst.WidthHz.Should().BeApproximately(400, 30);
        burst.Lines.Should().BeCloseTo(60, 3);
        burst.PeakSnrDb.Should().BeGreaterThan(15, "20 dB over the floor across the run");
        burst.EndedOnTimeout.Should().BeFalse();
    }

    [Fact]
    public void A_Narrow_Carrier_Is_Not_A_Burst()
    {
        // One hot bin is a tuning whistle or a het, not a modulated signal - and capturing every
        // carrier on 40 m would bury everything that matters.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        byte[] carrier = Line(lowHz: 1500, highHz: 1510);
        for (int i = 0; i < 60; i++)
        {
            detector.AddLine(line++, carrier);
        }

        byte[] quiet = Line();
        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().BeEmpty();
    }

    [Fact]
    public void Something_That_Never_Stops_Is_Reported_As_A_Timeout_Rather_Than_Held_Forever()
    {
        // A steady carrier would otherwise hold a burst open for the life of the process and
        // never be reported at all - so it is closed, and flagged so triage can drop it instead
        // of reading it as a packet nobody could classify.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector(maxSeconds: 2);
        long line = Warm(detector);

        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 120; i++)
        {
            detector.AddLine(line++, signal);
        }

        bursts.Should().NotBeEmpty();
        bursts[0].EndedOnTimeout.Should().BeTrue();
    }

    [Fact]
    public void A_Brief_Fade_Does_Not_Split_One_Transmission_Into_Three()
    {
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        byte[] quiet = Line();
        for (int block = 0; block < 3; block++)
        {
            for (int i = 0; i < 20; i++)
            {
                detector.AddLine(line++, signal);
            }

            // Two lines under threshold - 67 ms, well inside the grace window.
            detector.AddLine(line++, quiet);
            detector.AddLine(line++, quiet);
        }

        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().ContainSingle("a fade is not the end of a transmission");
    }

    [Fact]
    public void Two_Signals_In_Different_Parts_Of_The_Band_Are_Two_Bursts()
    {
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        var both = new byte[LineLength];
        Line(lowHz: 700, highHz: 1100).CopyTo(both, 0);
        int lowBin = (int)(2100 / BinWidthHz);
        int highBin = (int)(2500 / BinWidthHz);
        Line(lowHz: 2100, highHz: 2500).AsSpan(lowBin, highBin - lowBin).CopyTo(both.AsSpan(lowBin));

        for (int i = 0; i < 40; i++)
        {
            detector.AddLine(line++, both);
        }

        byte[] quiet = Line();
        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().HaveCount(2);
        bursts.Select(b => b.CentreHz).Order().Should().SatisfyRespectively(
            first => first.Should().BeApproximately(900, 30),
            second => second.Should().BeApproximately(2300, 30));
    }

    [Fact]
    public void A_Signal_Outlasting_The_Floors_Memory_Does_Not_Raise_Its_Own_Floor()
    {
        // The floor is the minimum over the last ~15 s. A signal that runs longer than that fills
        // the window with its own energy, and a naive floor then rises to meet it: the burst stops
        // looking like a burst and gets chopped in two. Measured before the fix, a 25 s over came
        // out as a pair of ~13 s "bursts" - each one short enough to pass a duration gate and be
        // captured as a packet. Bins carrying signal are excluded from the floor, because a floor
        // is a measurement of noise and a bin carrying a transmission is not measuring any.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector(maxSeconds: 120);
        long line = Warm(detector);

        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 25 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, signal);
        }

        byte[] quiet = Line();
        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        SurveyBurst burst = bursts.Should().ContainSingle("one transmission is one burst").Subject;
        burst.Lines.Should().BeCloseTo(25 * LinesPerSecond, 5, "its full length, not the first half");
    }

    [Fact]
    public void A_Weak_Signal_Is_Not_Lost_Halfway_Through_By_Its_Own_Floor_Climbing()
    {
        // The test above holds at 20 dB, which is enough margin to hide the defect: the floor
        // used to climb under a transmission at a rate that is a fraction of the distance to
        // the signal's own power, so it ate about 13 dB of a 25-second over and the burst
        // survived on what was left. A signal 8 dB over the noise has 2 dB to give, and 8 dB
        // over the noise is an ordinary capture rather than a corner case - it is the level
        // the survey's own MinPeakSnrDb is set just under.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector(maxSeconds: 60);
        long line = Warm(detector);

        byte[] weak = Line(lowHz: 1300, highHz: 1700, signalDb: -62);
        for (int i = 0; i < 20 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, weak);
        }

        byte[] quiet = Line();
        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        SurveyBurst burst = bursts.Should().ContainSingle(
            "the floor beneath a transmission is held still while it runs").Subject;
        burst.Lines.Should().BeCloseTo(20 * LinesPerSecond, 5);
        burst.PeakSnrDb.Should().BeApproximately(
            8, 1.5, "measured against the noise, which is what it stood over throughout");
    }

    [Fact]
    public void A_Band_Hot_For_Longer_Than_Any_Transmission_Gets_Its_Floor_Back()
    {
        // The other side of holding the floor still under a burst. A floor that is too low
        // makes ordinary noise read as signal, which opens a burst, which holds the floor -
        // so the thing that would put it right is the thing being suppressed. What breaks it
        // is that a burst is closed on the timeout whatever it is doing, and a burst closed
        // that way is not a transmission: the floor beneath it takes what the block actually
        // measured, and the band comes back in one block rather than never.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector(maxSeconds: 2);
        long line = Warm(detector);

        byte[] stuck = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 30 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, stuck);
        }

        bursts.Should().ContainSingle(
            "one timeout, and then the floor is where the band actually is");
        bursts[0].EndedOnTimeout.Should().BeTrue();
    }

    [Fact]
    public void A_Break_In_The_Line_Clock_Abandons_What_Was_In_Flight()
    {
        // The line clock stops while the station transmits. A burst stretched across that gap
        // would report a duration made of two different transmissions and our own keyup.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        for (int i = 0; i < 40; i++)
        {
            detector.AddLine(line++, signal);
        }

        line += 500;   // we transmitted
        byte[] quiet = Line();
        for (int i = 0; i < 40; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().BeEmpty("nothing that straddles the gap is a measurement of anything");
    }

    [Fact]
    public void A_Signal_Only_Just_Over_The_Noise_Is_Still_Found()
    {
        // The other half of the bargain. Tracking the floor up to the noise rather than down to
        // its minimum raises the bar every signal has to clear, and a survey that answers the
        // false positives by going deaf has not been fixed. Eight dB over the noise is two dB
        // over the threshold and near the weakest thing worth calling a burst; it has to survive
        // both the tracker and the five hundred lines of noise that preceded it.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector, lines: 500);

        byte[] weak = Line(lowHz: 1300, highHz: 1700, signalDb: -62);
        for (int i = 0; i < 2 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, weak);
        }

        byte[] quiet = Line();
        for (int i = 0; i < LinesPerSecond; i++)
        {
            detector.AddLine(line++, quiet);
        }

        SurveyBurst burst = bursts.Should().ContainSingle().Subject;
        burst.CentreHz.Should().BeApproximately(1500, 25);
        burst.PeakSnrDb.Should().BeApproximately(8, 2, "the floor is the noise, so the SNR is the truth");
    }

    [Fact]
    public void Five_Minutes_Of_Nothing_But_Noise_Reports_Nothing()
    {
        // The station-side symptom this whole family of tests exists for is a survey directory
        // filling up with captures of noise, so the flat-line case deserves a test with noise
        // that behaves like noise rather than a constant. Power in one bin of one line is
        // exponentially distributed (Rayleigh magnitude), which is a spread of about 5.6 dB -
        // single bins go 6 dB over the mean about one line in fifty, and the width and duration
        // gates are what have to turn that into silence.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        var random = new Random(20260805);

        byte ToByte(double db) => (byte)Math.Clamp(
            (db - WaterfallSource.FloorDb) * (255 / -WaterfallSource.FloorDb), 0, 255);

        var line = new byte[LineLength];
        for (int i = 0; i < 300 * LinesPerSecond; i++)
        {
            for (int bin = 0; bin < LineLength; bin++)
            {
                // -70 dB mean, exponentially distributed about it.
                line[bin] = ToByte(-70 + (10 * Math.Log10(-Math.Log(1 - random.NextDouble()))));
            }

            detector.AddLine(i, line);
        }

        bursts.Should().BeEmpty("noise is not a transmission, for as long as you care to watch it");
    }

    [Fact]
    public void A_Fade_Does_Not_Latch_The_Floor_Below_The_Noise_For_Good()
    {
        // The floor is a rolling minimum, and bins standing over it are held out of the average
        // that feeds it. Those two together latch. Once a dip deeper than the 6 dB threshold
        // enters the ring the floor follows it down; ordinary noise then stands over the lowered
        // floor, so every line is "hot"; a block in which every line was hot has nothing to
        // average, so it carries the previous ring entry forward - which is the dip. The low
        // value recirculates and the floor can never come back up, because coming back up needs
        // the noise to fall below a floor that is already below it.
        //
        // A fade of more than 6 dB is an ordinary quarter-minute on 40 m, and an audio dropout
        // is deeper still. What it should cost is one ring-length of memory (~15 s), not the
        // rest of the run.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        // One block - half a second - 10 dB down across part of the band. Nothing transmitted;
        // the band went quiet.
        byte[] fade = Line(lowHz: 1300, highHz: 1700, signalDb: -80);
        for (int i = 0; i < LinesPerSecond / 2; i++)
        {
            detector.AddLine(line++, fade);
        }

        // A full minute of exactly the noise the detector was warmed on, four times the floor's
        // own memory. Nothing is transmitting for any of it.
        byte[] quiet = Line();
        for (int i = 0; i < 60 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().BeEmpty(
            "the noise has not changed since the floor was banked, so there is nothing to report");
    }

    [Fact]
    public void A_Signal_After_A_Fade_Is_Reported_At_The_Snr_It_Actually_Has()
    {
        // Two consequences on the operator's side of the latch above. SNR is measured against the
        // floor, so a floor stuck 25 dB low adds 25 dB to every burst reported over it: on the
        // live 40 m station this is what puts 40-55 dB against captures whose audio holds nothing
        // that stands out from the noise at all, and an SNR that is not a measurement cannot be
        // the thing a classifier or an operator sorts captures by. Worse, a latched band is hot
        // on every line, so it holds one burst open indefinitely - and a real signal arriving in
        // it is absorbed into that burst rather than reported, which is the survey going deaf in
        // exactly the band where something once faded.
        (List<SurveyBurst> bursts, SpectralBurstDetector detector) = Detector();
        long line = Warm(detector);

        byte[] dropout = Line(lowHz: 1300, highHz: 1700, signalDb: -95);
        for (int i = 0; i < LinesPerSecond / 2; i++)
        {
            detector.AddLine(line++, dropout);
        }

        // Twenty seconds of unchanged noise - longer than the floor's memory, so a floor that
        // tracks noise has forgotten the dropout by the end of it.
        byte[] quiet = Line();
        for (int i = 0; i < 20 * LinesPerSecond; i++)
        {
            detector.AddLine(line++, quiet);
        }

        // Then a real signal, 20 dB over the noise, for a second.
        byte[] signal = Line(lowHz: 1300, highHz: 1700, signalDb: -50);
        for (int i = 0; i < LinesPerSecond; i++)
        {
            detector.AddLine(line++, signal);
        }

        for (int i = 0; i < LinesPerSecond; i++)
        {
            detector.AddLine(line++, quiet);
        }

        SurveyBurst burst = bursts.Should().ContainSingle(
            "one signal went past, and the twenty seconds of noise before it were not signals").Subject;
        burst.EndedOnTimeout.Should().BeFalse();
        burst.PeakSnrDb.Should().BeApproximately(20, 6, "that is how far over the noise it stood");
    }
}
