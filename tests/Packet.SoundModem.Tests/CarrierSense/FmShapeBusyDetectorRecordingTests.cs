using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// <see cref="FmShapeBusyDetector"/> scored against real open-squelch FM recordings.
/// </summary>
/// <remarks>
/// <para><b>Opt-in, because the recordings are not in the repository.</b> They are 68 MB of
/// station capture and belong beside the campaign evidence, not in git. Point
/// <c>FM_CARRIER_SENSE_FIXTURES</c> at a directory holding them and these run; without it they
/// skip, and the synthetic model in <see cref="OpenSquelchFmCarrierSenseTests"/> is what CI has.
/// On the dev box that directory is <c>/home/tf/fm-carrier-sense-evidence</c>.</para>
/// <para><b>The ground truth is the recording's own structure, not a table of timestamps.</b> The
/// reference capture holds 15 transmissions from a NinoTNC keying once every 1.2 s, all of them
/// the same frame sample for sample. So the test asserts the count, the spacing and the length of
/// what the detector found, which an instrument error cannot satisfy by accident: a statistic that
/// was really reading something else would not produce fifteen episodes 1.200 s apart.</para>
/// </remarks>
public class FmShapeBusyDetectorRecordingTests
{
    private const string FixtureVariable = "FM_CARRIER_SENSE_FIXTURES";

    /// <summary>One stretch the detector called busy.</summary>
    private readonly record struct Episode(double StartSeconds, double EndSeconds)
    {
        public double LengthMilliseconds => (EndSeconds - StartSeconds) * 1000;
    }

    private static string? Fixture(string name)
    {
        string? dir = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        string path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The reference fixture: 15 NinoTNC C4FSK 19k2 transmissions against real idle hiss.
    /// </summary>
    [Fact]
    public void Fifteen_Transmissions_Are_Found_And_Nothing_Else_Is()
    {
        if (Fixture("ninorx.wav") is not { } path)
        {
            Assert.Skip($"set {FixtureVariable} to the directory holding ninorx.wav");
            return;
        }

        (float[] audio, int rate) = WavFile.ReadMono(path);
        rate.Should().Be(48000);

        List<Episode> found = Run(audio, rate, out double idleSeconds, out double busySeconds);

        found.Should().HaveCount(15,
            "the recording holds 15 transmissions and the detector must find those and no others. "
            + "Found: {0}",
            string.Join(", ", found.Select(e => $"{e.StartSeconds:F3}s+{e.LengthMilliseconds:F0}ms")));

        // The NinoTNC keyed once every 1.2 s. An instrument reading something other than the
        // transmissions could not reproduce that.
        List<double> gaps = [.. found.Zip(found.Skip(1), (a, b) => b.StartSeconds - a.StartSeconds)];
        gaps.Should().AllSatisfy(g => g.Should().BeApproximately(1.200, 0.030));

        // Each keying is about 155 ms of carrier. The detector must cover most of it and must not
        // hang on far past it.
        found.Should().AllSatisfy(e => e.LengthMilliseconds.Should().BeInRange(100, 300));

        busySeconds.Should().BeGreaterThan(0);
        idleSeconds.Should().BeGreaterThan(20, "there is idle channel either side of the bursts");
    }

    /// <summary>
    /// A whole 660 s capture chunk: several of our own transmissions and a lot of idle channel.
    /// The false-busy figure is the one that matters, because false busy is deferral a station
    /// cannot see the reason for.
    /// </summary>
    [Fact]
    public void A_Whole_Capture_Chunk_Produces_No_False_Busy()
    {
        if (Fixture("radio1-chunk-with-idle-and-bursts.wav") is not { } path)
        {
            Assert.Skip($"set {FixtureVariable} to the directory holding the capture chunk");
            return;
        }

        (float[] audio, int rate) = WavFile.ReadMono(path);
        List<Episode> found = Run(audio, rate, out _, out double busySeconds);

        // The chunk holds exactly 11 transmissions totalling 66.5 s, established independently
        // from the collapse of power above the signal's band. Every extra episode here is false
        // busy, which is deferral a station cannot see the reason for.
        found.Should().HaveCount(11,
            "the chunk holds 11 transmissions. Found {0} totalling {1:F1} s: {2}",
            found.Count, busySeconds,
            string.Join(", ", found.Select(e => $"{e.StartSeconds:F1}s+{e.LengthMilliseconds:F0}ms")));

        busySeconds.Should().BeInRange(60, 75,
            "and it must cover those 66.5 s without hanging on past them");

        // 594 s of idle channel with nothing on it at all. This is the figure that decides
        // whether the detector is safe to have switched on by default.
        double lengthSeconds = (double)audio.Length / rate;
        (lengthSeconds - busySeconds).Should().BeGreaterThan(580);
    }

    /// <summary>
    /// The generalisation check: a SECOND station, a second sound card and a second Tait, hearing
    /// a different waveform.
    /// </summary>
    /// <remarks>
    /// Everything else here is radio1 hearing one NinoTNC frame fifteen times. This is 60 s off
    /// radio2 carrying 15 <c>ofdm-fm-8k</c> transmissions from radio1, and the ground truth is
    /// radio2's own frame log: it decoded them between 17.18 and 33.12 s into the file, with idle
    /// channel for 17 s before and 27 s after. Three of them arrived within a quarter of a second
    /// of each other, so the detector reports slightly fewer episodes than transmissions, which is
    /// the 100 ms hold doing its job rather than a miss.
    /// </remarks>
    [Fact]
    public void A_Second_Station_Hearing_A_Different_Waveform_Is_Found_Too()
    {
        if (Fixture("radio2-ofdm-bursts.wav") is not { } path)
        {
            Assert.Skip($"set {FixtureVariable} to the directory holding radio2-ofdm-bursts.wav");
            return;
        }

        (float[] audio, int rate) = WavFile.ReadMono(path);
        List<Episode> found = Run(audio, rate, out _, out _);

        found.Should().HaveCountGreaterThanOrEqualTo(10)
            .And.HaveCountLessThanOrEqualTo(15,
                "15 transmissions, some of them close enough together to be reported as one. "
                + "Found: {0}",
                string.Join(", ", found.Select(e => $"{e.StartSeconds:F2}s+{e.LengthMilliseconds:F0}ms")));

        // Nothing outside the stretch radio2's own frame log says it was hearing traffic in. The
        // idle channel either side of it is what a false-busy figure is made of.
        found.Should().AllSatisfy(e =>
        {
            e.StartSeconds.Should().BeGreaterThan(16.5);
            e.EndSeconds.Should().BeLessThan(33.6);
        });
    }

    /// <summary>
    /// The property no audio detector may ever lose: it must not be able to silence a station.
    /// </summary>
    /// <remarks>
    /// Real idle hiss, then the fault, then nothing else. Each of these is a path on which "the
    /// shape is unusual" means nothing, and on each the answer has to be no opinion rather than
    /// busy. The previous audio fallback answered busy to all of them, which is how one bench
    /// station stopped transmitting altogether.
    /// </remarks>
    [Theory]
    [InlineData("digital silence", 0.0)]
    [InlineData("radio1's own gated receive audio", -86.0)]
    [InlineData("radio2's own gated receive audio", -63.5)]
    public void A_Dead_Or_Gated_Input_Is_No_Opinion_And_Never_Busy(string what, double faultDbfs)
    {
        if (Fixture("ninorx.wav") is not { } path)
        {
            Assert.Skip($"set {FixtureVariable} to the directory holding ninorx.wav");
            return;
        }

        (float[] audio, int rate) = WavFile.ReadMono(path);

        // Eight seconds of the real idle channel to build a reference from, then the fault.
        int settle = rate * 8;
        var random = new Random(4242);
        double amplitude = faultDbfs == 0.0 ? 0.0 : Math.Pow(10, faultDbfs / 20.0) * Math.Sqrt(2);
        var stream = new float[settle + (rate * 20)];
        Array.Copy(audio, 0, stream, 0, settle);
        for (int i = settle; i < stream.Length; i++)
        {
            stream[i] = (float)((random.NextDouble() - 0.5) * 2 * amplitude);
        }

        var detector = new FmShapeBusyDetector(rate);
        int busyAfterFault = 0;
        int block = rate / 100;
        for (int at = 0; at < stream.Length; at += block)
        {
            detector.Process(stream.AsSpan(at, Math.Min(block, stream.Length - at)));
            if (at >= settle && detector.Busy == true)
            {
                busyAfterFault++;
            }
        }

        busyAfterFault.Should().Be(0, "{0} is not a carrier", what);
        detector.Busy.Should().BeNull("and the detector has to say it does not know");
    }

    /// <summary>Runs the detector over a recording and returns what it called busy.</summary>
    private static List<Episode> Run(
        float[] audio, int rate, out double idleSeconds, out double busySeconds)
    {
        var detector = new FmShapeBusyDetector(rate);
        List<Episode> episodes = [];
        bool inEpisode = false;
        double start = 0;
        int busyBlocks = 0;
        int clearBlocks = 0;

        // Fed in 10 ms slices, which is roughly what an audio callback delivers, so the detector's
        // own block boundaries are exercised rather than being handed the whole file at once.
        int slice = rate / 100;
        for (int at = 0; at < audio.Length; at += slice)
        {
            int take = Math.Min(slice, audio.Length - at);
            detector.Process(audio.AsSpan(at, take));
            double t = (double)(at + take) / rate;
            bool? busy = detector.Busy;

            if (busy == true)
            {
                busyBlocks++;
                if (!inEpisode)
                {
                    inEpisode = true;
                    start = t;
                }
            }
            else
            {
                if (busy == false)
                {
                    clearBlocks++;
                }

                if (inEpisode)
                {
                    inEpisode = false;
                    episodes.Add(new Episode(start, t));
                }
            }
        }

        if (inEpisode)
        {
            episodes.Add(new Episode(start, (double)audio.Length / rate));
        }

        busySeconds = busyBlocks * (double)slice / rate;
        idleSeconds = clearBlocks * (double)slice / rate;
        return episodes;
    }
}
