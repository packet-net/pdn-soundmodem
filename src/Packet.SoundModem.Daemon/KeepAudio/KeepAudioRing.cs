using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// The last few minutes of receive audio in memory, laid on the wall clock so a stretch can be
/// asked for by UTC time.
/// </summary>
/// <remarks>
/// <para><b>Laid on the wall clock, not counted.</b> The receive tap stops while the station
/// transmits, and a stalled device stops it too, so counting samples from start-up would drift
/// away from real time by every keyup. Each block is placed by the time it arrived instead: a
/// block that turns up more than <see cref="BehindTolerance"/> later than the samples so far
/// account for is preceded by silence, which is what the gap was, and audio that runs ahead of
/// the clock by more than <see cref="AheadTolerance"/> (a sound card a little fast, or a
/// backlog delivered in one go) moves the ring's origin back to match. So any instant is within
/// about a quarter of a second of where it belongs, which is plenty for a per-second log.</para>
/// <para>16-bit, because that is what the kept WAV is and it halves the memory. Not thread-safe:
/// the recorder holds its own lock around every call.</para>
/// </remarks>
internal sealed class KeepAudioRing
{
    /// <summary>A block later than this by the samples' own count is preceded by silence.</summary>
    internal static readonly TimeSpan BehindTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Audio further ahead of the clock than this moves the origin back.</summary>
    internal static readonly TimeSpan AheadTolerance = TimeSpan.FromSeconds(0.25);

    private readonly short[] _buffer;
    private readonly int _rate;
    private long _written;
    private DateTimeOffset? _origin;

    /// <param name="sampleRate">The rate of the audio appended.</param>
    /// <param name="length">How much audio the ring holds.</param>
    public KeepAudioRing(int sampleRate, TimeSpan length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _rate = sampleRate;
        _buffer = new short[checked((int)(length.TotalSeconds * sampleRate))];
    }

    /// <summary>The rate of the audio held.</summary>
    public int SampleRate => _rate;

    /// <summary>The time of the oldest sample held, or null before any audio.</summary>
    public DateTimeOffset? HeldFrom =>
        _origin is { } origin ? origin + Seconds(Math.Max(0, _written - _buffer.Length)) : null;

    /// <summary>The time just after the newest sample held, or null before any audio.</summary>
    public DateTimeOffset? HeldTo => _origin is { } origin ? origin + Seconds(_written) : null;

    /// <summary>Adds one block, whose last sample arrived at <paramref name="endsAt"/>.</summary>
    public void Append(ReadOnlySpan<float> samples, DateTimeOffset endsAt)
    {
        int n = samples.Length;
        if (n == 0)
        {
            return;
        }

        DateTimeOffset origin = _origin ??= endsAt - Seconds(n);
        long expectedEnd = (long)Math.Round((endsAt - origin).TotalSeconds * _rate);
        long end = _written + n;
        long behind = expectedEnd - end;
        if (behind > BehindTolerance.TotalSeconds * _rate)
        {
            Silence(behind);
        }
        else if (-behind > AheadTolerance.TotalSeconds * _rate)
        {
            _origin = origin - Seconds(-behind);
        }

        for (int i = 0; i < n; i++)
        {
            _buffer[(int)((_written + i) % _buffer.Length)] = Pcm16.FromFloat(samples[i]);
        }

        _written += n;
    }

    /// <summary>
    /// The audio from <paramref name="from"/> to <paramref name="to"/>, with silence wherever the
    /// ring holds nothing, and how many of its samples were really held.
    /// </summary>
    public (float[] Samples, int Held) Extract(DateTimeOffset from, DateTimeOffset to)
    {
        int count = Math.Max(0, (int)Math.Round((to - from).TotalSeconds * _rate));
        var samples = new float[count];
        if (_origin is not { } origin)
        {
            return (samples, 0);
        }

        long first = (long)Math.Round((from - origin).TotalSeconds * _rate);
        long oldest = Math.Max(0, _written - _buffer.Length);
        int held = 0;
        for (int i = 0; i < count; i++)
        {
            long index = first + i;
            if (index >= oldest && index < _written)
            {
                samples[i] = _buffer[(int)(index % _buffer.Length)] / 32768f;
                held++;
            }
        }

        return (samples, held);
    }

    private void Silence(long count)
    {
        // A gap longer than the ring wipes all of it; only the last ring's worth needs writing.
        long from = Math.Max(_written, _written + count - _buffer.Length);
        for (long index = from; index < _written + count; index++)
        {
            _buffer[(int)(index % _buffer.Length)] = 0;
        }

        _written += count;
    }

    private TimeSpan Seconds(long samples) => TimeSpan.FromSeconds((double)samples / _rate);
}
