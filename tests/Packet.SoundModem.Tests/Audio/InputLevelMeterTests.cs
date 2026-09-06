using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// The peak and RMS the operator page's level meter is drawn from, against blocks whose answer is
/// known from first principles.
/// </summary>
/// <remarks>
/// The maths is the whole feature: an operator is about to set a capture gain on the strength of
/// this number, and a meter that is 3 dB out is worse than no meter at all, because it will be
/// believed. So the cases here are the ones with an arithmetic answer - a full-scale square, a
/// half-amplitude sine, digital silence - rather than recordings.
/// </remarks>
public class InputLevelMeterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private static (InputLevelMeter Meter, FakeTimeProvider Clock) New()
    {
        var clock = new FakeTimeProvider();
        return (new InputLevelMeter(clock, Interval), clock);
    }

    /// <summary>
    /// Takes one block as both the card's samples and the modems', and lets the interval end -
    /// which is what a station with no resampler does, and what the taps do between them.
    /// </summary>
    private static InputLevel Measure(float[] block)
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();
        meter.AddCardSamples(block);
        meter.Add(block);
        clock.Advance(Interval);
        meter.TryTake(out InputLevel level).Should().BeTrue();
        return level;
    }

    /// <summary>One cycle-aligned sine, so the RMS is exactly the amplitude over root two.</summary>
    private static float[] Sine(float amplitude, int cycles = 10, int samples = 2400)
    {
        var block = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            block[n] = amplitude * MathF.Sin(2 * MathF.PI * cycles * n / samples);
        }

        return block;
    }

    [Fact]
    public void A_Full_Scale_Square_Reads_Zero_dBFS_On_Both_Peak_And_Rms()
    {
        var block = new float[64];
        for (int n = 0; n < block.Length; n++)
        {
            block[n] = n % 2 == 0 ? 1f : -1f;
        }

        InputLevel level = Measure(block);

        level.PeakDbFs.Should().Be(0, "0 dBFS is the top of the scale and nothing reads past it");
        level.RmsDbFs.Should().Be(0, "every sample is at full scale");
        level.Clipped.Should().BeTrue();
    }

    /// <summary>
    /// A sine at half amplitude: peak 20*log10(0.5) = -6.02 dBFS, RMS 3.01 dB below that.
    /// </summary>
    [Fact]
    public void A_Half_Amplitude_Sine_Reads_Six_Down_On_Peak_And_Nine_Down_On_Rms()
    {
        InputLevel level = Measure(Sine(0.5f));

        level.PeakDbFs.Should().BeApproximately(-6.02, 0.05);
        level.RmsDbFs.Should().BeApproximately(-9.03, 0.05, "a sine's RMS is its peak over root 2");
        level.Clipped.Should().BeFalse();
    }

    /// <summary>
    /// The band this meter exists to aim at, as a real received signal would present it.
    /// </summary>
    /// <remarks>
    /// 0.25 full scale is -12.04 dBFS, which is the middle of what this repository has measured
    /// as good on real hardware: the bench NinoTNC loop's GOOD band is 0.17 to 0.28 full-scale
    /// peak (docs/ninotnc-loop.md), and the CM108 interface is designed for -12 dBFS at 60% of
    /// class deviation (docs/hardware/tm8100-cm108-interface-notes.md).
    /// </remarks>
    [Fact]
    public void A_Signal_In_The_Target_Band_Reads_Inside_The_Target_Band()
    {
        InputLevel level = Measure(Sine(0.25f));

        level.PeakDbFs.Should().BeApproximately(-12.04, 0.05);
        level.PeakDbFs.Should().BeInRange(
            InputLevelMeter.TargetPeakLowDbFs, InputLevelMeter.TargetPeakHighDbFs);
    }

    [Fact]
    public void Digital_Silence_Reads_The_Floor_Rather_Than_Minus_Infinity()
    {
        InputLevel level = Measure(new float[512]);

        // -inf is not a number JSON can carry, and not a width a bar can be drawn at.
        level.PeakDbFs.Should().Be(InputLevelMeter.FloorDbFs);
        level.RmsDbFs.Should().Be(InputLevelMeter.FloorDbFs);
        level.Clipped.Should().BeFalse();
    }

    /// <summary>
    /// The largest positive 16-bit code is 32767/32768 of full scale, not 1.0.
    /// </summary>
    /// <remarks>
    /// <see cref="Pcm16.ToFloat"/> divides by 32768, so a capture clipped on its positive half
    /// arrives at 0.99997 and a threshold of 1.0 would never see it. That is the whole clip
    /// indicator failing silently on exactly the case it exists for.
    /// </remarks>
    [Fact]
    public void A_Positive_Half_Cycle_Clipped_At_The_Top_Code_Still_Counts_As_Clipping()
    {
        InputLevel level = Measure([0.1f, Pcm16.ToFloat(short.MaxValue), -0.1f]);

        level.Clipped.Should().BeTrue();
        level.PeakDbFs.Should().BeApproximately(
            0, 0.01, "the top code is 32767/32768, a quarter of a thousandth of a dB down");
    }

    [Fact]
    public void A_Signal_That_Never_Reaches_Full_Scale_Does_Not_Report_Clipping()
    {
        Measure(Sine(0.98f)).Clipped.Should().BeFalse("0.98 is loud, and loud is not clipped");
    }

    /// <summary>
    /// Exactly the two end codes count, and the code next to each does not.
    /// </summary>
    /// <remarks>
    /// A converter that runs out of range produces the rail, usually several samples of it, not a
    /// value near the rail. Widening this would only fire the operator-facing alarm on
    /// loud-but-clean audio, which is the fault the bench found in the first cut of this feature.
    /// </remarks>
    [Theory]
    [InlineData(short.MaxValue, true)]
    [InlineData(short.MinValue, true)]
    [InlineData(short.MaxValue - 1, false)]
    [InlineData(short.MinValue + 1, false)]
    [InlineData(0, false)]
    public void Only_The_Top_And_Bottom_Codes_Are_Clipping(int code, bool clipped) =>
        InputLevelMeter.IsClipped(Pcm16.ToFloat((short)code)).Should().Be(clipped);

    /// <summary>
    /// The clip indicator is not decided by the audio the modems hear.
    /// </summary>
    /// <remarks>
    /// That audio has been through the 48 to 12 kHz decimating filter on any 48 kHz card, and the
    /// filter's ripple moves peaks either way - so full scale there is not full scale at the
    /// converter. This is the unit-level half of that; the real path is in
    /// <c>WaterfallLevelMeterTests</c>.
    /// </remarks>
    [Fact]
    public void A_Full_Scale_Sample_In_The_Modem_Audio_Alone_Does_Not_Light_The_Indicator()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();

        meter.AddCardSamples(Sine(0.5f));            // the card had 6 dB of headroom
        meter.Add([0.1f, 1f, -0.1f]);                // and the resampler overshot anyway
        clock.Advance(Interval);

        meter.TryTake(out InputLevel level).Should().BeTrue();
        level.Clipped.Should().BeFalse("only the card's own samples answer that question");
        level.PeakDbFs.Should().Be(0, "and the reading is still clamped to the top of the scale");
    }

    [Fact]
    public void Nothing_Is_Reported_Until_The_Interval_Has_Run()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();

        meter.Add(Sine(0.5f));
        meter.TryTake(out _).Should().BeFalse("the interval is what paces the page, not the block");

        clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        meter.TryTake(out _).Should().BeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        meter.TryTake(out InputLevel level).Should().BeTrue();
        level.PeakDbFs.Should().BeApproximately(-6.02, 0.05);
    }

    [Fact]
    public void Each_Interval_Is_Measured_On_Its_Own_Rather_Than_Since_The_Start()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();

        meter.Add(Sine(0.9f));
        clock.Advance(Interval);
        meter.TryTake(out InputLevel loud).Should().BeTrue();

        meter.Add(Sine(0.05f));
        clock.Advance(Interval);
        meter.TryTake(out InputLevel quiet).Should().BeTrue();

        loud.PeakDbFs.Should().BeApproximately(-0.92, 0.05);
        quiet.PeakDbFs.Should().BeApproximately(
            -26.02, 0.05, "a peak from the last interval must not be held over into this one");
    }

    /// <summary>
    /// The rate is five a second and stays there, whatever the receive block period is.
    /// </summary>
    /// <remarks>
    /// The bench measured 3.4 a second against a documented five (radio1, 2026-09-06). The
    /// mechanism was in the take: the interval restarted from the moment of the take rather than
    /// from the boundary it crossed, and the boundary is only tested when a block arrives - so
    /// with 100 ms blocks, one block a hair short put the next test at 199.x ms, skipped it, and
    /// locked the meter at 300 ms spacing with no way back. The old FakeTimeProvider tests could
    /// never see it, because they advanced by exactly one interval; these advance by a block.
    /// </remarks>
    [Theory]
    [InlineData(20)]    // ARDOP's block
    [InlineData(100)]   // every packet station's block
    [InlineData(199)]   // and the awkward one, just under the interval
    public void The_Rate_Settles_At_Five_A_Second_Whatever_The_Block_Period_Is(int blockMs)
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();
        float[] block = Sine(0.25f, samples: 12 * blockMs);

        int blocks = 4000 / blockMs;
        int readings = 0;
        for (int n = 0; n < blocks; n++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(blockMs));
            meter.Add(block);
            if (meter.TryTake(out _))
            {
                readings++;
            }
        }

        double rate = readings / (blocks * blockMs / 1000.0);
        rate.Should().BeApproximately(
            5, 0.3, "the meter is paced by the interval, not by the block period");
    }

    /// <summary>
    /// The exact regression: blocks a hair under the interval's half used to halve the rate.
    /// </summary>
    /// <remarks>
    /// 99.9 ms blocks against a 200 ms interval. Restarting the interval from the take gives a
    /// steady 299.7 ms spacing, which is 3.34 a second and is what the bench probe measured;
    /// advancing by whole intervals gives 5.0 and cannot ratchet.
    /// </remarks>
    [Fact]
    public void A_Block_A_Hair_Under_Half_The_Interval_Does_Not_Ratchet_The_Rate_Down()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();
        var blockPeriod = TimeSpan.FromTicks(999_000);   // 99.9 ms
        float[] block = Sine(0.25f, samples: 1200);

        const int blocks = 200;
        int readings = 0;
        for (int n = 0; n < blocks; n++)
        {
            clock.Advance(blockPeriod);
            meter.Add(block);
            if (meter.TryTake(out _))
            {
                readings++;
            }
        }

        double rate = readings / (blocks * blockPeriod.TotalSeconds);
        rate.Should().BeGreaterThan(4.5, "3.4 a second was the defect this fixes");
        rate.Should().BeApproximately(5, 0.1);
    }

    /// <summary>
    /// An interval with no audio in it produces nothing, rather than a reading of the floor.
    /// </summary>
    /// <remarks>
    /// A card that has stopped delivering has not measured silence, it has measured nothing, and
    /// a bar drawn at the floor would say the first thing when the second is true. The dead-feed
    /// watch is what reports a stopped card, and it says so in words.
    /// </remarks>
    [Fact]
    public void An_Interval_With_No_Audio_At_All_Reports_Nothing()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();

        clock.Advance(Interval);

        meter.TryTake(out _).Should().BeFalse();
    }

    /// <summary>
    /// What the server does the moment the last page closes: the accumulation is thrown away.
    /// </summary>
    /// <remarks>
    /// Otherwise a half-interval kept across a gap of hours is handed to the next viewer as their
    /// first reading, showing them a peak from whenever the last one left - and, on a station
    /// that clipped once at three in the morning, a clip light on arrival.
    /// </remarks>
    [Fact]
    public void A_Reset_Throws_Away_What_Nobody_Was_Watching()
    {
        (InputLevelMeter meter, FakeTimeProvider clock) = New();

        meter.AddCardSamples(Sine(1f));
        meter.Add(Sine(1f));
        meter.Reset();

        clock.Advance(Interval);
        meter.TryTake(out _).Should().BeFalse("the interval was emptied as well as restarted");

        meter.Add(Sine(0.25f));
        clock.Advance(Interval);
        meter.TryTake(out InputLevel level).Should().BeTrue();
        level.PeakDbFs.Should().BeApproximately(-12.04, 0.05);
        level.Clipped.Should().BeFalse("the clip belonged to an interval nobody was watching");
    }

    [Theory]
    [InlineData(2.0, 0.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, -6.0206)]
    [InlineData(0.1, -20.0)]
    [InlineData(0.01, -40.0)]
    [InlineData(0.0, InputLevelMeter.FloorDbFs)]
    [InlineData(-1.0, InputLevelMeter.FloorDbFs)]
    public void A_Magnitude_Converts_To_The_dBFS_Everyone_Else_Would_Get(double magnitude, double dbfs) =>
        InputLevelMeter.DbFs(magnitude).Should().BeApproximately(dbfs, 0.001);

    /// <summary>
    /// The four figures the page paints its zone from, pinned here so a change is deliberate.
    /// </summary>
    /// <remarks>
    /// They are not a convention borrowed from elsewhere; they are what this repository has
    /// measured. See <see cref="InputLevelMeter"/> for the four sources. The page carries the
    /// same numbers as JavaScript constants, and <c>WaterfallPageTests</c> checks the two agree.
    /// </remarks>
    [Fact]
    public void The_Target_Zone_Is_The_One_This_Repository_Has_Measured()
    {
        InputLevelMeter.TargetPeakLowDbFs.Should().Be(-18);
        InputLevelMeter.TargetPeakHighDbFs.Should().Be(-9);
        InputLevelMeter.QuietPeakDbFs.Should().Be(-30);
        InputLevelMeter.HotPeakDbFs.Should().Be(-3);
        InputLevelMeter.DefaultInterval.Should().Be(
            TimeSpan.FromMilliseconds(200), "five a second: fast enough to aim a slider by");
        InputLevelMeter.TopCode.Should().Be(
            32767f / 32768f, "which is short.MaxValue through Pcm16.ToFloat, not 1.0");
        InputLevelMeter.BottomCode.Should().Be(-1f, "and short.MinValue is exactly -1.0");
    }
}
