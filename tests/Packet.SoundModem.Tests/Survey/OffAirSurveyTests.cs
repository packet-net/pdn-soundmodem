using Packet.SoundModem.Audio;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// The survey's burst detector against a real signal off 40 m
/// (<c>samples/offair/2026-08-24/</c> - provenance in that folder's README), which is the only
/// thing that shows what a modulated signal's spectrum actually does line to line. The
/// hand-painted lines the rest of the detector's tests use are flat rectangles: every bin of
/// the run stands the same distance over the floor on every line, so the floor's "is this bin
/// measuring noise" test is never asked a hard question. A real 300 baud signal asks it thirty
/// times a second, and it answered wrong until 2026-08-24, when the station wrote a 4.2 second
/// capture of a transmission that was still going when the file ended.
/// </summary>
/// <remarks>
/// The capture holds 0.6 s of channel noise and then 3.6 s of signal still at full strength in
/// the last sample of the file. Its spectrum lines are replayed to stand in for the longer
/// transmission it was cut from: the noise lines to seed a floor, then the signal's own lines
/// round and round. Replaying lines rather than looping the audio keeps the splice out of the
/// measurement - a joined-up copy of the waveform clicks, and a click is a burst.
/// </remarks>
public class OffAirSurveyTests
{
    private const string Fixture = "samples/offair/2026-08-24/20260824-133727-1149hz-unclaimed.wav";
    private const int LinesPerSecond = 30;
    private const double BinWidthHz = 12000.0 / 2048;
    private const int LineLength = 1024;

    [Fact]
    public void A_Sustained_Off_Air_Signal_Is_One_Burst_For_As_Long_As_It_Transmits()
    {
        // Before the fix this reported the first three seconds and then went blind: the floor
        // climbed 13.6 dB into the signal in half a minute, because a bin of a modulated signal
        // spends much of its time between 0 and 6 dB over the noise and every one of those
        // lines was averaged into the floor as if the channel were quiet. What reached the
        // operator was a WAV that cut off mid-transmission and a duration in the sidecar that
        // was a third of the truth.
        (List<byte[]> noise, List<byte[]> signal) = FixtureLines();
        var bursts = new List<SurveyBurst>();
        var detector = new SpectralBurstDetector(
            BinWidthHz, LinesPerSecond, LineLength, bursts.Add, maxSeconds: 30);

        long line = Feed(detector, noise, 0, 20 * LinesPerSecond);
        line = Feed(detector, signal, line, 20 * LinesPerSecond);
        Feed(detector, noise, line, 2 * LinesPerSecond);

        SurveyBurst burst = bursts.Should().ContainSingle(
            "one transmission is one burst, however long it runs").Subject;
        burst.EndedOnTimeout.Should().BeFalse();
        (burst.EndLine - burst.StartLine).Should().BeGreaterThan(
            19 * LinesPerSecond, "it was transmitting for all twenty seconds");
        burst.CentreHz.Should().BeApproximately(1140, 60);
        burst.PeakSnrDb.Should().BeGreaterThan(
            18, "the floor under it is the channel noise, which is where it started");
    }

    [Fact]
    public void The_Snr_Of_A_Sustained_Signal_Does_Not_Decay_As_It_Transmits()
    {
        // The same defect read off the number an operator sorts captures by, and the reason it
        // matters beyond the audio: a signal of unchanging strength measured against a floor
        // climbing into it reports weaker the longer it goes on. Two seconds of this one came
        // out at 21.5 dB and the same signal twenty seconds later at 14.6.
        (List<byte[]> noise, List<byte[]> signal) = FixtureLines();

        double brief = MeanSnrOfOneBurst(noise, signal, 2 * LinesPerSecond);
        double sustained = MeanSnrOfOneBurst(noise, signal, 22 * LinesPerSecond);

        sustained.Should().BeApproximately(
            brief, 2, "nothing about the signal changed between the second and the twentieth");
    }

    private static double MeanSnrOfOneBurst(List<byte[]> noise, List<byte[]> signal, int lines)
    {
        var bursts = new List<SurveyBurst>();
        var detector = new SpectralBurstDetector(
            BinWidthHz, LinesPerSecond, LineLength, bursts.Add, maxSeconds: 60);

        long line = Feed(detector, noise, 0, 20 * LinesPerSecond);
        line = Feed(detector, signal, line, lines);
        Feed(detector, noise, line, 2 * LinesPerSecond);

        return bursts.Should().ContainSingle().Subject.MeanSnrDb;
    }

    /// <summary>Feeds <paramref name="count"/> lines from <paramref name="pool"/>, round and
    /// round, and returns the next line index.</summary>
    private static long Feed(SpectralBurstDetector detector, List<byte[]> pool, long from, int count)
    {
        for (int i = 0; i < count; i++)
        {
            detector.AddLine(from + i, pool[i % pool.Count]);
        }

        return from + count;
    }

    /// <summary>The capture's own spectrum lines: the signal's, and a pool of noise to seed a
    /// floor and to close the transmission with.</summary>
    /// <remarks>
    /// The capture holds only about a third of a second of noise clear of the signal - one line's
    /// worth of it eleven times over, where a per-bin floor wants many independent readings of
    /// the same noise. So the noise is laid end to end with the sign of every other copy flipped
    /// (a join that does not click, and a different realisation of the same noise each time) and
    /// run through the same transform, which is what a station listening to that channel for ten
    /// seconds would have had.
    /// </remarks>
    private static (List<byte[]> Noise, List<byte[]> Signal) FixtureLines()
    {
        (float[] samples, int rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Fixture));
        rate.Should().Be(12000);

        // Line i covers the 170 ms ending at (i + 1) / 30 s. The signal starts at 0.58 s, so
        // the first window clear of the transition begins after 0.75; give it another 80 ms for
        // the signal to come fully up.
        List<byte[]> signal = Lines(samples, rate).GetRange(24, (samples.Length / 400) - 24);

        int noiseLength = (int)(0.55 * rate);
        var random = new Random(20260824);
        var noiseAudio = new float[10 * rate];
        for (int i = 0; i < noiseAudio.Length; i++)
        {
            noiseAudio[i] = samples[i % noiseLength] * (random.Next(2) == 0 ? 1f : -1f);
        }

        List<byte[]> noise = Lines(noiseAudio, rate);
        noise.RemoveRange(0, 10);   // the transform's own fill

        noise.Count.Should().BeGreaterThan(200);
        signal.Count.Should().BeGreaterThan(LinesPerSecond * 3);
        return (noise, signal);
    }

    private static List<byte[]> Lines(float[] audio, int rate)
    {
        var lines = new List<byte[]>();
        var source = new WaterfallSource(
            rate, (_, line) => lines.Add(line.ToArray()), LinesPerSecond, 2048);
        source.Process(audio);
        return lines;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
