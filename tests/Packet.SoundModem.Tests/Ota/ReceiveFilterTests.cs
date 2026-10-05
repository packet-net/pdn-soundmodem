using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The filter-study's receive-filter models: both put their -6 dB points where the spec string
/// says, the way a rig's SSB filter width is quoted, and the crystal is symmetric about its
/// centre as an IF filter is.
/// </summary>
public class ReceiveFilterTests
{
    [Theory]
    [InlineData("xtal:300-2700")]
    [InlineData("xtal:150-3150")]
    [InlineData("dsp:300-2700")]
    [InlineData("dsp:200-2900")]
    public void Minus_6_dB_Points_Land_On_The_Stated_Edges(string spec)
    {
        var filter = ReceiveFilter.Parse(spec, 9600);
        (double lo, double hi) = filter.Edges(-6);
        lo.Should().BeApproximately(filter.LowHz, 3);
        hi.Should().BeApproximately(filter.HighHz, 3);
        filter.GainDb((filter.LowHz + filter.HighHz) / 2).Should().BeInRange(-0.6, 0.01);
    }

    [Fact]
    public void Crystal_Skirts_Are_Symmetric_About_Its_Centre()
    {
        var filter = ReceiveFilter.Parse("xtal:600-3000", 9600);
        filter.GainDb(1800 - 1500).Should().BeApproximately(filter.GainDb(1800 + 1500), 0.5);
        filter.GainDb(3500).Should().BeLessThan(-50);
    }

    [Fact]
    public void Filtering_A_Passband_Tone_Keeps_Its_Level()
    {
        var filter = ReceiveFilter.Parse("xtal:300-2700", 9600);
        var tone = new float[9600];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * 1500 * i / 9600.0);
        }

        float[] output = filter.Apply(tone);
        double inPower = 0, outPower = 0;
        for (int i = 4800; i < tone.Length; i++)
        {
            inPower += tone[i] * (double)tone[i];
            outPower += output[i] * (double)output[i];
        }

        (10 * Math.Log10(outPower / inPower)).Should().BeApproximately(filter.GainDb(1500), 0.1);
    }

    [Fact]
    public void An_Unknown_Filter_Spec_Is_Refused()
    {
        Action parse = () => ReceiveFilter.Parse("butterworth:300-2700", 9600);
        parse.Should().Throw<ArgumentException>();
    }
}
