namespace Packet.SoundModem.Daemon;

/// <summary>Why a reply was taken back rather than transmitted.</summary>
internal enum ArdopReplyDrop
{
    /// <summary>It went out.</summary>
    None,

    /// <summary>It could not reach the air inside its turnaround.</summary>
    Deadline,

    /// <summary>The far end transmitted again, so the gap it belonged in has closed.</summary>
    FarEndTransmitted,
}

/// <summary>
/// Keeps this station's ARDOP replies inside the turnaround they belong to: the burst waiting for
/// the channel is taken back rather than transmitted once the gap it was meant for has closed.
/// </summary>
/// <remarks>
/// <para><b>Why a late burst is worse than no burst.</b> ARQ is built around a frame that never
/// arrives: the far end waits its turnaround, hears nothing and repeats. A reply that arrives
/// after that gap has closed is not an answer, it is interference - it lands while the far end is
/// transmitting or has moved on to something else. On GB7RDG a run of them went out as seven
/// consecutive keyups answering a call that had been given up on twenty seconds earlier. Dropping
/// the reply leaves the far end doing exactly what it would have done anyway.</para>
/// <para><b>Why ardopcf needs none of this.</b> It owns its sound card and blocks in playout, so
/// a reply starts at the turnaround the engine scheduled it for and cannot be late. This station
/// shares one transmitter between ARDOP and the packet modems, so a reply can sit behind a packet
/// keyup that is already under way and cannot be taken back. The sharing is the reason the
/// deadline has to exist here and does not exist there.</para>
/// <para><b>Two ways the gap closes.</b> The deadline passes, or the far end transmits again. The
/// second is exact rather than a timer: anything the demodulator recovers is proof that the
/// silence the reply belonged in is over. A failed decode counts too - the channel carried a
/// burst either way, and that is what makes the reply stale.</para>
/// <para>One burst is outstanding at a time, because the TNC's transmit worker plays them one at
/// a time. That is what makes <see cref="TakeDrop"/> exact: the drop it reports is the burst
/// whose <c>FrameTransmitted</c> is being raised.</para>
/// </remarks>
internal sealed class ArdopReplyWindow(TimeProvider time)
{
    /// <summary>
    /// How long a reply may wait for the channel past the turnaround it was scheduled for.
    /// </summary>
    /// <remarks>
    /// One second, and it is the protocol's own figure rather than a fitted one: the ARQ engine
    /// floors every inter-frame interval it computes at 1000 ms (<c>ComputeInterFrameInterval</c>,
    /// M0LTE.Ardop), which is the shortest time ARDOP admits a station may wait before acting
    /// again. A reply that has not started by then cannot be landing in a gap the far end is
    /// still holding open. Measured from the hand-off to the channel, which is already past the
    /// engine's own 250 ms turnaround wait, so EXTRADELAY does not eat into it.
    /// </remarks>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private CancellationTokenSource? _window;
    private ArdopReplyDrop _reason = ArdopReplyDrop.Deadline;
    private ArdopReplyDrop _dropped;

    /// <summary>
    /// Opens the window for one burst. Withdraw the burst from the channel on the returned token.
    /// </summary>
    internal CancellationToken Open()
    {
        lock (_gate)
        {
            _window?.Dispose();
            _dropped = ArdopReplyDrop.None;

            // A window nothing else closes runs out, so that is what an unexplained cancellation
            // is: the timer is the only other thing that can fire this token.
            _reason = ArdopReplyDrop.Deadline;
            _window = new CancellationTokenSource(Deadline, time);
            return _window.Token;
        }
    }

    /// <summary>Something arrived from the air, so any reply still waiting has missed its gap.</summary>
    internal void FarEndTransmitted()
    {
        CancellationTokenSource window;
        lock (_gate)
        {
            if (_window is null || _window.IsCancellationRequested)
            {
                return;
            }

            _reason = ArdopReplyDrop.FarEndTransmitted;
            window = _window;
        }

        // Cancelled outside the lock: this runs the channel's withdrawal callback inline, and
        // that callback takes the channel's own transmit lock. Two locks held in one order here
        // and the other order there is how a station stops transmitting for good.
        try
        {
            window.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Closed underneath us by the burst finishing. Nothing left to take back.
        }
    }

    /// <summary>The channel took the burst back; record why, for the line that says so.</summary>
    internal void NoteDropped()
    {
        lock (_gate)
        {
            _dropped = _reason;
        }
    }

    /// <summary>
    /// Why the burst just finished was dropped, or <see cref="ArdopReplyDrop.None"/> when it went
    /// out. Reading it clears it, so one drop is reported once.
    /// </summary>
    internal ArdopReplyDrop TakeDrop()
    {
        lock (_gate)
        {
            ArdopReplyDrop dropped = _dropped;
            _dropped = ArdopReplyDrop.None;
            return dropped;
        }
    }

    /// <summary>The burst is finished, one way or the other; stop its timer.</summary>
    /// <remarks>Does not clear the drop - that is <see cref="TakeDrop"/>'s, and it is read after
    /// this is called.</remarks>
    internal void Close()
    {
        lock (_gate)
        {
            _window?.Dispose();
            _window = null;
        }
    }
}
