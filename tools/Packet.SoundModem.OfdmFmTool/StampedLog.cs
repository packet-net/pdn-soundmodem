using System.Text;
using System.Threading.Channels;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>
/// Every line the tool prints: UTC wall time as HH:MM:SS.ss, two spaces, the text. The time is read when <see cref="Write"/> is called, from the injected clock, and the line is queued:
/// the caller may be the modem's audio thread, which must never wait for a console or a disk. One task writes the queue to the sinks, in order.
/// </summary>
internal sealed class StampedLog : IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly Action<string>[] _sinks;
    private readonly Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public StampedLog(TimeProvider time, params Action<string>[] sinks)
    {
        _time = time;
        _sinks = sinks;
        _writer = Task.Run(Drain);
    }

    /// <summary>The line as printed: the time, two spaces, the text with anything but plain ASCII replaced by a question mark.</summary>
    public static string Format(TimeProvider time, string text)
    {
        var line = new StringBuilder(text.Length + 14);
        line.Append(time.GetUtcNow().UtcDateTime.ToString("HH:mm:ss.ff", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
        foreach (char c in text)
        {
            line.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return line.ToString();
    }

    /// <summary>Stamps the line now and queues it. Never blocks.</summary>
    public void Write(string text) => _queue.Writer.TryWrite(Format(_time, text));

    /// <summary>Writes what is queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }

    private async Task Drain()
    {
        await foreach (string line in _queue.Reader.ReadAllAsync())
        {
            foreach (Action<string> sink in _sinks)
            {
                try
                {
                    sink(line);
                }
                catch (IOException)
                {
                    // A full disk must not stop the others.
                }
            }
        }
    }
}
