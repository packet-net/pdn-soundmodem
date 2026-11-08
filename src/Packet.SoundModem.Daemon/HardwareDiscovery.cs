using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>A sound card the machine has, as the configuration page offers it.</summary>
/// <param name="Id">The ALSA card id ("Device"), which survives a reboot better than its number.</param>
/// <param name="Number">The card's number, which can change from boot to boot.</param>
/// <param name="Name">What to call it: "C-Media USB PnP Sound Device".</param>
/// <param name="Device">The ALSA device string to put in <c>device</c>.</param>
/// <param name="Capture">Whether it can record.</param>
/// <param name="Playback">Whether it can play.</param>
/// <param name="Kind">"cm108", "aioc" or "soundcard".</param>
/// <param name="Hidraw">Its CM108-compatible HID node, if it has one.</param>
/// <param name="CaptureControls">Mixer controls that set the record level.</param>
/// <param name="PlaybackControls">Mixer controls that set the play level.</param>
/// <param name="MixerProblem">Why the mixer could not be read, if it could not.</param>
/// <param name="UsbDevice">Its USB device in sysfs, to match the HID node and serial port to it.</param>
internal sealed record FoundCard(
    string Id,
    int Number,
    string Name,
    string Device,
    bool Capture,
    bool Playback,
    string Kind,
    string? Hidraw,
    IReadOnlyList<string> CaptureControls,
    IReadOnlyList<string> PlaybackControls,
    string? MixerProblem,
    string? UsbDevice);

/// <summary>A /dev/hidraw node.</summary>
/// <param name="Path">The device node.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Cm108">Whether it takes the CM108 GPIO report, so can key a radio.</param>
/// <param name="CanOpen">Whether this process may open it for reading and writing.</param>
/// <param name="UsbDevice">Its USB device in sysfs.</param>
internal sealed record FoundHid(string Path, string Name, bool Cm108, bool CanOpen, string? UsbDevice);

/// <summary>A serial port.</summary>
/// <param name="Path">The device node.</param>
/// <param name="StablePath">The /dev/serial/by-id name, which survives a re-plug; null if none.</param>
/// <param name="Name">What it is: the USB product name, or "Built-in serial port".</param>
/// <param name="Usb">Whether it is on USB.</param>
/// <param name="CanOpen">Whether this process may open it for reading and writing.</param>
/// <param name="UsbDevice">Its USB device in sysfs.</param>
internal sealed record FoundSerial(
    string Path, string? StablePath, string Name, bool Usb, bool CanOpen, string? UsbDevice);

/// <summary>A FlexRadio that answered on the LAN.</summary>
internal sealed record FoundFlex(string Name, string Model, string Serial, string Ip);

/// <summary>An UberSDR instance from the public directory.</summary>
/// <param name="Host">The directory's host name.</param>
/// <param name="Device">The <c>ubersdr:</c> device string.</param>
/// <param name="Name">Its name, or its callsign.</param>
/// <param name="Location">Where it says it is.</param>
/// <param name="Free">Whether it has a free slot.</param>
internal sealed record FoundUberSdr(string Host, string Device, string Name, string Location, bool Free);

internal interface ISoundCardProbe
{
    /// <summary>The machine's sound cards, with their mixer control names. Reads only.</summary>
    IReadOnlyList<FoundCard> Find();
}

internal interface IHidProbe
{
    /// <summary>The machine's /dev/hidraw nodes. Reads only.</summary>
    IReadOnlyList<FoundHid> Find();
}

internal interface ISerialProbe
{
    /// <summary>The machine's serial ports. Reads only.</summary>
    IReadOnlyList<FoundSerial> Find();
}

internal interface IRigctldProbe
{
    /// <summary>Whether something accepts a connection at <see cref="Address"/> now.</summary>
    Task<bool> AnswersAsync(CancellationToken cancellation);

    /// <summary>The host:port asked.</summary>
    string Address { get; }
}

internal interface IFlexProbe
{
    /// <summary>The FlexRadios heard on the LAN within <paramref name="listen"/>.</summary>
    Task<IReadOnlyList<FoundFlex>> FindAsync(TimeSpan listen, CancellationToken cancellation);
}

internal interface IUberSdrProbe
{
    /// <summary>The UberSDR instances the public directory lists.</summary>
    Task<IReadOnlyList<FoundUberSdr>> FindAsync(CancellationToken cancellation);
}

/// <summary>
/// What this machine has that the configuration page can offer as a choice: sound cards and
/// their mixer controls, CM108 HID nodes, serial ports, whether rigctld answers, FlexRadios on the
/// LAN and UberSDR instances. It reads, and only reads: no sound card is opened for audio, no
/// line is keyed, nothing is written.
/// </summary>
/// <remarks>
/// <para>Each probe is bounded, and a failure in one is reported in its own section in words and
/// leaves the rest alone, so a machine with none of these answers with six empty lists and no
/// error. The probes are behind interfaces so that a test feeds them a written device tree and
/// fake network answers.</para>
/// <para>The two slow ones, Flex (a few seconds of listening) and the UberSDR directory (an HTTP
/// fetch), are remembered for <see cref="SlowCache"/>; "Look again" asks for a fresh look.</para>
/// </remarks>
internal sealed class HardwareDiscovery
{
    /// <summary>How long a Flex or UberSDR answer is reused.</summary>
    internal static readonly TimeSpan SlowCache = TimeSpan.FromSeconds(30);

    private readonly ISoundCardProbe _cards;
    private readonly IHidProbe _hid;
    private readonly ISerialProbe _serial;
    private readonly IRigctldProbe _rigctld;
    private readonly IFlexProbe _flex;
    private readonly IUberSdrProbe _uberSdr;
    private readonly TimeProvider _time;
    private readonly TimeSpan _fast;
    private readonly TimeSpan _slow;
    private readonly TimeSpan _flexListen;
    private readonly object _gate = new();
    private Task<JsonObject>? _running;
    private Section<FoundFlex>? _flexCache;
    private Section<FoundUberSdr>? _uberCache;

    public HardwareDiscovery(
        ISoundCardProbe cards, IHidProbe hid, ISerialProbe serial, IRigctldProbe rigctld,
        IFlexProbe flex, IUberSdrProbe uberSdr, TimeProvider? time = null,
        TimeSpan? fastLimit = null, TimeSpan? slowLimit = null, TimeSpan? flexListen = null)
    {
        _cards = cards;
        _hid = hid;
        _serial = serial;
        _rigctld = rigctld;
        _flex = flex;
        _uberSdr = uberSdr;
        _time = time ?? TimeProvider.System;
        _fast = fastLimit ?? TimeSpan.FromSeconds(3);
        _slow = slowLimit ?? TimeSpan.FromSeconds(4);
        _flexListen = flexListen ?? TimeSpan.FromMilliseconds(2500);
    }

    /// <summary>A discovery over this machine's own devices and network.</summary>
    public static HardwareDiscovery ForThisMachine()
    {
        if (Environment.GetEnvironmentVariable(HardwareFixture.Variable) is { Length: > 0 } fixture)
        {
            return HardwareFixture.Load(fixture);
        }

        (ISoundCardProbe cards, IHidProbe hid, ISerialProbe serial) = OperatingSystem.IsLinux()
            ? Linux()
            : (FailingProbes.NotLinux(), FailingProbes.NotLinux(), FailingProbes.NotLinux());
        return new HardwareDiscovery(
            cards, hid, serial, new TcpRigctldProbe("localhost", 4532), new UdpFlexProbe(),
            new DirectoryUberSdrProbe());
    }

    private static (ISoundCardProbe, IHidProbe, ISerialProbe) Linux()
    {
        var sysfs = new SysfsProbes(Packet.SoundModem.Linux.SystemDeviceTree.Instance, new AlsaMixerReader());
        return (sysfs, sysfs, sysfs);
    }

    private sealed record Section<T>(IReadOnlyList<T> Items, string? Problem, DateTimeOffset At);

    /// <summary>Looks, and describes what was found.</summary>
    /// <param name="fresh">Ignore the remembered Flex and UberSDR answers.</param>
    /// <param name="cancellation">Cancels the whole look.</param>
    public Task<JsonObject> DescribeAsync(bool fresh, CancellationToken cancellation)
    {
        // One look at a time: a second request (a double-clicked "Look again", two open pages)
        // takes the one in flight rather than starting its own Flex listen and directory fetch.
        Task<JsonObject> running;
        lock (_gate)
        {
            running = _running ??= RunAsync(fresh);
        }

        return running.WaitAsync(cancellation);
    }

    private async Task<JsonObject> RunAsync(bool fresh)
    {
        try
        {
            return await LookAsync(fresh, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    private async Task<JsonObject> LookAsync(bool fresh, CancellationToken cancellation)
    {
        Task<Section<FoundCard>> cards = Bounded(() => Task.Run(_cards.Find, cancellation), _fast, cancellation);
        Task<Section<FoundHid>> hid = Bounded(() => Task.Run(_hid.Find, cancellation), _fast, cancellation);
        Task<Section<FoundSerial>> serial = Bounded(() => Task.Run(_serial.Find, cancellation), _fast, cancellation);
        Task<(bool Answering, string? Problem)> rig = RigctldAsync(cancellation);
        Task<Section<FoundFlex>> flex = Slow(
            fresh, () => _flexCache, v => _flexCache = v,
            ct => _flex.FindAsync(_flexListen, ct),
            _flexListen + TimeSpan.FromSeconds(1), cancellation);
        Task<Section<FoundUberSdr>> uber = Slow(
            fresh, () => _uberCache, v => _uberCache = v, _uberSdr.FindAsync, _slow, cancellation);

        await Task.WhenAll(cards, hid, serial, rig, flex, uber).ConfigureAwait(false);

        DateTimeOffset now = _time.GetUtcNow();
        Section<FoundCard> c = cards.Result;
        Section<FoundHid> h = hid.Result;
        Section<FoundSerial> s = serial.Result;
        var answer = new JsonObject
        {
            ["checkedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["soundCards"] = Describe(c, card => CardJson(card)),
            ["hidDevices"] = Describe(h, item => HidJson(item, c.Items)),
            ["serialPorts"] = Describe(s, item => SerialJson(item, c.Items)),
            ["rigctld"] = new JsonObject
            {
                ["address"] = _rigctld.Address,
                ["answering"] = rig.Result.Answering,
                ["problem"] = rig.Result.Problem,
            },
            ["flexRadios"] = Describe(flex.Result, FlexJson, now),
            ["uberSdr"] = Describe(uber.Result, UberJson, now),
        };
        return answer;
    }

    private async Task<(bool, string?)> RigctldAsync(CancellationToken cancellation)
    {
        Section<bool> s = await Bounded(
            async () => (IReadOnlyList<bool>)[await _rigctld.AnswersAsync(cancellation).ConfigureAwait(false)],
            _fast, cancellation).ConfigureAwait(false);
        return (s.Items.Count > 0 && s.Items[0], s.Problem);
    }

    private async Task<Section<T>> Slow<T>(
        bool fresh, Func<Section<T>?> read, Action<Section<T>> write,
        Func<CancellationToken, Task<IReadOnlyList<T>>> probe, TimeSpan limit,
        CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (!fresh && read() is { } kept && _time.GetUtcNow() - kept.At < SlowCache)
            {
                return kept;
            }
        }

        Section<T> section = await Bounded(() => probe(cancellation), limit, cancellation).ConfigureAwait(false);
        // Only an answer is remembered: a failure (no network a moment ago) is tried again next time.
        if (section.Problem is null)
        {
            lock (_gate)
            {
                write(section);
            }
        }

        return section;
    }

    private async Task<Section<T>> Bounded<T>(
        Func<Task<IReadOnlyList<T>>> probe, TimeSpan limit, CancellationToken cancellation)
    {
        DateTimeOffset at = _time.GetUtcNow();
        try
        {
            Task<IReadOnlyList<T>> work = probe();
            Task finished = await Task.WhenAny(work, Task.Delay(limit, cancellation)).ConfigureAwait(false);
            if (finished != work)
            {
                cancellation.ThrowIfCancellationRequested();

                // Left running; whatever it returns later is thrown away.
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return new Section<T>([], $"did not answer within {limit.TotalSeconds:0.#} seconds", at);
            }

            return new Section<T>(await work.ConfigureAwait(false), null, at);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new Section<T>([], Plain(e), at);
        }
    }

    /// <summary>An exception as one plain ASCII sentence for the page.</summary>
    internal static string Plain(Exception e)
    {
        string text = e is NotSupportedException or PlatformNotSupportedException
            ? e.Message
            : $"could not look: {e.Message}";
        var ascii = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            ascii.Append(ch is >= ' ' and < (char)127 ? ch : ' ');
        }

        return ascii.ToString().Trim();
    }

    private static JsonObject Describe<T>(Section<T> section, Func<T, JsonNode> item, DateTimeOffset? now = null)
    {
        var items = new JsonArray();
        foreach (T found in section.Items)
        {
            items.Add(item(found));
        }

        var json = new JsonObject { ["problem"] = section.Problem, ["items"] = items };
        if (now is { } n)
        {
            json["ageSeconds"] = Math.Max(0, (int)(n - section.At).TotalSeconds);
        }

        return json;
    }

    private static JsonNode CardJson(FoundCard c) => new JsonObject
    {
        ["id"] = c.Id,
        ["number"] = c.Number,
        ["name"] = c.Name,
        ["device"] = c.Device,
        ["capture"] = c.Capture,
        ["playback"] = c.Playback,
        ["kind"] = c.Kind,
        ["cm108"] = c.Kind is "cm108" or "aioc",
        ["hidraw"] = c.Hidraw,
        ["captureControls"] = new JsonArray(c.CaptureControls.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        ["playbackControls"] = new JsonArray(c.PlaybackControls.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        ["mixerProblem"] = c.MixerProblem,
    };

    private static JsonNode HidJson(FoundHid h, IReadOnlyList<FoundCard> cards) => new JsonObject
    {
        ["path"] = h.Path,
        ["name"] = h.Name,
        ["cm108"] = h.Cm108,
        ["canOpen"] = h.CanOpen,
        ["soundCard"] = h.UsbDevice is null
            ? null
            : cards.FirstOrDefault(c => c.UsbDevice == h.UsbDevice)?.Id,
    };

    private static JsonNode SerialJson(FoundSerial s, IReadOnlyList<FoundCard> cards) => new JsonObject
    {
        ["path"] = s.StablePath ?? s.Path,
        ["node"] = s.Path,
        ["name"] = s.Name,
        ["usb"] = s.Usb,
        ["canOpen"] = s.CanOpen,
        ["soundCard"] = s.UsbDevice is null
            ? null
            : cards.FirstOrDefault(c => c.UsbDevice == s.UsbDevice)?.Id,
    };

    private static JsonNode FlexJson(FoundFlex f) => new JsonObject
    {
        ["name"] = f.Name,
        ["model"] = f.Model,
        ["serial"] = f.Serial,
        ["ip"] = f.Ip,
        ["device"] = $"flex:{f.Ip}",
    };

    private static JsonNode UberJson(FoundUberSdr u) => new JsonObject
    {
        ["host"] = u.Host,
        ["device"] = u.Device,
        ["name"] = u.Name,
        ["location"] = u.Location,
        ["free"] = u.Free,
    };
}
