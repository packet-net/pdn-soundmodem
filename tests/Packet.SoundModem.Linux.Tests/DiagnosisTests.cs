namespace Packet.SoundModem.Linux.Tests;

public class DiagnosisTests
{
    private static RadioInterface Aioc(FakeDeviceTree tree) => SysfsDiscovery.Discover(tree).Single();

    [Fact]
    public void An_interface_whose_nodes_can_all_be_opened_has_no_access_problems()
    {
        var tree = new FakeDeviceTree().Aioc();

        DeviceAccess.Check(tree, Aioc(tree), PttMethod.Cm108Hid, null).Should().BeEmpty();
    }

    [Fact]
    public void A_root_only_hidraw_node_is_a_ptt_access_problem()
    {
        var tree = new FakeDeviceTree().Aioc().Refuse("/dev/hidraw0");

        AccessProblem problem = DeviceAccess.Check(tree, Aioc(tree), PttMethod.Cm108Hid, null).Should().ContainSingle().Subject;

        problem.Should().Be(new AccessProblem("/dev/hidraw0", DevicePurpose.Ptt, Missing: false));
    }

    [Fact]
    public void The_hidraw_node_does_not_matter_when_keying_by_serial()
    {
        var tree = new FakeDeviceTree().Aioc().Refuse("/dev/hidraw0").Refuse("/dev/serial/by-id/usb-AIOC_All-In-One-Cable_d4c9081b-if04");

        AccessProblem problem = DeviceAccess.Check(tree, Aioc(tree), PttMethod.Serial, null).Should().ContainSingle().Subject;

        problem.Purpose.Should().Be(DevicePurpose.Ptt);
        problem.Node.Should().Contain("by-id");
    }

    [Fact]
    public void A_sound_device_the_user_cannot_open_is_named_with_its_purpose()
    {
        var tree = new FakeDeviceTree().Aioc().Refuse("/dev/snd/pcmC0D0c").Refuse("/dev/snd/controlC0");

        DeviceAccess.Check(tree, Aioc(tree), PttMethod.None, null)
            .Select(p => p.Purpose).Should().Equal(DevicePurpose.Receive, DevicePurpose.Mixer);
    }

    [Fact]
    public void A_serial_port_that_is_not_there_is_missing_rather_than_refused()
    {
        var tree = new FakeDeviceTree().Aioc();

        AccessProblem problem = DeviceAccess.Check(tree, Aioc(tree), PttMethod.Serial, "/dev/ttyUSB7").Should().ContainSingle().Subject;

        problem.Missing.Should().BeTrue();
    }

    [Fact]
    public void A_busy_card_names_the_process_holding_it()
    {
        var tree = new FakeDeviceTree().Aioc()
            .File("/proc/asound/card0/pcm0c/sub0/status", "state: RUNNING\nowner_pid   : 1234\ntrigger_time: 123.456\n")
            .File("/proc/1234/comm", "pipewire\n");
        AlsaCard card = Aioc(tree).Card!;

        CardUsers.Holder(tree, card, capture: true).Should().Be("pipewire (pid 1234)");
        CardUsers.HolderProgram(tree, card, capture: true).Should().Be("pipewire");
    }

    [Fact]
    public void A_closed_pcm_has_no_holder()
    {
        var tree = new FakeDeviceTree().Aioc().File("/proc/asound/card0/pcm0p/sub0/status", "closed");

        CardUsers.Holder(tree, Aioc(tree).Card!, capture: false).Should().BeNull();
    }

    [Fact]
    public void Pipewire_nodes_are_found_by_card_whichever_way_the_card_is_spelled()
    {
        const string dump = """
            [
              { "id": 30, "type": "PipeWire:Interface:Device", "info": { "props": { "alsa.card": "0" } } },
              { "id": 41, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "alsa.card": "0", "media.class": "Audio/Source",
                  "node.name": "alsa_input.usb-AIOC_All-In-One-Cable_d4c9081b-00.mono-fallback" } } },
              { "id": 42, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "api.alsa.pcm.card": 0, "media.class": "Audio/Sink",
                  "node.name": "alsa_output.usb-AIOC_All-In-One-Cable_d4c9081b-00.mono-fallback" } } },
              { "id": 50, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "alsa.card": "1", "media.class": "Audio/Sink", "node.name": "alsa_output.pci-0000_00_1f.3.analog-stereo" } } },
              { "id": 60, "type": "PipeWire:Interface:Node", "info": null }
            ]
            """;

        PipeWireNodes nodes = PipeWire.Parse(dump, 0);

        nodes.Source.Should().Be("alsa_input.usb-AIOC_All-In-One-Cable_d4c9081b-00.mono-fallback");
        nodes.Sink.Should().Be("alsa_output.usb-AIOC_All-In-One-Cable_d4c9081b-00.mono-fallback");
        nodes.CapturePcm.Should().Be("pipewire:NODE=alsa_input.usb-AIOC_All-In-One-Cable_d4c9081b-00.mono-fallback");
    }

    [Fact]
    public void A_card_pipewire_does_not_know_has_no_nodes()
    {
        PipeWire.Parse("[]", 3).Should().Be(new PipeWireNodes(null, null));
    }
}
