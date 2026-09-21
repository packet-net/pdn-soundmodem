using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The two defects issue #518 found in the C4FSK receive path on a real FM bench, neither of
/// which a clean loopback or a simulated channel can reach.
/// </summary>
public class C4fskOnAirTests
{
    private const int Rate = 48000;

    /// <summary>
    /// The envelope tracker's runaway. <c>TrackEnvelope</c> moves whichever outer peak the
    /// decision names toward the reading it was decided from, and had nothing stopping that
    /// reading being on the far side of the midpoint: once <c>_peakHigh</c> crosses
    /// <c>_peakLow</c> the half-swing clamps to its floor, every normalised value rails, every
    /// decision goes outer, and the burst is dead from there on. Measured on a real off-air
    /// burst with the gate forced open, the reported peak went +0.23 -> +0.08 -> -3.1 -> -43 ->
    /// -33173 about four seconds in. A baseline that wanders far enough reproduces it without a
    /// recording, which is what this drives.
    /// </summary>
    [Fact]
    public void The_Outer_Envelope_Never_Inverts_However_Far_The_Baseline_Wanders()
    {
        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            foreach (double step in new double[] { 6, 12, 20 })
            {
                (float smallestSwing, float largestSwing) = SwingExtremes(mode, step);

                smallestSwing.Should().BeGreaterThan(
                    0,
                    $"{mode} across a {step:0} dB step in receiver noise must not let the upper "
                    + "outer peak cross the lower one, whatever the decisions say");
                largestSwing.Should().BeLessThan(
                    10,
                    $"{mode} across a {step:0} dB step in receiver noise must not let the envelope "
                    + "run away upward either; the audio itself never exceeds full scale");
            }
        }
    }

    /// <summary>The smallest and largest outer swing the tracker holds at any phase-0 decision
    /// over audio built to reproduce the bench case: a stretch of receiver noise at one level
    /// followed by a big step up in that noise, which is exactly what the end of an FM
    /// transmission looks like to this gate and is where it opens on the real recording.</summary>
    private static (float Smallest, float Largest) SwingExtremes(string mode, double step)
    {
        float[] audio = NoiseStep(step, 11);
        float smallest = float.MaxValue;
        float largest = 0;
        C4fskModem modem = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(Rate, _ => { })
            : C4fskModem.C4fsk19200(Rate, _ => { });
        modem.DecisionObserver = d =>
        {
            if (d.Phase != 0)
            {
                return;
            }

            float swing = d.PeakHigh - d.PeakLow;
            smallest = Math.Min(smallest, swing);
            largest = Math.Max(largest, Math.Abs(swing));
        };
        WanderRig.Feed(audio, modem.Process);
        return (smallest, largest);
    }

    /// <summary>Half a second of noise, then three seconds of the same noise
    /// <paramref name="stepDb"/> louder.</summary>
    private static float[] NoiseStep(double stepDb, int seed)
    {
        var random = new Random(seed);
        var audio = new float[Rate * 7 / 2];
        int step = Rate / 2;
        float quiet = 0.02f;
        float loud = (float)(quiet * Math.Pow(10, stepDb / 20));
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double gaussian = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            audio[i] = (float)(gaussian * (i < step ? quiet : loud));
        }

        return audio;
    }
}

/// <summary>
/// Bench probe for issue #518, gated on a recording this repository does not carry: point
/// <c>C4FSK_ONAIR_WAV</c> at an off-air capture (radio1's own 48 kHz <c>rawCapture</c> will do)
/// and this reports what the receive path makes of it - how much of the file the energy gate
/// calls busy, where the block levels sit against the tracked floor, and what the envelope
/// tracker does once the gate is open. The gate and the envelope are the two things that were
/// wrong on the real bench and that no simulated channel reaches, so they are what it reads.
/// </summary>
public class C4fskOnAirRecordingProbe(ITestOutputHelper output)
{
    [Fact]
    public void What_The_Receive_Path_Makes_Of_A_Real_Recording()
    {
        string? path = Environment.GetEnvironmentVariable("C4FSK_ONAIR_WAV");
        Assert.SkipWhen(path is null || !File.Exists(path), "set C4FSK_ONAIR_WAV to a recording");

        (float[] audio, int rate) = Packet.SoundModem.Audio.WavFile.ReadMono(path!);
        output.WriteLine($"{path}: {audio.Length} samples at {rate} Hz, {audio.Length / (double)rate:0.0} s");

        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            var frames = new List<int>();
            C4fskModem modem = mode == "c4fsk9600"
                ? C4fskModem.C4fsk9600(rate, f => frames.Add(f.Length))
                : C4fskModem.C4fsk19200(rate, f => frames.Add(f.Length));
            float smallest = float.MaxValue;
            float largest = 0;
            long decisions = 0;
            long inverted = 0;
            modem.DecisionObserver = d =>
            {
                if (d.Phase != 0)
                {
                    return;
                }

                decisions++;
                float swing = d.PeakHigh - d.PeakLow;
                inverted += swing <= 0 ? 1 : 0;
                smallest = Math.Min(smallest, swing);
                largest = Math.Max(largest, Math.Abs(swing));
            };

            int block = rate / 50;
            int busyBlocks = 0;
            int blocks = 0;
            for (int pos = 0; pos < audio.Length; pos += block)
            {
                modem.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
                blocks++;
                busyBlocks += modem.ChannelBusy ? 1 : 0;
            }

            output.WriteLine(
                $"{mode}: frames {frames.Count}, channel busy {busyBlocks}/{blocks} blocks "
                + $"({100.0 * busyBlocks / blocks:0.0} %), decisions {decisions}, "
                + $"envelope swing smallest {smallest:0.0000} largest {largest:0.0000}, "
                + $"inverted at {inverted} decisions");
        }
    }
}
