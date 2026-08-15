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
public sealed class SoundModemChannel
{
    private readonly Dictionary<int, IModem> _modems = [];
    private readonly List<ReceiveTap> _receiveTaps = [];
    private readonly Channel<(Func<int, float[]> Modulate, TaskCompletionSource Done, Action<Exception>? Rejected, bool OwnsTiming)> _txQueue =
        System.Threading.Channels.Channel.CreateUnbounded<(Func<int, float[]>, TaskCompletionSource, Action<Exception>?, bool)>();
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
            FrameReceivedWithQuality?.Invoke(
                subChannel, frame, quality with { SnrDb = _burstSnr.MeasureBurst(subChannel) });
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
                rejection => TransmitRejected?.Invoke(subChannel, frame, rejection))
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
    public Task EnqueueTransmit(
        Func<int, float[]> modulate, Action<Exception>? rejected = null, bool ownsChannelTiming = false)
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

        return !ownsChannelTiming && TransmitInhibit is not null
            ? EnqueueWhenPermittedAsync(modulate, rejected)
            : EnqueueNow(modulate, rejected, ownsChannelTiming);
    }

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

    private async Task EnqueueWhenPermittedAsync(Func<int, float[]> modulate, Action<Exception>? rejected)
    {
        // The injected clock, per the repo's wall-clock discipline - this was the library's
        // one Stopwatch and its one bare Task.Delay, which no test could virtualise.
        long waitedFrom = _time.GetTimestamp();
        while (TransmitInhibit?.Invoke() == true)
        {
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

        await EnqueueNow(modulate, rejected, ownsChannelTiming: false).ConfigureAwait(false);
    }

    /// <summary>Coarse on purpose: this gates against sessions lasting minutes.</summary>
    private static readonly TimeSpan InhibitPollInterval = TimeSpan.FromMilliseconds(50);

    private Task EnqueueNow(Func<int, float[]> modulate, Action<Exception>? rejected, bool ownsChannelTiming)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_txQueue.Writer.TryWrite((modulate, done, rejected, ownsChannelTiming)))
        {
            done.SetException(new InvalidOperationException("transmit queue closed"));
        }

        return done.Task;
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

        var reader = _txQueue.Reader;
        while (await reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
        {
            // Classic p-persistence (AX.25 §6.4): when the channel is clear, roll p; on
            // failure wait one slot and try again; while busy, keep waiting slots.
            //
            // A transmission that owns the channel's timing skips all of it. ARDOP runs its own
            // channel discipline against ARQ turnaround budgets, and the busy it would be
            // deferring to is partly its own signal - at a shifted centre it sits inside a
            // packet modem's passband and asserts that modem's busy detector.
            while (!(reader.TryPeek(out var next) && next.OwnsTiming))
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

            _transmitting = true;
            TransmittingChanged?.Invoke(true);
            bool keyed = false;
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
                    while (reader.TryRead(out var queued))
                    {
                        queued.Done.TrySetException(keyFailure);
                        queued.Rejected?.Invoke(keyFailure);
                    }

                    PttFailed?.Invoke(keyFailure);
                    continue;
                }

                bool first = true;
                (Func<int, float[]> Modulate, TaskCompletionSource Done, Action<Exception>? Rejected, bool OwnsTiming)? inFlight = null;
                try
                {
                    while (reader.TryRead(out var item))
                    {
                        inFlight = item;
                        // Subsequent frames in one keyup need only a token preamble.
                        int txDelay = first ? Csma.TxDelayMilliseconds : 30;
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

                        first = false;
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

                    while (reader.TryRead(out var queued))
                    {
                        queued.Done.TrySetException(deviceFailure);
                        queued.Rejected?.Invoke(deviceFailure);
                    }

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

                TransmittingChanged?.Invoke(false);
                _transmitting = false;
            }
        }
    }

    private Task Delay(int milliseconds, CancellationToken cancellation) =>
        Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _time, cancellation);
}
