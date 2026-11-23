namespace Packet.SoundModem.Audio;

/// <summary>
/// What keeping a playback stream running needs on top of the transfer loop: how much is queued,
/// and a way to queue silence. <see cref="AlsaPcm"/> is the only implementation outside the
/// tests; like <see cref="IPcmTransfer"/> it is a seam, not an abstraction layer.
/// </summary>
internal interface IPlaybackPcm : IPcmTransfer
{
    /// <summary><c>snd_pcm_delay</c>: frames written that have not played yet, or a negative
    /// errno (<c>-EPIPE</c> for a stream that has underrun).</summary>
    long Delay();

    /// <summary><c>snd_pcm_writei</c> of silence: frames taken, or a negative errno.</summary>
    long WriteSilence(int frames);

    /// <summary><c>snd_pcm_avail</c>: how much room the card's ring has, in frames, read from the
    /// kernel at the moment of asking, or a negative errno.</summary>
    long Avail();

    /// <summary><c>snd_pcm_rewind</c>: frames actually taken back off the end of what was
    /// written, which can be fewer than asked, or a negative errno.</summary>
    long Rewind(long frames);
}

/// <summary>
/// Keeps a playback stream running for as long as it is open, playing silence whenever nothing
/// is being transmitted (issue #828).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A CM108B's playback clock, restarted after a pause, runs 300 to 850 ppm slow
/// through the first burst: 112 of 120 first bursts with the stream closed in between, 5 of 120
/// with it left running (two boards, six hours, 2026-11-23). A wide OFDM burst sent in that state
/// is lost. The stream used to be drained and prepared again after every transmission, so every
/// transmission began on a restarted stream. Now it is started when it is opened and never
/// stopped: for every mode, and not as a setting.</para>
/// <para><b>How.</b> While nothing is being transmitted a feeder thread tops the card up with
/// silence, to <see cref="QueueMilliseconds"/> and no more. A transmission runs from its first
/// write to its drain, and for all of that time the feeder writes nothing, whatever gaps the
/// transmitter leaves between its writes: silence must never land in the middle of a
/// transmission, and a transmitter that is too slow underruns exactly as it always could. The
/// drain notes where the transmission ended, lets the silence back in behind it at once, and
/// returns when the card has played that far. It does not stop the stream.</para>
/// <para><b>The start of a transmission.</b> Its audio would otherwise wait behind the queued
/// silence, 45 to 60 ms of carrier with nothing on it at the front of every keyup. So the first
/// write takes the silence back first (<c>snd_pcm_rewind</c>): everything still in the card's
/// ring, which is the buffer size less <c>snd_pcm_avail</c>, short of a guard. See
/// <see cref="GuardFrames"/> for the guard and for why the ring is measured that way and no
/// other. What cannot come back is what the driver has already taken out of the ring, about
/// 12 ms on a CM108B, so with a 1 ms period and its 3 ms guard 14 to 15 ms of silence is left
/// ahead, the same to within a millisecond at every keyup (two boards, 2026-11-23). A layer
/// that will not rewind takes nothing back, and the transmission goes behind the silence as
/// before. Whatever is taken back comes off the count of frames written, which is what the
/// drain's arithmetic stands on. Nothing is asked of the card between the rewind and the first
/// frame, and nothing slow is done there.</para>
/// <para><b>The lead a transmitter can count on.</b> <see cref="PromiseLead"/> says, before the
/// audio is rendered, how much silence will be ahead of the next transmission, so the
/// transmitter can leave that much of its own leading silence out. Nothing is rewound then: the
/// feeder goes on feeding while the audio is rendered and converted, and the rewind is the last
/// thing before the first frame is written. It leaves no less than was promised, and puts
/// silence back if there is less.</para>
/// <para><b>Underruns.</b> One that happens with none of a transmission's audio written is a gap
/// in silence. It is recovered and counted in <see cref="IdleUnderruns"/>, not in the stream's
/// xrun count, which goes on meaning audio the transmitter did not send.</para>
/// <para><b>Threading.</b> Every call on the stream is made either under the gate or by the
/// transmitter inside a transmission, which the gate keeps the feeder out of. So the handle is
/// never used by two threads at once.</para>
/// </remarks>
internal sealed class ContinuousPlayback : IPcmTransfer
{
    /// <summary>How much silence is kept queued. It is how long the feeder can go unscheduled
    /// before the stream underruns and the card's clock has to restart. In milliseconds and not
    /// in periods: the period is chosen for the rewind, and this is not.</summary>
    internal const int QueueMilliseconds = 60;

    /// <summary>How often the feeder looks at the queue, so it never falls by much more than a
    /// quarter between looks.</summary>
    internal const int FeedMilliseconds = 15;

    /// <summary>The largest piece a full-speed USB card's driver takes from the ring at once:
    /// one URB of six 1 ms packets.</summary>
    internal const int DriverPieceMilliseconds = 6;

    /// <summary>What a rewind leaves in the ring over and above one of the driver's pieces.</summary>
    internal const int GuardMarginMilliseconds = 2;

    /// <summary>How long past the time the audio should have taken a drain goes on waiting
    /// before it gives the card up as stalled. The old <c>snd_pcm_drain</c> gave up too, after
    /// about a buffer's length without progress.</summary>
    internal const int DrainSlackMilliseconds = 250;

    /// <summary>How long <see cref="Stop"/> waits for the feeder.</summary>
    internal const int StopTimeoutMilliseconds = 2000;

    private const int Epipe = 32;
    private const int Estrpipe = 86;

    // An object for Monitor.Wait and Pulse, which System.Threading.Lock does not have.
    private readonly object _gate = new();
    private readonly IPlaybackPcm _pcm;
    private readonly int _sampleRate;
    private readonly int _queueFrames;
    private readonly int _bufferFrames;
    private readonly Thread? _feeder;

    // Written under the gate, or by the transmitter while _keyed keeps the feeder away.
    private bool _keyed;
    private bool _stopped;
    private long _written;
    private long _keyupFrames;
    private long _transmitEnd;
    private long _promised;
    private long _lead;
    private bool _rewindRefused;
    private int _idleUnderruns;

    /// <summary>Queues the first silence, starts the stream and starts the feeder.</summary>
    /// <param name="pcm">The stream, prepared and not yet started.</param>
    /// <param name="sampleRate">Frames per second.</param>
    /// <param name="bufferFrames">The buffer the card gave, or 0 if it would not say. Nothing
    /// is rewound without it.</param>
    /// <param name="periodFrames">The period the card gave, which is not always the one asked
    /// for, or 0 if it would not say.</param>
    /// <param name="startFeeder">False leaves the feeder thread out, for a test that calls
    /// <see cref="Feed"/> itself.</param>
    internal ContinuousPlayback(
        IPlaybackPcm pcm, int sampleRate, int bufferFrames, int periodFrames, bool startFeeder = true)
    {
        _pcm = pcm;
        _sampleRate = sampleRate;
        _bufferFrames = bufferFrames;

        int piece = (int)((long)sampleRate * DriverPieceMilliseconds / 1000);
        GuardFrames = (periodFrames > 0 ? Math.Min(piece, periodFrames) : piece)
            + (int)((long)sampleRate * GuardMarginMilliseconds / 1000);

        int queue = Math.Max(1, (int)((long)sampleRate * QueueMilliseconds / 1000));
        if (bufferFrames > 0)
        {
            // Half the buffer at most, so that a top-up always fits and never blocks.
            queue = Math.Min(queue, Math.Max(1, bufferFrames / 2));
        }

        _queueFrames = queue;

        lock (_gate)
        {
            _ = FeedLocked();
        }

        if (startFeeder)
        {
            _feeder = new Thread(RunFeeder) { IsBackground = true, Name = "alsa-playback-idle" };
            _feeder.Start();
        }
    }

    /// <summary>Underruns recovered with no transmit audio lost: the stream ran dry while it was
    /// playing silence. Every one is a restart of the card's clock, so a count that climbs means
    /// the feeder is not being scheduled.</summary>
    internal int IdleUnderruns => Volatile.Read(ref _idleUnderruns);

    /// <summary>Silence kept queued while idle, in frames.</summary>
    internal int QueueFrames => _queueFrames;

    /// <summary>
    /// How much a rewind leaves in the card's ring, in frames: one of the driver's pieces and
    /// <see cref="GuardMarginMilliseconds"/> more.
    /// </summary>
    /// <remarks>
    /// <para><b>More than one piece.</b> The card's driver takes its audio out of the ring a piece
    /// at a time, whenever the piece before has gone. On a full-speed USB card a piece is as many
    /// 1 ms packets as fit in a period, six at most. That can fall between the rewind and the
    /// first frame of the transmission, and the piece has to be there: with less in the ring the
    /// transmission begins with an underrun. So the guard is the smaller of 6 ms and the period,
    /// and 2 ms. It follows the period the card actually gave, read back after the open, since a
    /// card that would not take the period asked for takes bigger pieces.</para>
    /// <para><b>Measured from the kernel's own pointer.</b> What is in the ring is the buffer size
    /// less <c>snd_pcm_avail</c>, which asks the kernel. <c>snd_pcm_rewindable</c> looks like the
    /// call for this and is not: it answers from a copy of the driver's position that can be a
    /// whole piece old, and <c>snd_pcm_delay</c> does not refresh that copy. A rewind sized from
    /// it went up to 6 ms too far, through the guard, and one burst in ten began with an underrun
    /// on the bench CM108B, with a 4 ms guard and a 7 ms one alike. Sized from
    /// <c>snd_pcm_avail</c> there were none in over 500 transmissions on two boards
    /// (2026-11-23).</para>
    /// </remarks>
    internal int GuardFrames { get; }

    /// <summary>Frames of silence that were ahead of the first frame of the transmission in
    /// progress, or of the last one, when it started: what was left after the rewind.</summary>
    internal long LeadFrames
    {
        get
        {
            lock (_gate)
            {
                return _lead;
            }
        }
    }

    /// <inheritdoc />
    bool IPcmTransfer.IsCapture => false;

    /// <summary>How much silence will be ahead of the next transmission's first frame, in
    /// frames, for a transmitter that would otherwise write leading silence of its own. No less
    /// than this is left ahead when that transmission starts. 0 inside a transmission, whose
    /// next frames go straight behind its last.</summary>
    internal long PromiseLead()
    {
        lock (_gate)
        {
            if (_keyed || _stopped)
            {
                return 0;
            }

            // What is with the driver and the guard, which is what the rewind will leave. A card
            // that would not rewind last time is not expected to this time, so all of it stays.
            long queued = _pcm.Delay();
            _promised = queued <= 0 ? 0 : queued - (_rewindRefused ? 0 : Spare(queued, 0));
            return _promised;
        }
    }

    /// <summary>Writes a transmission's frames, as <see cref="PcmTransfer.Run(IPcmTransfer, int, out int, bool)"/>
    /// does. The first write of a transmission takes back the queued silence and stops the
    /// feeder until the drain.</summary>
    /// <param name="frameCount">Frames wanted, from the buffer the stream was handed.</param>
    /// <param name="failure">0, or the negative errno the stream gave up with.</param>
    /// <returns>Frames written.</returns>
    internal int Write(int frameCount, out int failure)
    {
        lock (_gate)
        {
            // Taking the gate waits out a top-up in flight, so none of it lands after this.
            if (!_keyed)
            {
                _keyed = true;
                _keyupFrames = 0;
                StartLocked();
            }
        }

        return PcmTransfer.Run(this, frameCount, out failure);
    }

    /// <summary>Ends the transmission and blocks until its last frame has played. The stream
    /// goes on running, with silence behind the transmission.</summary>
    internal void Drain()
    {
        int budget = -1;
        while (true)
        {
            long remaining;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                if (_keyed)
                {
                    _transmitEnd = _written;
                    _keyed = false;
                    Monitor.PulseAll(_gate);
                }

                // Fed from here as well as by the feeder: the silence has to be behind the
                // transmission before its last frame plays, without waiting on another thread
                // being scheduled.
                long queued = FeedLocked();
                if (queued < 0)
                {
                    // Not a stream that is going to play anything more, so there is nothing to
                    // wait for; the next write reports it.
                    return;
                }

                remaining = _transmitEnd - (_written - queued);
            }

            if (remaining <= 0)
            {
                return;
            }

            // The wait is counted in what was asked for rather than read off a clock. A sleep
            // only ever runs long, so the budget cannot run out before the audio has had its time.
            int milliseconds = (int)Math.Min(
                FeedMilliseconds, ((remaining * 1000) + _sampleRate - 1) / _sampleRate);
            if (budget < 0)
            {
                budget = (int)(remaining * 1000 / _sampleRate) + DrainSlackMilliseconds;
            }

            budget -= milliseconds;
            if (budget < 0)
            {
                return;
            }

            _pcm.Pause(milliseconds);
        }
    }

    /// <summary>One look at the queue, as the feeder takes: a top-up if nothing is being
    /// transmitted, nothing at all otherwise.</summary>
    internal void Feed()
    {
        lock (_gate)
        {
            if (!_keyed && !_stopped)
            {
                _ = FeedLocked();
            }
        }
    }

    /// <summary>Stops the feeder, and anything else here from touching the stream.</summary>
    /// <returns>False if the feeder did not stop in time. It may then still be inside a call on
    /// the stream, which must not be closed under it.</returns>
    internal bool Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.PulseAll(_gate);
        }

        return _feeder is null || _feeder.Join(StopTimeoutMilliseconds);
    }

    /// <inheritdoc />
    long IPcmTransfer.Transfer(int frameOffset, int frames)
    {
        long moved = _pcm.Transfer(frameOffset, frames);
        if (moved > 0)
        {
            _written += moved;
            _keyupFrames += moved;
        }

        return moved;
    }

    /// <inheritdoc />
    int IPcmTransfer.Recover(int error) => _pcm.Recover(error);

    /// <inheritdoc />
    int IPcmTransfer.Prepare() => _pcm.Prepare();

    /// <inheritdoc />
    int IPcmTransfer.Start() => _pcm.Start();

    /// <inheritdoc />
    void IPcmTransfer.CountXrun()
    {
        if (_keyupFrames == 0)
        {
            // Met by a transmission's first frames: the stream ran dry before any of them were
            // written, so the gap is in the silence ahead of the transmission and not in it.
            Interlocked.Increment(ref _idleUnderruns);
        }
        else
        {
            _pcm.CountXrun();
        }
    }

    /// <inheritdoc />
    void IPcmTransfer.Pause(int milliseconds) => _pcm.Pause(milliseconds);

    private void RunFeeder()
    {
        lock (_gate)
        {
            while (!_stopped)
            {
                if (_keyed)
                {
                    // Until the drain, however long the transmitter takes between writes.
                    Monitor.Wait(_gate);
                    continue;
                }

                _ = FeedLocked();
                Monitor.Wait(_gate, FeedMilliseconds);
            }
        }
    }

    /// <summary>The start of a transmission, immediately before its first frame is written:
    /// takes back the queued silence, as far as the guard and what was promised allow. The
    /// reading of the ring and the rewind are the last calls made on the card here, so the
    /// rewind is sized from a pointer that is as fresh as it can be and the first frames follow
    /// it with nothing in between.</summary>
    private void StartLocked()
    {
        long promised = _promised;
        _promised = 0;
        _lead = 0;

        long queued = _pcm.Delay();
        if (queued < 0)
        {
            // Run dry in the silence, and found here first.
            RecoverIdle((int)queued);
            queued = _pcm.Delay();
            if (queued < 0)
            {
                return;
            }
        }

        long spare = Spare(queued, promised);
        if (spare > 0)
        {
            long taken = _pcm.Rewind(spare);
            _rewindRefused = taken <= 0;
            if (taken > 0)
            {
                // Those frames were never played and never will be.
                _written -= taken;
                queued -= taken;
            }
        }

        if (queued < promised)
        {
            // Less ahead than a transmitter was told to count on: the queue was caught low, or
            // ran dry. It has left that much silence out of its audio, so it is put back here.
            long wrote = _pcm.WriteSilence((int)(promised - queued));
            if (wrote > 0)
            {
                _written += wrote;
                queued += wrote;
            }
        }

        _lead = queued;
    }

    /// <summary>How many of the <paramref name="queued"/> frames a rewind may take back: what
    /// is in the card's ring this instant less the guard, and never so many that fewer than
    /// <paramref name="keep"/> are left ahead. 0 where the ring cannot be read.</summary>
    private long Spare(long queued, long keep)
    {
        if (_bufferFrames <= 0 || queued <= keep)
        {
            return 0;
        }

        long avail = _pcm.Avail();
        if (avail < 0)
        {
            return 0;
        }

        long inRing = _bufferFrames - avail;
        return Math.Max(0, Math.Min(inRing - GuardFrames, queued - keep));
    }

    /// <summary>Tops the queue up with silence and makes sure the stream is running.</summary>
    /// <returns>Frames now queued, or the negative errno of a stream that would not take
    /// them.</returns>
    private long FeedLocked()
    {
        long queued = _pcm.Delay();
        if (queued < 0)
        {
            RecoverIdle((int)queued);
            queued = _pcm.Delay();
            if (queued < 0)
            {
                // Nothing is written to a stream that will not say how full it is: a write that
                // did not fit would block, with the gate held.
                return queued;
            }
        }

        long wanted = _queueFrames - queued;
        if (wanted > 0)
        {
            long wrote = _pcm.WriteSilence((int)wanted);
            if (wrote < 0)
            {
                // Left for the next look rather than retried here, so a card that keeps failing
                // costs one attempt per look and no more.
                RecoverIdle((int)wrote);
                return wrote;
            }

            _written += wrote;
            queued += wrote;
        }

        // A stream that is prepared but not running is started by hand: one just opened or just
        // recovered, or one whose transmission was recovered from an underrun and ended before
        // it had written the start threshold's worth. A running stream refuses harmlessly.
        _ = _pcm.Start();
        return queued;
    }

    private void RecoverIdle(int error)
    {
        if (error is -Epipe or -Estrpipe)
        {
            Interlocked.Increment(ref _idleUnderruns);
        }

        // As PcmTransfer does it: snd_pcm_recover knows the xrun and the suspend, and hands
        // anything else straight back to be prepared by hand.
        if (_pcm.Recover(error) < 0)
        {
            _ = _pcm.Prepare();
        }
    }
}
