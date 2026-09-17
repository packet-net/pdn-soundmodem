using AwesomeAssertions;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The bytes <see cref="Cm108Ptt"/> puts on the wire, pinned against the C-Media report layout
/// that Hamlib's `cm108.c` quotes and Dire Wolf's `cm108_write` sends: { report-id, write-GPIO,
/// output values, data-direction register, SPDIF }.
/// </summary>
/// <remarks>
/// <para>Nothing here needs a CM108. The class writes its report to a <see cref="FileStream"/>,
/// so a plain file stands in for the hidraw node and the test reads back what a dongle would
/// have been sent.</para>
/// <para>Why this suite exists at all: the data and mask bytes were the wrong way round from the
/// class being written until 2026-09-17, and no test caught it because there was no test. The
/// keyed report is symmetric (both bytes carry the same pin bit), so the fault was invisible on
/// the assert and showed up only as a release that sets the direction register to 0 - pin to
/// input, floating, rather than driven low. Assert on the unkey, or this passes while swapped.
/// </para>
/// </remarks>
public class Cm108PttTests
{
    private const byte Gpio3 = 0x04;

    /// <summary>
    /// A hidraw node stands in as a file, with the reports the class wrote read back in order.
    /// </summary>
    private static byte[][] ReportsFrom(Action<Cm108Ptt> use, int gpio = 3)
    {
        using var scratch = new ScratchDirectory("cm108-ptt-tests");
        string path = Path.Combine(scratch.FullName, "hidraw");
        File.WriteAllBytes(path, []);

        using (var ptt = new Cm108Ptt(path, gpio))
        {
            use(ptt);
        }

        return File.ReadAllBytes(path).Chunk(5).ToArray();
    }

    [Fact]
    public void A_Keyup_Drives_The_Pin_High_With_The_Direction_Register_Set_To_Output()
    {
        byte[][] reports = ReportsFrom(static ptt => ptt.Key());

        // Construction de-asserts, then the keyup, then the de-assert Dispose does.
        reports.Should().HaveCount(3);
        reports[1].Should().Equal([0x00, 0x00, Gpio3, Gpio3, 0x00]);
    }

    [Fact]
    public void An_Unkey_Drives_The_Pin_Low_Rather_Than_Turning_It_Back_Into_An_Input()
    {
        byte[][] reports = ReportsFrom(static ptt =>
        {
            ptt.Key();
            ptt.Unkey();
        });

        // The whole point of the suite: data 0x00 with the direction register still 0x04. The
        // swapped ordering writes 0x04, 0x00 here, which floats the pin instead of pulling it
        // down, and on a board with no gate pull-down the radio stays keyed.
        reports.Should().HaveCount(4);
        reports[2].Should().Equal([0x00, 0x00, 0x00, Gpio3, 0x00]);
    }

    [Fact]
    public void The_Device_Is_Left_Unkeyed_When_The_Line_Is_Disposed()
    {
        byte[][] reports = ReportsFrom(static ptt => ptt.Key());

        reports[^1].Should().Equal([0x00, 0x00, 0x00, Gpio3, 0x00]);
    }

    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(3, 0x04)]
    [InlineData(8, 0x80)]
    public void The_Pin_Number_Selects_One_Bit_Of_The_Gpio_Register(int gpio, byte bit)
    {
        byte[][] reports = ReportsFrom(ptt => ptt.Key(), gpio);

        reports[1].Should().Equal([0x00, 0x00, bit, bit, 0x00]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void A_Pin_Outside_The_Chip_Is_Refused_Before_The_Device_Is_Opened(int gpio)
    {
        using var scratch = new ScratchDirectory("cm108-ptt-tests");
        string path = Path.Combine(scratch.FullName, "hidraw");

        Action open = () => new Cm108Ptt(path, gpio).Dispose();

        // Before, not after: the path does not exist, so a range check that ran second would
        // report a missing device rather than the setting that is actually wrong.
        open.Should().Throw<ArgumentOutOfRangeException>();
    }
}
