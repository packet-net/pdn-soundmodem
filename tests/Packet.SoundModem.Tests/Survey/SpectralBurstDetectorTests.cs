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

    /// <summary>Feeds enough quiet lines that a noise floor has been banked.</summary>
    private static long Warm(SpectralBurstDetector detector, long from = 0, int lines = 60)
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
}
