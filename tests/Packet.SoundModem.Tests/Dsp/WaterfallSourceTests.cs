using AwesomeAssertions;
using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Tests.Dsp;

public class WaterfallSourceTests
{
    private static float[] Sine(int sampleRate, double frequency, double amplitude, int count)
    {
        var samples = new float[count];
        for (int n = 0; n < count; n++)
        {
            samples[n] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * n / sampleRate));
        }

        return samples;
    }

    [Theory]
    [InlineData(12000, 2048)]
    [InlineData(48000, 8192)]
    public void Emits_thirty_lines_per_second_at_the_default_sizes(int rate, int expectedFft)
    {
        int lines = 0;
        var source = new WaterfallSource(rate, (_, _) => lines++);

        source.Process(Sine(rate, 1000, 0.5, rate));

        lines.Should().Be(30);
        source.LineLength.Should().Be(expectedFft / 2);
        source.BinWidthHz.Should().BeApproximately(rate / (double)expectedFft, 1e-9);
        source.NextLineIndex.Should().Be(30);
    }

    [Fact]
    public void Line_indices_count_up_from_zero()
    {
        var indices = new List<long>();
        var source = new WaterfallSource(12000, (index, _) => indices.Add(index));

        source.Process(Sine(12000, 1000, 0.5, 12000 / 30 * 3));

        indices.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void A_tone_lands_in_its_bin()
    {
        byte[]? line = null;
        var source = new WaterfallSource(12000, (_, l) => line = l.ToArray());

        source.Process(Sine(12000, 1500, 0.5, 12000));

        line.Should().NotBeNull();
        int peakBin = Array.IndexOf(line!, line!.Max());
        double peakHz = peakBin * source.BinWidthHz;
        peakHz.Should().BeApproximately(1500, source.BinWidthHz);
    }

    [Fact]
    public void A_full_scale_sine_reads_near_the_top_of_the_byte_scale()
    {
        byte[]? line = null;
        var source = new WaterfallSource(12000, (_, l) => line = l.ToArray());

        source.Process(Sine(12000, 1500, 1.0, 12000));

        line!.Max().Should().BeGreaterThanOrEqualTo(250);
    }

    [Fact]
    public void Silence_reads_at_the_byte_floor()
    {
        byte[]? line = null;
        var source = new WaterfallSource(12000, (_, l) => line = l.ToArray());

        source.Process(new float[12000]);

        line!.Max().Should().Be(0);
    }

    [Fact]
    public void Overlapping_transforms_show_a_one_hop_burst_in_several_lines()
    {
        // A burst exactly one hop (400 samples) long sits inside ~5 overlapping 2048-point
        // windows, so it must light its bin in more than one consecutive line.
        var linesWithEnergy = new List<long>();
        var source = new WaterfallSource(12000, (index, line) =>
        {
            if (line.ToArray().Max() > 100)
            {
                linesWithEnergy.Add(index);
            }
        });

        source.Process(new float[6000]);
        source.Process(Sine(12000, 1500, 0.9, 400));
        source.Process(new float[6000]);

        linesWithEnergy.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Rejects_invalid_construction()
    {
        Action badRate = () => new WaterfallSource(12000, (_, _) => { }, linesPerSecond: 7);
        Action badFft = () => new WaterfallSource(12000, (_, _) => { }, fftSize: 3000);
        Action shortFft = () => new WaterfallSource(12000, (_, _) => { }, linesPerSecond: 30, fftSize: 256);

        badRate.Should().Throw<ArgumentException>();
        badFft.Should().Throw<ArgumentException>();
        shortFft.Should().Throw<ArgumentException>();
    }
}
