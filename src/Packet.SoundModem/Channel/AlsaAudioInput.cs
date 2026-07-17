using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>ALSA-backed <see cref="IAudioInput"/> — a thin float-converting wrapper over an
/// <see cref="AlsaPcm"/> capture stream (the daemon previously opened <see cref="AlsaPcm"/>
/// directly and converted in the loop).</summary>
public sealed class AlsaAudioInput : IAudioInput, IDisposable
{
    private readonly AlsaPcm _pcm;
    private short[] _buffer = [];

    /// <summary>Opens a mono capture stream on <paramref name="device"/>.</summary>
    /// <param name="device">ALSA device name.</param>
    /// <param name="sampleRate">Capture rate (card-native; the daemon decimates).</param>
    /// <param name="latencyMicroseconds">Buffer target — larger rides out device hiccups
    /// (the daemon uses a deeper buffer for ARDOP).</param>
    public AlsaAudioInput(string device, int sampleRate, int latencyMicroseconds = 120_000)
    {
        _pcm = AlsaPcm.Open(
            device, AlsaPcm.Direction.Capture, channels: 1, sampleRate, latencyMicroseconds);
        SampleRate = sampleRate;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>Xruns recovered so far (capture overruns) — see <see cref="AlsaPcm.Xruns"/>.</summary>
    public int Xruns => _pcm.Xruns;

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (_buffer.Length < destination.Length)
        {
            _buffer = new short[destination.Length];
        }

        int got = _pcm.Read(_buffer.AsSpan(0, destination.Length));
        for (int i = 0; i < got; i++)
        {
            destination[i] = _buffer[i] / 32768f;
        }

        return got;
    }

    /// <inheritdoc />
    public void Dispose() => _pcm.Dispose();
}
