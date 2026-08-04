using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class BandActivityTrackerTests
{
    private const double BinWidthHz = 12000.0 / 2048;
    private const int LinesPerSecond = 30;
    private const int LineLength = 1024;

    // Byte 60 ≈ −76.5 dBFS noise, byte 120 ≈ −52.9 dBFS signal → ≈ 23.5 dB SNR.
    private const byte NoiseByte = 60;
    private const byte SignalByte = 120;

    private static BandActivityTracker Tracker() =>
        new(BinWidthHz, LinesPerSecond, LineLength, lowHz: 1200, highHz: 1800);

    private static byte[] Line(byte background, byte bandLevel, double lowHz = 1200, double highHz = 1800)
    {
        var line = new byte[LineLength];
        Array.Fill(line, background);
        for (int bin = (int)(lowHz / BinWidthHz); bin < (int)(highHz / BinWidthHz); bin++)
        {
            line[bin] = bandLevel;
        }

        return line;
    }

    private static void Feed(BandActivityTracker tracker, byte[] line, int count)
    {
        for (int n = 0; n < count; n++)
        {
            tracker.AddLine(line);
        }
    }

    [Fact]
    public void Measures_burst_snr_and_length_against_the_noise_floor()
    {
        var tracker = Tracker();
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 3);
        Feed(tracker, Line(NoiseByte, SignalByte), 45);

        tracker.TryMeasureBurst(out double snrDb, out int burstLines).Should().BeTrue();

        snrDb.Should().BeApproximately(23.5, 1.5);
        burstLines.Should().Be(45);
    }

    [Fact]
    public void A_quiet_band_yields_no_burst()
    {
        var tracker = Tracker();
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 5);

        tracker.TryMeasureBurst(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_just_ended_burst_stays_measurable_through_the_grace_window()
    {
        var tracker = Tracker();
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 3);
        Feed(tracker, Line(NoiseByte, SignalByte), 30);
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond); // 1 s after burst end

        tracker.TryMeasureBurst(out double snrDb, out int burstLines).Should().BeTrue();
        burstLines.Should().Be(30);
        snrDb.Should().BeGreaterThan(20);
    }

    [Fact]
    public void An_old_burst_is_forgotten_after_the_grace_window()
    {
        var tracker = Tracker();
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 3);
        Feed(tracker, Line(NoiseByte, SignalByte), 30);
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 3);

        tracker.TryMeasureBurst(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Energy_outside_the_band_does_not_register()
    {
        var tracker = Tracker();
        Feed(tracker, Line(NoiseByte, NoiseByte), LinesPerSecond * 3);
        // A strong signal at 2500-3000 Hz, outside this tracker's 1200-1800 band.
        Feed(tracker, Line(NoiseByte, SignalByte, lowHz: 2500, highHz: 3000), 30);

        tracker.TryMeasureBurst(out _, out _).Should().BeFalse();
    }
}
