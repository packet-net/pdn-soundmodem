using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// Transmit power configuration: <c>"flex": { "txPowerWatts": 30 }</c>.
/// </summary>
/// <remarks>
/// <para>Watts, not the radio's 0–100 number, because watts is what an operator means and what
/// a licence is written in. Every 6000-series model has a 100 W PA, so the two coincide there —
/// the conversion exists to keep the config honest about its units, not because the arithmetic
/// is hard.</para>
/// <para>Why this is configured here at all rather than left to the rig: RF power is held per
/// station and only the client owning the transmit slice can set it. In a headless station that
/// client is the daemon, so with pdn-soundmodem running, an external tool's request is accepted
/// by the radio and discarded — measured on a FLEX-6500, fw 4.2.20.41343.</para>
/// </remarks>
public class FlexTransmitPowerTests
{
    [Theory]
    [InlineData(30.0, 30)]
    [InlineData(0.0, 0)]
    [InlineData(100.0, 100)]
    [InlineData(5.0, 5)]
    public void Watts_Convert_To_The_Radios_Power_Number(double watts, int expected) =>
        FlexDevice.ToRfPowerPercent(watts).Should().Be(expected);

    [Fact]
    public void A_Fractional_Watt_Rounds_Rather_Than_Truncating()
    {
        // The radio takes whole numbers. Truncating 29.7 W to 29 would quietly under-run the
        // request; half a watt of rounding is below anything downstream can tell apart.
        FlexDevice.ToRfPowerPercent(29.7).Should().Be(30);
        FlexDevice.ToRfPowerPercent(29.4).Should().Be(29);
        FlexDevice.ToRfPowerPercent(0.5).Should().Be(1, "away from zero, so a request is never rounded off");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    [InlineData(1000.0)]
    public void A_Power_The_Pa_Cannot_Produce_Is_Refused_Here_Not_At_The_Radio(double watts)
    {
        // The radio answers an out-of-range power with a bare protocol code and no hint about
        // which setting produced it.
        Action convert = () => FlexDevice.ToRfPowerPercent(watts);

        convert.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*0 and 100 W*");
    }

    [Fact]
    public void Unset_Leaves_The_Radios_Own_Power_Alone()
    {
        // Not zero, and not a default: a station that has never been told a power must transmit
        // at whatever the operator set at the rig, exactly as it did before this setting existed.
        var tuning = new FlexTuning();

        tuning.TxPowerWatts.Should().BeNull();
    }

    [Fact]
    public void The_Config_Carries_Watts_Through_To_The_Tuning()
    {
        var config = new FlexConfig { TxPowerWatts = 30 };

        var tuning = new FlexTuning { TxPowerWatts = config.TxPowerWatts };

        tuning.TxPowerWatts.Should().Be(30);
        FlexDevice.ToRfPowerPercent(tuning.TxPowerWatts!.Value).Should().Be(30);
    }

    [Fact]
    public void The_Pa_Size_Matches_What_The_Radio_Reports()
    {
        // slice N max_internal_pa_power read 100 off M0LTE's FLEX-6500 (fw 4.2.20.41343). Every
        // model in the family is the same; if that ever changes this constant is the thing to fix.
        FlexDevice.PaWatts.Should().Be(100.0);
    }
}
