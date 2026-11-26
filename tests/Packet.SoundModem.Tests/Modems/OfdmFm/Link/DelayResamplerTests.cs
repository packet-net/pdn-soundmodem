using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class DelayResamplerTests
{
    [Fact]
    public void The_Output_Of_Each_Vector_Matches_The_Prototype_To_A_Billionth()
    {
        var found = TrackedVectors.Cases.Value.Where(c => c.Resample != null).ToArray();
        found.Select(c => c.Resample!.Variant).Should().Contain(["symbols", "fine"]);
        double largest = 0;
        foreach (TrackedCase c in found)
        {
            ResampleVector v = c.Resample!;
            var output = new double[v.N];
            DelayResampler.Resample(c.Capture, v.At, v.N, v.Knots, v.Delays, v.Lead, output);
            int at = 0;
            for (int i = 0; i < v.N; i++)
            {
                if (!v.Kept(i))
                {
                    continue;
                }

                double d = Math.Abs(output[i] - v.Output[at++]);
                largest = Math.Max(largest, d);
                d.Should().BeLessThan(1e-9, $"{c.Name} sample {i}");
            }

            at.Should().Be(v.Output.Length, c.Name);
            var rounded = new float[v.N];
            DelayResampler.Resample(c.Capture, v.At, v.N, v.Knots, v.Delays, v.Lead, rounded);
            for (int i = 0; i < v.N; i += 97)
            {
                rounded[i].Should().Be((float)output[i], c.Name);
            }
        }

        largest.Should().BeLessThan(1e-9);
    }

    [Fact]
    public void No_Delay_Returns_The_Capture_And_The_Ends_Read_The_Capture_Edges()
    {
        var capture = new float[300];
        for (int i = 0; i < capture.Length; i++)
        {
            capture[i] = MathF.Sin(i * 0.3f) + (i * 0.001f);
        }

        var output = new double[200];
        DelayResampler.Resample(capture, 50, 200, [0.0], [0.0], 10, output);
        for (int i = 0; i < output.Length; i++)
        {
            output[i].Should().BeApproximately(capture[50 + i], 1e-12);
        }

        // reading before the capture and after it takes its first and last samples
        var edges = new double[40];
        DelayResampler.Resample(capture, -60, 40, [0.0], [0.0], 10, edges);
        float firstSample = capture[0];
        edges.Should().OnlyContain(x => Math.Abs(x - firstSample) < 1e-9);
        DelayResampler.Resample(capture, 400, 40, [0.0], [0.0], 10, edges);
        float lastSample = capture[capture.Length - 1];
        edges.Should().OnlyContain(x => Math.Abs(x - lastSample) < 1e-9);
    }

    [Fact]
    public void The_Curve_Is_Flat_Outside_Its_Knots_And_Linear_Between_Them()
    {
        int segment = 0;
        double[] xs = [1.0, 2.0, 4.0];
        double[] ys = [10.0, 20.0, 0.0];
        DelayResampler.Interpolate(0.5, xs, ys, ref segment).Should().Be(10.0);
        DelayResampler.Interpolate(1.5, xs, ys, ref segment).Should().BeApproximately(15.0, 1e-12);
        DelayResampler.Interpolate(2.0, xs, ys, ref segment).Should().Be(20.0);
        DelayResampler.Interpolate(3.0, xs, ys, ref segment).Should().BeApproximately(10.0, 1e-12);
        DelayResampler.Interpolate(4.0, xs, ys, ref segment).Should().Be(0.0);
        DelayResampler.Interpolate(9.0, xs, ys, ref segment).Should().Be(0.0);
    }

    [Fact]
    public void A_Delay_Of_A_Whole_Sample_Reads_That_Sample_Later()
    {
        var capture = new float[300];
        for (int i = 0; i < capture.Length; i++)
        {
            capture[i] = MathF.Cos(i * 0.2f);
        }

        var output = new double[100];
        double delay = 3.0 / BurstRenderer.SampleRate;
        DelayResampler.Resample(capture, 100, 100, [0.0], [delay], 0, output);
        for (int i = 0; i < output.Length; i++)
        {
            output[i].Should().BeApproximately(capture[103 + i], 1e-9);
        }
    }

    [Fact]
    public void Empty_Input_Is_Refused()
    {
        var output = new double[10];
        Action noKnots = () => DelayResampler.Resample(new float[100], 0, 10, [], [], 0, output);
        noKnots.Should().Throw<ArgumentException>();
        Action noCapture = () => DelayResampler.Resample([], 0, 10, [0.0], [0.0], 0, output);
        noCapture.Should().Throw<ArgumentException>();
        Action small = () => DelayResampler.Resample(new float[100], 0, 11, [0.0], [0.0], 0, output);
        small.Should().Throw<ArgumentException>();
    }
}
