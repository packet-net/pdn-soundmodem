namespace Packet.SoundModem.Modems;

/// <summary>
/// Suppresses repaired-frame echoes of bursts the receiver already decoded cleanly. A
/// diversity bank (or one demodulator's timing phases) reads every burst many ways; when one
/// read decodes it clean and another read of the SAME burst is damaged, the damaged read's
/// repair can pass the FCS by coincidence with a few bytes differing from the clean copy -
/// a false duplicate the content-hash deduplication cannot see, because its bytes differ by
/// exactly the damage the repair half-healed. Measured on the WA8LMF corpus: this class of
/// false delivery outnumbered the genuine repairs before the gate existed.
/// </summary>
/// <remarks>
/// The rule is proximity, not equality: a repaired frame landing within
/// <see cref="WindowFraction"/> of a clean delivery and differing from it by at most
/// <see cref="HammingThreshold"/> bytes (at equal length) is the same transmission and is
/// dropped. Genuine repairs survive by construction - they are the bursts NO read decoded
/// cleanly, so there is no clean neighbour to echo. The threshold sits above the byte damage
/// a one-to-three-bit repair leaves beside a clean copy (measured 2-6 bytes on the corpus's
/// echo population) and far below the distance between two genuinely different frames of one
/// station (position reports differing in timestamp and coordinates run 8+ bytes apart, and
/// those arrive seconds apart, outside the window - the window is what makes the threshold
/// safe, since distinct transmissions of near-identical content are burst-times apart).
/// Timestamps are the receiver's own sample clock at delivery, in whatever unit the caller
/// counts it.
/// </remarks>
internal sealed class RepairEchoGate
{
    /// <summary>How far either side of a clean delivery a repaired copy still counts as its
    /// echo, as a fraction of a second: covers the bank's cross-branch delivery skew (tens of
    /// milliseconds) plus the chunk-end emission quantisation (100 ms), with margin.</summary>
    internal const double WindowFraction = 0.35;

    /// <summary>Largest byte distance from a clean copy that still reads as the same
    /// transmission - see the type remarks.</summary>
    internal const int HammingThreshold = 10;

    private readonly double _window;
    private readonly List<(byte[] Frame, long At)> _clean = [];

    /// <summary>Creates a gate on a receiver's sample clock.</summary>
    /// <param name="sampleRate">The clock's rate, for sizing the window.</param>
    internal RepairEchoGate(int sampleRate) => _window = WindowFraction * sampleRate;

    /// <summary>Remembers a clean delivery, for the repaired copies that follow or preceded
    /// it within the window.</summary>
    internal void RecordClean(ReadOnlySpan<byte> frame, long at)
    {
        Prune(at);
        _clean.Add((frame.ToArray(), at));
    }

    /// <summary>Whether a repaired frame is an echo of a clean delivery within the window -
    /// close in time, equal in length, and within <see cref="HammingThreshold"/> bytes of
    /// identical.</summary>
    internal bool IsEcho(ReadOnlySpan<byte> repaired, long at)
    {
        Prune(at);
        foreach ((byte[] frame, long cleanAt) in _clean)
        {
            if (Math.Abs(at - cleanAt) <= _window
                && frame.Length == repaired.Length
                && Hamming(frame, repaired) <= HammingThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets everything, e.g. at a carrier re-acquisition boundary.</summary>
    internal void Clear() => _clean.Clear();

    private void Prune(long now)
    {
        _clean.RemoveAll(entry => now - entry.At > _window);
    }

    private static int Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int distance = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                distance++;
            }
        }

        return distance;
    }
}
