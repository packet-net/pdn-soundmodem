using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>ALSA-backed <see cref="IAudioOutput"/>. The stream runs from the moment it is opened
/// until it is disposed, playing silence between transmissions (issue #828): a transmission is
/// everything from a first <see cref="Write"/> to the <see cref="Drain"/> that follows it.</summary>
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
            _buffer[i] = Audio.Pcm16.FromFloat(samples[i]);
        }

        _pcm.Write(_buffer.AsSpan(0, samples.Length));
    }

    /// <summary>Xruns recovered so far (playback underruns) - see <see cref="AlsaPcm.Xruns"/>.
    /// Every one is audio the transmitter did not send, recovered silently.</summary>
    public int Xruns => _pcm.Xruns;

    /// <summary>Underruns recovered in the silence between transmissions - see
    /// <see cref="AlsaPcm.IdleUnderruns"/>. No transmit audio was lost to them.</summary>
    public int IdleUnderruns => _pcm.IdleUnderruns;

    /// <summary>Why the buffer asked for was not the one used, or null when it was - see
    /// <see cref="AlsaPcm.ConfigurationWarning"/>.</summary>
    public string? ConfigurationWarning => _pcm.ConfigurationWarning;

    /// <summary>Blocks until the last frame written has played. The stream is not stopped.</summary>
    public void Drain() => _pcm.Drain();

    /// <inheritdoc />
    public void Dispose() => _pcm.Dispose();
}
