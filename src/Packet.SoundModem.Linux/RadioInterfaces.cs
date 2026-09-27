using System.Globalization;
using System.Runtime.Versioning;

namespace Packet.SoundModem.Linux;

/// <summary>What kind of radio interface a device is, as far as can be told from USB IDs.</summary>
public enum RadioInterfaceKind
{
    /// <summary>A sound card with nothing else recognised on it: VOX, or PTT from elsewhere.</summary>
    SoundCard,

    /// <summary>A C-Media CM108/CM119-family dongle with GPIO PTT.</summary>
    Cm108,

    /// <summary>An AIOC: CM108-compatible HID PTT and a serial port on the same USB device.</summary>
    Aioc,
}

/// <summary>How a station keys its transmitter.</summary>
public enum PttMethod
{
    /// <summary>No PTT line: the radio's VOX, or the interface's own.</summary>
    None,

    /// <summary>CM108-compatible HID GPIO, through the hidraw node.</summary>
    Cm108Hid,

    /// <summary>Serial control lines.</summary>
    Serial,
}

/// <summary>An ALSA sound card, and the PCM devices on it that a station would use.</summary>
/// <param name="Number">Its index, which can change from boot to boot.</param>
/// <param name="Id">Its id ("AllInOneCable"), which does not, short of two alike.</param>
/// <param name="CaptureDevice">The lowest-numbered capture PCM, if it has one.</param>
/// <param name="PlaybackDevice">The lowest-numbered playback PCM, if it has one.</param>
public sealed record AlsaCard(int Number, string Id, int? CaptureDevice, int? PlaybackDevice)
{
    /// <summary>The capture PCM by id, through the plug layer: <c>plughw:CARD=Id,DEV=0</c>.</summary>
    public string? CapturePcm => CaptureDevice is int device ? Pcm(device) : null;

    /// <summary>The playback PCM by id, through the plug layer.</summary>
    public string? PlaybackPcm => PlaybackDevice is int device ? Pcm(device) : null;

    /// <summary>The card's mixer: <c>hw:CARD=Id</c>.</summary>
    public string Mixer => $"hw:CARD={Id}";

    /// <summary>The device node the capture PCM opens.</summary>
    public string? CaptureNode => CaptureDevice is int device ? $"/dev/snd/pcmC{Number}D{device}c" : null;

    /// <summary>The device node the playback PCM opens.</summary>
    public string? PlaybackNode => PlaybackDevice is int device ? $"/dev/snd/pcmC{Number}D{device}p" : null;

    /// <summary>The device node the mixer opens.</summary>
    public string ControlNode => $"/dev/snd/controlC{Number}";

    private string Pcm(int device) => $"plughw:CARD={Id},DEV={device.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// The sound card, hidraw node and serial port that belong to one physical radio interface, found
/// by the USB device they hang off in sysfs: the Linux counterpart of a Windows container ID.
/// </summary>
/// <param name="Name">What to call it: "AIOC All-In-One-Cable".</param>
/// <param name="Kind">What it is.</param>
/// <param name="Card">Its sound card, if it has one.</param>
/// <param name="Hidraw">Its CM108-compatible hidraw node (<c>/dev/hidraw0</c>), if it has one.</param>
/// <param name="SerialPort">Its serial port, by its stable <c>/dev/serial/by-id</c> name where
/// there is one, else <c>/dev/ttyACM0</c>.</param>
/// <param name="Key">What identifies it across re-plugs and reboots: the USB IDs and serial
/// number, or the USB port for a device with no serial number, or the card id for one that is not
/// on USB at all.</param>
/// <param name="UsbDevice">The USB device's directory in sysfs, when it is on USB.</param>
/// <param name="VendorId">The USB vendor ID, when it is on USB.</param>
/// <param name="ProductId">The USB product ID, when it is on USB.</param>
public sealed record RadioInterface(
    string Name,
    RadioInterfaceKind Kind,
    AlsaCard? Card,
    string? Hidraw,
    string? SerialPort,
    string Key,
    string? UsbDevice,
    ushort? VendorId,
    ushort? ProductId)
{
    /// <summary>The PTT method to offer first: HID GPIO where there is a CM108-compatible
    /// node, then a serial port, then none.</summary>
    public PttMethod SuggestedPtt =>
        Hidraw is not null ? PttMethod.Cm108Hid : SerialPort is not null ? PttMethod.Serial : PttMethod.None;

    /// <summary>Whether this can be a station on its own: it has audio in both directions.</summary>
    public bool IsComplete => Card is { CaptureDevice: not null, PlaybackDevice: not null };
}

/// <summary>Finds radio interfaces: every sound card, and what else its USB device offers.</summary>
[SupportedOSPlatform("linux")]
public static class RadioInterfaces
{
    /// <summary>
    /// Every sound card, with the hidraw node and serial port of the same USB device. Radio
    /// interfaces (AIOC, CM108) sort first, then complete sound cards by name.
    /// </summary>
    public static IReadOnlyList<RadioInterface> Discover() => SysfsDiscovery.Discover(SystemDeviceTree.Instance);
}

/// <summary>
/// Discovery over an <see cref="IDeviceTree"/>: pure reading and grouping, so it runs in a test
/// against a written tree on any platform.
/// </summary>
internal static class SysfsDiscovery
{
    /// <summary>The C-Media USB vendor ID (CM108, CM119 and the rest of the family).</summary>
    public const ushort CMediaVendorId = 0x0D8C;

    /// <summary>The AIOC's USB vendor ID (pid.codes).</summary>
    public const ushort AiocVendorId = 0x1209;

    /// <summary>The AIOC's USB product ID.</summary>
    public const ushort AiocProductId = 0x7388;

    internal static IReadOnlyList<RadioInterface> Discover(IDeviceTree tree)
    {
        IReadOnlyList<string> sound = tree.List("/sys/class/sound");
        Dictionary<int, (string ShortName, string LongName)> names = CardNames(tree.Read("/proc/asound/cards"));
        List<(string UsbDevice, string Node, ushort Vendor, ushort Product)> hids = Hidraws(tree);
        List<(string UsbDevice, string Node)> ttys = SerialPorts(tree);

        var result = new List<RadioInterface>();
        foreach (string entry in sound)
        {
            if (!entry.StartsWith("card", StringComparison.Ordinal)
                || !int.TryParse(entry.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                continue;
            }

            string classPath = $"/sys/class/sound/{entry}";
            string id = tree.Read($"{classPath}/id") ?? entry;
            var card = new AlsaCard(
                number,
                id,
                LowestPcm(sound, number, 'c'),
                LowestPcm(sound, number, 'p'));

            string? usb = tree.Resolve($"{classPath}/device") is { } device ? UsbAncestor(tree, device) : null;
            ushort? vendor = usb is null ? null : Hex(tree.Read($"{usb}/idVendor"));
            ushort? product = usb is null ? null : Hex(tree.Read($"{usb}/idProduct"));
            string? hid = usb is null ? null : hids.FirstOrDefault(h => h.UsbDevice == usb && IsCm108Compatible(h.Vendor, h.Product)).Node;
            string? tty = usb is null ? null : ttys.FirstOrDefault(t => t.UsbDevice == usb).Node;
            RadioInterfaceKind kind = vendor == AiocVendorId && product == AiocProductId
                ? RadioInterfaceKind.Aioc
                : hid is not null ? RadioInterfaceKind.Cm108 : RadioInterfaceKind.SoundCard;

            result.Add(new RadioInterface(
                Name(tree, usb, id, names.GetValueOrDefault(number)),
                kind,
                card,
                hid,
                tty is null ? null : StableSerialName(tree, tty),
                Key(tree, usb, vendor, product, id),
                usb,
                vendor,
                product));
        }

        return result
            .OrderBy(r => r.Kind == RadioInterfaceKind.SoundCard ? 1 : 0)
            .ThenBy(r => r.IsComplete ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether a HID device takes the CM108 GPIO report: the C-Media family, and the AIOC, which
    /// implements the same report from firmware 1.2.0. The same rule as the Windows library.
    /// </summary>
    internal static bool IsCm108Compatible(ushort vendor, ushort product) =>
        vendor == CMediaVendorId || (vendor == AiocVendorId && product == AiocProductId);

    /// <summary>
    /// The nearest directory at or above <paramref name="device"/> that is a USB device (has an
    /// <c>idVendor</c>): the device a sound card's interface, a hidraw node's HID device and a
    /// serial port's interface all hang off. Null when there is none, which is a card on PCI.
    /// </summary>
    internal static string? UsbAncestor(IDeviceTree tree, string device)
    {
        for (string? path = device; !string.IsNullOrEmpty(path) && path != "/sys/devices"; path = Parent(path))
        {
            if (tree.Read($"{path}/idVendor") is not null)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>/proc/asound/cards</c>, which is where a card's names are: a line
    /// <c> 0 [AllInOneCable  ]: USB-Audio - All-In-One-Cable</c> and under it the long name,
    /// <c>AIOC All-In-One-Cable at usb-0000:00:14.0-1, full speed</c>.
    /// </summary>
    internal static Dictionary<int, (string ShortName, string LongName)> CardNames(string? cards)
    {
        var names = new Dictionary<int, (string, string)>();
        if (cards is null)
        {
            return names;
        }

        string[] lines = cards.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int bracket = line.IndexOf('[', StringComparison.Ordinal);
            int colon = line.IndexOf("]:", StringComparison.Ordinal);
            if (bracket <= 0 || colon < bracket
                || !int.TryParse(line.AsSpan(0, bracket).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                continue;
            }

            // "USB-Audio - All-In-One-Cable": the driver, then the short name.
            string rest = line[(colon + 2)..].Trim();
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            string shortName = dash >= 0 ? rest[(dash + 3)..].Trim() : rest;
            string longName = i + 1 < lines.Length ? lines[i + 1].Trim() : shortName;
            names[number] = (shortName, longName);
        }

        return names;
    }

    private static string Name(IDeviceTree tree, string? usb, string id, (string ShortName, string LongName) names)
    {
        // A USB card's long name is "<manufacturer> <product> at <where>, <speed>", which is the
        // most useful thing to call it with the plumbing cut off; failing that, the USB strings
        // themselves; a card that is not on USB has a short name like "HDA Intel PCH".
        if (usb is not null)
        {
            if (names.LongName is { Length: > 0 } longName && longName.IndexOf(" at ", StringComparison.Ordinal) is int at and > 0)
            {
                return longName[..at].Trim();
            }

            string product = tree.Read($"{usb}/product") ?? string.Empty;
            string maker = tree.Read($"{usb}/manufacturer") ?? string.Empty;
            string both = product.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? product : $"{maker} {product}".Trim();
            if (both.Length > 0)
            {
                return both;
            }
        }

        return names.ShortName is { Length: > 0 } shortName ? shortName : id;
    }

    private static string Key(IDeviceTree tree, string? usb, ushort? vendor, ushort? product, string id)
    {
        if (usb is null || vendor is not ushort v || product is not ushort p)
        {
            return $"card:{id}";
        }

        // The serial number where there is one (the AIOC has one); a CM108 dongle usually does
        // not, and then the port it is plugged into is what tells two of them apart.
        string ids = $"usb:{v:x4}:{p:x4}";
        return tree.Read($"{usb}/serial") is { Length: > 0 } serial
            ? $"{ids}:{serial}"
            : $"{ids}@{Path.GetFileName(usb)}";
    }

    private static int? LowestPcm(IReadOnlyList<string> sound, int card, char direction)
    {
        string prefix = $"pcmC{card.ToString(CultureInfo.InvariantCulture)}D";
        int? lowest = null;
        foreach (string entry in sound)
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal) && entry[^1] == direction
                && int.TryParse(entry.AsSpan(prefix.Length, entry.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int device)
                && (lowest is null || device < lowest))
            {
                lowest = device;
            }
        }

        return lowest;
    }

    private static List<(string UsbDevice, string Node, ushort Vendor, ushort Product)> Hidraws(IDeviceTree tree)
    {
        var found = new List<(string, string, ushort, ushort)>();
        foreach (string entry in tree.List("/sys/class/hidraw"))
        {
            if (tree.Resolve($"/sys/class/hidraw/{entry}/device") is not { } device
                || UsbAncestor(tree, device) is not { } usb)
            {
                continue;
            }

            // HID_ID=0003:00001209:00007388: bus, vendor, product. The HID device's own IDs rather
            // than the USB device's, though for everything seen they are the same.
            (ushort vendor, ushort product) = HidIds(tree.Read($"{device}/uevent"))
                ?? (Hex(tree.Read($"{usb}/idVendor")) ?? 0, Hex(tree.Read($"{usb}/idProduct")) ?? 0);
            found.Add((usb, $"/dev/{entry}", vendor, product));
        }

        return found;
    }

    private static List<(string UsbDevice, string Node)> SerialPorts(IDeviceTree tree)
    {
        var found = new List<(string, string)>();
        foreach (string entry in tree.List("/sys/class/tty"))
        {
            if (!entry.StartsWith("ttyACM", StringComparison.Ordinal) && !entry.StartsWith("ttyUSB", StringComparison.Ordinal))
            {
                continue;
            }

            if (tree.Resolve($"/sys/class/tty/{entry}/device") is { } device && UsbAncestor(tree, device) is { } usb)
            {
                found.Add((usb, $"/dev/{entry}"));
            }
        }

        return found;
    }

    /// <summary>
    /// <c>/dev/serial/by-id/usb-AIOC_All-In-One-Cable_d4c9081b-if04</c> for <c>/dev/ttyACM0</c>,
    /// which udev keeps pointing at the same device however the numbering falls out; the node
    /// itself when there is no such link.
    /// </summary>
    private static string StableSerialName(IDeviceTree tree, string node)
    {
        foreach (string link in tree.List("/dev/serial/by-id"))
        {
            string path = $"/dev/serial/by-id/{link}";
            if (tree.Resolve(path) == node)
            {
                return path;
            }
        }

        return node;
    }

    private static (ushort Vendor, ushort Product)? HidIds(string? uevent)
    {
        foreach (string line in (uevent ?? string.Empty).Split('\n'))
        {
            if (!line.StartsWith("HID_ID=", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line[7..].Split(':');
            if (parts.Length == 3
                && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint vendor)
                && uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint product))
            {
                return ((ushort)vendor, (ushort)product);
            }
        }

        return null;
    }

    private static ushort? Hex(string? text) =>
        ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value) ? value : null;

    private static string? Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? null : path[..slash];
    }
}
