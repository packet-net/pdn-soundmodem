using M0LTE.Radio.Audio;

namespace Packet.SoundModem.FlexRadio;

/// <summary>
/// The Flex slice PTT, with a bound on how long a keyup or an unkey may wait for the radio
/// (issue #790).
/// </summary>
/// <remarks>
/// <para>M0LTE.Flex's PTTs send their commands and wait for the radio's answer with no timeout.
/// A command sent after the radio has ended the session gets no answer ever (the client fails
/// only the commands pending when its read loop stops), so a radio lost mid-keyup left the
/// transmitter loop blocked for good, and the station could not stop. This gives up instead: when
/// the session has ended, or the call has run past its limit, it throws and says the radio is
/// gone, and the call it abandoned is left blocked on a pool thread with nothing waiting on it.
/// The proper fix belongs in M0LTE.Flex (a command sent after the session ended should fail at
/// once); until then this is where the wait is bounded.</para>
/// <para>The keyup's limit is generous because an arbitrated keyup legitimately waits for another
/// station to clear the radio before it claims it; the unkey's is short, because nothing about
/// releasing the transmitter should take seconds.</para>
/// </remarks>
internal sealed class BoundedFlexPtt : IPttControl, IDisposable
{
    /// <summary>How long an unkey may wait for the radio.</summary>
    internal static readonly TimeSpan UnkeyLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly IPttControl _inner;
    private readonly Func<bool> _sessionAlive;
    private readonly TimeSpan _keyLimit;
    private readonly TimeSpan _unkeyLimit;

    /// <param name="inner">The PTT that talks to the radio.</param>
    /// <param name="sessionAlive">Whether the radio's session is still up.</param>
    /// <param name="keyLimit">How long a keyup may take, arbitration waits included.</param>
    /// <param name="unkeyLimit">How long an unkey may take; <see cref="UnkeyLimit"/> when null.</param>
    internal BoundedFlexPtt(IPttControl inner, Func<bool> sessionAlive, TimeSpan keyLimit, TimeSpan? unkeyLimit = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sessionAlive);
        _inner = inner;
        _sessionAlive = sessionAlive;
        _keyLimit = keyLimit;
        _unkeyLimit = unkeyLimit ?? UnkeyLimit;
    }

    /// <summary>The PTT this one bounds.</summary>
    internal IPttControl Inner => _inner;

    /// <summary>Raised, with the sentence to journal, when a call is abandoned because the radio
    /// did not answer: the host treats that as the radio being gone.</summary>
    internal event Action<string>? RadioUnanswered;

    /// <inheritdoc />
    public void Key() => Bounded(_inner.Key, "keyup", _keyLimit, abandoningKeyup: true);

    /// <inheritdoc />
    public void Unkey() => Bounded(_inner.Unkey, "unkey", _unkeyLimit, abandoningKeyup: false);

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();

    private void Bounded(Action call, string what, TimeSpan limit, bool abandoningKeyup)
    {
        Task running = Task.Factory.StartNew(
            call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        long started = Environment.TickCount64;
        while (!running.Wait(Poll))
        {
            bool alive = _sessionAlive();
            if (alive && Environment.TickCount64 - started < (long)limit.TotalMilliseconds)
            {
                continue;
            }

            // Read the fault, if it ever comes, so it is never an unobserved task exception.
            _ = running.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

            // A keyup given up on while the session is still up may yet key the radio: the
            // abandoned call can still send its xmit 1 after this. So unkey now, for a claim it
            // has already made, and again once it finishes, for one it makes later. Neither waits
            // for the radio's answer; a session that has ended has nothing to unkey.
            if (abandoningKeyup && alive)
            {
                FireAndForgetUnkey();
                _ = running.ContinueWith(
                    _ => FireAndForgetUnkey(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            string why = alive
                ? $"flex: the radio did not answer the {what} in {limit.TotalSeconds:0.#} s; treating it as gone"
                : $"flex: the radio's session ended during the {what}";
            RadioUnanswered?.Invoke(why);
            throw new TimeoutException(why);
        }

        // Rethrows the call's own exception, unwrapped, exactly as calling it directly would.
        running.GetAwaiter().GetResult();
    }

    /// <summary>One unkey on a thread of its own, waited for by nobody.</summary>
    private void FireAndForgetUnkey()
    {
        Task unkey = Task.Factory.StartNew(
            _inner.Unkey, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = unkey.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
}
