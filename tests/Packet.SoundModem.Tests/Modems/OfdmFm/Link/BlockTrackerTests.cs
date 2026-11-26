using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class BlockTrackerTests
{
    [Fact]
    public void The_Times_And_Delays_Of_Each_Vector_Match_The_Prototype_To_A_Picosecond()
    {
        var found = TrackedVectors.Cases.Value.Where(c => c.Tracker != null).ToArray();
        found.Length.Should().Be(3);
        double largest = 0;
        foreach (TrackedCase c in found)
        {
            TrackerVector v = c.Tracker!;
            var tracker = new BlockTracker(v.Reference.Length);
            var times = new double[tracker.MaxBlocks];
            var delays = new double[tracker.MaxBlocks];
            int m = tracker.Track(v.Capture, v.Reference, times, delays);
            m.Should().Be(v.Times.Length, c.Name);
            for (int i = 0; i < m; i++)
            {
                double t = Math.Abs(times[i] - v.Times[i]);
                double d = Math.Abs(delays[i] - v.Delays[i]);
                largest = Math.Max(largest, Math.Max(t, d));
                t.Should().BeLessThan(1e-12, $"{c.Name} block {i} time");
                d.Should().BeLessThan(1e-12, $"{c.Name} block {i} delay");
            }
        }

        largest.Should().BeLessThan(1e-12);
    }

    [Fact]
    public void A_Known_Delay_Is_Found_Relative_To_The_Mean()
    {
        // a burst-like reference (band-limited noise), the capture the same read late from a source read later by a ramp of 0 to 20 microseconds
        var rng = new Random(5);
        int n = 20000;
        var reference = new double[n];
        var samples = new float[n];
        var raw = new float[n + 200];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (float)(rng.NextDouble() - 0.5);
        }

        for (int i = 0; i < n; i++)
        {
            reference[i] = i < 1000 ? 0 : raw[i + 100];
        }

        var knots = new[] { 0.0, n / (double)BurstRenderer.SampleRate };
        var delays = new[] { 0.0, 20e-6 };
        var shifted = new double[n];
        // the capture reads the reference's source later by the ramp
        DelayResampler.Resample(raw, 100, n, knots, delays, 0, shifted);
        for (int i = 0; i < n; i++)
        {
            samples[i] = i < 1000 ? 0 : (float)shifted[i];
        }

        var tracker = new BlockTracker(n);
        var times = new double[tracker.MaxBlocks];
        var tau = new double[tracker.MaxBlocks];
        int m = tracker.Track(samples, reference, times, tau);
        m.Should().BeGreaterThan(40);
        // reading the source later makes the signal arrive earlier, so the delay falls by about the ramp's span
        tau[0].Should().BeGreaterThan(tau[m - 1] + 8e-6);
        tau.AsSpan(0, m).ToArray().Average().Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void A_Reference_With_No_Room_For_A_Block_Gives_Nothing()
    {
        var tracker = new BlockTracker(4096);
        var times = new double[tracker.MaxBlocks];
        var delays = new double[tracker.MaxBlocks];
        tracker.Track(new float[500], new double[500], times, delays).Should().Be(0);
    }
}
