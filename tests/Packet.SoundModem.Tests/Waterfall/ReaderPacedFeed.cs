namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// Audio fed into a channel at the pace a reader drains it, not at the pace of any clock.
/// </summary>
/// <remarks>
/// The waterfall server's per-client queue is bounded and drops the oldest message when it
/// overflows, so a test that wants one particular message must read while it feeds, as a browser
/// does. The feeder is held to the reader by a signal: each message the reader takes is counted
/// and announced, and after each block the feeder waits until the reader has taken at least as
/// many messages as blocks fed. A block of a tenth of a second yields several spectrum lines, so
/// the reader is never more than a handful of messages behind and the queue never fills; nothing
/// is waited on but the reader's own progress, and the wait ends the moment the reader is done.
/// </remarks>
internal sealed class ReaderPacedFeed
{
    private readonly object _gate = new();
    private TaskCompletionSource _taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _count;

    /// <summary>The reader calls this for every message it takes off the socket.</summary>
    public void Taken()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            _count++;
            done = _taken;
            _taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        done.TrySetResult();
    }

    /// <summary>Feeds <paramref name="audio"/> in blocks, each only once the reader has kept up with the last.</summary>
    public async Task FeedAsync(Action<ReadOnlyMemory<float>> feed, float[] audio, int block, Task reader)
    {
        long fed = 0;
        for (int at = 0; at < audio.Length; at += block)
        {
            feed(audio.AsMemory(at, Math.Min(block, audio.Length - at)));
            fed++;
            while (!reader.IsCompleted)
            {
                // Taken before the count is read, so a message that lands in between is not lost.
                Task next;
                lock (_gate)
                {
                    if (_count >= fed)
                    {
                        break;
                    }

                    next = _taken.Task;
                }

                await Eventually.SignalAsync(Task.WhenAny(next, reader), "the reader to take the audio fed so far");
            }
        }
    }
}
