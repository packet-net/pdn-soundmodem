namespace Packet.SoundModem.Modems;

/// <summary>
/// A modem that can say, of the frame it has just delivered, which stretch of receive audio it
/// was made of - the sample the frame's sync was taken at and the sample its last bit was taken
/// at, on the same grid as the samples handed to <see cref="IModem.Process"/>.
/// </summary>
/// <remarks>
/// <para><b>Why an interface and not a field on <see cref="FrameQuality"/></b>: it is answered
/// by the demodulator and consumed by the channel, and nothing in between - a KISS host, a frame
/// log, a monitor on the other end of an uplink - has any use for a sample index into audio it
/// never saw. What those do get is what the channel makes of it
/// (<see cref="FrameQuality.PeakDbFs"/>).</para>
/// <para><b>Why the modem is asked rather than the channel inferring it</b> (issue #426, and the
/// review of the first cut of it): a frame is reported from inside the modem's own per-sample
/// loop, so the channel sees only the block it happened in - 100 ms on a packet station, longer
/// than a whole qpsk3600 frame. Every attempt to place the frame inside that block from outside
/// it is a guess, and a peak taken over a guessed window reads whatever was loudest nearby,
/// which on an FM receiver with the squelch open is the hiss the frame was quieter than. The
/// demodulator is the only thing that knows, and it knows exactly.</para>
/// <para>A modem that cannot answer simply does not implement this, and its frames carry no
/// level rather than a guessed one. See <see cref="FrameSpan"/> for what implementing it
/// amounts to.</para>
/// </remarks>
public interface IFrameSpanSource
{
    /// <summary>
    /// The span of the frame just delivered, consumed by the asking: true once per delivered
    /// frame, and false at any other time.
    /// </summary>
    /// <remarks>
    /// Take-once, deliberately. The caller is the channel, asking from inside the frame event
    /// itself, and a span that could be read twice is a span that can be read stale - attached
    /// to a later frame from a modem that did not manage to mark one, which would be the same
    /// class of wrong answer as the guessing this replaces.
    /// </remarks>
    /// <param name="fromSample">The sample the frame's sync (IL2P sync word, HDLC opening flag,
    /// FX.25 correlation tag) was taken at.</param>
    /// <param name="toSample">The sample its last bit was taken at.</param>
    /// <returns>False when this modem has no span to report.</returns>
    bool TryTakeFrameSpan(out long fromSample, out long toSample);
}

/// <summary>
/// The two marks a demodulator sets to answer <see cref="IFrameSpanSource"/>: where the frame's
/// sync was, and where its last bit was. One per modem.
/// </summary>
/// <remarks>
/// <para>The positions are counts of samples handed to <see cref="IModem.Process"/> since the
/// modem was built, which is the same clock
/// <see cref="Packet.SoundModem.Audio.InputLevelHistory"/> runs on, because the channel hands
/// the same blocks to both and counts nothing else.</para>
/// <para><b>Both marks are late by the demodulator's own front-end group delay</b> - the
/// band-pass and matched filters between the antenna and the bit, which on the slower modes runs
/// to 15 ms or so. Late by the same amount at both ends, because both marks are taken from the
/// same chain, so the span's <em>length</em> is right and its <em>position</em> is a few
/// milliseconds late. What consumes it allows for that with a margin rather than each
/// demodulator publishing a latency figure nobody could check
/// (<c>FrameLevelMonitor.Measure</c>).</para>
/// <para>Not thread-safe, and does not need to be: it is written from the receive thread inside
/// the modem's own loop and read from the same thread inside the frame event.</para>
/// </remarks>
public sealed class FrameSpan
{
    private long _syncAt = -1;
    private long _from;
    private long _to;
    private bool _pending;

    /// <summary>The frame's sync was taken here. Called from the deframer's own sync hook.</summary>
    /// <param name="position">Samples handed to <c>Process</c> so far.</param>
    public void Sync(long position) => _syncAt = position;

    /// <summary>
    /// A frame is about to be delivered, and its last bit was taken here. Call immediately
    /// before raising the frame event, so the channel's handler reads this frame's span.
    /// </summary>
    /// <param name="position">Samples handed to <c>Process</c> so far.</param>
    public void Complete(long position)
    {
        if (_syncAt < 0 || position <= _syncAt)
        {
            // No sync was seen (a deframer reset between the two, or a modem whose framing does
            // not mark one), so there is nothing honest to report about this frame.
            _pending = false;
            return;
        }

        _from = _syncAt;
        _to = position;
        _pending = true;
    }

    /// <summary>
    /// Takes over the span of the modem that actually decoded the frame, for a diversity bank
    /// or a wrapper that delivers somebody else's decode as its own.
    /// </summary>
    /// <param name="source">The branch or inner modem; anything that is not an
    /// <see cref="IFrameSpanSource"/> leaves this with nothing to report.</param>
    public void Adopt(IModem source)
    {
        _pending = source is IFrameSpanSource inner && inner.TryTakeFrameSpan(out _from, out _to);
    }

    /// <summary>Records a span taken earlier, for a bank that holds candidates before it emits.</summary>
    public void Set(long fromSample, long toSample)
    {
        _from = fromSample;
        _to = toSample;
        _pending = toSample > fromSample;
    }

    /// <summary>Forgets any sync mark, for a deframer reset - see <see cref="Complete"/>.</summary>
    public void Forget() => _syncAt = -1;

    /// <inheritdoc cref="IFrameSpanSource.TryTakeFrameSpan"/>
    public bool TryTakeFrameSpan(out long fromSample, out long toSample)
    {
        fromSample = _from;
        toSample = _to;
        bool had = _pending;
        _pending = false;
        return had;
    }
}
