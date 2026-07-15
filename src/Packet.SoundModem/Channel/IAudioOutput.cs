using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>Blocking, device-paced audio sink for transmit.</summary>
public interface IAudioOutput
{
    /// <summary>Sink sample rate; must match the samples written.</summary>
    int SampleRate { get; }

    /// <summary>Writes samples; blocks while the device consumes them.</summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>Blocks until everything written has actually left the device — the
    /// sample-domain part of PTT release.</summary>
    void Drain();
}

/// <summary>ALSA-backed <see cref="IAudioOutput"/>.</summary>
public sealed class AlsaAudioOutput : IAudioOutput, IDisposable
{
    private readonly AlsaPcm _pcm;
    private short[] _buffer = [];

    /// <summary>Opens a mono playback stream on <paramref name="device"/>.</summary>
    public AlsaAudioOutput(string device, int sampleRate)
    {
        _pcm = AlsaPcm.Open(device, AlsaPcm.Direction.Playback, channels: 1, sampleRate);
        SampleRate = sampleRate;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        if (_buffer.Length < samples.Length)
        {
            _buffer = new short[samples.Length];
        }

        for (int i = 0; i < samples.Length; i++)
        {
            _buffer[i] = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
        }

        _pcm.Write(_buffer.AsSpan(0, samples.Length));
    }

    /// <inheritdoc />
    public void Drain() => _pcm.Drain();

    /// <inheritdoc />
    public void Dispose() => _pcm.Dispose();
}
