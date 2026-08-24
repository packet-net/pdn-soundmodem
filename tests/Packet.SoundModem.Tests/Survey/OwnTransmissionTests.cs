using Packet.SoundModem.Audio;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// What the survey does with a hole in the audio - which is, most often, the station's own
/// transmission.
/// </summary>
/// <remarks>
/// <para>
/// Receive is gated while the channel transmits, so the tap is fed nothing at all and the line
/// clock stops with it. That is the invariant <see cref="SignalSurvey"/> is built on and it is
/// not enough: the <em>radio's</em> receive audio does not come back the instant the daemon
/// clears its flag. A Flex's DAX stream stays muted a little longer, so what arrives is real
/// samples that are exactly zero, then a step back to full audio. The line clock never stopped,
/// so the gap check misses it; <see cref="SignalSurvey.Reset"/> is on the keyed edge, so nothing
/// has reset by then. The step is broadband and one window long, and it opens a burst.
/// </para>
/// <para>
/// Measured on the live station: a capture at 16:52:16 holds 2,051 consecutive zero samples
/// (170.9 ms), audio cut off mid-waveform at amplitude 885, against a journal line reading
/// <c>tx[2] bpsk300</c> at that second. 12% of 1,309 sampled captures hold at least 20 ms of
/// exact zeros - 71 inside the receiver's passband and 85 outside, so this is not a filter-edge
/// effect.
/// </para>
/// </remarks>
public class OwnTransmissionTests
{
    private const int Rate = 12000;
    private const int LinesPerSecond = 30;

    [Fact]
    public void A_Hole_In_The_Audio_Is_Not_A_Signal_Arriving()
    {
        (SignalSurvey survey, List<BurstCapture> written, WaterfallSource lines, string directory) =
            Station();
        using (survey)
        {
            float[] noise = Noise(6 * Rate, seed: 1);
            Feed(survey, lines, noise);

            // The station keys up: 170 ms of exactly-zero samples, the length measured off the
            // live capture, then the audio comes back.
            Feed(survey, lines, new float[(int)(0.171 * Rate)]);
            Feed(survey, lines, Noise(6 * Rate, seed: 2));
        }

        survey.Holes.Should().Be(1, "one hole, and the survey should have counted it");
        written.Should().BeEmpty("nothing transmitted; the station keyed up and stopped again");
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void A_Quiet_Channel_Is_Not_A_Hole()
    {
        // The rule has to distinguish "nothing is being received" from "nothing is arriving". A
        // receiver does not deliver silence: even a dead band arrives as noise, and noise does
        // not land on exactly zero for a hundred samples running. Very quiet noise is still
        // noise, and must not read as a break in the stream.
        (SignalSurvey survey, _, WaterfallSource lines, string directory) = Station();
        using (survey)
        {
            Feed(survey, lines, Noise(12 * Rate, seed: 3, amplitude: 1e-5f));
        }

        survey.Holes.Should().Be(0);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void A_Real_Signal_After_A_Hole_Is_Still_Captured()
    {
        // The other half of the bargain: abandoning what is in flight must not become deafness.
        // A station that keys up and then hears somebody has to keep the somebody.
        (SignalSurvey survey, List<BurstCapture> written, WaterfallSource lines, string directory) =
            Station();
        using (survey)
        {
            Feed(survey, lines, Noise(8 * Rate, seed: 4));
            Feed(survey, lines, new float[(int)(0.171 * Rate)]);
            Feed(survey, lines, Noise(2 * Rate, seed: 5));
            Feed(survey, lines, Burst(1300, 1800, 2.0));
            Feed(survey, lines, Noise(3 * Rate, seed: 6));
        }

        written.Should().NotBeEmpty(
            "the transmission after the hole is a real one and must still be kept");
        written.Should().OnlyContain(
            c => c.PeakSnrDb > 10, "and measured against the floor it actually stood over");
        Directory.Delete(directory, recursive: true);
    }

    private static (SignalSurvey Survey, List<BurstCapture> Written, WaterfallSource Lines, string Directory)
        Station()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "survey-own-tx-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new SignalSurveyOptions
        {
            Directory = directory,
            MarginSeconds = 0.5,
            MaxPerHour = 100,
            CooldownSeconds = 0,
        };

        SignalSurvey? survey = null;
        var source = new WaterfallSource(
            Rate, (index, line) => survey!.AddLine(index, line.Span), LinesPerSecond, 2048);
        survey = new SignalSurvey(
            options, [], Rate, source.BinWidthHz, source.LinesPerSecond, source.LineLength);

        var written = new List<BurstCapture>();
        survey.CaptureWritten += (capture, _) => written.Add(capture);
        return (survey, written, source, directory);
    }

    private static void Feed(SignalSurvey survey, WaterfallSource lines, float[] audio)
    {
        for (int at = 0; at < audio.Length; at += 480)
        {
            ReadOnlySpan<float> block = audio.AsSpan(at, Math.Min(480, audio.Length - at));
            survey.AddAudio(block);
            lines.Process(block);
        }
    }

    private static float[] Noise(int samples, int seed, float amplitude = 0.02f)
    {
        var random = new Random(seed);
        var audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            // Never exactly zero: a receiver's own noise is not silence, and a test whose noise
            // happened to land on zero would be testing the wrong thing.
            float sample = (float)((random.NextDouble() - 0.5) * 2 * amplitude);
            audio[i] = sample == 0 ? amplitude : sample;
        }

        return audio;
    }

    /// <summary>A band of noise, which is what a modulated signal looks like to an energy
    /// detector - a run of hot bins wide enough to clear the minimum width, with none of the
    /// structure a real modem would put in it. The band is approximate, which is why no test
    /// here asserts where it lands.</summary>
    /// <remarks>
    /// Not a keyed carrier: sign-flipping a sine suppresses the carrier and leaves two lobes
    /// either side of a null, so the run of hot bins is two narrow ones and neither clears
    /// <c>minWidthHz</c>. That is a fact about the test signal rather than the detector, and it
    /// cost an afternoon once.
    /// </remarks>
    private static float[] Burst(double lowHz, double highHz, double seconds)
    {
        int length = (int)(seconds * Rate);
        var random = new Random(99);
        var noise = new double[length];
        for (int i = 0; i < length; i++)
        {
            noise[i] = (random.NextDouble() - 0.5) * 2;
        }

        // A crude band-pass: difference of two running means, which is enough to put the energy
        // where it is wanted without pulling a filter design into a test.
        var audio = new float[length];
        int wide = (int)(Rate / lowHz / 2);
        int narrow = (int)(Rate / highHz / 2);
        double wideSum = 0;
        double narrowSum = 0;
        for (int i = 0; i < length; i++)
        {
            wideSum += noise[i] - (i >= wide ? noise[i - wide] : 0);
            narrowSum += noise[i] - (i >= narrow ? noise[i - narrow] : 0);
            audio[i] = (float)(0.5 * ((narrowSum / narrow) - (wideSum / wide)));
        }

        return audio;
    }
}
