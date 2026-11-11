using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

public sealed class IdentCentreCheckTests
{
    [Fact]
    public void Warns_when_the_tone_sits_300_Hz_above_the_data()
    {
        string? line = IdentCentreCheck.Warning(4, 1800, 1500, false, 7_052_300, true, true);

        Assert.Equal(
            "modem 4: WARNING - the CW ident is at 1800 Hz audio (7.054100 MHz) but the modem's signal is "
            + "centred at 1500 Hz (7.053800 MHz). Remove \"toneHz\" from \"identify\" to send it on the signal's centre.",
            line);
    }

    [Fact]
    public void Leaves_out_RF_figures_without_a_dial()
    {
        string? line = IdentCentreCheck.Warning(1, 1800, 1500, false, null, true, true);

        Assert.NotNull(line);
        Assert.DoesNotContain("MHz", line);
    }

    [Fact]
    public void Says_nothing_when_the_ident_defaults_to_the_centre()
        => Assert.Null(IdentCentreCheck.Warning(4, 1500, 1500, false, 7_052_300, true, false));

    [Theory]
    [InlineData(1550)]
    [InlineData(1450)]
    [InlineData(1500)]
    public void Says_nothing_within_50_Hz(double tone)
        => Assert.Null(IdentCentreCheck.Warning(4, tone, 1500, false, 7_052_300, true, true));

    [Fact]
    public void Says_nothing_on_FM()
        => Assert.Null(IdentCentreCheck.Warning(1, 1800, 1500, true, null, true, true));

    [Fact]
    public void Says_nothing_without_an_audio_centre()
        => Assert.Null(IdentCentreCheck.Warning(1, 1800, null, false, null, true, true));
}
