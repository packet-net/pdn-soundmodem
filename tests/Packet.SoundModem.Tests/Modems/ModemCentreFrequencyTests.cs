using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Where each mode sits in the audio band, as the catalogue declares it.
/// </summary>
/// <remarks>
/// The declaration exists because the number is used to choose a dial frequency, and a spectrum
/// estimate's few-Hz error would land in that answer. But a declaration nobody checks is a comment:
/// these tests measure each modem's actual spectrum and hold the declaration to it, so a modem that
/// moves its centre cannot leave the catalogue - and every band plan built on it - behind.
/// </remarks>
public class ModemCentreFrequencyTests
{
    /// <summary>Half an FFT bin at 48 kHz/4096, plus room for an asymmetric spectrum.</summary>
    private const double ToleranceHz = 30;

    private static double MeasuredCentre(string mode, double? requestedCentre = null)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        IModem modem = ModemCatalog.Create(
            mode, rate, static _ => { }, new ModemOptions(CentreFrequencyHz: requestedCentre));
        ModemBandProbe.TryMeasure(modem, rate, out double low, out double high)
            .Should().BeTrue($"{mode} has to be measurable to be checked");
        return (low + high) / 2;
    }

    [Theory]
    [InlineData("ms110d-wn4")]
    [InlineData("ms110d-wn0")]
    [InlineData("ms110d-wn13")]
    [InlineData("freedv-datac1")]
    [InlineData("freedv-datac3")]
    [InlineData("freedv-datac4")]
    [InlineData("freedv-datac13")]
    [InlineData("freedv-datac14")]
    public void A_Spec_Fixed_Mode_Sits_Where_The_Catalogue_Says_It_Does(string mode)
    {
        // These are the ones that matter most: their centres cannot be overridden, so a band plan
        // has no way to correct a wrong declaration - it would put the dial in the wrong place and
        // the modem would simply be somewhere else on the band than the operator asked for.
        double? declared = ModemCatalog.DefaultCentreFrequencyFor(mode);

        declared.Should().NotBeNull($"{mode} has a centre fixed by its own standard");
        MeasuredCentre(mode).Should().BeApproximately(declared!.Value, ToleranceHz);
    }

    [Theory]
    [InlineData("afsk1200")]
    [InlineData("afsk300")]
    [InlineData("bpsk300")]
    [InlineData("qpsk2400")]
    [InlineData("qpsk3600")]
    public void A_Variable_Centre_Mode_Declares_The_Default_It_Actually_Uses(string mode)
    {
        double? declared = ModemCatalog.DefaultCentreFrequencyFor(mode);

        declared.Should().NotBeNull();
        MeasuredCentre(mode).Should().BeApproximately(declared!.Value, ToleranceHz,
            "the declared default has to be the one Create applies when no centre is given");
    }

    [Fact]
    public void A_Variable_Centre_Mode_Follows_An_Override()
    {
        // The declaration is only the default; where one is given, that is the centre.
        MeasuredCentre("bpsk300", requestedCentre: 2200).Should().BeApproximately(2200, ToleranceHz);
    }

    [Theory]
    [InlineData("fsk9600")]
    [InlineData("fsk4800-il2p")]
    [InlineData("c4fsk9600")]
    [InlineData("c4fsk19200")]
    public void A_Baseband_Mode_Declares_No_Centre_At_All(string mode)
    {
        // Not an oversight: these occupy the audio band from DC upwards, so there is no centre to
        // place them by - which is exactly what the band planner needs to know to refuse the job
        // rather than invent a number.
        ModemCatalog.DefaultCentreFrequencyFor(mode).Should().BeNull();
    }

    [Fact]
    public void Every_Known_Mode_Either_Declares_A_Centre_Or_Is_Baseband()
    {
        // A new mode added to the catalogue without a centre would silently become "baseband" and
        // be refused a band plan for the wrong reason.
        string[] undeclared = [.. ModemCatalog.KnownModes
            .Where(m => ModemCatalog.DefaultCentreFrequencyFor(m) is null)
            .Where(m => !m.StartsWith("fsk", StringComparison.Ordinal)
                        && !m.StartsWith("c4fsk", StringComparison.Ordinal))];

        undeclared.Should().BeEmpty(
            "a mode with a centre but no declaration cannot be placed on the band");
    }
}
