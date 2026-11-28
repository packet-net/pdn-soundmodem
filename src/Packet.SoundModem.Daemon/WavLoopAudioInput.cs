using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Replays a WAV file as if it were a live capture device: reads pace out at wall-clock
/// rate and the file repeats forever with a half-second silence gap between passes. Where
/// <c>--wav</c> decodes a file flat-out and exits, this drives the whole live daemon -
/// KISS, waterfall, the lot - from a recording, with no soundcard or radio involved
/// (demos, UI work, bench boxes).
/// </summary>
internal sealed class WavLoopAudioInput : IAudioInput
{
    private readonly float[] _samples;
    private long _position;
    private readonly IPacer _pacer;
    private long _startMilliseconds = -1;

    /// <param name="pacer">What reads are paced against; null is the wall clock.</param>
    public WavLoopAudioInput(string path, IPacer? pacer = null)
    {
        _pacer = pacer ?? RealTimePacer.Instance;
        (float[] audio, int rate) = WavFile.ReadMono(path);
        SampleRate = rate;
        _samples = new float[audio.Length + rate / 2];
        audio.CopyTo(_samples, 0);
    }

    public int SampleRate { get; }

    public int Read(Span<float> buffer)
    {
        if (_startMilliseconds < 0)
        {
            _startMilliseconds = _pacer.NowMilliseconds;
        }

        // Hold each read until wall clock catches up with the sample clock, so the daemon
        // sees the recording at the rate a capture device would deliver it.
        long dueMilliseconds = _position * 1000 / SampleRate;
        long aheadBy = _startMilliseconds + dueMilliseconds - _pacer.NowMilliseconds;
        if (aheadBy > 0)
        {
            _pacer.Pause((int)aheadBy);
        }

        for (int n = 0; n < buffer.Length; n++)
        {
            buffer[n] = _samples[_position % _samples.Length];
            _position++;
        }

        return buffer.Length;
    }
}

/// <summary>Discards transmit audio (the WAV-loop daemon has no transmitter to feed).</summary>
internal sealed class NullAudioOutput(int sampleRate) : IAudioOutput
{
    public int SampleRate { get; } = sampleRate;

    public void Write(ReadOnlySpan<float> samples)
    {
    }

    public void Drain()
    {
    }
}
