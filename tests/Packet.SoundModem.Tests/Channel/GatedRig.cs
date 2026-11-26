using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A sound card and PTT line that play nothing by themselves: each <see cref="Write"/> records
/// what it was given and then waits until the test lets it through, so the test, not the clock,
/// decides how far "playback" has got.
/// </summary>
/// <remarks>
/// Nothing here measures time. A test says "let one chunk through, begin stopping, let the rest
/// through" and asserts counts and the order of <see cref="Events"/>. The 30 s timeouts only turn
/// a hang into a failure; no correct run waits anywhere near them.
/// </remarks>
internal sealed class GatedRig(int sampleRate) : IAudioOutput, IPttControl
{
    /// <summary>How long a wait for the code under test may take before it is called a hang.</summary>
    internal static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly List<string> _events = [];
    private readonly List<int> _writes = [];
    private readonly SemaphoreSlim _entered = new(0);
    private readonly SemaphoreSlim _admitted = new(0);

    public int SampleRate { get; } = sampleRate;

    /// <summary>"key", "write" (one per Write, at the moment it is taken) and "unkey", in order.</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The length in samples of every Write taken so far, in order.</summary>
    public IReadOnlyList<int> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    public TaskCompletionSource Keyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Unkeyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once one more Write has been taken (each call consumes one).</summary>
    public async Task NextWriteTakenAsync()
    {
        if (!await _entered.WaitAsync(HangAfter))
        {
            throw new TimeoutException("the transmitter took no further write");
        }
    }

    /// <summary>Lets <paramref name="count"/> blocked (or future) Writes return.</summary>
    public void Admit(int count) => _admitted.Release(count);

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            _writes.Add(samples.Length);
            _events.Add("write");
        }

        _entered.Release();
        if (!_admitted.Wait(HangAfter))
        {
            throw new TimeoutException("a write was never let through");
        }
    }

    public void Drain()
    {
    }

    public void Key()
    {
        lock (_gate)
        {
            _events.Add("key");
        }

        Keyed.TrySetResult();
    }

    public void Unkey()
    {
        lock (_gate)
        {
            _events.Add("unkey");
        }

        Unkeyed.TrySetResult();
    }
}
