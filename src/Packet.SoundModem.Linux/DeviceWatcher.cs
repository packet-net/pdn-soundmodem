using System.Runtime.Versioning;

namespace Packet.SoundModem.Linux;

/// <summary>
/// Notices radio interfaces arriving and leaving: sound cards, hidraw nodes and USB serial ports
/// appearing in or disappearing from <c>/dev</c>.
/// </summary>
/// <remarks>
/// Watches the device nodes rather than listening to udev's netlink socket: a node is what a
/// station opens, it appears only once udev has finished with it (permissions and all), and
/// inotify on <c>/dev</c> needs nothing but the filesystem. One plug is a burst of nodes (a card's
/// control and PCMs, a hidraw, a ttyACM), so the burst is reported once, after it goes quiet.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class DeviceWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ITimer _settle;
    private readonly object _gate = new();
    private bool _watchingSound;
    private bool _disposed;

    /// <summary>Starts watching.</summary>
    /// <param name="time">The clock the settling delay runs on.</param>
    public DeviceWatcher(TimeProvider? time = null)
    {
        _settle = (time ?? TimeProvider.System).CreateTimer(_ => Changed?.Invoke(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        WatchSound();

        // "snd" too: on a machine with no sound card, /dev/snd itself only appears with the
        // first one, and its nodes are then watched from that moment.
        Watch("/dev", "hidraw*", "ttyACM*", "ttyUSB*", "snd");
    }

    /// <summary>Raised, on a timer thread, once a burst of arrivals or departures has settled.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _settle.Dispose();
    }

    private void WatchSound()
    {
        lock (_gate)
        {
            if (!_watchingSound && !_disposed && Directory.Exists("/dev/snd"))
            {
                _watchingSound = Watch("/dev/snd", "pcmC*");
            }
        }
    }

    private bool Watch(string directory, params string[] filters)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = false };
        foreach (string filter in filters)
        {
            watcher.Filters.Add(filter);
        }

        watcher.Created += (_, e) =>
        {
            if (e.Name == "snd")
            {
                WatchSound();
            }

            Poke();
        };
        watcher.Deleted += (_, _) => Poke();
        watcher.EnableRaisingEvents = true;
        lock (_gate)
        {
            _watchers.Add(watcher);
        }

        return true;
    }

    private void Poke() => _settle.Change(Settle, Timeout.InfiniteTimeSpan);
}
