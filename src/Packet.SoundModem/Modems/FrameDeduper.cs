namespace Packet.SoundModem.Modems;

/// <summary>
/// Content-based frame deduplication over a sliding sample-time window, for receivers
/// that legitimately decode the same transmission more than once (parallel decoder
/// branches; FX.25 alongside its embedded plain-HDLC frame).
/// </summary>
/// <remarks>
/// A frame the receiver reported but did not pass to its host (see
/// <see cref="FrameQuality.MonitorOnly"/>) is remembered as such, and does not stop an identical
/// frame that <em>is</em> for the host from getting through later in the window. Without that
/// distinction a plain-IL2P reading of a burst whose CRC would not verify would eat the window,
/// and a retransmission of the same frame a second later - the ordinary AX.25 answer to a
/// damaged copy, and this time verifying - would be deduped away and never reach the host. Losing
/// a real delivery is a much worse trade than an extra row on a display, which is the only thing
/// the reverse case costs.
/// </remarks>
internal sealed class FrameDeduper(long windowSamples)
{
    private readonly List<(ulong Hash, long At, bool Delivered)> _recent = [];

    /// <summary>Returns true if the frame was not already emitted within the window
    /// ending at <paramref name="now"/> (in samples), recording it if so.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="now">The receiver's sample clock.</param>
    /// <param name="delivered">Whether this copy is going to the host. A copy that is not only
    /// suppresses later copies that are also not; the first one that is gets through and takes
    /// the window's entry over.</param>
    public bool ShouldEmit(ReadOnlySpan<byte> frame, long now, bool delivered = true)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in frame)
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        // Oldest first, so this only ever trims from the front.
        int expired = 0;
        while (expired < _recent.Count && now - _recent[expired].At > windowSamples)
        {
            expired++;
        }

        if (expired > 0)
        {
            _recent.RemoveRange(0, expired);
        }

        for (int i = 0; i < _recent.Count; i++)
        {
            if (_recent[i].Hash != hash)
            {
                continue;
            }

            if (_recent[i].Delivered || !delivered)
            {
                return false;
            }

            // The first copy was shown and withheld; this one is the host's. Let it through and
            // let it own the entry, so a third copy is deduped against a delivery. Re-added at the
            // end rather than updated in place: the list is kept oldest-first so the trim above
            // can stop at the first live entry, and a refreshed timestamp in the middle would
            // hold everything behind it in the window.
            _recent.RemoveAt(i);
            _recent.Add((hash, now, true));
            return true;
        }

        _recent.Add((hash, now, delivered));
        return true;
    }
}
