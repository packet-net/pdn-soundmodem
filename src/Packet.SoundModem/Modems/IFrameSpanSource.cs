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
    /// <para>Take-once, deliberately. The caller is the channel, asking from inside the frame
    /// event itself, and a span that could be read twice is a span that can be read stale -
    /// attached to a later frame from a modem that did not manage to mark one, which would be
    /// the same class of wrong answer as the guessing this replaces.</para>
    /// <para><b>The <c>out</c> values are meaningless when this returns false</b>, and a caller
    /// that stores them anyway has invented a span. That is not a hypothetical: it is what the
    /// four diversity banks did in the first cut of this, and it put a previous frame's span on
    /// a later one.</para>
    /// </remarks>
    /// <param name="fromSample">The sample the frame's sync (IL2P sync word, HDLC opening flag,
    /// FX.25 correlation tag) was taken at.</param>
    /// <param name="toSample">The sample its last bit was taken at.</param>
    /// <returns>False when this modem has no span to report.</returns>
    bool TryTakeFrameSpan(out long fromSample, out long toSample);

    /// <summary>
    /// How much of the end of one of this modem's spans to leave out of a reading of it, in
    /// samples - see <see cref="FrameSpan.MarginSamplesFor"/> for what it is made of.
    /// </summary>
    /// <remarks>
    /// Published by the modem because it is a property of the modem: it scales with the bit
    /// rate, and the bit rates here run from 300 to 19200. One figure for all of them was 20 ms
    /// at 12 kHz, which is half of a qpsk3600 supervisory frame and more than twice the whole of
    /// a c4fsk19200 one - so the fast modes, the ones this feature was asked for, got no level at
    /// all on the frames a working link is mostly made of.
    /// </remarks>
    int FrameSpanMarginSamples { get; }
}

/// <summary>
/// The marks a demodulator sets to answer <see cref="IFrameSpanSource"/>: where each of its
/// readings last saw a frame's sync, and where the reading that delivered took its last bit.
/// One per modem.
/// </summary>
/// <remarks>
/// <para>The positions are counts of samples handed to <see cref="IModem.Process"/> since the
/// modem was built, which is the same clock
/// <see cref="Packet.SoundModem.Audio.InputLevelHistory"/> runs on, because the channel hands
/// the same blocks to both and counts nothing else.</para>
/// <para><b>A mark per reading, not per modem.</b> Every modem here runs a deframer per timing
/// phase, and on the HDLC framings each of them opens a frame on every flag - including the
/// closing flag of the frame another phase is in the middle of delivering. With one shared mark
/// a phase that decoded nothing could move the mark of the phase that did, and the measured
/// result was a span of two milliseconds on an fsk9600 frame that had lasted thirty. Each
/// reading marks its own, and delivering takes that reading's.</para>
/// <para><b>A mark is spent when it is used.</b> <see cref="Complete"/> clears the reading's
/// mark whether or not it produced a span, so a frame that arrives without a sync of its own
/// gets no span rather than the previous frame's. Without that, a missed sync handed out a span
/// covering both frames and everything between them, which on the bench read as the tone in the
/// gap and badged the frame TOO LOUD.</para>
/// <para><b>Both marks are late by the demodulator's own front-end group delay</b> - the
/// band-pass and matched filters between the antenna and the bit. Late by the same amount at
/// both ends, because both marks are taken from the same chain, so the span's <em>length</em> is
/// right and its <em>position</em> is a little late. What consumes it allows for that with a
/// margin rather than each demodulator publishing a latency figure nobody could check
/// (<c>FrameLevelMonitor.Measure</c>).</para>
/// <para>Not thread-safe, and does not need to be: it is written from the receive thread inside
/// the modem's own loop and read from the same thread inside the frame event.</para>
/// </remarks>
public sealed class FrameSpan
{
    /// <summary>
    /// How much of a span's late end is not read, in bit periods of the mode's own bit rate.
    /// </summary>
    /// <remarks>
    /// <para>Two things put the last-bit mark past the end of the burst, and both are counted in
    /// bits rather than in milliseconds, which is why this is.</para>
    /// <para><b>The front end's group delay</b>, measured through the real channel at 4 to 20 bit
    /// periods across the catalogue: 5.2 bits on bpsk300 (17.2 ms), 10.2 on qpsk600, 10.6 on
    /// qpsk2400, 10.8 on afsk1200, 18.8 on afsk1200-il2p, 19.8 on qpsk3600 (5.5 ms), and under 4
    /// on every 48 kHz mode. <b>And the trailer a held plain reading waits through</b>, which is
    /// the IL2P trailing CRC's 32 bits: a frame the CRC reading refused and the plain reading
    /// released is delivered that much after its own last bit.</para>
    /// <para>48 covers the worst of both with better than twice the headroom on the front end,
    /// and is 13.3 ms at 3600 bps against the 20 ms a flat figure cost there. The near end takes
    /// no margin at all: the sync mark is late by the same group delay, so it already sits inside
    /// the frame, with the sender's transmit delay in front of that.</para>
    /// </remarks>
    public const int MarginBits = 48;

    /// <summary>
    /// <see cref="MarginBits"/> at a mode's own bit rate, in samples at the channel's rate.
    /// </summary>
    /// <param name="sampleRate">The channel's sample rate.</param>
    /// <param name="bitRate">The mode's bit rate: 1200 for afsk1200, 3600 for qpsk3600, 19200
    /// for c4fsk19200. Bits on the wire, not symbols, so a mode carrying two bits a symbol
    /// counts both.</param>
    public static int MarginSamplesFor(int sampleRate, int bitRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bitRate, 0);
        return (int)Math.Ceiling((double)MarginBits * sampleRate / bitRate);
    }

    private readonly long[] _syncAt;
    private long _from;
    private long _to;
    private bool _pending;

    /// <summary>Creates the marks for a modem with <paramref name="readings"/> deframers.</summary>
    /// <param name="readings">How many deframers push bits into this modem - its timing phases,
    /// usually. Each marks its own sync.</param>
    public FrameSpan(int readings = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readings, 1);
        _syncAt = new long[readings];
        Array.Fill(_syncAt, -1);
    }

    /// <summary>One reading saw a frame's sync here. Called from that deframer's sync hook.</summary>
    /// <param name="reading">Which deframer; 0 on a modem with one.</param>
    /// <param name="position">Samples handed to <c>Process</c> so far.</param>
    /// <remarks>
    /// The most recent sync wins, which is the safe direction on the two ways a hunt can be
    /// wrong. A hunt that fires spuriously part way through a frame moves the mark forward and
    /// shortens the span, which stays inside the burst and at worst leaves too little to measure;
    /// keeping the first mark instead would let a spurious hit in the noise <em>before</em> the
    /// burst put the noise inside the reading, which is the failure this whole feature exists to
    /// avoid.
    /// </remarks>
    public void Sync(int reading, long position) => _syncAt[reading] = position;

    /// <summary>
    /// One reading is about to deliver a frame, and its last bit was taken here. Call
    /// immediately before raising the frame event, so the channel's handler reads this frame's
    /// span.
    /// </summary>
    /// <param name="reading">Which deframer is delivering.</param>
    /// <param name="position">Samples handed to <c>Process</c> so far.</param>
    public void Complete(int reading, long position)
    {
        long syncAt = _syncAt[reading];

        // Spent either way. A mark belongs to one frame, and a frame that arrives without one of
        // its own must get no span rather than the last frame's.
        _syncAt[reading] = -1;
        if (syncAt < 0 || position <= syncAt)
        {
            _pending = false;
            return;
        }

        _from = syncAt;
        _to = position;
        _pending = true;
    }

    /// <summary>
    /// Records a span taken earlier, for a diversity bank that holds its branches' copies of a
    /// frame until the chunk ends and then emits one of them.
    /// </summary>
    /// <param name="fromSample">The winning branch's sync mark, or 0 with
    /// <paramref name="toSample"/> for a branch that reported no span at all.</param>
    /// <param name="toSample">Its last-bit mark.</param>
    /// <remarks>
    /// An empty range is how a bank says "the branch that decoded this had nothing to report",
    /// which is what it must say when
    /// <see cref="IFrameSpanSource.TryTakeFrameSpan"/> returned false rather than passing on
    /// whatever the <c>out</c> parameters happened to hold.
    /// </remarks>
    public void Set(long fromSample, long toSample)
    {
        _from = fromSample;
        _to = toSample;
        _pending = toSample > fromSample;
    }

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
