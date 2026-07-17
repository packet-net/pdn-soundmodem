using Packet.SoundModem.Channel;

namespace Packet.SoundModem.FlexRadio;

// The M0LTE.Flex package exposes its own audio/PTT seams (M0LTE.Flex.IAudioInput/
// IAudioOutput/IPttControl) so it has no dependency on this modem. These thin adapters
// re-present a Flex DAX source/sink/PTT through the soundmodem channel seams
// (Packet.SoundModem.Channel.*) the daemon consumes. The two interface families are
// intentionally identical in shape, so each adapter is a straight delegation.

/// <summary>Presents a Flex DAX-RX source as a soundmodem <see cref="IAudioInput"/>.</summary>
internal sealed class FlexAudioInputAdapter(M0LTE.Flex.IAudioInput inner) : IAudioInput, IDisposable
{
    public int SampleRate => inner.SampleRate;

    public int Read(Span<float> destination) => inner.Read(destination);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>Presents a Flex DAX-TX sink as a soundmodem <see cref="IAudioOutput"/>.</summary>
internal sealed class FlexAudioOutputAdapter(M0LTE.Flex.IAudioOutput inner) : IAudioOutput, IDisposable
{
    public int SampleRate => inner.SampleRate;

    public void Write(ReadOnlySpan<float> samples) => inner.Write(samples);

    public void Drain() => inner.Drain();

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>Presents a Flex slice PTT as a soundmodem <see cref="IPttControl"/>.</summary>
internal sealed class FlexPttAdapter(M0LTE.Flex.IPttControl inner) : IPttControl
{
    public void Key() => inner.Key();

    public void Unkey() => inner.Unkey();
}
