using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// A pacing clock that moves only when the input under test waits for it: the wait is the move.
/// An input paced against it takes as much virtual time as it asks for and no real time at all.
/// </summary>
internal sealed class VirtualPacer : IPacer
{
    private long _now;

    /// <inheritdoc />
    public long NowMilliseconds => Volatile.Read(ref _now);

    /// <inheritdoc />
    public void Pause(int milliseconds) => Interlocked.Add(ref _now, milliseconds);
}
