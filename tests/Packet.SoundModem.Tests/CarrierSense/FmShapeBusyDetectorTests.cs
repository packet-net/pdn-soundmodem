using AwesomeAssertions;
using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// <see cref="FmShapeBusyDetector"/> against the measured open-squelch FM channel model.
/// </summary>
/// <remarks>
/// <para>These run in CI; <see cref="FmShapeBusyDetectorRecordingTests"/> scores the same detector
/// against the recordings the model was measured from, and needs those recordings.</para>
/// <para><b>Most of this file is about the failure that must never come back.</b> The previous
/// audio fallback had the physics right and absolute dBFS thresholds, and it stopped a bench
/// station transmitting because two nominally identical stations measured 22 dB apart. So the
/// tests that matter most here are not "does it find the burst" but "what does it do when it is
/// handed something it cannot judge", and "does its answer move when the station's audio path
/// does".</para>
/// </remarks>
public class FmShapeBusyDetectorTests
{
    private const int Rate = OpenSquelchFmReceiver.SampleRate;

    [Fact]
    public void It_Finds_A_Transmission_And_Not_The_Idle_Channel_Around_It()
    {
        float[] audio = OpenSquelchFmReceiver.IdleThenKeyedThenIdle(6.0, 2.0, seed: 101);
        bool?[] busy = Run(audio, Rate);

        int keyup = OpenSquelchFmReceiver.KeyupSample(6.0);
        int unkey = keyup + (int)(2.0 * Rate);

        // The warm-up is 2 s, so judge the idle stretch from 3 s in.
        BusyFraction(busy, 3 * Rate, keyup).Should().Be(0, "idle open-squelch hiss is not a signal");
        BusyFraction(busy, keyup, unkey).Should().BeGreaterThan(0.9,
            "and a transmission is, for nearly all of its length");
    }

    [Fact]
    public void A_Long_Idle_Channel_Never_Reads_Busy()
    {
        // A minute of nothing at all. False busy is deferral a station cannot see the reason for,
        // and it is the cost side of every threshold choice in the detector.
        bool?[] busy = Run(OpenSquelchFmReceiver.Idle(60.0, seed: 202), Rate);

        BusyFraction(busy, 3 * Rate, busy.Length).Should().Be(0);
    }

    /// <summary>
    /// The one that stops history repeating: the decision must not move when the station's own
    /// audio path does.
    /// </summary>
    /// <remarks>
    /// Three audio paths over the same channel - as captured, through a sound card rolling off at
    /// 6 kHz, and one at 15 kHz - plus 20 dB of gain either way. An absolute threshold set on any
    /// one of these silences or deafens the others; the measured spread of the raw statistic
    /// across plausible paths is 9.6 dB.
    /// </remarks>
    [Theory]
    [InlineData(6000, 1.0)]
    [InlineData(15000, 1.0)]
    [InlineData(6000, 10.0)]
    [InlineData(15000, 0.1)]
    public void A_Different_Station_Reaches_The_Same_Answer(double cornerHz, double gain)
    {
        float[] idle = OpenSquelchFmReceiver.IdleThrough(6.0, seed: 303, cornerHz);
        float[] keyed = OpenSquelchFmReceiver.KeyedThrough(2.0, seed: 304, cornerHz);
        var audio = new float[idle.Length + keyed.Length];
        idle.CopyTo(audio, 0);
        keyed.CopyTo(audio, idle.Length);
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (float)(audio[i] * gain);
        }

        bool?[] busy = Run(audio, Rate);

        BusyFraction(busy, 3 * Rate, idle.Length).Should().Be(0,
            "a station whose card rolls off at {0} Hz must not read its own empty channel as busy",
            cornerHz);
        BusyFraction(busy, idle.Length, audio.Length).Should().BeGreaterThan(0.9,
            "and must still hear a transmission on it");
    }

    /// <summary>
    /// Digital silence, and both bench stations' own receive audio while they transmit.
    /// </summary>
    /// <remarks>
    /// radio1's gated audio measured below -86 dBFS and radio2's -63.5 dBFS, 22 dB apart, which is
    /// exactly what an absolute guard cannot straddle. Here neither is a number the detector
    /// knows: both are far below the level the station itself showed while idle, so both decline.
    /// </remarks>
    [Theory]
    [InlineData("digital silence", 0.0)]
    [InlineData("radio1 transmitting", -86.0)]
    [InlineData("radio2 transmitting", -63.5)]
    [InlineData("a squelched receiver", -55.0)]
    public void A_Quiet_Input_Is_No_Opinion_And_Never_Busy(string what, double faultDbfs)
    {
        bool?[] busy = RunWithFault(faultDbfs, seed: 404, out FmShapeBusyDetector detector);

        BusyCount(busy, 6 * Rate, busy.Length).Should().Be(0, "{0} is not a carrier", what);
        detector.Busy.Should().BeNull("and the detector has to say that it does not know");
        detector.Engaged.Should().BeFalse();
    }

    /// <summary>
    /// An additive path: a signal that makes the receiver LOUDER, which is SSB, a wired loop, or a
    /// squelched receiver opening up.
    /// </summary>
    /// <remarks>
    /// The right answer is no opinion, and it is not a limitation. On such a path
    /// <see cref="Packet.SoundModem.Modems.EnergyBusyDetector"/> does exactly what it says, and it
    /// keeps the job. What matters is that this detector recognises the path rather than
    /// volunteering an answer about one it was not built for.
    /// </remarks>
    [Fact]
    public void An_Additive_Path_Is_No_Opinion_Rather_Than_A_Guess()
    {
        float[] quiet = OpenSquelchFmReceiver.Idle(6.0, seed: 505);
        float[] loud = OpenSquelchFmReceiver.Idle(4.0, seed: 506);
        for (int i = 0; i < loud.Length; i++)
        {
            loud[i] *= 100f;   // 40 dB, which is what an SSB signal does to a quiet receiver
        }

        var audio = new float[quiet.Length + loud.Length];
        quiet.CopyTo(audio, 0);
        loud.CopyTo(audio, quiet.Length);

        bool?[] busy = Run(audio, Rate);

        BusyCount(busy, quiet.Length, audio.Length).Should().Be(0);
        busy[^1].Should().BeNull("the detector declines a path where a signal ADDS power");
    }

    /// <summary>
    /// The known limitation, pinned so nobody switches it on where it does not work.
    /// </summary>
    /// <remarks>
    /// At 12 kHz the split lands at 2 kHz, inside a wideband mode's own occupancy, and the
    /// statistic stops separating: measured on the reference recording decimated, and
    /// cross-checked against an ideal brickwall to take the decimator out of the question, the
    /// best margin over every split from 1 to 4 kHz is 0.6 dB against 19.3 dB at 48 kHz.
    /// <see cref="FmShapeBusyDetector.MinimumSampleRate"/> is what a caller gates on; this shows
    /// what happens if it does not.
    /// </remarks>
    [Fact]
    public void At_Twelve_Kilohertz_It_Does_Not_Find_The_Transmission()
    {
        const int Slow = 12000;
        float[] idle = OpenSquelchFmReceiver.Idle(6.0, seed: 606, Slow);
        float[] keyed = OpenSquelchFmReceiver.Keyed(2.0, seed: 607, Slow);
        var audio = new float[idle.Length + keyed.Length];
        idle.CopyTo(audio, 0);
        keyed.CopyTo(audio, idle.Length);

        bool?[] busy = Run(audio, Slow);

        BusyFraction(busy, idle.Length, audio.Length).Should().BeLessThan(0.5,
            "this is a record of a limitation, not a wish: 12 kHz is below "
            + "FmShapeBusyDetector.MinimumSampleRate and the caller must not consult it there");
        FmShapeBusyDetector.MinimumSampleRate.Should().BeGreaterThan(Slow);
    }

    /// <summary>
    /// Nothing in the decision may be an absolute number, which is checkable by reading the
    /// constants rather than by arguing about it.
    /// </summary>
    [Fact]
    public void Every_Threshold_Is_A_Ratio_Against_Something_The_Station_Measured_Itself()
    {
        // Each of these is "how far from the station's own idle", so scaling a station's whole
        // audio path by any constant leaves all four unchanged. That is the property the previous
        // fallback did not have.
        FmShapeBusyDetector.AssertAboveReferenceDb.Should()
            .BeGreaterThan(FmShapeBusyDetector.ReleaseAboveReferenceDb, "hysteresis");
        FmShapeBusyDetector.DeadInputBelowReferenceDb.Should().BeGreaterThan(20,
            "radio2's own gated audio sits 47 dB below its idle and radio1's 70 dB below, and a "
            + "burst only 2.7 to 5.0 dB below, so this has to clear the burst by a long way and "
            + "still catch both stations");
        FmShapeBusyDetector.LoudInputAboveReferenceDb.Should().BeGreaterThan(2,
            "real idle rises by up to 1.2 dB over ten minutes");
        FmShapeBusyDetector.MaxBusySeconds.Should().BeGreaterThan(13.6,
            "the longest transmission measured on this channel; a 10 s backstop cut a real one "
            + "short");
    }

    private static bool?[] RunWithFault(
        double faultDbfs, int seed, out FmShapeBusyDetector detector)
    {
        // Six seconds of the real idle channel to build a reference from, then the fault.
        float[] settle = OpenSquelchFmReceiver.Idle(6.0, seed);
        var audio = new float[settle.Length + (Rate * 10)];
        settle.CopyTo(audio, 0);

        var random = new Random(seed + 1);
        double amplitude = faultDbfs == 0.0 ? 0.0 : Math.Pow(10, faultDbfs / 20.0) * Math.Sqrt(3);
        for (int i = settle.Length; i < audio.Length; i++)
        {
            audio[i] = (float)((random.NextDouble() - 0.5) * 2 * amplitude);
        }

        detector = new FmShapeBusyDetector(Rate);
        return Feed(detector, audio, Rate);
    }

    private static bool?[] Run(float[] audio, int rate) =>
        Feed(new FmShapeBusyDetector(rate), audio, rate);

    /// <summary>Feeds in 10 ms slices, which is roughly what an audio callback delivers, so the
    /// detector's own block boundaries are exercised rather than handed the whole file.</summary>
    private static bool?[] Feed(FmShapeBusyDetector detector, float[] audio, int rate)
    {
        var busy = new bool?[audio.Length];
        int slice = rate / 100;
        for (int at = 0; at < audio.Length; at += slice)
        {
            int take = Math.Min(slice, audio.Length - at);
            detector.Process(audio.AsSpan(at, take));
            for (int i = at; i < at + take; i++)
            {
                busy[i] = detector.Busy;
            }
        }

        return busy;
    }

    private static double BusyFraction(bool?[] busy, int from, int to) =>
        (double)BusyCount(busy, from, to) / Math.Max(to - from, 1);

    private static int BusyCount(bool?[] busy, int from, int to)
    {
        int count = 0;
        for (int i = from; i < to; i++)
        {
            if (busy[i] == true)
            {
                count++;
            }
        }

        return count;
    }
}
