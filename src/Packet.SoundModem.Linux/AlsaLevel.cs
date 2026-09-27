using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Linux;

/// <summary>
/// One of a card's levels (the receive or the transmit level) in dB, never above 0 dB: the
/// Linux counterpart of the Windows library's <c>EndpointLevel</c>.
/// </summary>
/// <remarks>
/// <para>Changes made anywhere else (alsamixer, the desktop's sound settings, a re-plug) are
/// noticed by reading the card back twice a second, and one that goes above the ceiling is put
/// back to it: the rule is "nothing above 0 dB", not "nothing above 0 dB that we set".</para>
/// <para>The mixer is the caller's and outlives this; <see cref="AlsaMixer"/> is safe to share
/// between the two levels of a card and the thread that polls them.</para>
/// </remarks>
public sealed class AlsaLevel : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAlsaMixer _mixer;
    private readonly MixerDbRange _range;
    private readonly ITimer _poll;
    private readonly object _gate = new();
    private double _last;
    private bool _disposed;

    private AlsaLevel(IAlsaMixer mixer, string control, MixerDirection direction, MixerDbRange range, TimeProvider time)
    {
        _mixer = mixer;
        Control = control;
        Direction = direction;
        _range = range;
        _last = Read() ?? CeilingDb;
        _poll = time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
    }

    /// <summary>Raised, on a timer thread, when the level changes here or anywhere else.</summary>
    public event Action? Changed;

    /// <summary>The control, as the card names it.</summary>
    public string Control { get; }

    /// <summary>Which side of it.</summary>
    public MixerDirection Direction { get; }

    /// <summary>The bottom of the range, dB.</summary>
    public double MinDb => _range.MinDb;

    /// <summary>The highest level that will be set: 0 dB, or lower if the card stops short.</summary>
    public double CeilingDb => MixerHygiene.Ceiling(_range);

    /// <summary>Whether the step below <see cref="MinDb"/> is the card's mute.</summary>
    public bool MutesBelowMin => _range.MutesBelowMin;

    /// <summary>The level, dB, as the card reads back; writes are clamped to
    /// [<see cref="MinDb"/>, <see cref="CeilingDb"/>] and the card takes its nearest step.</summary>
    public double LevelDb
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }

        set
        {
            double wanted = Math.Clamp(value, MinDb, CeilingDb);
            _ = _mixer.TrySetDb(Control, Direction, wanted);
            Poll();
        }
    }

    /// <summary>
    /// A level control for <paramref name="control"/>, or null when the card publishes no dB
    /// scale for it (there is then no way to hold it at 0 dB, and no honest slider to offer).
    /// </summary>
    public static AlsaLevel? Open(IAlsaMixer mixer, string control, MixerDirection direction, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(control);
        return mixer.ReadDbRange(control, direction) is { } range
            ? new AlsaLevel(mixer, control, direction, range, time ?? TimeProvider.System)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _poll.Dispose();
    }

    private double? Read() =>
        _mixer.TryReadVolume(Control, Direction, out _, out double? decibels) ? decibels : null;

    private void Poll()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _mixer.Refresh();
            if (Read() is not double now)
            {
                return;
            }

            if (now > CeilingDb + 0.05)
            {
                // Raised above the ceiling somewhere else: put it back.
                _ = _mixer.TrySetDb(Control, Direction, CeilingDb);
                now = Read() ?? now;
            }

            changed = Math.Abs(now - _last) > 0.01;
            _last = now;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
