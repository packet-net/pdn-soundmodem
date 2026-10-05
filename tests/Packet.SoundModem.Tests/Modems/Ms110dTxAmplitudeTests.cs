using Packet.SoundModem.Modems;
using Xunit;

namespace Packet.SoundModem.Tests.Modems;

public class Ms110dTxAmplitudeTests
{
    private static double Peak(double? amplitude)
    {
        IModem modem = ModemCatalog.Create("ms110d-wn4", 48000, _ => { }, new ModemOptions(TxAmplitude: amplitude));
        var frame = new byte[300];
        new Random(1).NextBytes(frame);
        return modem.Modulate(frame, 0).Max(x => Math.Abs(x));
    }

    [Fact]
    public void A_Level_Of_One_Doubles_The_Peak_Of_The_Default_And_Stays_Inside_Full_Scale()
    {
        double standard = Peak(null);
        double full = Peak(1.0);
        Assert.InRange(full / standard, 1.95, 2.05);
        Assert.InRange(full, 0.6, 0.9);
    }

    [Fact]
    public void The_Default_Is_Unchanged()
    {
        Assert.Equal(Peak(null), Peak(0.5), 6);
    }

    [Theory]
    [InlineData("bpsk300", 0.7)]
    [InlineData("ms110d-wn4", 0.0)]
    [InlineData("ms110d-wn4", 1.2)]
    public void A_Level_Is_Refused_Where_It_Cannot_Apply(string mode, double amplitude)
    {
        Assert.Throws<ArgumentException>(() => ModemCatalog.Create(mode, 48000, _ => { }, new ModemOptions(TxAmplitude: amplitude)));
    }
}
