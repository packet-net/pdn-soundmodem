using Packet.SoundModem.Audio;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// What the burst detector does where the receiver is delivering nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// A per-bin SNR is the right measurement for finding a weak signal in a noisy channel and it
/// stops being a measurement of anything once a bin has nothing in it. Above the live 40 m
/// station's slice receive filter (a 2550 Hz high cut) the audio is numerically empty: the
/// floor sits at exactly -100 dBFS, which is <see cref="WaterfallSource.FloorDb"/>, the bottom
/// of the byte scale. Any energy at all in such a bin is therefore an enormous SNR.
/// </para>
/// <para>
/// And there is energy: a break in the waveform - a splice, a repeated block, a phase step
/// where two buffers were joined - is broadband by construction and puts about -47 dBFS at every
/// frequency out to 6 kHz for exactly one transform window, while the 50 ms RMS envelope does
/// not move at all. In the passband that is invisible under the real signal. Above the cut it
/// reads as a 430 Hz burst at 37 dB SNR, and <b>3,433 of the station's 8,874 unclaimed captures
/// were it</b>, every one of them above the cut, because that is the only part of the spectrum
/// quiet enough for it to show.
/// </para>
/// </remarks>
public class EmptyBandTests
{
    /// <summary>One of the 3,433, committed with its sidecar.</summary>
    private const string Capture =
        "samples/offair/2026-08-23/20260823-131719-2921hz-unclaimed.wav";

    private const int LinesPerSecond = 30;

    [Fact]
    public void A_Break_In_The_Waveform_Above_The_Receivers_Cut_Is_Not_A_Burst()
    {
        // The station recorded this as 431 Hz wide at 37.1 dB SNR, centred 2921 Hz, lasting
        // 0.167 s. Everything in that sentence is arithmetic on an absence.
        (List<SurveyBurst> bursts, long deadBins) = Detect(Capture);

        bursts.Should().BeEmpty("there is nothing arriving above the receiver's high cut");
        deadBins.Should().BeGreaterThan(0, "and the detector should say it noticed");
    }

    [Fact]
    public void A_Real_Signal_In_The_Passband_Is_Still_Found()
    {
        // The other half of the bargain, and the one that makes the rule safe: refusing to
        // measure against nothing must not become refusing to measure. This is the PD4R-12
        // beacon, in the middle of the passband where the floor is a real noise measurement.
        (List<SurveyBurst> bursts, _) = Detect(
            "samples/offair/2026-08-24/20260824-152242-1134hz-unclaimed.wav");

        bursts.Should().NotBeEmpty();
        bursts.Should().OnlyContain(
            b => Math.Abs(b.CentreHz - 1130) < 60, "the beacon is where it always was");
        bursts.Max(b => b.PeakSnrDb).Should().BeGreaterThan(20);
        bursts.Sum(b => b.Lines).Should().BeGreaterThan(
            100, "most of the four and a half seconds of it is still seen");
    }

    [Fact]
    public void The_Shortest_Burst_Accepted_Is_The_One_The_Caller_Asked_For()
    {
        // Math.Round(0.15 * 30) is 4, not 5: 4.5 goes to even. So "at least 0.15 s" was really
        // "at least 0.133 s", and the two thirtieths of a second in the gap are exactly where a
        // one-window event lives. Ceiling is what the parameter has always said.
        var bursts = new List<SurveyBurst>();
        var detector = new SpectralBurstDetector(
            12000.0 / 2048, LinesPerSecond, 1024, bursts.Add, minSeconds: 0.15);

        long line = Warm(detector);
        byte[] signal = Line(lowHz: 1300, highHz: 1700);
        byte[] quiet = Line();

        // Four lines: 0.133 s, which used to pass.
        for (int i = 0; i < 4; i++)
        {
            detector.AddLine(line++, signal);
        }

        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().BeEmpty("0.133 s is less than the 0.15 s asked for");

        // Five lines: 0.167 s, the shortest thing that is genuinely at least 0.15 s.
        for (int i = 0; i < 5; i++)
        {
            detector.AddLine(line++, signal);
        }

        for (int i = 0; i < 30; i++)
        {
            detector.AddLine(line++, quiet);
        }

        bursts.Should().ContainSingle();
    }

    private static (List<SurveyBurst> Bursts, long DeadBins) Detect(string fixture)
    {
        (float[] samples, int rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), fixture));

        // A floor has to be seeded before a detection means anything, and it has to be seeded on
        // this capture's own spectrum - holes included. Tiling its quiet lead-in with a
        // crossfade does that; tiling it with random sign flips, which an earlier version of
        // this harness did, is a white-noise generator that fills every empty bin at the file's
        // own RMS and hides the very thing under test.
        float[] seeded = Seed(samples, (int)(0.8 * rate), 15 * rate);
        var audio = new float[seeded.Length + samples.Length];
        seeded.CopyTo(audio, 0);
        samples.CopyTo(audio, seeded.Length);

        var bursts = new List<SurveyBurst>();
        SpectralBurstDetector? detector = null;
        var source = new WaterfallSource(
            rate, (index, line) => detector!.AddLine(index, line.Span), LinesPerSecond, 2048);
        detector = new SpectralBurstDetector(
            source.BinWidthHz, source.LinesPerSecond, source.LineLength, bursts.Add,
            maxSeconds: 40);

        for (int at = 0; at < audio.Length; at += 480)
        {
            source.Process(audio.AsSpan(at, Math.Min(480, audio.Length - at)));
        }

        return (bursts, detector.DeadBins);
    }

    /// <summary>Repeats <paramref name="count"/> samples of <paramref name="source"/> up to
    /// <paramref name="total"/>, crossfading the joins so no join is itself an event.</summary>
    private static float[] Seed(float[] source, int count, int total)
    {
        var seeded = new float[total];
        int fade = Math.Min(count / 8, 480);
        int at = 0;
        while (at < total)
        {
            int take = Math.Min(count, total - at);
            for (int i = 0; i < take; i++)
            {
                float sample = source[i];
                if (at > 0 && i < fade)
                {
                    double weight = 0.5 * (1 - Math.Cos(Math.PI * i / fade));
                    seeded[at + i] = (float)((seeded[at + i] * (1 - weight)) + (sample * weight));
                }
                else
                {
                    seeded[at + i] = sample;
                }
            }

            at += take - (at + take < total ? fade : 0);
        }

        return seeded;
    }

    private static byte[] Line(double floorDb = -70, double? lowHz = null, double? highHz = null)
    {
        const double binWidthHz = 12000.0 / 2048;
        var line = new byte[1024];
        static byte ToByte(double db) => (byte)Math.Clamp(
            (db - WaterfallSource.FloorDb) * (255 / -WaterfallSource.FloorDb), 0, 255);

        line.AsSpan().Fill(ToByte(floorDb));
        if (lowHz is double low && highHz is double high)
        {
            int lowBin = (int)(low / binWidthHz);
            line.AsSpan(lowBin, (int)(high / binWidthHz) - lowBin).Fill(ToByte(-50));
        }

        return line;
    }

    private static long Warm(SpectralBurstDetector detector)
    {
        byte[] quiet = Line();
        for (int i = 0; i < 90; i++)
        {
            detector.AddLine(i, quiet);
        }

        return 90;
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
