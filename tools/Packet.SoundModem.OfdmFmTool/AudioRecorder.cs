using System.Threading.Channels;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>
/// Writes the audio the modem was given to a 16-bit mono WAV, for later analysis. <see cref="Tap"/> runs on the receive thread and only queues a copy; a task of its own writes the file.
/// </summary>
internal sealed class AudioRecorder : IAsyncDisposable
{
    private readonly PcmWavWriter _wav;
    private readonly Channel<float[]> _queue = System.Threading.Channels.Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public AudioRecorder(string path, int sampleRate)
    {
        _wav = new PcmWavWriter(path, sampleRate, channels: 1);
        _writer = Task.Run(Drain);
    }

    /// <summary>A <see cref="Packet.SoundModem.Channel.ReceiveTap"/>.</summary>
    public void Tap(ReadOnlySpan<float> samples) => _queue.Writer.TryWrite(samples.ToArray());

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
        _wav.Dispose();
    }

    private async Task Drain()
    {
        await foreach (float[] block in _queue.Reader.ReadAllAsync())
        {
            _wav.WriteSamples(block);
        }
    }
}
