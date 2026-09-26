using System.Runtime.Versioning;
using Packet.SoundModem.Windows.Native;

namespace Packet.SoundModem.Windows;

/// <summary>What kind of radio interface a device is, as far as can be told from USB IDs.</summary>
public enum RadioInterfaceKind
{
    /// <summary>A sound device with nothing else recognised on it: VOX, or PTT from elsewhere.</summary>
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

    /// <summary>CM108-compatible HID GPIO.</summary>
    Cm108Hid,

    /// <summary>Serial control lines.</summary>
    Serial,
}

/// <summary>
/// The audio, HID and serial devices that belong to one physical radio interface, found by the
/// container ID Windows gives every function of one USB device.
/// </summary>
/// <param name="Name">The device's own name, e.g. "AIOC Audio".</param>
/// <param name="Kind">What it is.</param>
/// <param name="Capture">Its recording endpoint (audio from the radio), if it has one.</param>
/// <param name="Render">Its playback endpoint (audio to the radio), if it has one.</param>
/// <param name="Hid">Its CM108-compatible HID collection, if it has one.</param>
/// <param name="SerialPort">Its COM port, if it has one.</param>
/// <param name="ContainerId">The physical device, or null for an endpoint Windows did not
/// place in a container (which is then an interface of its own).</param>
public sealed record RadioInterface(
    string Name,
    RadioInterfaceKind Kind,
    AudioEndpoint? Capture,
    AudioEndpoint? Render,
    HidDevice? Hid,
    string? SerialPort,
    Guid? ContainerId)
{
    /// <summary>The PTT method to offer first: HID GPIO where there is a CM108-compatible
    /// collection, then a serial port, then none.</summary>
    public PttMethod SuggestedPtt =>
        Hid is not null ? PttMethod.Cm108Hid : SerialPort is not null ? PttMethod.Serial : PttMethod.None;

    /// <summary>Whether this can be a station on its own: it has audio in both directions.</summary>
    public bool IsComplete => Capture is not null && Render is not null;
}

/// <summary>Finds radio interfaces: every physical device with audio, and what else it offers.</summary>
[SupportedOSPlatform("windows")]
public static class RadioInterfaces
{
    /// <summary>
    /// Every device with an active audio endpoint, with its HID and serial functions attached.
    /// Radio-looking interfaces (AIOC, CM108) sort first, then complete sound cards by name.
    /// </summary>
    public static IReadOnlyList<RadioInterface> Discover()
    {
        IReadOnlyList<AudioEndpoint> captures = AudioEndpoints.List(AudioFlow.Capture);
        IReadOnlyList<AudioEndpoint> renders = AudioEndpoints.List(AudioFlow.Render);
        IReadOnlyList<HidDevice> hids = HidDevices.List();
        IReadOnlyList<DeviceInterface> ports = DeviceInterfaces.Enumerate(NativeMethods.ComPortInterface, readPortName: true);
        return RadioInterfaceGrouping.Group(captures, renders, hids, ports.Select(p => (p.ContainerId, p.PortName)).ToList());
    }

}

/// <summary>
/// How devices are grouped into radio interfaces: pure data, with no Windows calls, so that it is
/// testable (and compiles without platform warnings) everywhere.
/// </summary>
internal static class RadioInterfaceGrouping
{
    /// <summary>The grouping, separated from enumeration so it can be tested without devices.</summary>
    internal static IReadOnlyList<RadioInterface> Group(
        IReadOnlyList<AudioEndpoint> captures,
        IReadOnlyList<AudioEndpoint> renders,
        IReadOnlyList<HidDevice> hids,
        IReadOnlyList<(Guid? ContainerId, string? PortName)> ports)
    {
        var result = new List<RadioInterface>();
        var used = new HashSet<AudioEndpoint>();

        // A device's audio endpoints share its container; a machine's built-in audio typically
        // shares the machine's own container ({00000000-0000-0000-ffff-ffffffffffff}) with
        // everything else built in, so those are grouped by device name within it as well.
        IEnumerable<IGrouping<(Guid?, string), AudioEndpoint>> groups = captures.Concat(renders)
            .GroupBy(e => (e.ContainerId, e.DeviceName));

        foreach (IGrouping<(Guid? Container, string DeviceName), AudioEndpoint> group in groups)
        {
            AudioEndpoint? capture = group.FirstOrDefault(e => e.Flow == AudioFlow.Capture);
            AudioEndpoint? render = group.FirstOrDefault(e => e.Flow == AudioFlow.Render);
            Guid? container = group.Key.Container;
            bool isMachine = container is null || container == MachineContainer;
            HidDevice? hid = isMachine ? null : hids.FirstOrDefault(h => h.ContainerId == container && h.IsCm108Compatible);
            string? port = isMachine ? null : ports.FirstOrDefault(p => p.ContainerId == container && p.PortName is not null).PortName;
            RadioInterfaceKind kind = hid switch
            {
                { IsAioc: true } => RadioInterfaceKind.Aioc,
                { VendorId: HidDevice.CMediaVendorId } => RadioInterfaceKind.Cm108,
                _ => RadioInterfaceKind.SoundCard,
            };

            result.Add(new RadioInterface(group.Key.DeviceName, kind, capture, render, hid, port, container));
        }

        return result
            .OrderBy(r => r.Kind == RadioInterfaceKind.SoundCard ? 1 : 0)
            .ThenBy(r => r.IsComplete ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The container Windows puts everything built into the machine in.</summary>
    internal static readonly Guid MachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");
}
