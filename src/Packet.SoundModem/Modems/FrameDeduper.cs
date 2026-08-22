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

    /// <summary>
    /// The receiver acquired a carrier after losing it. Whatever was heard before belongs to an
    /// earlier transmission, so nothing remembered may suppress what this one carries: a
    /// station that dropped carrier and keyed up again chose to send those bytes again, however
    /// quickly it did so, and dedupe exists to merge copies of one transmission, never to
    /// second-guess a sender (issue #342: a byte-identical ARQ retransmission is the ordinary
    /// AX.25 repair, and a time window wide enough to catch it stalls the link). The time
    /// window stays as the fallback for the case where the acquisition boundary is not seen,
    /// e.g. a channel so busy the carrier detect never falls between bursts.
    /// </summary>
    public void CarrierAcquired() => _recent.Clear();

    /// <summary>Records a copy that is going to the host regardless of what the window holds,
    /// so that a later copy of the same transmission arriving by another route still dedupes
    /// against it. For decode routes that can never legitimately produce a duplicate: the
    /// embedded-HDLC reading of an FX.25 block always precedes its own block's FX.25 reading,
    /// so nothing already remembered can be a copy of it, and suppressing it on content alone
    /// would eat a genuine retransmission (issue #342). Replaces any remembered entry for the
    /// same bytes, so the suppression that follows is anchored at this copy, not a stale
    /// one.</summary>
    public void RecordDelivery(ReadOnlySpan<byte> frame, long now)
    {
        ulong hash = Hash(frame);
        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (_recent[i].Hash == hash)
            {
                _recent.RemoveAt(i);
            }
        }

        _recent.Add((hash, now, true));
    }

    /// <summary>Returns true if the frame was not already emitted within the window
    /// ending at <paramref name="now"/> (in samples), recording it if so.</summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="now">The receiver's sample clock.</param>
    /// <param name="delivered">Whether this copy is going to the host. A copy that is not only
    /// suppresses later copies that are also not; the first one that is gets through and takes
    /// the window's entry over.</param>
    public bool ShouldEmit(ReadOnlySpan<byte> frame, long now, bool delivered = true)
    {
        ulong hash = Hash(frame);

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

    private static ulong Hash(ReadOnlySpan<byte> frame)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in frame)
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        return hash;
    }
}
