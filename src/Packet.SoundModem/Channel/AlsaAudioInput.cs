using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>ALSA-backed <see cref="IAudioInput"/> - a thin float-converting wrapper over an
/// <see cref="AlsaPcm"/> capture stream (the daemon previously opened <see cref="AlsaPcm"/>
/// directly and converted in the loop).</summary>
public sealed class AlsaAudioInput : IAudioInput, IDisposable
{
    private readonly AlsaPcm _pcm;
    private short[] _buffer = [];

    /// <summary>Opens a mono capture stream on <paramref name="device"/>.</summary>
    /// <param name="device">ALSA device name.</param>
    /// <param name="sampleRate">Capture rate (card-native; the daemon decimates).</param>
    /// <param name="bufferMicroseconds">How long the receive loop may be away before the card
    /// overruns. Half a second by default, which is not about steady-state latency (the period
    /// stays short, and a read still returns as soon as its frames are there) but about the one
    /// stall that is certain: the first pass through a modem, JIT-compiled and building its
    /// filters, took 150 ms on a Pi and overran a 120 ms buffer at every start-up.</param>
    public AlsaAudioInput(string device, int sampleRate, int bufferMicroseconds = 500_000)
    {
        _pcm = AlsaPcm.Open(
            device, AlsaPcm.Direction.Capture, channels: 1, sampleRate, bufferMicroseconds);
        SampleRate = sampleRate;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>The buffer the card gave us, in milliseconds (0 if it would not say).</summary>
    public int BufferMilliseconds => _pcm.BufferMilliseconds;

    /// <summary>The period the card gave us, in milliseconds (0 if it would not say).</summary>
    public int PeriodMilliseconds => _pcm.PeriodMilliseconds;

    /// <summary>Why the buffer asked for was not the one used, or null when it was - see
    /// <see cref="AlsaPcm.ConfigurationWarning"/>.</summary>
    public string? ConfigurationWarning => _pcm.ConfigurationWarning;

    /// <summary>Xruns recovered so far (capture overruns) - see <see cref="AlsaPcm.Xruns"/>.</summary>
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
            destination[i] = Audio.Pcm16.ToFloat(_buffer[i]);
        }

        return got;
    }

    /// <inheritdoc />
    public void Dispose() => _pcm.Dispose();
}
