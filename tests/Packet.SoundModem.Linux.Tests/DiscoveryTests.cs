namespace Packet.SoundModem.Linux.Tests;

public class DiscoveryTests
{
    [Fact]
    public void An_aioc_is_one_interface_with_its_card_hidraw_and_serial_port()
    {
        var tree = new FakeDeviceTree().Aioc().Cards(Trees.AiocCmediaOnboardCards);

        RadioInterface aioc = SysfsDiscovery.Discover(tree).Should().ContainSingle().Subject;

        aioc.Name.Should().Be("AIOC All-In-One-Cable");
        aioc.Kind.Should().Be(RadioInterfaceKind.Aioc);
        aioc.Card!.CapturePcm.Should().Be("plughw:CARD=AllInOneCable,DEV=0");
        aioc.Card.PlaybackPcm.Should().Be("plughw:CARD=AllInOneCable,DEV=0");
        aioc.Card.Mixer.Should().Be("hw:CARD=AllInOneCable");
        aioc.Hidraw.Should().Be("/dev/hidraw0");
        aioc.SerialPort.Should().Be("/dev/serial/by-id/usb-AIOC_All-In-One-Cable_d4c9081b-if04");
        aioc.Key.Should().Be("usb:1209:7388:d4c9081b");
        aioc.UsbDevice.Should().Be(Trees.AiocUsb);
        aioc.SuggestedPtt.Should().Be(PttMethod.Cm108Hid);
        aioc.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Radio_interfaces_sort_ahead_of_the_machines_own_card()
    {
        var tree = new FakeDeviceTree().Onboard().Cm108().Aioc().Cards(Trees.AiocCmediaOnboardCards);

        IReadOnlyList<RadioInterface> found = SysfsDiscovery.Discover(tree);

        found.Select(r => r.Kind).Should().Equal(RadioInterfaceKind.Aioc, RadioInterfaceKind.Cm108, RadioInterfaceKind.SoundCard);
        found.Select(r => r.Name).Should().Equal("AIOC All-In-One-Cable", "C-Media Electronics Inc. USB PnP Sound Device", "HDA Intel PCH");
    }

    [Fact]
    public void A_cm108_without_a_serial_number_is_known_by_the_port_it_is_in()
    {
        var tree = new FakeDeviceTree().Cm108().Cards(Trees.AiocCmediaOnboardCards);

        RadioInterface cm108 = SysfsDiscovery.Discover(tree).Should().ContainSingle().Subject;

        cm108.Key.Should().Be("usb:0d8c:0012@3-2");
        cm108.Hidraw.Should().Be("/dev/hidraw1");
        cm108.SerialPort.Should().BeNull();
        cm108.Card!.CapturePcm.Should().Be("plughw:CARD=Device,DEV=0");
    }

    [Fact]
    public void A_card_that_is_not_on_usb_is_known_by_its_id_and_has_no_ptt()
    {
        var tree = new FakeDeviceTree().Onboard().Cards(Trees.AiocCmediaOnboardCards);

        RadioInterface pch = SysfsDiscovery.Discover(tree).Should().ContainSingle().Subject;

        pch.Key.Should().Be("card:PCH");
        pch.Kind.Should().Be(RadioInterfaceKind.SoundCard);
        pch.Hidraw.Should().BeNull();
        pch.UsbDevice.Should().BeNull();
        pch.SuggestedPtt.Should().Be(PttMethod.None);
        pch.Card!.PlaybackDevice.Should().Be(0, "the lowest playback device, not HDMI on 3");
    }

    [Fact]
    public void Two_aiocs_keep_their_own_parts_and_are_told_apart_by_serial_number()
    {
        var tree = new FakeDeviceTree()
            .Aioc(card: 0, usb: "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-1", serial: "aaaa1111", hidraw: 0, acm: 0)
            .Aioc(card: 1, usb: "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-4", serial: "bbbb2222", hidraw: 1, acm: 1);

        IReadOnlyList<RadioInterface> found = SysfsDiscovery.Discover(tree);

        found.Should().HaveCount(2);
        RadioInterface second = found.Single(r => r.Key == "usb:1209:7388:bbbb2222");
        second.Card!.Number.Should().Be(1);
        second.Hidraw.Should().Be("/dev/hidraw1");
        second.SerialPort.Should().Be("/dev/serial/by-id/usb-AIOC_All-In-One-Cable_bbbb2222-if04");
    }

    [Fact]
    public void A_card_with_playback_only_is_not_complete()
    {
        var tree = new FakeDeviceTree()
            .Link("/sys/class/sound/card3", "/sys/devices/platform/hdmi/sound/card3")
            .File("/sys/devices/platform/hdmi/sound/card3/id", "HDMI")
            .Link("/sys/class/sound/pcmC3D0p", "/sys/devices/platform/hdmi/sound/card3/pcmC3D0p");

        RadioInterface hdmi = SysfsDiscovery.Discover(tree).Should().ContainSingle().Subject;

        hdmi.IsComplete.Should().BeFalse();
        hdmi.Card!.CapturePcm.Should().BeNull();
        hdmi.Name.Should().Be("HDMI", "with no /proc/asound/cards entry, the id is all there is");
    }

    [Fact]
    public void No_sound_cards_is_an_empty_list()
    {
        SysfsDiscovery.Discover(new FakeDeviceTree()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x0D8C, 0x0012, true)]
    [InlineData(0x0D8C, 0x013C, true)]
    [InlineData(0x1209, 0x7388, true)]
    [InlineData(0x1209, 0x0001, false)]
    [InlineData(0x046D, 0xC52B, false)]
    public void Cm108_compatibility_is_the_c_media_family_and_the_aioc(int vendor, int product, bool compatible)
    {
        SysfsDiscovery.IsCm108Compatible((ushort)vendor, (ushort)product).Should().Be(compatible);
    }

    [Fact]
    public void Card_names_come_from_proc_asound_cards()
    {
        Dictionary<int, (string ShortName, string LongName)> names = SysfsDiscovery.CardNames(Trees.AiocCmediaOnboardCards);

        names.Should().HaveCount(3);
        names[0].ShortName.Should().Be("All-In-One-Cable");
        names[0].LongName.Should().Be("AIOC All-In-One-Cable at usb-vhci_hcd.0-1, full speed");
        names[2].ShortName.Should().Be("HDA Intel PCH");
    }
}
