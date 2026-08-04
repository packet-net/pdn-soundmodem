namespace Packet.SoundModem.Survey;

/// <summary>
/// A fixed window of the most recent channel audio, kept so that a burst can be written out
/// <em>after</em> it has ended - which is the only time its extent is known.
/// </summary>
/// <remarks>
/// <para>
/// The detector reports a burst once it stops, by which point the audio that carried it is
/// already past. Without somewhere to look back into, capturing it would mean either predicting
/// the future or recording everything.
/// </para>
/// <para>
/// Preallocated and never resized: this is written from the receive path, which must not
/// allocate. Single-threaded by contract - the writer and the reader are both the receive path,
/// which copies a burst out synchronously and hands the copy to a background thread to write.
/// </para>
/// </remarks>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private long _written;

    /// <summary>Creates a ring holding <paramref name="capacitySamples"/> of audio.</summary>
    public AudioRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacitySamples, 0);
        _samples = new float[capacitySamples];
    }

    /// <summary>How much audio the ring holds.</summary>
    public int Capacity => _samples.Length;

    /// <summary>Total samples ever written - the clock burst extents are resolved against.</summary>
    public long Written => _written;

    /// <summary>Appends a block. Audio older than <see cref="Capacity"/> is overwritten.</summary>
    public void Write(ReadOnlySpan<float> block)
    {
        // A block bigger than the ring: only its tail can survive, and copying the rest would be
        // work whose result is immediately overwritten.
        ReadOnlySpan<float> tail = block.Length > _samples.Length
            ? block[^_samples.Length..]
            : block;

        // Where the *tail* starts, which is not where the block starts when the head was
        // discarded above - getting this wrong silently shifts every later read by the
        // difference, so the WAV would hold real audio from the wrong moment.
        int at = (int)((_written + (block.Length - tail.Length)) % _samples.Length);
        int first = Math.Min(tail.Length, _samples.Length - at);
        tail[..first].CopyTo(_samples.AsSpan(at));
        tail[first..].CopyTo(_samples.AsSpan(0));
        _written += block.Length;
    }

    /// <summary>
    /// Copies <paramref name="destination"/>.Length samples starting at absolute sample position
    /// <paramref name="from"/>. False when that range has already been overwritten or has not
    /// been written yet - a capture that arrives too late is skipped, not filled with silence
    /// that would read as a dead channel.
    /// </summary>
    public bool TryCopy(long from, Span<float> destination)
    {
        long to = from + destination.Length;
        if (from < 0 || to > _written || from < _written - _samples.Length)
        {
            return false;
        }

        int at = (int)(from % _samples.Length);
        int first = Math.Min(destination.Length, _samples.Length - at);
        _samples.AsSpan(at, first).CopyTo(destination);
        _samples.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
        return true;
    }
}
