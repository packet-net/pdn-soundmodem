using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The station's PTT as the transmitter sees it, with one extra door: <see cref="Release"/>, the
/// last unkey before a forced exit (issue #790).
/// </summary>
/// <remarks>
/// <para>The last unkey runs on a thread of its own while the transmitter may still be inside a
/// keyup, so every call goes through one lock: the two can never interleave inside a PTT that is
/// not built for it (a CM108's hidraw writes, an arbitrated Flex keyup part way through its
/// claim). <see cref="Release"/> marks the PTT first and then waits its turn, so a keyup already
/// under way finishes and is then unkeyed, and any keyup asked for after it is refused. Nothing in
/// this process keys the radio again once it has been released.</para>
/// </remarks>
internal sealed class ReleasablePtt(IPttControl inner) : IPttControl
{
    /// <summary>What a keyup asked for after the release is told.</summary>
    internal const string Refusal =
        "the station is stopping and has released its transmitter; no further keyup";

    private readonly Lock _gate = new();
    private volatile bool _released;

    /// <summary>Whether <see cref="Release"/> has been called.</summary>
    internal bool Released => _released;

    /// <inheritdoc />
    public void Key()
    {
        lock (_gate)
        {
            if (_released)
            {
                throw new InvalidOperationException(Refusal);
            }

            inner.Key();
        }
    }

    /// <inheritdoc />
    public void Unkey()
    {
        lock (_gate)
        {
            inner.Unkey();
        }
    }

    /// <summary>Refuses every later keyup, then unkeys once any keyup in progress has finished.</summary>
    public void Release()
    {
        _released = true;
        lock (_gate)
        {
            inner.Unkey();
        }
    }
}
