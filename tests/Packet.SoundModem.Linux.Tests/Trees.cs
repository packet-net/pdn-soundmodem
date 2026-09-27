namespace Packet.SoundModem.Linux.Tests;

/// <summary>
/// Device trees as real machines lay them out. The AIOC is transcribed from one attached to a
/// Debian 13 machine (through usbip, hence vhci_hcd); the CM108 and the onboard card follow the
/// same kernel layout on PCI.
/// </summary>
internal static class Trees
{
    public const string AiocUsb = "/sys/devices/platform/vhci_hcd.0/usb1/1-1";
    public const string Cm108Usb = "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-2";

    public static FakeDeviceTree Aioc(this FakeDeviceTree tree, int card = 0, string usb = AiocUsb, string serial = "d4c9081b", int hidraw = 0, int acm = 0)
    {
        string port = Path.GetFileName(usb);
        tree.File($"{usb}/idVendor", "1209").File($"{usb}/idProduct", "7388")
            .File($"{usb}/manufacturer", "AIOC").File($"{usb}/product", "All-In-One-Cable").File($"{usb}/serial", serial);
        Card(tree, card, $"{usb}/{port}:1.0", "AllInOneCable", capture: true, playback: true);

        string hid = $"{usb}/{port}:1.3/0003:1209:7388.0001";
        tree.Link($"/sys/class/hidraw/hidraw{hidraw}", $"{hid}/hidraw/hidraw{hidraw}")
            .Link($"{hid}/hidraw/hidraw{hidraw}/device", hid)
            .File($"{hid}/uevent", "DRIVER=hid-generic\nHID_ID=0003:00001209:00007388\nHID_NAME=AIOC All-In-One-Cable\nHID_UNIQ=" + serial)
            .Node($"/dev/hidraw{hidraw}");

        string serialIface = $"{usb}/{port}:1.4";
        tree.Link($"/sys/class/tty/ttyACM{acm}", $"{serialIface}/tty/ttyACM{acm}")
            .Link($"{serialIface}/tty/ttyACM{acm}/device", serialIface)
            .File($"{serialIface}/bInterfaceNumber", "04")
            .Link($"/dev/serial/by-id/usb-AIOC_All-In-One-Cable_{serial}-if04", $"/dev/ttyACM{acm}")
            .Node($"/dev/ttyACM{acm}");
        return tree;
    }

    public static FakeDeviceTree Cm108(this FakeDeviceTree tree, int card = 1, string usb = Cm108Usb, int hidraw = 1)
    {
        string port = Path.GetFileName(usb);
        tree.File($"{usb}/idVendor", "0d8c").File($"{usb}/idProduct", "0012")
            .File($"{usb}/manufacturer", "C-Media Electronics Inc.").File($"{usb}/product", "USB PnP Sound Device");
        Card(tree, card, $"{usb}/{port}:1.0", "Device", capture: true, playback: true);

        string hid = $"{usb}/{port}:1.3/0003:0D8C:0012.0002";
        tree.Link($"/sys/class/hidraw/hidraw{hidraw}", $"{hid}/hidraw/hidraw{hidraw}")
            .Link($"{hid}/hidraw/hidraw{hidraw}/device", hid)
            .File($"{hid}/uevent", "DRIVER=hid-generic\nHID_ID=0003:00000D8C:00000012")
            .Node($"/dev/hidraw{hidraw}");
        return tree;
    }

    public static FakeDeviceTree Onboard(this FakeDeviceTree tree, int card = 2)
    {
        string pci = "/sys/devices/pci0000:00/0000:00:1f.3";
        tree.File($"{pci}/vendor", "0x8086");
        Card(tree, card, pci, "PCH", capture: true, playback: true);

        // HDMI: playback only, on a higher device number.
        tree.Link($"/sys/class/sound/pcmC{card}D3p", $"{pci}/sound/card{card}/pcmC{card}D3p");
        return tree;
    }

    public static FakeDeviceTree Cards(this FakeDeviceTree tree, string cards) => tree.File("/proc/asound/cards", cards);

    public const string AiocCmediaOnboardCards = """
         0 [AllInOneCable  ]: USB-Audio - All-In-One-Cable
                              AIOC All-In-One-Cable at usb-vhci_hcd.0-1, full speed
         1 [Device         ]: USB-Audio - USB PnP Sound Device
                              C-Media Electronics Inc. USB PnP Sound Device at usb-0000:00:14.0-2, full speed
         2 [PCH            ]: HDA-Intel - HDA Intel PCH
                              HDA Intel PCH at 0xf1340000 irq 145
        """;

    private static void Card(FakeDeviceTree tree, int card, string device, string id, bool capture, bool playback)
    {
        string dir = $"{device}/sound/card{card}";
        tree.Link($"/sys/class/sound/card{card}", dir)
            .File($"{dir}/id", id)
            .File($"{dir}/number", card.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Link($"{dir}/device", device)
            .Link($"/sys/class/sound/controlC{card}", $"{dir}/controlC{card}")
            .Node($"/dev/snd/controlC{card}");
        if (capture)
        {
            tree.Link($"/sys/class/sound/pcmC{card}D0c", $"{dir}/pcmC{card}D0c").Node($"/dev/snd/pcmC{card}D0c");
        }

        if (playback)
        {
            tree.Link($"/sys/class/sound/pcmC{card}D0p", $"{dir}/pcmC{card}D0p").Node($"/dev/snd/pcmC{card}D0p");
        }
    }
}
