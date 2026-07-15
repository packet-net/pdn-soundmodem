namespace Packet.SoundModem.Modems;

/// <summary>
/// Content-based frame deduplication over a sliding sample-time window, for receivers
/// that legitimately decode the same transmission more than once (parallel decoder
/// branches; FX.25 alongside its embedded plain-HDLC frame).
/// </summary>
internal sealed class FrameDeduper(long windowSamples)
{
    private readonly Queue<(ulong Hash, long At)> _recent = new();

    /// <summary>Returns true if the frame was not already emitted within the window
    /// ending at <paramref name="now"/> (in samples), recording it if so.</summary>
    public bool ShouldEmit(ReadOnlySpan<byte> frame, long now)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in frame)
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        while (_recent.Count > 0 && now - _recent.Peek().At > windowSamples)
        {
            _recent.Dequeue();
        }

        foreach ((ulong seen, _) in _recent)
        {
            if (seen == hash)
            {
                return false;
            }
        }

        _recent.Enqueue((hash, now));
        return true;
    }
}
