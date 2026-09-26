namespace Packet.SoundModem.Windows;

/// <summary>
/// A fixed-size FIFO of samples between a device thread and a caller. Not thread-safe: both
/// WASAPI wrappers hold their own lock around it, because the lock is also what they wait on.
/// </summary>
internal sealed class SampleRing
{
    private readonly float[] _buffer;
    private int _head;

    public SampleRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new float[capacity];
    }

    /// <summary>How many samples the ring holds when full.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>How many samples are queued.</summary>
    public int Count { get; private set; }

    /// <summary>How many more samples fit before the ring is full.</summary>
    public int Free => _buffer.Length - Count;

    /// <summary>
    /// Appends as many of <paramref name="samples"/> as fit and returns how many that was. Never
    /// overwrites: a writer that must not block (a capture thread) calls <see cref="Discard"/>
    /// first to make room.
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        int n = Math.Min(samples.Length, Free);
        int tail = (_head + Count) % _buffer.Length;
        int first = Math.Min(n, _buffer.Length - tail);
        samples[..first].CopyTo(_buffer.AsSpan(tail));
        samples[first..n].CopyTo(_buffer);
        Count += n;
        return n;
    }

    /// <summary>Takes up to <paramref name="destination"/>.Length samples, oldest first.</summary>
    public int Read(Span<float> destination)
    {
        int n = Math.Min(destination.Length, Count);
        int first = Math.Min(n, _buffer.Length - _head);
        _buffer.AsSpan(_head, first).CopyTo(destination);
        _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
        _head = (_head + n) % _buffer.Length;
        Count -= n;
        return n;
    }

    /// <summary>Drops up to <paramref name="count"/> of the oldest samples; returns how many.</summary>
    public int Discard(int count)
    {
        int n = Math.Clamp(count, 0, Count);
        _head = (_head + n) % _buffer.Length;
        Count -= n;
        return n;
    }

    /// <summary>Empties the ring.</summary>
    public void Clear()
    {
        _head = 0;
        Count = 0;
    }
}
