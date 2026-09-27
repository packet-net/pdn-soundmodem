using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Linux.Tests;

/// <summary>A card's mixer with whatever controls a test gives it, behaving as AlsaMixer does.</summary>
internal sealed class FakeMixer : IAlsaMixer
{
    private readonly List<Control> _controls = [];

    public string Card => "hw:CARD=Fake";

    public IReadOnlyList<string> Controls => _controls.Select(c => c.Name).ToList();

    public int Refreshes { get; private set; }

    /// <summary>Adds a control; each side has a volume when given a range (null range with
    /// hasVolume is a volume with no dB scale) and a switch when given its state.</summary>
    public FakeMixer Add(
        string name,
        (double Min, double Max, double Level)? capture = null,
        (double Min, double Max, double Level)? playback = null,
        bool? captureSwitch = null,
        bool? playbackSwitch = null,
        bool? globalSwitch = null,
        bool captureWithoutScale = false)
    {
        _controls.Add(new Control(name)
        {
            Capture = capture is { } c ? new Volume(c.Min, c.Max, c.Level) : captureWithoutScale ? new Volume(0, 0, 0) { NoScale = true, Percent = 50 } : null,
            Playback = playback is { } p ? new Volume(p.Min, p.Max, p.Level) : null,
            CaptureSwitch = captureSwitch,
            PlaybackSwitch = playbackSwitch,
            GlobalSwitch = globalSwitch,
        });
        return this;
    }

    public Volume? VolumeOf(string control, MixerDirection direction) =>
        Find(control) is { } c ? (direction == MixerDirection.Capture ? c.Capture : c.Playback) : null;

    public bool? SwitchOf(string control, MixerDirection direction) =>
        Find(control) is { } c ? (direction == MixerDirection.Capture ? c.CaptureSwitch : c.PlaybackSwitch) : null;

    public bool? GlobalSwitchOf(string control) => Find(control)?.GlobalSwitch;

    /// <summary>Moves a level as alsamixer in another window would.</summary>
    public void SetElsewhere(string control, MixerDirection direction, double db) => VolumeOf(control, direction)!.Level = db;

    public void Refresh() => Refreshes++;

    public bool TrySetDb(string control, MixerDirection direction, double decibels)
    {
        if (VolumeOf(control, direction) is not { NoScale: false } volume)
        {
            return false;
        }

        volume.Level = Math.Clamp(Math.Round(decibels), volume.Min, volume.Max);
        return true;
    }

    public MixerDbRange? ReadDbRange(string control, MixerDirection direction) =>
        VolumeOf(control, direction) is { NoScale: false } v ? new MixerDbRange(v.Min, v.Max, false) : null;

    public bool TrySetVolume(string control, MixerDirection direction, int percent)
    {
        if (VolumeOf(control, direction) is not { } volume)
        {
            return false;
        }

        volume.Percent = percent;
        if (!volume.NoScale)
        {
            volume.Level = volume.Min + ((volume.Max - volume.Min) * percent / 100.0);
        }

        return true;
    }

    public bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels)
    {
        percent = 0;
        decibels = null;
        if (VolumeOf(control, direction) is not { } volume)
        {
            return false;
        }

        percent = volume.NoScale ? volume.Percent : (int)Math.Round((volume.Level - volume.Min) * 100 / (volume.Max - volume.Min));
        decibels = volume.NoScale ? null : volume.Level;
        return true;
    }

    public bool TrySetSwitch(string control, bool on)
    {
        if (Find(control) is not { } c)
        {
            return false;
        }

        if (c.GlobalSwitch is not null)
        {
            c.GlobalSwitch = on;
            return true;
        }

        if (c.CaptureSwitch is not null)
        {
            c.CaptureSwitch = on;
            return true;
        }

        if (c.PlaybackSwitch is not null)
        {
            c.PlaybackSwitch = on;
            return true;
        }

        return false;
    }

    public bool TryReadSwitch(string control, out bool on)
    {
        on = false;
        if (Find(control) is not { } c)
        {
            return false;
        }

        bool? state = c.GlobalSwitch ?? c.CaptureSwitch ?? c.PlaybackSwitch;
        on = state ?? false;
        return state is not null;
    }

    public bool HasVolume(string control, MixerDirection direction) => VolumeOf(control, direction) is not null;

    public bool TrySetSwitch(string control, MixerDirection direction, bool on)
    {
        if (Find(control) is not { } c)
        {
            return false;
        }

        if (direction == MixerDirection.Capture && c.CaptureSwitch is not null)
        {
            c.CaptureSwitch = on;
            return true;
        }

        if (direction == MixerDirection.Playback && c.PlaybackSwitch is not null)
        {
            c.PlaybackSwitch = on;
            return true;
        }

        return false;
    }

    public bool TryReadSwitch(string control, MixerDirection direction, out bool on)
    {
        bool? state = SwitchOf(control, direction);
        on = state ?? false;
        return state is not null;
    }

    public void Dispose()
    {
    }

    private Control? Find(string name) => _controls.FirstOrDefault(c => c.Name == name);

    internal sealed class Volume(double min, double max, double level)
    {
        public double Min { get; } = min;

        public double Max { get; } = max;

        public double Level { get; set; } = level;

        public bool NoScale { get; init; }

        public int Percent { get; set; }
    }

    private sealed class Control(string name)
    {
        public string Name { get; } = name;

        public Volume? Capture { get; init; }

        public Volume? Playback { get; init; }

        public bool? CaptureSwitch { get; set; }

        public bool? PlaybackSwitch { get; set; }

        public bool? GlobalSwitch { get; set; }
    }
}
