namespace Packet.SoundModem.Daemon;

/// <summary>
/// What an input that stands in for a capture device paces itself against: a clock to read and a
/// way to wait for it to move.
/// </summary>
/// <remarks>
/// A real sound card hands up samples at the rate its own crystal runs, so an input that replays a
/// file or a pipe holds each read until the wall clock has caught up with the sample clock. The
/// reading and the waiting are here, behind one seam, so that a test can give the input a clock
/// that moves only when the input waits for it, and the real one is the default.
/// </remarks>
internal interface IPacer
{
    /// <summary>Milliseconds on this pacer's clock; only differences mean anything.</summary>
    long NowMilliseconds { get; }

    /// <summary>Waits for the clock to move on by <paramref name="milliseconds"/>.</summary>
    void Pause(int milliseconds);
}

/// <summary>The wall clock: the device clock is the point of pacing a replay to real time.</summary>
internal sealed class RealTimePacer : IPacer
{
    /// <summary>The one instance; it holds no state.</summary>
    public static readonly RealTimePacer Instance = new();

    private RealTimePacer()
    {
    }

    /// <inheritdoc />
    public long NowMilliseconds => Environment.TickCount64;

    /// <inheritdoc />
    public void Pause(int milliseconds) => Thread.Sleep(milliseconds);
}
