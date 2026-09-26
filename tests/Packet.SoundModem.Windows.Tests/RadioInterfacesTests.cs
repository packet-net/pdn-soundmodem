namespace Packet.SoundModem.Windows.Tests;

public class RadioInterfacesTests
{
    private static readonly Guid Aioc = Guid.NewGuid();
    private static readonly Guid Cm108 = Guid.NewGuid();

    private static AudioEndpoint Endpoint(string device, AudioFlow flow, Guid? container) =>
        new($"id-{device}-{flow}", $"{flow} ({device})", device, flow, container);

    [Fact]
    public void An_aiocs_audio_hid_and_serial_port_become_one_interface()
    {
        var hid = new HidDevice("hid-path", HidDevice.AiocVendorId, HidDevice.AiocProductId, "AIOC HID", 5, Aioc);

        IReadOnlyList<RadioInterface> found = RadioInterfaceGrouping.Group(
            [Endpoint("AIOC Audio", AudioFlow.Capture, Aioc)],
            [Endpoint("AIOC Audio", AudioFlow.Render, Aioc)],
            [hid],
            [(Aioc, "COM8")]);

        RadioInterface aioc = found.Should().ContainSingle().Subject;
        aioc.Kind.Should().Be(RadioInterfaceKind.Aioc);
        aioc.IsComplete.Should().BeTrue();
        aioc.Hid.Should().Be(hid);
        aioc.SerialPort.Should().Be("COM8");
        aioc.SuggestedPtt.Should().Be(PttMethod.Cm108Hid);
    }

    [Fact]
    public void Radio_interfaces_sort_ahead_of_built_in_sound_cards()
    {
        IReadOnlyList<RadioInterface> found = RadioInterfaceGrouping.Group(
            [Endpoint("Realtek", AudioFlow.Capture, RadioInterfaceGrouping.MachineContainer), Endpoint("USB PnP", AudioFlow.Capture, Cm108)],
            [Endpoint("Realtek", AudioFlow.Render, RadioInterfaceGrouping.MachineContainer), Endpoint("USB PnP", AudioFlow.Render, Cm108)],
            [new HidDevice("p", HidDevice.CMediaVendorId, 0x0012, null, 5, Cm108)],
            []);

        found.Select(r => r.Kind).Should().Equal(RadioInterfaceKind.Cm108, RadioInterfaceKind.SoundCard);
    }

    [Fact]
    public void Built_in_devices_are_never_paired_with_a_hid_or_port_by_sharing_the_machine_container()
    {
        IReadOnlyList<RadioInterface> found = RadioInterfaceGrouping.Group(
            [Endpoint("Realtek", AudioFlow.Capture, RadioInterfaceGrouping.MachineContainer)],
            [Endpoint("Realtek", AudioFlow.Render, RadioInterfaceGrouping.MachineContainer)],
            [new HidDevice("p", HidDevice.CMediaVendorId, 0x0012, null, 5, RadioInterfaceGrouping.MachineContainer)],
            [(RadioInterfaceGrouping.MachineContainer, "COM1")]);

        RadioInterface card = found.Should().ContainSingle().Subject;
        card.Hid.Should().BeNull();
        card.SerialPort.Should().BeNull();
        card.SuggestedPtt.Should().Be(PttMethod.None);
    }

    [Fact]
    public void A_hid_too_small_for_the_gpio_report_is_not_offered_for_ptt()
    {
        IReadOnlyList<RadioInterface> found = RadioInterfaceGrouping.Group(
            [Endpoint("USB PnP", AudioFlow.Capture, Cm108)],
            [Endpoint("USB PnP", AudioFlow.Render, Cm108)],
            [new HidDevice("p", HidDevice.CMediaVendorId, 0x0012, null, 2, Cm108)],
            []);

        found.Single().Hid.Should().BeNull();
    }
}
