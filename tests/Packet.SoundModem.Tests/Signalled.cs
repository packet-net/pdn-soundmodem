using System.Collections;

namespace Packet.SoundModem.Tests;

/// <summary>
/// A list a test double or handler records into, that a test can wait on without polling: every
/// change wakes the waiters, and what they were waiting for is looked at again.
/// </summary>
/// <remarks>
/// <para>The wait is on the change, not on a pause: <see cref="WhenAsync(Func{IReadOnlyList{T}, bool}, string)"/>
/// takes the signal for the next change before it looks at the list, so a change between the look and
/// the wait is not lost, and a condition that already holds returns at once. The limit is the same
/// generous hang guard as <see cref="Eventually"/>; it only turns "this never happens" into a failure
/// that says what was awaited and what the list held.</para>
/// <para>Reads (<see cref="Count"/>, the indexer, enumeration, <see cref="ToArray"/>) are taken under the
/// list's lock and enumerate a snapshot, so a test can use it wherever it used a locked
/// <see cref="List{T}"/>.</para>
/// </remarks>
internal sealed class SignalledList<T> : IReadOnlyList<T>
{
    private readonly ChangeSignal _changes = new();
    private readonly List<T> _items = [];
    private readonly Func<T, string>? _describe;

    /// <param name="describe">How an item reads in a failure message; null is its <c>ToString</c>.</param>
    public SignalledList(Func<T, string>? describe = null) => _describe = describe;

    public int Count
    {
        get
        {
            lock (_items)
            {
                return _items.Count;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_items)
            {
                return _items[index];
            }
        }
    }

    /// <summary>Forgets everything so far, so a later count starts from here.</summary>
    public void Clear()
    {
        lock (_items)
        {
            _items.Clear();
        }

        _changes.Pulse();
    }

    /// <summary>Records one item and wakes whoever is waiting.</summary>
    public void Add(T item)
    {
        lock (_items)
        {
            _items.Add(item);
        }

        _changes.Pulse();
    }

    /// <summary>The items so far, as they stand.</summary>
    public T[] ToArray()
    {
        lock (_items)
        {
            return [.. _items];
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds of the list; fails saying what was awaited
    /// and what the list held if it never does within <see cref="Eventually.HangGuard"/>.</summary>
    public Task WhenAsync(Func<IReadOnlyList<T>, bool> condition, string what) =>
        WhenAsync(condition, what, Eventually.HangGuard);

    /// <summary>Waits until the list holds at least <paramref name="count"/> items.</summary>
    public Task WhenCountAsync(int count, string what) =>
        WhenAsync(items => items.Count >= count, what, Eventually.HangGuard);

    internal Task WhenAsync(Func<IReadOnlyList<T>, bool> condition, string what, TimeSpan limit) =>
        _changes.WhenAsync(
            () => condition(ToArray()),
            what,
            () => $"{Count} item(s): [{string.Join("; ", ToArray().Take(20).Select(i => _describe?.Invoke(i) ?? i?.ToString() ?? "null"))}]",
            limit);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A number a test double counts into, that a test can wait on without polling.</summary>
/// <remarks>Waits as <see cref="SignalledList{T}"/> does: on the change, with the signal taken before the look.</remarks>
internal sealed class SignalledCounter
{
    private readonly ChangeSignal _changes = new();
    private long _value;

    public long Value => Volatile.Read(ref _value);

    /// <summary>Adds one and wakes whoever is waiting; returns the new value.</summary>
    public long Increment()
    {
        long value = Interlocked.Increment(ref _value);
        _changes.Pulse();
        return value;
    }

    /// <summary>Waits until <paramref name="condition"/> holds of the value.</summary>
    public Task WhenAsync(Func<long, bool> condition, string what) =>
        WhenAsync(condition, what, Eventually.HangGuard);

    internal Task WhenAsync(Func<long, bool> condition, string what, TimeSpan limit) =>
        _changes.WhenAsync(() => condition(Value), what, () => $"the value {Value}", limit);
}

/// <summary>The wake-up both of them share: one task per change, replaced as it is completed.</summary>
internal sealed class ChangeSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            done = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        done.TrySetResult();
    }

    private Task Next()
    {
        lock (_gate)
        {
            return _next.Task;
        }
    }

    /// <summary>Waits, with the hang guard, until <paramref name="condition"/> holds; looked at again on every pulse.</summary>
    public Task WhenAsync(Func<bool> condition, string what, Func<string> held) =>
        WhenAsync(condition, what, held, Eventually.HangGuard);

    public async Task WhenAsync(Func<bool> condition, string what, Func<string> held, TimeSpan limit)
    {
        using var guard = new CancellationTokenSource(limit);
        while (true)
        {
            // Taken before the look, so a change in between completes this one and is not lost.
            Task changed = Next();
            if (condition())
            {
                return;
            }

            try
            {
                await changed.WaitAsync(guard.Token);
            }
            catch (OperationCanceledException)
            {
                // One last look, so a change that landed as the limit ran out is not missed.
                if (condition())
                {
                    return;
                }

                throw new TimeoutException(
                    $"waited {limit.TotalSeconds:0.###} s for {what}, and it never happened; it held {held()}");
            }
        }
    }
}
