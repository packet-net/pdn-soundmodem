namespace Packet.SoundModem.Windows.Tests;

public class Cm108ReportTests
{
    [Fact]
    public void Keying_gpio3_sets_data_and_direction_to_the_pin()
    {
        byte mask = Cm108Report.Mask(3);
        Cm108Report.Build(asserted: true, mask, 5).Should().Equal(0x00, 0x00, 0x04, 0x04, 0x00);
    }

    [Fact]
    public void Unkeying_drives_the_pin_low_rather_than_releasing_it()
    {
        // Data 0 with the direction still 1: the pin stays an output, driven low. Writing the
        // direction as 0 instead would float it, which is the Linux bug Cm108Ptt documents.
        byte mask = Cm108Report.Mask(3);
        Cm108Report.Build(asserted: false, mask, 5).Should().Equal(0x00, 0x00, 0x00, 0x04, 0x00);
    }

    [Fact]
    public void The_report_is_padded_to_the_collection_output_length()
    {
        Cm108Report.Build(true, Cm108Report.Mask(1), 9).Should().HaveCount(9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Gpio_pins_outside_1_to_8_are_refused(int gpio)
    {
        FluentActions.Invoking(() => Cm108Report.Mask(gpio)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
