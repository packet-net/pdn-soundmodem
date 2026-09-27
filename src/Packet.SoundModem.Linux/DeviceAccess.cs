using System.Runtime.Versioning;

namespace Packet.SoundModem.Linux;

/// <summary>What a device node is for, in a station.</summary>
public enum DevicePurpose
{
    /// <summary>Audio from the radio.</summary>
    Receive,

    /// <summary>Audio to the radio.</summary>
    Transmit,

    /// <summary>The card's mixer: its levels, AGC and monitor switches.</summary>
    Mixer,

    /// <summary>The transmitter key.</summary>
    Ptt,
}

/// <summary>A device node a station needs and this process cannot open.</summary>
/// <param name="Node">The node, e.g. <c>/dev/hidraw0</c>.</param>
/// <param name="Purpose">What the station wants it for.</param>
/// <param name="Missing">True when it is not there at all, rather than there and refused.</param>
public sealed record AccessProblem(string Node, DevicePurpose Purpose, bool Missing);

/// <summary>
/// Whether the operator can open what a station on an interface needs, asked before opening it
/// so the answer can name the node and the fix rather than surface as "Access denied" from deep in
/// a PTT constructor.
/// </summary>
/// <remarks>
/// The usual failure is PTT. Sound devices are opened to the logged-in user by the desktop (the
/// <c>uaccess</c> tag udev puts on every sound card) or by the <c>audio</c> group, but a hidraw node
/// is root's alone unless a rule says otherwise, and serial ports belong to <c>dialout</c>, which a
/// desktop user is seldom in.
/// </remarks>
public static class DeviceAccess
{
    /// <summary>What this process cannot open of what a station on <paramref name="radio"/> needs.</summary>
    /// <param name="radio">The interface.</param>
    /// <param name="ptt">How it will be keyed.</param>
    /// <param name="serialPort">The serial port for <see cref="PttMethod.Serial"/>, when it is not
    /// the interface's own.</param>
    [SupportedOSPlatform("linux")]
    public static IReadOnlyList<AccessProblem> Check(RadioInterface radio, PttMethod ptt, string? serialPort = null) =>
        Check(SystemDeviceTree.Instance, radio, ptt, serialPort);

    internal static IReadOnlyList<AccessProblem> Check(IDeviceTree tree, RadioInterface radio, PttMethod ptt, string? serialPort)
    {
        ArgumentNullException.ThrowIfNull(radio);
        var problems = new List<AccessProblem>();
        void Need(string? node, DevicePurpose purpose, bool read, bool write)
        {
            if (node is null)
            {
                return;
            }

            if (tree.Resolve(node) is null)
            {
                problems.Add(new AccessProblem(node, purpose, Missing: true));
            }
            else if (!tree.CanOpen(node, read, write))
            {
                problems.Add(new AccessProblem(node, purpose, Missing: false));
            }
        }

        // alsa-lib opens a hw PCM and a control device read-write whichever way the audio flows.
        if (radio.Card is { } card)
        {
            Need(card.CaptureNode, DevicePurpose.Receive, read: true, write: true);
            Need(card.PlaybackNode, DevicePurpose.Transmit, read: true, write: true);
            Need(card.ControlNode, DevicePurpose.Mixer, read: true, write: true);
        }

        switch (ptt)
        {
            case PttMethod.Cm108Hid:
                Need(radio.Hidraw, DevicePurpose.Ptt, read: false, write: true);
                break;
            case PttMethod.Serial:
                Need(serialPort ?? radio.SerialPort, DevicePurpose.Ptt, read: true, write: true);
                break;
        }

        return problems;
    }
}

/// <summary>Who has a sound card's PCM open.</summary>
public static class CardUsers
{
    /// <summary>
    /// The process holding one of <paramref name="card"/>'s PCMs open, as "pipewire (pid 1234)",
    /// or null when it is free. The answer to a busy card that is otherwise just EBUSY, and nearly
    /// always the desktop's sound server.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static string? Holder(AlsaCard card, bool capture) => Holder(SystemDeviceTree.Instance, card, capture);

    /// <summary>The program name alone ("pipewire"), or null when the PCM is free.</summary>
    [SupportedOSPlatform("linux")]
    public static string? HolderProgram(AlsaCard card, bool capture) => HolderProgram(SystemDeviceTree.Instance, card, capture);

    internal static string? Holder(IDeviceTree tree, AlsaCard card, bool capture) =>
        Owner(tree, card, capture) is not int pid
            ? null
            : tree.Read($"/proc/{pid}/comm") is { Length: > 0 } comm ? $"{comm} (pid {pid})" : $"pid {pid}";

    internal static string? HolderProgram(IDeviceTree tree, AlsaCard card, bool capture) =>
        Owner(tree, card, capture) is int pid ? tree.Read($"/proc/{pid}/comm") ?? $"pid {pid}" : null;

    /// <summary>
    /// <c>owner_pid</c> from <c>/proc/asound/cardN/pcmDc/sub0/status</c>, which reads "closed"
    /// while nobody has the PCM open.
    /// </summary>
    private static int? Owner(IDeviceTree tree, AlsaCard card, bool capture)
    {
        ArgumentNullException.ThrowIfNull(card);
        if ((capture ? card.CaptureDevice : card.PlaybackDevice) is not int device)
        {
            return null;
        }

        string? status = tree.Read($"/proc/asound/card{card.Number}/pcm{device}{(capture ? 'c' : 'p')}/sub0/status");
        foreach (string line in (status ?? string.Empty).Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim() == "owner_pid"
                && int.TryParse(line[(colon + 1)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid))
            {
                return pid;
            }
        }

        return null;
    }
}
