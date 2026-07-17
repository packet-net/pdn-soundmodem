using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>
/// Blocking, device-paced audio source for receive — the capture-side mirror of
/// <see cref="IAudioOutput"/>. The daemon reads from one of these and decimates to the DSP
/// rate, so an ALSA card and a FlexRadio DAX stream present the same shape.
/// </summary>
public interface IAudioInput
{
    /// <summary>Source sample rate; the daemon decimates from here to the DSP rate.</summary>
    int SampleRate { get; }

    /// <summary>Reads up to <paramref name="destination"/>.Length samples as normalised
    /// floats (−1..1). Blocks until at least one sample is available; returns the count
    /// written (0 only when the source is closing).</summary>
    int Read(Span<float> destination);
}

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
