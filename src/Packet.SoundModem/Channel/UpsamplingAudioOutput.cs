using M0LTE.Radio.Audio;
using M0LTE.Dsp;

namespace Packet.SoundModem.Channel;

/// <summary>
/// Presents a card-native-rate sink (e.g. ALSA at 48 kHz) as an <see cref="IAudioOutput"/>
/// at the modem DSP rate, upsampling with proper image rejection on the way through.
/// Needed because consumer audio devices commonly refuse to open at 12 kHz directly
/// (observed: "snd_pcm_set_params: Invalid argument" for 12 kHz playback on an HDA codec).
/// </summary>
public sealed class UpsamplingAudioOutput : IAudioOutput, ITransmitLead, IDisposable
{
    private readonly IAudioOutput _inner;
    private readonly Upsampler _upsampler;
    private float[] _buffer = [];

    /// <summary>Wraps <paramref name="inner"/> (at its native rate) as a sink at
    /// <paramref name="dspRate"/>. The native rate must be an integer multiple.</summary>
    public UpsamplingAudioOutput(IAudioOutput inner, int dspRate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner.SampleRate % dspRate != 0 || inner.SampleRate == dspRate)
        {
            throw new ArgumentException(
                $"inner rate {inner.SampleRate} must be an integer multiple of {dspRate}", nameof(inner));
        }

        _inner = inner;
        SampleRate = dspRate;
        _upsampler = new Upsampler(inner.SampleRate, inner.SampleRate / dspRate);
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        int needed = _upsampler.OutputLength(samples.Length);
        if (_buffer.Length < needed)
        {
            _buffer = new float[needed];
        }

        _upsampler.Process(samples, _buffer.AsSpan(0, needed));
        _inner.Write(_buffer.AsSpan(0, needed));
    }

    /// <inheritdoc />
    public void Drain() => _inner.Drain();

    /// <summary>The inner output's answer, or none if it has no lead to offer.</summary>
    public TimeSpan PromiseLead() => (_inner as ITransmitLead)?.PromiseLead() ?? TimeSpan.Zero;

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
