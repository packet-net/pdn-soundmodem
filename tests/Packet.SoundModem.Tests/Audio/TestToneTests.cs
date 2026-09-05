using AwesomeAssertions;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// The audio a transmitter test puts on the air: the tones are the ones an operator will read a
/// meter against, and the level is the one the station's data goes out at, so both are measured
/// here rather than taken on trust.
/// </summary>
/// <remarks>
/// The two-tone level convention is the assertion that matters most and the one most easily got
/// wrong: two equal tones, each half of the stated peak, so the sum peaks at exactly the peak a
/// single tone would reach. Give each tone the full amplitude instead and the transmitter is
/// driven 6 dB harder by the test than by any frame, which is the opposite of what the test is
/// for - the ALC reading would be of the test, not of the station.
/// </remarks>
public class TestToneTests
{
    private const int Rate = 12000;

    /// <summary>The amplitude of a tone at <paramref name="hz"/>, by Goertzel.</summary>
    /// <remarks>Over the steady middle of the burst, so the shaped edges do not drag the answer
    /// down: the envelope is 1 there and the amplitude read is the amplitude sent.</remarks>
    private static double Amplitude(IReadOnlyList<float> audio, double hz, int from, int count)
    {
        double w = 2 * Math.PI * hz / Rate;
        double cosine = Math.Cos(w), sine = Math.Sin(w);
        double s1 = 0, s2 = 0;
        for (int n = 0; n < count; n++)
        {
            double s = audio[from + n] + (2 * cosine * s1) - s2;
            s2 = s1;
            s1 = s;
        }

        double real = s1 - (s2 * cosine);
        double imaginary = s2 * sine;
        return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / count;
    }

    private static (int From, int Count) Steady(int length) => (length / 4, length / 2);

    [Fact]
    public void The_Two_Tone_Test_Is_Seven_Hundred_And_Nineteen_Hundred_Hertz()
    {
        float[] audio = new TestTone(
            [TestTone.TwoToneLowHz, TestTone.TwoToneHighHz], 0.8, Rate, 1.0).Render();
        (int from, int count) = Steady(audio.Length);

        Amplitude(audio, 700, from, count).Should().BeApproximately(0.4, 0.01);
        Amplitude(audio, 1900, from, count).Should().BeApproximately(0.4, 0.01);

        // And nothing anywhere else. A third tone would be an intermodulation product of our own
        // making, which is exactly the thing the operator is about to measure the radio for.
        foreach (double elsewhere in new[] { 300.0, 1000.0, 1300.0, 2500.0, 3100.0 })
        {
            Amplitude(audio, elsewhere, from, count).Should().BeLessThan(
                0.01, "a two-tone test with a third tone in it measures itself");
        }
    }

    [Fact]
    public void The_Two_Tones_Are_Equal_And_Their_Sum_Peaks_At_The_Transmit_Level()
    {
        float[] audio = new TestTone(
            [TestTone.TwoToneLowHz, TestTone.TwoToneHighHz], 0.8, Rate, 1.0).Render();
        (int from, int count) = Steady(audio.Length);

        double low = Amplitude(audio, 700, from, count);
        double high = Amplitude(audio, 1900, from, count);
        low.Should().BeApproximately(high, 0.005, "a two-tone test is two EQUAL tones");

        // The envelope of two equal tones reaches their sum, which here is the level a frame
        // goes out at. 700 and 1900 Hz beat at 1200 Hz, so a one-second burst has 1,200 peaks in
        // it and the highest of them lands within a fraction of a percent of the true peak.
        double peak = 0;
        for (int n = from; n < from + count; n++)
        {
            peak = Math.Max(peak, Math.Abs(audio[n]));
        }

        peak.Should().BeApproximately(0.8, 0.01);
    }

    [Fact]
    public void A_Single_Tone_Carries_The_Whole_Transmit_Level()
    {
        float[] audio = new TestTone([999], 0.8, Rate, 1.0).Render();
        (int from, int count) = Steady(audio.Length);

        // The carrier check is one tone at the level a frame peaks at, so the key-down power an
        // operator reads is the peak envelope power the two-tone test above drives.
        Amplitude(audio, 999, from, count).Should().BeApproximately(0.8, 0.01);
    }

    [Fact]
    public void The_Burst_Lasts_As_Long_As_It_Was_Asked_For()
    {
        new TestTone([1000], 0.8, Rate, 5).Render().Length.Should().Be(5 * Rate);
        new TestTone([1000], 0.8, Rate, 0.25).Render().Length.Should().Be(Rate / 4);
        new TestTone([1000], 0.8, 48000, 3).Render().Length.Should().Be(3 * 48000);
    }

    [Fact]
    public void Blocks_Join_Without_A_Seam()
    {
        // The generator has to be able to hand out the burst in pieces without a phase step at
        // every join, or a test assembled block by block would carry a click per block.
        float[] whole = new TestTone([700, 1900], 0.8, Rate, 0.5).Render();

        var blocked = new List<float>();
        var pieces = new TestTone([700, 1900], 0.8, Rate, 0.5);
        while (!pieces.Complete)
        {
            blocked.AddRange(pieces.Next(333));
        }

        blocked.Should().HaveCount(whole.Length);
        for (int n = 0; n < whole.Length; n++)
        {
            blocked[n].Should().BeApproximately(whole[n], 1e-6f);
        }
    }

    [Fact]
    public void The_Edges_Are_Shaped_So_The_Burst_Does_Not_Click()
    {
        float[] audio = new TestTone([1000], 0.8, Rate, 0.5).Render();
        int edge = (int)Math.Round(TestTone.EdgeSeconds * Rate);

        // Starts and ends at nothing, rather than at whatever the cosine happened to be.
        Math.Abs(audio[0]).Should().BeLessThan(0.001f);
        Math.Abs(audio[^1]).Should().BeLessThan(0.001f);

        // And gets there gradually: the peak inside the first millisecond is a small fraction of
        // the peak once the rise is over. A rectangular envelope's sidelobes decay at 6 dB per
        // octave, which on the air is a key click either side of the test.
        float early = 0, established = 0;
        for (int n = 0; n < Rate / 1000; n++)
        {
            early = Math.Max(early, Math.Abs(audio[n]));
        }

        for (int n = edge * 2; n < (edge * 2) + (Rate / 100); n++)
        {
            established = Math.Max(established, Math.Abs(audio[n]));
        }

        early.Should().BeLessThan(established / 3);
        established.Should().BeApproximately(0.8f, 0.01f);
    }

    [Fact]
    public void Stopping_Fades_The_Burst_Out_And_Then_Sends_Nothing()
    {
        var tone = new TestTone([1000], 0.8, Rate, 30);
        float[] before = tone.Next(Rate);        // one second of it
        tone.Stop();

        float[] after = tone.Next(Rate);
        after.Length.Should().BeLessThan(
            Rate, "a stopped burst runs on only for as long as the fall takes");
        Math.Abs(after[^1]).Should().BeLessThan(
            0.001f, "and gets there through the envelope, not by being cut");
        Math.Abs(before[^1]).Should().BeGreaterThan(
            0.0f, "the samples already rendered are untouched");

        tone.Complete.Should().BeTrue();
        tone.Next(Rate).Should().BeEmpty("nothing follows a stop");
        tone.Render().Should().BeEmpty();
    }

    [Fact]
    public void A_Test_Stopped_Before_It_Started_Never_Reaches_Full_Amplitude()
    {
        // The case the two envelope shapes have to agree about: a stop arriving inside the rise.
        // Taking only the rise would put a hard edge at the end of the burst, which is the click
        // both edges exist to prevent arriving by the back door.
        var tone = new TestTone([1000], 0.8, Rate, 30);
        tone.Stop();

        float[] all = tone.Render();
        all.Should().NotBeEmpty();
        all.Max(Math.Abs).Should().BeLessThan(0.5f);
        Math.Abs(all[^1]).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void The_Bessel_Null_Presets_Are_The_Four_Deviations_An_Fm_Station_Wants()
    {
        // Tom's four pairs, from the roadmap: the carrier of an FM signal vanishes at a
        // modulation index of 2.405, so a tone of f nulls at 2.405f of deviation.
        TestTone.BesselNullTonesHz.Should().Equal(500, 999, 1248, 2079);

        TestTone.BesselNullDeviationHz(500).Should().BeApproximately(1200, 5);
        TestTone.BesselNullDeviationHz(999).Should().BeApproximately(2400, 5);
        TestTone.BesselNullDeviationHz(1248).Should().BeApproximately(3000, 5);
        TestTone.BesselNullDeviationHz(2079).Should().BeApproximately(5000, 5);
    }

    [Fact]
    public void A_Tone_The_Channel_Cannot_Carry_Is_Refused_Rather_Than_Aliased()
    {
        // Above Nyquist a tone comes out somewhere else entirely, and an operator calibrating a
        // deviation off it would be calibrating against the wrong frequency without being told.
        Action tooHigh = () => new TestTone([7000], 0.8, Rate, 1);
        tooHigh.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Nyquist*");

        Action noTones = () => new TestTone([], 0.8, Rate, 1);
        noTones.Should().Throw<ArgumentException>();

        Action tooLoud = () => new TestTone([1000], 1.5, Rate, 1);
        tooLoud.Should().Throw<ArgumentOutOfRangeException>();

        Action noTime = () => new TestTone([1000], 0.8, Rate, 0);
        noTime.Should().Throw<ArgumentOutOfRangeException>();
    }
}
