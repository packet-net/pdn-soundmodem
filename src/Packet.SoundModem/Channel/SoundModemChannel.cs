using M0LTE.Radio.Audio;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Channel;

/// <summary>KISS channel-access parameters, in KISS units where noted.</summary>
public sealed class CsmaParameters
{
    /// <summary>Preamble length in milliseconds. Default 300.</summary>
    public int TxDelayMilliseconds { get; set; } = 300;

    /// <summary>p-persistence parameter, 0-255 (p = (value+1)/256). Default 63 (p=0.25).</summary>
    public int Persistence { get; set; } = 63;

    /// <summary>Slot time in milliseconds. Default 100.</summary>
    public int SlotTimeMilliseconds { get; set; } = 100;

    /// <summary>Audio kept flowing after the last frame, in milliseconds. Software modems
    /// need a non-zero tail so they do not clip their own transmissions. Default 20.</summary>
    public int TxTailMilliseconds { get; set; } = 20;
}

/// <summary>Receives the same audio the channel's modems see (see
/// <see cref="SoundModemChannel.AddReceiveTap"/>).</summary>
public delegate void ReceiveTap(ReadOnlySpan<float> samples);

/// <summary>
/// One audio channel hosting up to 16 logical modems (the QtSoundModem multiplex model,
/// addressed by KISS sub-channel): fans received audio into every modem plus the spectrum
/// source, aggregates carrier sense, and runs the transmit side - classic AX.25 §6
/// p-persistent CSMA gated on the aggregated <see cref="ChannelBusy"/>, PTT keying, and
/// device-paced audio with a drain before unkey (sample-domain TX-complete).
/// </summary>
/// <remarks>
/// <para><b>The transmit side is a scheduler, not a queue.</b> Everything that can key this
/// radio - each KISS modem, the paging endpoint, ARDOP, the CW ident - is a <i>transmitter</i>
/// with a queue of its own, and the channel serves them round robin. There is nothing special
/// about there being two, or about their being modems; the number and the kinds are open.</para>
/// <para><b>What makes that necessary is half duplex.</b> Keying up makes EVERY receiver on this
/// radio deaf, not just the one whose traffic caused it, so one transmitter's frame is airtime
/// taken from all of the others and silence imposed on all of them. Two rules follow, and both
/// are properties of the shared resource rather than of any protocol: a keyup carries one
/// transmitter's traffic (see <see cref="RunTransmitterAsync"/>), and a transmission that expects
/// an answer keeps the rest quiet until the answer has had its chance (see
/// <see cref="QuietAfterTransmit"/> and <see cref="TurnaroundHold"/>).</para>
/// </remarks>
public sealed class SoundModemChannel
{
    private readonly Dictionary<int, IModem> _modems = [];
    private readonly List<ReceiveTap> _receiveTaps = [];

    /// <summary>One queued transmission, with the identity that decides whose keyup it is.</summary>
    private sealed record TxItem(
        Func<int, float[]> Modulate,
        TaskCompletionSource Done,
        Action<Exception>? Rejected,
        bool OwnsTiming,
        object Source,
        TimeSpan? QuietAfter)
    {
        /// <summary>
        /// The token registration that withdraws this item, disposed once it has gone out or been
        /// withdrawn, so a long-lived token does not accumulate registrations for finished work.
        /// </summary>
        public CancellationTokenRegistration Withdrawal { get; set; }
    }

    // A queue PER TRANSMITTER rather than one for the channel. With a single queue a deferred
    // frame at the head blocks everything behind it, including the link that is not deferred -
    // so the moment any transmission can be held back, per-source queues stop being a nicety.
    private readonly object _txGate = new();
    private readonly Dictionary<object, Queue<TxItem>> _txQueues = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _txOrder = [];
    private TaskCompletionSource _txSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The turnaround hold: after a transmission that expects an answer, the other transmitters
    // stay off the air until that answer has had its chance. See TurnaroundHold.
    private object? _quietOwner;
    private long _quietFrom;
    private long _quietOwnerSince;
    private TimeSpan _quietWindow;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly SpectrumSource? _spectrum;
    private readonly BurstSnrMonitor _burstSnr;
    private readonly Action<int, ReadOnlyMemory<byte>>? _constellationSink;
    private volatile bool _transmitting;

    /// <summary>Creates a channel.</summary>
    /// <param name="sampleRate">DSP sample rate all modems and TX audio run at.</param>
    /// <param name="time">Clock for CSMA waits (injectable per repo discipline).</param>
    /// <param name="spectrumSink">Optional waterfall line sink (see
    /// <see cref="SpectrumSource"/>).</param>
    /// <param name="constellationSink">Optional per-symbol constellation-frame sink
    /// (sub-channel, frame). Wired to any PSK modem added to the channel - see
    /// <see cref="ConstellationSource"/>; a no-op for the non-PSK modes.</param>
    /// <param name="randomSeed">Seed for the p-persistence roll (tests); null = random.</param>
    public SoundModemChannel(
        int sampleRate,
        TimeProvider? time = null,
        Action<ReadOnlyMemory<byte>>? spectrumSink = null,
        Action<int, ReadOnlyMemory<byte>>? constellationSink = null,
        int? randomSeed = null)
    {
        SampleRate = sampleRate;
        _time = time ?? TimeProvider.System;
        _random = randomSeed is int seed ? new Random(seed) : new Random();
        if (spectrumSink is not null)
        {
            _spectrum = new SpectrumSource(sampleRate, spectrumSink);
        }

        _burstSnr = new BurstSnrMonitor(sampleRate);
        _constellationSink = constellationSink;
    }

    /// <summary>The channel's DSP sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Channel-access tunables (KISS parameter commands update these).</summary>
    public CsmaParameters Csma { get; } = new();

    /// <summary>Raised for every received frame a modem passes up, with the sub-channel that
    /// decoded it. Called from the receive-processing thread. This is the <b>host</b> path: it
    /// comes from each modem's constructor frame sink, and it is what a KISS client is sent.
    /// Not every frame the station reads arrives here - see
    /// <see cref="FrameReceivedWithQuality"/>.</summary>
    public event Action<int, byte[]>? FrameReceived;

    /// <summary>Per-frame receive diagnostics (sub-channel, frame, quality) for every frame the
    /// station decoded - FEC corrections, CRC state, winning decoder branch. See
    /// <see cref="Modems.FrameQuality"/>.</summary>
    /// <remarks>
    /// The <b>monitor</b> path, from <see cref="IModem.FrameDecoded"/>: display, frame log,
    /// journal and survey hang off this. It is a superset of <see cref="FrameReceived"/>, not a
    /// companion to it - a frame marked <see cref="Modems.FrameQuality.MonitorOnly"/> is raised
    /// here and never there, which is how a station shows an operator traffic it is deliberately
    /// not handing to its host.
    /// </remarks>
    public event Action<int, byte[], Modems.FrameQuality>? FrameReceivedWithQuality;

    /// <summary>Raised when a queued frame is dropped (sub-channel, frame, reason): its
    /// modem refused to modulate it (e.g. a frame beyond the mode's size bound), or the
    /// sub-channel has no modem at all. The frame's
    /// <see cref="EnqueueTransmit(int, byte[])"/> task faults with the same exception;
    /// the transmitter keeps running.</summary>
    public event Action<int, byte[], Exception>? TransmitRejected;

    /// <summary>True while any modem sees packet or energy busy, or we are transmitting.</summary>
    public bool ChannelBusy => _transmitting || _modems.Values.Any(m => m.ChannelBusy);

    /// <summary>True while any modem's packet DCD is asserted.</summary>
    public bool CarrierDetect => _modems.Values.Any(m => m.CarrierDetect);

    /// <summary>The modems keyed by sub-channel.</summary>
    public IReadOnlyDictionary<int, IModem> Modems => _modems;

    /// <summary>Adds a modem on a KISS sub-channel (0-15).</summary>
    public void AddModem(int subChannel, Func<Action<byte[]>, IModem> factory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(subChannel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subChannel, 15);
        IModem modem = factory(frame => FrameReceived?.Invoke(subChannel, frame));

        // Enriched here, at the one point every modem's quality passes through, so the frame
        // log, the journal line, the KISS quality frame and the waterfall all carry the SAME
        // burst-SNR figure - the lesson of the branch-index offsets, applied before the second
        // number exists rather than after it bites.
        modem.FrameDecoded += (frame, quality) =>
        {
            // Anything decoded on this modem renews its hold, because it means the exchange this
            // station is holding the channel for is alive. Deliberately an over-approximation:
            // the channel does not parse addresses, so somebody else's frame on the same
            // sub-channel renews it too. That costs the others at most one more window each, and
            // a carrier we can decode is one CSMA would have deferred to anyway.
            NoteHeard(modem);
            FrameReceivedWithQuality?.Invoke(
                subChannel, frame, quality with { SnrDb = _burstSnr.MeasureBurst(subChannel) });
        };
        if (_constellationSink is { } sink && modem is IConstellationSource psk)
        {
            var constellation = new ConstellationSource(frame => sink(subChannel, frame));
            constellation.Attach(psk);
        }

        _burstSnr.AddModem(subChannel, modem);
        _modems.Add(subChannel, modem);
    }

    /// <summary>Adds a non-KISS receive listener - a service decoder (e.g. POCSAG
    /// paging) that shares the channel's audio without occupying a KISS sub-channel.
    /// Called with the same half-duplex-gated samples the modems get.</summary>
    public void AddReceiveTap(ReceiveTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        _receiveTaps.Add(tap);
    }

    /// <summary>Feeds received audio to every modem and the spectrum source. Skipped
    /// while transmitting (half duplex).</summary>
    public void ProcessReceive(ReadOnlySpan<float> samples)
    {
        _spectrum?.Process(samples);
        if (_transmitting)
        {
            return;
        }

        // Below the half-duplex gate on purpose: our own transmission is not a signal we
        // heard, and feeding it here would attribute a huge SNR to whatever decodes next.
        _burstSnr.Process(samples);
        foreach (IModem modem in _modems.Values)
        {
            modem.Process(samples);
        }

        foreach (ReceiveTap tap in _receiveTaps)
        {
            tap(samples);
        }
    }

    /// <summary>Queues a frame for transmission on a sub-channel. The returned task
    /// completes when the frame's audio has fully left the device (ACKMODE's answer).</summary>
    public Task EnqueueTransmit(int subChannel, byte[] frame)
    {
        if (!_modems.TryGetValue(subChannel, out IModem? modem))
        {
            // A sub-channel nothing transmits on - a typo'd nibble, or the ARDOP entry, which
            // is a receive tap rather than a modem. This used to fault a task most callers
            // discard: no DROPPED line, no observed exception, the host's traffic simply
            // vanished with nothing to distinguish it from a dead band. Announced like every
            // other refused frame, and observed here because a fire-and-forget caller cannot.
            var refusal = new ArgumentException($"no modem on sub-channel {subChannel}");
            TransmitRejected?.Invoke(subChannel, frame, refusal);
            Task faulted = Task.FromException(refusal);
            _ = faulted.Exception;
            return faulted;
        }

        return SendAndAnnounceAsync(subChannel, frame, modem);
    }

    /// <summary>
    /// Sends the frame and then announces it, so that awaiting the transmission is enough to know
    /// the announcement has happened.
    /// </summary>
    /// <remarks>
    /// Announced after the send rather than when it was queued, because a frame can wait behind
    /// CSMA or an ARQ session for seconds and a log line claiming a transmission that has not
    /// happened yet is worse than none. Sequenced by <c>await</c> rather than a continuation: a
    /// continuation lets the caller's own await resume first, so anything checking the event
    /// immediately after awaiting the send is racing it - which it will lose on a loaded machine,
    /// intermittently, in someone else's CI. A rejection throws out of the await, so a frame is
    /// announced by exactly one of this and <see cref="TransmitRejected"/>, never both.
    /// </remarks>
    /// <summary>
    /// Asked, per frame, how many Hz to nudge the transmit centre for it; null (the default)
    /// always transmits on the nominal centre.
    /// </summary>
    /// <remarks>
    /// For answering a station whose rig is off frequency on the frequency its receiver is
    /// actually listening on. The policy lives with the caller rather than here: only the caller
    /// knows whether a frame is addressed to one station or broadcast to the whole channel, and
    /// a trim aimed at one station's oscillator is aimed away from everybody else's. A modem
    /// that cannot be trimmed ignores this, so the hook is always safe to install.
    /// </remarks>
    public Func<int, byte[], double>? TransmitTrimHz { get; set; }

    /// <summary>Matches <c>FrequencyShiftedModem</c>: enough taps that the Hilbert transform's
    /// low-frequency edge is well below anything a packet mode occupies.</summary>
    private const int TrimHilbertTaps = 639;

    /// <summary>Hard ceiling on <see cref="TransmitTrimHz"/>, in Hz.</summary>
    public const double MaxTransmitTrimHz = 500;

    /// <summary>
    /// Applies the transmit trim to a rendered burst, by translating the whole thing.
    /// </summary>
    /// <remarks>
    /// <para>Done here rather than inside the modem because most modems are never wrapped in a
    /// frequency shifter: the AFSK and PSK families carry a settable centre natively and
    /// generate their carrier at it, so there is no shift stage to lean on, and those are
    /// exactly the modes that talk to the stations this is for. Translating the finished burst
    /// works for every mode on the same code path, at the cost of one Hilbert pass per
    /// transmission - which is nothing beside the airtime that follows it.</para>
    /// <para>A fresh shifter per burst, and a group delay of zeros flushed through it, for the
    /// same reason the modem's own shifter does that: the FIR delays everything by (taps-1)/2
    /// samples, and without the pad that much of the end of the burst never comes out.</para>
    /// </remarks>
    /// <summary>
    /// What <see cref="TransmitTrimHz"/> asked for, reduced to what will actually be done.
    /// </summary>
    /// <remarks>
    /// Clamped here rather than at the point of use so that everything downstream - the burst,
    /// the event, the frame log, the panel - reports the same number, and that number is the one
    /// that went on air. The clamp is a backstop on the caller, not a tuning knob: correcting for
    /// another station's oscillator is a few tens of Hz, and anything approaching this ceiling is
    /// a bug upstream that a transmitter is the wrong place to discover.
    /// </remarks>
    private static double ResolveTrim(double? requestedHz)
    {
        double hz = requestedHz ?? 0;
        return double.IsNaN(hz) ? 0 : Math.Clamp(hz, -MaxTransmitTrimHz, MaxTransmitTrimHz);
    }

    private float[] ApplyTransmitTrim(float[] burst, double trimHz)
    {
        if (trimHz == 0 || burst.Length == 0)
        {
            return burst;
        }

        const int groupDelay = (TrimHilbertTaps - 1) / 2;
        var shifter = new FrequencyShifter(SampleRate, trimHz, TrimHilbertTaps);
        var shifted = new float[burst.Length + groupDelay];
        shifter.Process(burst, shifted.AsSpan(0, burst.Length));
        shifter.Process(new float[groupDelay], shifted.AsSpan(burst.Length));
        return shifted;
    }

    private async Task SendAndAnnounceAsync(int subChannel, byte[] frame, IModem modem)
    {
        double applied = 0;
        await EnqueueTransmit(
                // Inside the modulate callback, so the trim is chosen when the burst is actually
                // rendered rather than when it was queued - a frame can wait behind CSMA for
                // seconds, and the estimate may have moved on by then.
                txDelay =>
                {
                    applied = ResolveTrim(TransmitTrimHz?.Invoke(subChannel, frame));
                    return ApplyTransmitTrim(modem.Modulate(frame, txDelay), applied);
                },
                rejection => TransmitRejected?.Invoke(subChannel, frame, rejection),
                // The modem is the keyup's identity: this sub-channel's frames run back-to-back
                // under one PTT, another sub-channel's do not.
                source: modem,
                quietAfter: QuietAfterTransmit?.Invoke(subChannel, frame))
            .ConfigureAwait(false);

        FrameTransmitted?.Invoke(subChannel, frame);
        FrameTransmittedWithTrim?.Invoke(subChannel, frame, applied);
    }

    /// <summary>
    /// Raised alongside <see cref="FrameTransmitted"/>, carrying how far the burst was shifted
    /// off the nominal centre to suit the station it was addressed to; 0 when it went out
    /// straight.
    /// </summary>
    /// <remarks>
    /// Separate from the receive side's offset on purpose. A received frame's offset is a
    /// <em>measurement</em> of somebody else's transmitter; this is a <em>command</em> to our
    /// own, known exactly rather than estimated. Reporting them as one number would make a
    /// station's average offset meaningless, mixing what they did with what we did about it.
    /// </remarks>
    public event Action<int, byte[], double>? FrameTransmittedWithTrim;

    /// <summary>
    /// Raised once a KISS-addressed frame has been transmitted - after the audio has gone to the
    /// device, not when the frame was queued.
    /// </summary>
    /// <remarks>
    /// The receive side has had <see cref="FrameReceived"/> since the beginning and the transmit
    /// side had only <see cref="TransmitRejected"/>, so a station's journal recorded every frame
    /// it failed to send and none that it sent. Service transmitters that are not KISS modems
    /// (paging, ARDOP) go through the delegate overload and are not announced here - they are not
    /// frames on a sub-channel.
    /// </remarks>
    public event Action<int, byte[]>? FrameTransmitted;

    /// <summary>Queues an arbitrary transmission - the channel-access path (CSMA, PTT,
    /// pacing, TX-complete) for service transmitters that are not KISS-addressed modems
    /// (e.g. POCSAG paging). The delegate receives the TXDELAY budget in milliseconds
    /// (full on the keyup's first transmission, a token 30 ms after) and returns the
    /// audio at the channel rate.</summary>
    /// <param name="modulate">Renders the transmission; an <see cref="ArgumentException"/>
    /// thrown here drops the item and faults the returned task, as for frames.</param>
    /// <param name="rejected">Optional observer for such a rejection.</param>
    /// <param name="ownsChannelTiming">
    /// True for a transmitter that owns the channel's timing rather than sharing it - an ARDOP
    /// ARQ session, whose turnarounds are what <see cref="TransmitInhibit"/> protects. Such a
    /// transmission skips <b>both</b> the inhibit and the p-persistence roll: it is not one of
    /// the stations contending for the channel, it is the one running it, and deferring would
    /// mean deferring partly to its own signal - a shifted ARDOP centre sits inside a packet
    /// modem's passband and trips its busy detector. Everything else leaves this false.
    /// </param>
    /// <param name="source">
    /// Who is transmitting - the identity a keyup is held for. Consecutive transmissions carrying
    /// the same <paramref name="source"/> share one keyup and the token preamble that goes with
    /// it; a different one ends the keyup and contends for its own. Null means "no identity", and
    /// is treated as unique per transmission, so an unidentified caller never rides on somebody
    /// else's keyup. See <see cref="RunTransmitterAsync"/>.
    /// </param>
    /// <param name="quietAfter">
    /// How long to keep the OTHER transmitters off the air once this one has finished, so that a
    /// reply to it is not transmitted over. Null (the default) never holds. See
    /// <see cref="QuietAfterTransmit"/> for the frame-addressed form and for why the decision
    /// belongs to the caller.
    /// </param>
    /// <param name="withdraw">
    /// Cancelling this takes the transmission back off the queue, so it never keys the radio and
    /// the returned task is cancelled. For a caller that can change its mind while a transmission
    /// waits out a busy channel - the operator's test transmission - where emptying the burst
    /// would not be enough, the transmitter having already keyed by the time it asks for audio.
    /// Silent once the transmission is under way; see <c>Withdraw</c>. The default cannot be
    /// cancelled and costs nothing.
    /// </param>
    public Task EnqueueTransmit(
        Func<int, float[]> modulate, Action<Exception>? rejected = null, bool ownsChannelTiming = false,
        object? source = null, TimeSpan? quietAfter = null, CancellationToken withdraw = default)
    {
        ArgumentNullException.ThrowIfNull(modulate);
        if (ReceiveOnlyReason is string receiveOnly)
        {
            var refusal = new InvalidOperationException(receiveOnly);
            rejected?.Invoke(refusal);
            Task faulted = Task.FromException(refusal);
            _ = faulted.Exception; // observed here: a fire-and-forget caller cannot, and on a
                                   // receive-only channel this happens to every frame.
            return faulted;
        }

        object identity = source ?? new object();
        return !ownsChannelTiming && TransmitInhibit is not null
            ? EnqueueWhenPermittedAsync(modulate, rejected, identity, quietAfter, withdraw)
            : EnqueueNow(modulate, rejected, ownsChannelTiming, identity, quietAfter, withdraw);
    }

    /// <summary>
    /// Asked, per frame, how long this station should stay off the air after sending it so that
    /// the reply is not transmitted over by one of the channel's OTHER modems. Null (the default
    /// hook, and a null result) never holds, which is the behaviour of a channel that says
    /// nothing about reply timing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is a hook.</b> Carrier sense cannot solve this: at the moment we roll
    /// p-persistence the reply has not started, so there is nothing to detect and deferring is
    /// down to luck. The only tool is prediction, and predicting means knowing whether a frame is
    /// the kind that gets an answer - which is a property of the PROTOCOL the frame belongs to,
    /// not of the modem carrying it. So the modem asks and does not guess. The AX.25 answer lives
    /// in <see cref="Modems.Ax25ReplyExpectation"/>, and anything else this channel ever carries
    /// brings its own or leaves this null and keeps today's behaviour exactly. Same shape and
    /// same reasoning as <see cref="TransmitTrimHz"/>.</para>
    /// <para>The window itself is a calculation, not a fitted constant: the peer needs its own
    /// TXDELAY to key up and a few p-persistence slots to win the channel, both of which are
    /// wall-clock KISS parameters this channel already holds. It is very nearly independent of
    /// baud rate, because what is being waited out is the far end keying up rather than anything
    /// being sent. See <see cref="TurnaroundHold"/>.</para>
    /// </remarks>
    public Func<int, byte[], TimeSpan?>? QuietAfterTransmit { get; set; }

    /// <summary>
    /// The turnaround window <see cref="QuietAfterTransmit"/> implementations should use: long
    /// enough for the far end to key up and win the channel, and no longer.
    /// </summary>
    /// <remarks>
    /// <para>Calculated, not fitted. What is being waited out is the far end KEYING UP, which is
    /// its own TXDELAY (it runs the same convention, so ours is the fair estimate of theirs),
    /// plus its rig and decode latency, for which a second TXDELAY is the honest allowance, plus
    /// one slot of contention. Note what that makes it nearly independent of: baud rate. Nothing
    /// here is a symbol count - TXDELAY and slot time are wall-clock KISS parameters - so a
    /// 9600 Bd link waits about as long as a 300 Bd one, which is the opposite of what the
    /// intuition says.</para>
    /// <para>On the shipped defaults (300 ms, 100 ms) that is 700 ms. Against 4,997 real
    /// turnarounds in GB7RDG-2's frame log it covers 80 % of them, and the curve is a knee:
    /// another 600 ms buys only 11 more points (1.30 s covers 91 %).</para>
    /// <para>Slot time deliberately carries little weight, because a host may set it to zero and
    /// one does: LinBPQ configures this very station with TXDELAY 300 ms, persistence 50 and
    /// <b>slot time 0</b>, which turns the p-persistence backoff into a spin and leaves this hold
    /// as the only thing keeping the station off the air after its own transmission. A window
    /// built mostly out of slot time would have quietly become 300 ms there.</para>
    /// </remarks>
    public TimeSpan TurnaroundHold =>
        TimeSpan.FromMilliseconds((2 * Csma.TxDelayMilliseconds) + Csma.SlotTimeMilliseconds);

    /// <summary>
    /// A ceiling on how long one transmitter can keep the hold by being answered. Not a fairness
    /// knob for the ordinary case - an exchange that keeps getting replies is exactly what the
    /// hold is for. This is the backstop for the pathological one, where a busy frequency renews
    /// the hold indefinitely and every other transmitter on the channel is muted for good.
    /// </summary>
    public TimeSpan MaxTurnaroundHold { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set to say the channel has no transmitter at all, with the reason an operator or a host
    /// should be told. Every transmission is then refused the moment it is queued, carrying this
    /// as its message.
    /// </summary>
    /// <remarks>
    /// This is not <see cref="TransmitInhibit"/>, which holds a transmission back until the
    /// channel frees up. Nothing here ever frees up - a web receiver has no transmitter - so
    /// queueing would only turn "cannot" into a 30-second wait ending in the wrong explanation.
    /// It also bypasses <c>ownsChannelTiming</c>: ARDOP owning the timing of a channel it cannot
    /// key changes nothing about whether the frame goes out.
    /// </remarks>
    public string? ReceiveOnlyReason { get; set; }

    /// <summary>
    /// Raised with each block of audio as it is handed to the sound device - the station's own
    /// transmission, at the channel rate.
    /// </summary>
    /// <remarks>
    /// For a display that wants to draw what we put on the air. Receive processing is gated off
    /// while transmitting (half duplex), so without this a waterfall simply stops for the length
    /// of every keyup and its time axis quietly stops meaning anything.
    ///
    /// Raised at the moment the samples are written, which is slightly ahead of them leaving the
    /// device - there is a buffer and a drain behind this. Close enough for a display; not a
    /// timing reference.
    /// </remarks>
    public event Action<ReadOnlyMemory<float>>? TransmittedAudio;

    /// <summary>
    /// Raised with true when the transmitter takes the channel and false when it gives it back.
    /// </summary>
    /// <remarks>
    /// For a display. Receive processing stops the moment this goes true, but the first
    /// transmitted audio does not exist until the frame has been modulated and handed to the
    /// device - so anything drawing the channel needs to know a keyup has begun rather than
    /// inferring it from audio that has not arrived yet, or it simply stops for that gap.
    /// </remarks>
    public event Action<bool>? TransmittingChanged;

    /// <summary>
    /// A PTT keyup or unkey failed. Raised instead of letting the exception kill the
    /// transmitter loop, which is what happened before this existed: the loop had no catch,
    /// the daemon swallowed the task's fault, and one throwing Key() left a receive-only
    /// station with no log line - for every PTT type, not just a Flex. On a keyup failure
    /// every queued frame is faulted (a definite answer for ACKMODE hosts and the reject
    /// path, matching TransmitInhibitTimeout's philosophy) and the loop carries on: the
    /// next enqueue tries again, so a transient contention or an unplugged serial lead
    /// costs frames, never the transmitter.
    /// </summary>
    public event Action<Exception>? PttFailed;

    /// <summary>
    /// Consulted before a shared transmission is queued; while it returns true the transmission
    /// waits. Set by a host that has to keep a stretch of the channel clear - an ARDOP ARQ
    /// session, whose timing an AX.25 frame landing mid-turnaround would break. Null (the
    /// default) means nothing is holding the channel and every transmission queues immediately.
    /// </summary>
    public Func<bool>? TransmitInhibit { get; set; }

    /// <summary>
    /// How long a transmission waits on <see cref="TransmitInhibit"/> before being rejected.
    /// A held frame cannot wait indefinitely: an AX.25 host will have retried long before an
    /// ARQ session ends, so a definite answer beats a transmission that eventually escapes
    /// minutes late as a duplicate.
    /// </summary>
    public TimeSpan TransmitInhibitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private async Task EnqueueWhenPermittedAsync(
        Func<int, float[]> modulate, Action<Exception>? rejected, object source, TimeSpan? quietAfter,
        CancellationToken withdraw)
    {
        // The injected clock, per the repo's wall-clock discipline - this was the library's
        // one Stopwatch and its one bare Task.Delay, which no test could virtualise.
        long waitedFrom = _time.GetTimestamp();
        while (TransmitInhibit?.Invoke() == true)
        {
            // Withdrawn before it was ever queued, which is the cheapest place for it to happen.
            withdraw.ThrowIfCancellationRequested();

            if (_time.GetElapsedTime(waitedFrom) > TransmitInhibitTimeout)
            {
                var refusal = new InvalidOperationException(
                    $"another service is holding the channel (waited {TransmitInhibitTimeout.TotalSeconds:F0}s); "
                    + "transmission dropped");
                rejected?.Invoke(refusal);
                throw refusal;
            }

            await Task.Delay(InhibitPollInterval, _time).ConfigureAwait(false);
        }

        await EnqueueNow(modulate, rejected, ownsChannelTiming: false, source, quietAfter, withdraw)
            .ConfigureAwait(false);
    }

    /// <summary>Coarse on purpose: this gates against sessions lasting minutes.</summary>
    private static readonly TimeSpan InhibitPollInterval = TimeSpan.FromMilliseconds(50);

    private Task EnqueueNow(
        Func<int, float[]> modulate, Action<Exception>? rejected, bool ownsChannelTiming, object source,
        TimeSpan? quietAfter, CancellationToken withdraw = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new TxItem(modulate, done, rejected, ownsChannelTiming, source, quietAfter);
        lock (_txGate)
        {
            if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue))
            {
                queue = new Queue<TxItem>();
                _txQueues[source] = queue;
                _txOrder.Add(source);
            }

            queue.Enqueue(item);
            _txSignal.TrySetResult();
        }

        if (withdraw.CanBeCanceled)
        {
            // Registered after the enqueue, and Withdraw is a no-op on an item that is no longer
            // in the queue, so a token that fires between the two is not a lost transmission.
            item.Withdrawal = withdraw.Register(() => Withdraw(item));
        }

        return done.Task;
    }

    /// <summary>
    /// Takes a queued transmission back off the queue, so it never reaches the transmitter and
    /// never causes a keyup.
    /// </summary>
    /// <remarks>
    /// <para>The whole point is the keyup. A caller that has given up - an operator cancelling a
    /// test transmission that has been waiting for a busy channel - must not have the radio key
    /// up minutes later on their behalf, unannounced. Emptying the burst is not enough: the
    /// transmitter has already keyed by the time it asks for the audio.</para>
    /// <para>Silent when the item has already been taken for a keyup that is under way. Nothing
    /// can be done about that one - the audio may already be in the sound card - so the task
    /// completes normally as the transmission it was, and it is the caller's business to render
    /// nothing if it no longer wants to be heard.</para>
    /// </remarks>
    private void Withdraw(TxItem item)
    {
        bool removed = false;
        lock (_txGate)
        {
            if (_txQueues.TryGetValue(item.Source, out Queue<TxItem>? queue) && queue.Contains(item))
            {
                // Rebuilt without it rather than dequeued: the item may be behind others of the
                // same source, and their order is the order they will go out in.
                var kept = new Queue<TxItem>(queue.Where(queued => !ReferenceEquals(queued, item)));
                removed = true;
                if (kept.Count == 0)
                {
                    _txQueues.Remove(item.Source);
                    _txOrder.Remove(item.Source);
                }
                else
                {
                    _txQueues[item.Source] = kept;
                }
            }
        }

        if (removed)
        {
            item.Done.TrySetCanceled();
        }
    }

    /// <summary>Renews the hold when the link it protects is heard from.</summary>
    private void NoteHeard(object source)
    {
        lock (_txGate)
        {
            if (_quietOwner is not null && ReferenceEquals(source, _quietOwner))
            {
                _quietFrom = _time.GetTimestamp();
            }
        }
    }

    /// <summary>Blocks until some transmitter has something queued.</summary>
    private async Task WaitForWorkAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task signal;
            lock (_txGate)
            {
                if (_txOrder.Count > 0)
                {
                    return;
                }

                // Replaced under the same lock an enqueue takes, so an enqueue either saw a
                // non-empty queue above or will complete the instance we are about to await.
                _txSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = _txSignal.Task;
            }

            await signal.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The transmitter whose turn it is, or null while every transmitter with traffic is waiting
    /// out somebody else's turnaround.
    /// </summary>
    /// <remarks>
    /// Round robin over the sources that have work, skipping any the hold is keeping quiet. A
    /// transmission that owns the channel's timing is never held: ARDOP is running the channel
    /// rather than sharing it, and its own ARQ turnarounds are what it is protecting.
    /// </remarks>
    private object? NextEligibleSource()
    {
        lock (_txGate)
        {
            foreach (object source in _txOrder)
            {
                Queue<TxItem> queue = _txQueues[source];
                if (queue.Count > 0 && (queue.Peek().OwnsTiming || !IsHeldLocked(source)))
                {
                    return source;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// How long until the hold lets somebody else transmit - the exact wait, so the scheduler
    /// sleeps once rather than polling.
    /// </summary>
    /// <remarks>
    /// Floored at one tick of a millisecond so a hold that expires between the eligibility check
    /// and this call cannot produce a zero-length wait, and capped by what the hold itself can be
    /// (its window, or the ceiling, whichever bites first) so a lost wake-up cannot strand the
    /// scheduler. A renewal shortens nothing and lengthens by at most one window, because the
    /// loop re-checks after every wait.
    /// </remarks>
    private TimeSpan RemainingHold()
    {
        lock (_txGate)
        {
            if (_quietOwner is null)
            {
                return MinimumSchedulerWait;
            }

            TimeSpan left = _quietWindow - _time.GetElapsedTime(_quietFrom);
            TimeSpan untilCeiling = MaxTurnaroundHold - _time.GetElapsedTime(_quietOwnerSince);
            TimeSpan wait = left < untilCeiling ? left : untilCeiling;
            return wait < MinimumSchedulerWait ? MinimumSchedulerWait : wait;
        }
    }

    /// <summary>Never wait zero: a scheduler that polls with no delay is a spinning core.</summary>
    private static readonly TimeSpan MinimumSchedulerWait = TimeSpan.FromMilliseconds(1);

    private bool IsHeldLocked(object source)
    {
        if (_quietOwner is null || ReferenceEquals(source, _quietOwner))
        {
            return false;
        }

        return _time.GetElapsedTime(_quietOwnerSince) < MaxTurnaroundHold
            && _time.GetElapsedTime(_quietFrom) < _quietWindow;
    }

    /// <summary>Takes the next item from one transmitter's queue, or null when it has run dry.</summary>
    private TxItem? TakeFrom(object source)
    {
        lock (_txGate)
        {
            if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue) || queue.Count == 0)
            {
                return null;
            }

            TxItem item = queue.Dequeue();
            item.Withdrawal.Dispose();
            if (queue.Count == 0)
            {
                _txQueues.Remove(source);
                _txOrder.Remove(source);
            }
            else
            {
                // Round robin: a transmitter that has just had a keyup goes to the back, so a
                // long backlog on one link cannot mute the other indefinitely.
                _txOrder.Remove(source);
                _txOrder.Add(source);
            }

            return item;
        }
    }

    private TxItem? PeekFrom(object source)
    {
        lock (_txGate)
        {
            return _txQueues.TryGetValue(source, out Queue<TxItem>? queue) && queue.Count > 0
                ? queue.Peek()
                : null;
        }
    }

    /// <summary>Starts, renews or clears the turnaround hold after a keyup.</summary>
    private void SetHold(object? keyupSource, TimeSpan? quietAfter)
    {
        lock (_txGate)
        {
            if (keyupSource is null || quietAfter is not TimeSpan window || window <= TimeSpan.Zero)
            {
                _quietOwner = null;
                return;
            }

            long now = _time.GetTimestamp();
            if (!ReferenceEquals(_quietOwner, keyupSource))
            {
                _quietOwnerSince = now;
            }

            _quietOwner = keyupSource;
            _quietFrom = now;
            _quietWindow = window;
        }
    }

    /// <summary>Fails every queued transmission - a keyup or a device that has died takes them all.</summary>
    private void FaultEverything(Exception reason)
    {
        List<TxItem> queued = [];
        lock (_txGate)
        {
            foreach (Queue<TxItem> queue in _txQueues.Values)
            {
                queued.AddRange(queue);
            }

            _txQueues.Clear();
            _txOrder.Clear();
        }

        foreach (TxItem item in queued)
        {
            item.Done.TrySetException(reason);
            item.Rejected?.Invoke(reason);
        }
    }

    /// <summary>
    /// Runs the transmit side until cancelled: waits for queued frames, acquires the
    /// channel (p-persistent CSMA), keys PTT, plays every queued frame back-to-back,
    /// drains, unkeys.
    /// </summary>
    public async Task RunTransmitterAsync(IAudioOutput output, IPttControl ptt, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        if (output.SampleRate != SampleRate)
        {
            throw new ArgumentException(
                $"output rate {output.SampleRate} != channel rate {SampleRate}", nameof(output));
        }

        while (true)
        {
            // Thrown rather than tested in the while condition, because cancelling this loop has
            // always faulted the task with OperationCanceledException and callers rely on it -
            // the old `await reader.WaitToReadAsync(cancellation)` threw from the await. With the
            // scheduler's own wait now able to return immediately when work is already queued,
            // a plain `while (!IsCancellationRequested)` let the loop exit cleanly instead, which
            // is a silent stop for a transmitter that was cancelled. Race-dependent, so it passed
            // locally in Debug and failed on CI in Release.
            cancellation.ThrowIfCancellationRequested();
            await WaitForWorkAsync(cancellation).ConfigureAwait(false);

            // Whose turn is it? While a turnaround hold is running the answer is only the
            // transmitter it protects; everyone else waits a slot and asks again. This is the
            // half CSMA cannot do: at this instant the reply we are protecting has not started,
            // so carrier sense has nothing to detect and deferring would be down to the roll.
            object? source = null;
            while (source is null)
            {
                cancellation.ThrowIfCancellationRequested();
                source = NextEligibleSource();
                if (source is null)
                {
                    // Wait out what is left of the hold rather than polling at slot intervals.
                    // Exact, and safe against a slot time of zero: a host may set one, and
                    // LinBPQ was sending exactly that to this station until its config gained a
                    // SLOTTIME line, which would have made this a busy loop burning a core for
                    // the length of every hold. The channel-access roll below still uses the
                    // operator's slot time, because there it IS the channel-access parameter.
                    await Delay(RemainingHold(), cancellation).ConfigureAwait(false);
                }
            }

            // Classic p-persistence (AX.25 §6.4): when the channel is clear, roll p; on
            // failure wait one slot and try again; while busy, keep waiting slots.
            //
            // A transmission that owns the channel's timing skips all of it. ARDOP runs its own
            // channel discipline against ARQ turnaround budgets, and the busy it would be
            // deferring to is partly its own signal - at a shifted centre it sits inside a
            // packet modem's passband and asserts that modem's busy detector.
            while (!(PeekFrom(source) is { OwnsTiming: true }))
            {
                if (ChannelBusy)
                {
                    await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
                    continue;
                }

                if (_random.Next(256) <= Csma.Persistence)
                {
                    break;
                }

                await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
            }

            // Withdrawn while we waited for the channel. Nothing is left to send for this
            // source, so there is nothing to key for: a transmission taken back must not leave
            // the radio keying up on an empty burst, which is the whole point of being able to
            // take one back. Nothing else removes a queued item, so on every other path this is
            // the item NextEligibleSource just found and the test never fires.
            if (PeekFrom(source) is null)
            {
                continue;
            }

            _transmitting = true;
            TransmittingChanged?.Invoke(true);
            bool keyed = false;
            // Declared out here because the finally that starts the turnaround hold needs them:
            // the hold has to be set while receive is still gated, so that its window starts at
            // the unkey rather than wherever this loop next gets scheduled.
            object? keyupSource = null;
            TimeSpan? quietAfter = null;
            try
            {
                try
                {
                    ptt.Key();
                    keyed = true;
                }
                catch (Exception keyFailure) when (keyFailure is not OperationCanceledException)
                {
                    // Fault everything queued rather than leaving enqueuers to time out one by
                    // one: the answer is definite, the frames are lost, and the loop survives
                    // to try the next keyup. FlexTxContendedException lands here by design -
                    // "another station holds the PA" is an outcome, not a broken radio.
                    FaultEverything(keyFailure);
                    PttFailed?.Invoke(keyFailure);
                    continue;
                }

                // keyupSource stays null until something has actually gone out, so a keyup whose
                // first frame the modem refuses is still free to be taken by whatever follows.
                TxItem? inFlight = null;
                try
                {
                    // ONE TRANSMITTER PER KEYUP, which per-source queues now make structural:
                    // this loop can only ever see one transmitter's traffic. Draining the whole
                    // channel put several transmitters' frames under one PTT - on a station with modems at
                    // 850 Hz and 2150 Hz, one burst starting on one and finishing on the other,
                    // the waterfall picture that looks like a frame torn in half and is really
                    // two whole frames sharing a keyup.
                    //
                    // What it cost was hearing. Receive is gated for the length of a keyup, so an
                    // appended frame kept us transmitting for another 0.7 to 3.4 s at 300 Bd,
                    // straight through the window the answer to the frame we just sent arrives
                    // in. GB7RDG-2's frame log measured it, and the classification was exact
                    // rather than a heuristic: a shared keyup's inter-frame gap IS the appended
                    // burst rendered with the token preamble below (0.667 s for that station's
                    // 15 B RR), while a separate keyup's is the same burst with a full TXDELAY,
                    // plus the tail, plus the p-persistence wait (0.887 + 0.020 + more). Of the
                    // 83 frames sent with ANOTHER MODEM's frame appended behind them, 2 were
                    // answered within 6 s - 2.4 %, against 35.5 % of the 14,634 that ended their
                    // keyup. Being second cost a little copy too, a strong adjacent burst ending
                    // as yours begins being worth a decibel or two at the knee (bpsk300 behind
                    // the AFSK ident, 24 trials: 10 of 24 at -4 dB AWGN against 22 of 24 in its
                    // own keyup). The token preamble below was the obvious suspect and was
                    // measured to be innocent - it scored the same as a full one on every row -
                    // but its premise, that the far end is already locked to this waveform, is
                    // still only true while the modem stays the same.
                    while (TakeFrom(source) is { } item)
                    {
                        inFlight = item;
                        // Subsequent frames in one keyup need only a token preamble.
                        int txDelay = keyupSource is null ? Csma.TxDelayMilliseconds : 30;
                        float[] samples;
                        try
                        {
                            samples = item.Modulate(txDelay);
                        }
                        catch (ArgumentException rejection)
                        {
                            // A frame the modem refuses (oversize for the mode, empty) is
                            // dropped - it must not kill the transmitter loop. The enqueuer's
                            // task faults so ACKMODE hosts see the loss.
                            item.Done.TrySetException(rejection);
                            item.Rejected?.Invoke(rejection);
                            inFlight = null;
                            continue;
                        }

                        keyupSource = item.Source;
                        // The hold belongs to the LAST thing actually sent, so a keyup that ends
                        // with a frame nobody will answer does not keep the others waiting.
                        quietAfter = item.QuietAfter;
                        // Told before the write, not after it. A real device's Write blocks until its
                        // buffer has room, so a burst longer than the buffer does not return from it
                        // until most of the burst has already played - and a display told afterwards
                        // spends the whole transmission painting silence and then paints the burst
                        // over again, taking twice as long with the first half black. Measured on the
                        // air and reproduced: 92 black lines ahead of 97 lines of signal.
                        // What this costs the transmitter is one scale-and-copy of the burst before
                        // the audio goes out, which is bounded, allocation-only and does not wait on
                        // anything - the rule that the transmitter must never wait on a picture still
                        // holds.
                        TransmittedAudio?.Invoke(samples);
                        output.Write(samples);
                        output.Drain();
                        item.Done.TrySetResult();
                        inFlight = null;
                    }

                    if (Csma.TxTailMilliseconds > 0)
                    {
                        // The tail is silence, but it is time we held the channel - a display that
                        // skips it under-reports how long the keyup actually was.
                        var tail = new float[SampleRate * Csma.TxTailMilliseconds / 1000];
                        TransmittedAudio?.Invoke(tail);
                        output.Write(tail);
                    }

                    output.Drain();
                }
                catch (Exception deviceFailure) when (deviceFailure is not OperationCanceledException)
                {
                    // The output device failed mid-keyup (an unplugged USB card, a dead ALSA
                    // handle). Everything queued gets a definite answer first - the in-flight
                    // frame's enqueuer would otherwise wait forever on a Done nobody will ever
                    // complete - and then the fault propagates: the task's owner decides what a
                    // station without a transmitter does, rather than this loop quietly dying
                    // and leaving a healthy-looking receive-only station.
                    if (inFlight is { } dying)
                    {
                        dying.Done.TrySetException(deviceFailure);
                        dying.Rejected?.Invoke(deviceFailure);
                    }

                    FaultEverything(deviceFailure);
                    throw;
                }
            }
            finally
            {
                // Best-effort: an unkey that throws (the radio's session died mid-burst) must
                // not mask the burst's own result or kill the loop - and after a failed keyup
                // there is nothing to unkey. The arbitrated Flex PTT additionally suppresses
                // unkey for a keyup it did not win, so this cannot cut a peer's burst.
                if (keyed)
                {
                    try
                    {
                        ptt.Unkey();
                    }
                    catch (Exception unkeyFailure) when (unkeyFailure is not OperationCanceledException)
                    {
                        PttFailed?.Invoke(unkeyFailure);
                    }
                }

                // Receive is still gated: sweep the demodulators clean of our own transmission
                // BEFORE handing the channel back. The other order re-opened receive first, so
                // the audio thread could re-enter modem.Process concurrently with this loop's
                // ResetCarrierState - torn filter and DCD state exactly when the first reply
                // after our transmission arrives. (IModem documents ResetCarrierState as a call
                // for while the channel transmits.) TransmittingChanged's own subscribers that
                // reset receive taps - the id-beacon ghosts - get the same still-gated
                // guarantee, which is why the event too fires before the gate opens.
                foreach (IModem modem in _modems.Values)
                {
                    modem.ResetCarrierState();
                }

                // Set before receive reopens, so the window starts at the unkey rather than
                // wherever this loop next gets scheduled.
                SetHold(keyed ? keyupSource : null, keyed ? quietAfter : null);

                TransmittingChanged?.Invoke(false);
                _transmitting = false;
            }
        }
    }

    private Task Delay(int milliseconds, CancellationToken cancellation) =>
        Delay(TimeSpan.FromMilliseconds(milliseconds), cancellation);

    private Task Delay(TimeSpan wait, CancellationToken cancellation) =>
        Task.Delay(wait, _time, cancellation);
}
