using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Linux;
using Packet.SoundModem.UberSdr;
using M0LTE.Flex;

namespace Packet.SoundModem.Daemon;

/// <summary>What a mixer has on it, by side.</summary>
internal sealed record MixerNames(IReadOnlyList<string> Capture, IReadOnlyList<string> Playback, string? Problem);

internal interface IMixerReader
{
    /// <summary>The control names on a card's mixer (<c>hw:CARD=Id</c>), by side. Reads only.</summary>
    MixerNames Read(string mixerCard);
}

/// <summary>Reads a card's mixer controls through ALSA. Nothing is written and no PCM is opened.</summary>
internal sealed class AlsaMixerReader : IMixerReader
{
    public MixerNames Read(string mixerCard)
    {
        if (!AlsaMixer.TryOpen(mixerCard, out AlsaMixer? mixer, out string why) || mixer is null)
        {
            return new MixerNames([], [], why);
        }

        using (mixer)
        {
            var capture = new List<string>();
            var playback = new List<string>();
            foreach (string control in mixer.Controls)
            {
                if (mixer.HasVolume(control, MixerDirection.Capture))
                {
                    capture.Add(control);
                }

                if (mixer.HasVolume(control, MixerDirection.Playback))
                {
                    playback.Add(control);
                }
            }

            return new MixerNames(capture, playback, null);
        }
    }
}

/// <summary>
/// Sound cards, HID nodes and serial ports read from sysfs, /proc and /dev through the Linux
/// library's <see cref="IDeviceTree"/>, so a test feeds it a tree it wrote.
/// </summary>
internal sealed class SysfsProbes(IDeviceTree tree, IMixerReader mixers) : ISoundCardProbe, IHidProbe, ISerialProbe
{
    IReadOnlyList<FoundCard> ISoundCardProbe.Find()
    {
        var found = new List<FoundCard>();
        foreach (RadioInterface radio in SysfsDiscovery.Discover(tree))
        {
            if (radio.Card is not { } card)
            {
                continue;
            }

            int pcm = card.CaptureDevice ?? card.PlaybackDevice ?? 0;
            MixerNames names = mixers.Read(card.Mixer);
            found.Add(new FoundCard(
                card.Id,
                card.Number,
                radio.Name,
                $"plughw:CARD={card.Id},DEV={pcm.ToString(CultureInfo.InvariantCulture)}",
                card.CaptureDevice is not null,
                card.PlaybackDevice is not null,
                radio.Kind switch
                {
                    RadioInterfaceKind.Aioc => "aioc",
                    RadioInterfaceKind.Cm108 => "cm108",
                    _ => "soundcard",
                },
                radio.Hidraw,
                names.Capture,
                names.Playback,
                names.Problem,
                radio.UsbDevice));
        }

        return found;
    }

    IReadOnlyList<FoundHid> IHidProbe.Find()
    {
        var found = new List<FoundHid>();
        foreach (string entry in tree.List("/sys/class/hidraw"))
        {
            if (!entry.StartsWith("hidraw", StringComparison.Ordinal))
            {
                continue;
            }

            string? device = tree.Resolve($"/sys/class/hidraw/{entry}/device");
            string? usb = device is null ? null : SysfsDiscovery.UsbAncestor(tree, device);
            (ushort vendor, ushort product) = SysfsDiscovery.HidIds(device is null ? null : tree.Read($"{device}/uevent"))
                ?? (usb is null ? ((ushort)0, (ushort)0)
                    : (SysfsDiscovery.Hex(tree.Read($"{usb}/idVendor")) ?? 0, SysfsDiscovery.Hex(tree.Read($"{usb}/idProduct")) ?? 0));
            string node = $"/dev/{entry}";
            found.Add(new FoundHid(
                node,
                HidName(device, usb),
                SysfsDiscovery.IsCm108Compatible(vendor, product),
                tree.CanOpen(node, read: true, write: true),
                usb));
        }

        return found;
    }

    IReadOnlyList<FoundSerial> ISerialProbe.Find()
    {
        var found = new List<FoundSerial>();
        foreach (string entry in tree.List("/sys/class/tty"))
        {
            bool usbName = entry.StartsWith("ttyUSB", StringComparison.Ordinal) || entry.StartsWith("ttyACM", StringComparison.Ordinal);
            if (!usbName && !entry.StartsWith("ttyS", StringComparison.Ordinal) && !entry.StartsWith("ttyAMA", StringComparison.Ordinal))
            {
                continue;
            }

            // The 8250 driver makes a ttyS entry, with a device link, for every port it might
            // have (ttyS0 to ttyS31 on a typical PC); one that is not there has a UART type of 0.
            // USB adapters have no such file.
            if (tree.Read($"/sys/class/tty/{entry}/type") == "0")
            {
                continue;
            }

            string? device = tree.Resolve($"/sys/class/tty/{entry}/device");
            if (device is null)
            {
                continue;
            }

            string? usb = SysfsDiscovery.UsbAncestor(tree, device);
            string node = $"/dev/{entry}";
            found.Add(new FoundSerial(
                node,
                StableName(node),
                usb is null ? "Built-in serial port" : UsbName(usb) ?? "USB serial port",
                usb is not null,
                tree.CanOpen(node, read: true, write: true),
                usb));
        }

        return found;
    }

    private string? StableName(string node)
    {
        foreach (string link in tree.List("/dev/serial/by-id"))
        {
            string path = $"/dev/serial/by-id/{link}";
            if (tree.Resolve(path) == node)
            {
                return path;
            }
        }

        return null;
    }

    private string HidName(string? device, string? usb)
    {
        foreach (string line in (device is null ? "" : tree.Read($"{device}/uevent") ?? "").Split('\n'))
        {
            if (line.StartsWith("HID_NAME=", StringComparison.Ordinal) && line.Length > 9)
            {
                return line[9..].Trim();
            }
        }

        return (usb is null ? null : UsbName(usb)) ?? "HID device";
    }

    private string? UsbName(string usb)
    {
        string product = tree.Read($"{usb}/product") ?? "";
        string maker = tree.Read($"{usb}/manufacturer") ?? "";
        string both = maker.Length == 0 || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase)
            ? product
            : $"{maker} {product}".Trim();
        return both.Length > 0 ? both : null;
    }
}

/// <summary>
/// Probes that all say why they cannot look, in words: a machine that is not Linux, or a test
/// fixture file that cannot be read. Each section reports it and the page carries on.
/// </summary>
internal sealed class FailingProbes(string why) : ISoundCardProbe, IHidProbe, ISerialProbe, IRigctldProbe, IFlexProbe, IUberSdrProbe
{
    /// <summary>The probes on a machine that is not Linux.</summary>
    internal static FailingProbes NotLinux() => new("this is only looked for on Linux; type the value by hand");

    public string Address => "localhost:4532";

    IReadOnlyList<FoundCard> ISoundCardProbe.Find() => throw new NotSupportedException("Sound cards: " + why);

    IReadOnlyList<FoundHid> IHidProbe.Find() => throw new NotSupportedException("PTT pins: " + why);

    IReadOnlyList<FoundSerial> ISerialProbe.Find() => throw new NotSupportedException("Serial ports: " + why);

    Task<bool> IRigctldProbe.AnswersAsync(CancellationToken cancellation) => throw new NotSupportedException("rigctld: " + why);

    Task<IReadOnlyList<FoundFlex>> IFlexProbe.FindAsync(TimeSpan listen, CancellationToken cancellation) =>
        throw new NotSupportedException("FlexRadios: " + why);

    Task<IReadOnlyList<FoundUberSdr>> IUberSdrProbe.FindAsync(CancellationToken cancellation) =>
        throw new NotSupportedException("UberSDR: " + why);
}

/// <summary>One short connect to rigctld's port. Nothing is sent.</summary>
internal sealed class TcpRigctldProbe(string host, int port) : IRigctldProbe
{
    public string Address => $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

    public async Task<bool> AnswersAsync(CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMilliseconds(700));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            return false;
        }
    }
}

/// <summary>Listens for FlexRadio discovery broadcasts, as the station does, but for all radios.</summary>
internal sealed class UdpFlexProbe : IFlexProbe
{
    public async Task<IReadOnlyList<FoundFlex>> FindAsync(TimeSpan listen, CancellationToken cancellation)
    {
        var radios = new Dictionary<string, FoundFlex>(StringComparer.Ordinal);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, Vita49.DiscoveryPort));
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"cannot listen for FlexRadios on UDP {Vita49.DiscoveryPort} ({e.SocketErrorCode})");
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        window.CancelAfter(listen);
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int n = await socket.ReceiveAsync(buffer, SocketFlags.None, window.Token).ConfigureAwait(false);
                if (FlexDiscovery.TryParsePacket(buffer.AsSpan(0, n)) is { Ip.Length: > 0 } info)
                {
                    radios[info.Serial.Length > 0 ? info.Serial : info.Ip] =
                        new FoundFlex(info.Name, info.Model, info.Serial, info.Ip);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // The listening window ended; that is how this finishes.
        }

        return radios.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>The public UberSDR directory, through the client the monitor uses.</summary>
internal sealed class DirectoryUberSdrProbe : IUberSdrProbe
{
    /// <summary>
    /// The <c>ubersdr:</c> device string for an endpoint. The bare host form means HTTPS, so a
    /// receiver that is not on TLS has to be written as the URL an operator would paste.
    /// </summary>
    internal static string DeviceString(UberSdrEndpoint endpoint) =>
        endpoint.Ssl
            ? $"ubersdr:{endpoint}"
            : $"ubersdr:http://{endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}/";

    public async Task<IReadOnlyList<FoundUberSdr>> FindAsync(CancellationToken cancellation)
    {
        // Receivers that can tune 3.5 to 29.7 MHz: the directory's own filter, and a width most
        // public instances have. Typing an address by hand reaches the rest.
        var directory = new UberSdrDirectory(
            new UberSdrDirectoryOptions
            {
                Url = "https://instances.ubersdr.org/api/instances",
                Refresh = TimeSpan.Zero,
                WindowLowHz = 3_500_000,
                WindowHighHz = 29_700_000,
            },
            new StationJournal("discover", _ => { }, _ => { }));
        await directory.RefreshAsync(cancellation).ConfigureAwait(false);
        DirectorySnapshot snapshot = directory.Snapshot;
        if (snapshot.Stale && snapshot.Receivers.Count == 0)
        {
            throw new InvalidOperationException($"the directory did not answer ({snapshot.Problem})");
        }

        return snapshot.Receivers
            .Select(r => new FoundUberSdr(
                r.Host,
                DeviceString(r.Endpoint),
                r.Name.Length > 0 ? r.Name : r.Callsign,
                r.Location,
                r.Offered))
            .OrderByDescending(r => r.Free)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
