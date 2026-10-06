using System.Globalization;
using System.Text;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// One transmitter test: the operator asks, the station keys up, sends tones for a bounded time
/// at the level its data goes out at, and unkeys.
/// </summary>
/// <remarks>
/// <para><b>It is a licensed transmission.</b> Nothing here ever runs by itself - there is no
/// timer, no start-up check and no retry. Every keyup is something the operator asked for, from
/// the page, from <c>/api/txtest</c> or from the <c>--two-tone</c> switch, and all three arrive
/// through <see cref="RunAsync"/> so there is one set of rules rather than three.</para>
/// <para><b>The same path a frame takes.</b> The audio is queued on the channel exactly as a
/// modulated frame is (<see cref="SoundModemChannel.EnqueueTransmit(Func{int, float[]},
/// Action{Exception}, bool, object, TimeSpan?, CancellationToken, Func{bool}, Action{int})"/>),
/// so it waits out a busy channel on the same p-persistence roll, defers to the same
/// <see cref="SoundModemChannel.TransmitInhibit"/> an ARQ session sets, and goes out through
/// whichever transmitter the station has - the sound card and its serial or CM108 PTT, or the
/// Flex DAX path. What is measured is therefore what a frame gets. The one thing it does not
/// share is a modem: the tones are generated here, because a modem modulates frames and there is
/// no frame.</para>
/// <para><b>One keyup, one array, rendered whole.</b> The burst is rendered as a single array
/// rather than a stream of blocks - a modem modulates frames and there is no frame here either,
/// so there is nothing to render incrementally against. That is not the same as it going to the
/// device in one call nothing can interrupt any more (#425): the channel writes it to the device
/// in blocks with a stop check between them, and nothing is drained until the whole write is
/// over, so a real card is never stopped and re-primed mid-burst the way draining between queued
/// items would - see <c>SoundModemChannel.WriteStoppably</c>. The length is still capped rather
/// than merely defaulted, because the render itself is instant and unbounded airtime would still
/// be a licensing problem even though a stop can now cut it short.</para>
/// <para><b>A cancelled test transmits only what had already reached the device.</b>
/// <see cref="Stop"/> withdraws the transmission from the channel's queue while it is still
/// waiting for one - so a test given up on while the channel is busy cannot key the radio minutes
/// later when it finally clears, and the run is held open until the channel has actually given it
/// back, so a retry cannot queue behind a stale one - and, once the burst is on the air, ends the
/// write within about one block: the block already handed to the device finishes, the next one is
/// faded to silence instead of started, and PTT drops as soon as that drains. The channel-wait
/// timeout uses the same withdrawal for a test that never got a clear channel to key on at
/// all.</para>
/// </remarks>
internal sealed class TxTestRunner
{
    /// <summary>How long a test runs when nobody says. Long enough to read a meter, short enough
    /// that pressing the button is not a commitment.</summary>
    internal const double DefaultSeconds = 5;

    /// <summary>The default cap on one test.</summary>
    internal const double DefaultMaxSeconds = 30;

    /// <summary>
    /// The most a configuration may set the cap to. A cap is a safety limit, so it has one of its
    /// own: a typo in <c>maxSeconds</c> must not be able to hold the PA up for an hour.
    /// </summary>
    internal const double CeilingSeconds = 60;

    /// <summary>The lowest single tone that will be sent. Below this a tone is below any radio's
    /// audio response and is measuring the coupling, not the transmitter.</summary>
    internal const double MinToneHz = 50;

    /// <summary>
    /// How often the same refusal reaches the journal. A caller that cannot transmit at all - a
    /// station with <c>"enabled": false</c>, or with no PTT - can be asked over and over from a
    /// page or a script, and one line each would bury everything else the station has to say.
    /// The first is always printed and the repeats are counted into the next one, which is what
    /// the transmit-drop suppressor already does for the same reason.
    /// </summary>
    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(1);

    private readonly TxTestOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _saidAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressed = new(StringComparer.Ordinal);
    private Run? _running;

    internal TxTestRunner(TxTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// One test in flight: the tones, and the token that takes it back off the channel's queue.
    /// </summary>
    /// <remarks>
    /// The cancellation lives here rather than on the runner because the runner's state is
    /// cleared when the run ends, and a withdrawn transmission may outlive the run that queued
    /// it. The callback that renders the burst holds this object, so what it asks is "was THIS
    /// test cancelled", which stays true for ever.
    /// </remarks>
    private sealed class Run(TestTone tone, double burstSeconds, ProbeBurst? probe)
    {
        internal TestTone Tone { get; } = tone;

        /// <summary>The whole keyup's audio, in seconds, TXDELAY aside: the tone, and the gap and
        /// probe after it when there is one. Fixed when the run is prepared, so the channel wait
        /// is measured against it whether or not the render has happened yet.</summary>
        internal double BurstSeconds { get; } = burstSeconds;

        /// <summary>The probe that follows the tone, or null for the tone alone.</summary>
        internal ProbeBurst? Probe { get; } = probe;

        internal CancellationTokenSource Withdrawal { get; } = new();

        internal bool Cancelled => Withdrawal.IsCancellationRequested;

        internal void Cancel()
        {
            // The tone first: harmless once it has already been rendered whole (the channel's own
            // write loop is what fades a burst already on the air, in blocks - see
            // SoundModemChannel.WriteStoppably), but it shortens what gets rendered at all in the
            // rare case where this lands before the render has happened yet. The withdrawal then
            // takes the item off the queue if the transmitter has not reached it yet either.
            Tone.Stop();
            try
            {
                Withdrawal.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended between the read and the cancel; there is nothing left to stop.
            }
        }
    }

    /// <summary>What the operator's page is offered.</summary>
    internal TxTestControl Control => new()
    {
        DefaultSeconds = _options.DefaultSeconds,
        MaxSeconds = _options.MaxSeconds,
        LowToneHz = TestTone.TwoToneLowHz,
        HighToneHz = TestTone.TwoToneHighHz,
        Presets = [.. TestTone.BesselNullTonesHz.Select(TxTestPreset.For)],
        Refusal = _options.Refusal,
        Start = request => _ = Task.Run(() => RunAsync(request)),
        Stop = Stop,
        IsRunning = () => IsRunning,
    };

    /// <summary>Whether a test is queued or on the air right now - what a page that just connected
    /// or reconnected is told, in the config message, since a <see cref="TxTestStatus"/> only ever
    /// reaches a page that was already listening when it was sent.</summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>
    /// Ends the test that is running, or cancels one still waiting for the channel. Does nothing
    /// when there is none, which is what a page reconnecting and a doubled click both look like.
    /// </summary>
    internal void Stop()
    {
        Run? run;
        lock (_gate)
        {
            run = _running;
        }

        run?.Cancel();
    }

    /// <summary>
    /// Runs one test to its end, and says what happened. Never throws: a refusal is the answer,
    /// and so is a failure.
    /// </summary>
    internal async Task<TxTestOutcome> RunAsync(TxTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.Refusal is string cannot)
        {
            // Known before anything is prepared, and true for the life of the process: a station
            // with no PTT and a station with no transmitter at all are both refused here.
            return Refuse(cannot);
        }

        // A transmit lease gives the transmitter to one sub-channel, and a test tone from anybody
        // else is exactly what it exists to keep off the air. The holder's own tone (the mailcast
        // head end's calibration tone) is the one exception, and it says it is the holder's by
        // naming the sub-channel. Anything else that could ask - the page's button, a script
        // that does not know about the lease - names nothing and is refused here.
        if (request.SubChannel is int named && !_options.Channel.Modems.ContainsKey(named))
        {
            return Refuse($"no modem transmits on sub-channel {named}");
        }

        TransmitLease lease = _options.Channel.TransmitLease;
        if (lease.Holder is int holder && request.SubChannel != holder)
        {
            return Refuse(
                $"sub-channel {holder} holds the transmit lease, so a test runs now only for it "
                + $"(\"subChannel\": {holder})");
        }

        (Run Run, string Text, double AudioHz)? prepared;
        string? why;
        try
        {
            prepared = Prepare(request, out why);
        }
        catch (Exception unreadable)
        {
            // Reading the request cannot throw today - the two levels are checked when the config
            // is read, and the tone frequency is refused before TestTone sees it - but it is the
            // one step outside the net below, and a throw here would escape into a discarded
            // Task.Run and leave the page amber for ever.
            return Fail(unreadable);
        }

        if (prepared is not { } ready)
        {
            return Refuse(why ?? "a test transmission is already running");
        }

        (Run run, string text, double audioHz) = ready;
        if (request.SubChannel is int sub)
        {
            // Queued under the run itself, attributed to the sub-channel it names, so a lease that
            // sub-channel holds (or takes while the test waits for the channel) lets it through
            // and one anybody else takes refuses it.
            lease.Attribute(run, sub);
        }

        try
        {
            _options.Journal.Write($"tx test: {text}");
            _options.Report?.Invoke(new TxTestStatus("running", text));

            string? rejection = null;
            int leadSamples = 0;
            int toneSamples = 0;
            int producedSamples = 0;
            Task send = _options.Channel.EnqueueTransmit(
                txDelay =>
                {
                    if (run.Cancelled)
                    {
                        // Withdrawn between being taken for this keyup and being asked for audio.
                        // The keyup cannot be undone from here, but nothing is put on the air.
                        return [];
                    }

                    // TXDELAY spent on silence, as the CW ident spends it: the PTT settling time
                    // wants the transmitter keyed and quiet, and an SSB rig radiates nothing
                    // without audio.
                    //
                    // Rendered whole, still - a modem modulates frames and there is no frame here
                    // either, so there is nothing to render incrementally against. What changed
                    // (#425) is not the render but the write: the channel now walks this array to
                    // the device in blocks with a stop check between them (see
                    // SoundModemChannel.WriteStoppably) rather than handing it over as one call
                    // nothing can interrupt, which is what let a 5, 15 or 60 second test outlive
                    // Stop entirely - the whole burst was already on its way to the sound card
                    // before the button could do anything about it.
                    float[] burst = run.Tone.Render();
                    leadSamples = (int)Math.Round(txDelay / 1000.0 * _options.Channel.SampleRate);
                    toneSamples = burst.Length;

                    // The probe, when asked for, goes in the same array: tone, gap, probe, one
                    // keyup, so the transmitter never unkeys between them and Stop, the channel
                    // wait and the lease treat the three as the one transmission they are. Left
                    // off if a stop landed while the tone was being rendered.
                    ProbeBurst? probe = run.Cancelled ? null : run.Probe;
                    int probeAt = leadSamples + burst.Length + (probe?.GapSamples ?? 0);
                    var audio = new float[probeAt + (probe?.Audio.Length ?? 0)];
                    burst.CopyTo(audio, leadSamples);
                    probe?.Audio.CopyTo(audio, probeAt);
                    return audio;
                },
                rejected: refusal => rejection = refusal.Message,
                // Its own identity, so the test takes its own keyup rather than being appended to
                // a modem's - the operator wants to measure the tones, not the frame in front of
                // them. The run rather than the runner, because only this run is attributed to
                // the sub-channel it named.
                source: run,
                withdraw: run.Withdrawal.Token,
                stopEarly: () => run.Cancelled,
                // What actually reached the device, in case a stop cut the write short - not the
                // same number as run.Tone.Produced any more, which the render above already set
                // to the whole burst regardless of how much of it a stop later let through.
                written: n => producedSamples = n);

            // A bound on the wall clock as well as on the airtime. The airtime is bounded by the
            // burst itself; this is the other half - the transmitter's own wait for a clear
            // channel has no timeout, so a channel busy for minutes would otherwise leave the
            // page saying "running" for ever.
            //
            // The keyup's own TXDELAY lead and tail count too: they are airtime the burst spends
            // after the channel clears, and leaving them out would let a channel that clears just
            // inside the wait have the end of its burst withdrawn and be told nothing went out.
            CsmaParameters csma = _options.Channel.Csma;
            TimeSpan keying = TimeSpan.FromMilliseconds(
                Math.Max(0, csma.TxDelayMilliseconds) + Math.Max(0, csma.TxTailMilliseconds));
            Task waited = Task.Delay(
                TimeSpan.FromSeconds(run.BurstSeconds) + keying + _options.ChannelWait,
                _options.Time);
            if (await Task.WhenAny(send, waited).ConfigureAwait(false) == waited)
            {
                // Withdrawn, not merely emptied: an abandoned test must not key the radio when
                // the channel eventually frees up. Awaited afterwards so the run is not declared
                // over until the channel has actually given the transmission back - which is what
                // stops a retry queueing behind it.
                run.Cancel();
                await SettleAsync(send).ConfigureAwait(false);
                return Refuse(
                    $"the channel did not clear within {_options.ChannelWait.TotalSeconds:F0} s, "
                    + "so the test was withdrawn and nothing was transmitted");
            }

            try
            {
                await send.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped while it waited: withdrawn from the queue, so the radio never keyed.
                return Refuse("stopped before it reached the air, so nothing was transmitted");
            }
            catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
            {
                // The answers the channel gives on purpose: a station that receives only, a
                // service holding the channel, and - through the transmitter's keyup catch - an
                // arbitrated radio another station is holding, which that path documents as an
                // outcome rather than a broken radio. All three are the station saying no, so
                // they read as refusals and carry the sentence the operator needs.
                return Refuse(rejection ?? refused.Message);
            }
            catch (Exception failure)
            {
                // And everything else, which is a real list: a serial or hidraw line that has
                // gone, an output device that died mid-keyup, a disposed handle. PTT is released
                // by the transmitter's own finally in every one of them; what must not happen is
                // the page left reading "Stop" for ever, the API caller losing its connection
                // with no explanation, and the command line dying before it has closed the radio
                // down.
                return Fail(failure);
            }

            double onAir = Math.Max(0, producedSamples - leadSamples) / (double)_options.Channel.SampleRate;
            if (onAir <= 0)
            {
                if (producedSamples > 0)
                {
                    // Every sample this branch counts is TXDELAY silence (onAir <= 0 means none
                    // of it was tone), and reaching here at all means the item was taken off the
                    // queue and keyed - the withdrawn-while-still-queued case above is a separate,
                    // exception-driven path that never reaches this line. So the radio really did
                    // key, briefly, for silence nobody asked to hear - worth its own line, or
                    // "refused, nothing was transmitted" reads like a contradiction next to a PTT
                    // event on the radio's own log.
                    _options.Journal.Write(
                        "tx test: the radio keyed for the TXDELAY lead only, stopped before any tone went out");
                }

                return Refuse("stopped before it reached the air, so nothing was transmitted");
            }

            string done = run.Cancelled
                ? $"stopped after {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s"
                : $"done, {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s on air";
            _options.Journal.Write($"tx test: {done}");
            _options.Report?.Invoke(new TxTestStatus("done", $"{text} - {done}"));

            // Written down like a transmission, because it was one. See TxTestOptions.Recorded.
            _options.Recorded?.Invoke(new TxTestRecord(
                request.SubChannel ?? _options.SubChannel, $"tx test: {text} - {done}", audioHz));

            // The probe's id goes back only when all of it reached the air: the head end reads it
            // as "this station sent the probe", a measurement it can use, and a probe a stop cut
            // part way is not one. ProbeComplete says which, so a cut probe reads as false rather
            // than as a station that never knew the field (which leaves both keys out).
            bool? probeComplete = run.Probe is { } sentProbe
                ? producedSamples >= leadSamples + toneSamples + sentProbe.GapSamples + sentProbe.Audio.Length
                : null;
            return new TxTestOutcome(true, text, null)
            {
                Probe = probeComplete == true ? run.Probe!.Descriptor.Id : null,
                ProbeComplete = probeComplete,
            };
        }
        catch (Exception unexpected)
        {
            // The last net. Nothing above should reach here, and a transmitter test that throws
            // out of its own runner would leave the page amber for ever - which is the failure
            // this catch exists to make impossible rather than unlikely.
            return Fail(unexpected);
        }
        finally
        {
            run.Withdrawal.Dispose();
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    /// <summary>Waits for a withdrawn transmission to be given back, however it ends.</summary>
    private static async Task SettleAsync(Task send)
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled (the ordinary case - it was withdrawn), refused, or faulted. The timeout
            // is the honest answer to the operator whichever it was.
        }
    }

    /// <summary>
    /// Reads the request into a burst, or returns null with the reason. The clamping happens here
    /// rather than at each of the three entry points, so the page, the API and the command line
    /// cannot grow three opinions about the cap.
    /// </summary>
    private (Run Run, string Text, double AudioHz)? Prepare(TxTestRequest request, out string? refusal)
    {
        refusal = null;
        double cap = Math.Clamp(
            double.IsFinite(_options.MaxSeconds) ? _options.MaxSeconds : DefaultMaxSeconds,
            1,
            CeilingSeconds);
        double seconds = double.IsFinite(request.Seconds) && request.Seconds > 0
            ? request.Seconds
            : _options.DefaultSeconds;
        double requested = seconds;
        bool capped = seconds > cap;
        seconds = Math.Min(seconds, cap);

        double[] tones;
        double audioHz;
        string what;
        if (request.TwoTone)
        {
            tones = [TestTone.TwoToneLowHz, TestTone.TwoToneHighHz];
            audioHz = (TestTone.TwoToneLowHz + TestTone.TwoToneHighHz) / 2;
            what = $"two-tone {TestTone.TwoToneLowHz:F0}+{TestTone.TwoToneHighHz:F0} Hz";
        }
        else
        {
            double hz = request.ToneHz;
            if (!double.IsFinite(hz) || hz < MinToneHz || hz >= _options.Channel.SampleRate / 2.0)
            {
                // Refused rather than clamped: a tone frequency is a measurement setting, and
                // silently moving it would make the deviation the operator reads off the null
                // wrong by exactly as much.
                refusal =
                    $"a test tone must be between {MinToneHz:F0} Hz and the "
                    + $"{_options.Channel.SampleRate / 2.0:F0} Hz Nyquist of this channel";
                return null;
            }

            tones = [hz];
            audioHz = hz;
            what = $"single tone {hz:F0} Hz (FM Bessel null at "
                + $"{TestTone.BesselNullDeviationHz(hz) / 1000:F1} kHz deviation)";
        }

        ProbeBurst? probe = null;
        string probeText = "";
        if (request.Probe is { } asked)
        {
            // Against the length asked for, not the capped one: a tone with a probe is refused
            // rather than cut, so the cap is applied to what the head end actually wanted.
            if (PrepareProbe(asked, requested, cap, out refusal) is not { } ready)
            {
                return null;
            }

            probe = ready;
            probeText = $", then after {Seconds(asked.GapSeconds)} s the {ready.Descriptor.Id} probe at "
                + $"{asked.AudioHz.ToString("0", CultureInfo.InvariantCulture)} Hz, "
                + $"{Seconds(ready.Descriptor.DurationSeconds)} s";
        }

        double burstSeconds = seconds + (probe is { } extra
            ? (extra.GapSamples + extra.Audio.Length) / (double)_options.Channel.SampleRate
            : 0);
        var run = new Run(
            new TestTone(tones, _options.Amplitude, _options.Channel.SampleRate, seconds),
            burstSeconds,
            probe);
        string text =
            $"{what}, {Seconds(seconds)} s"
            + (capped ? $" (capped from {Seconds(request.Seconds)} s)" : "")
            + probeText
            + $", peak level {_options.Amplitude.ToString("0.00", CultureInfo.InvariantCulture)}";

        lock (_gate)
        {
            if (_running is not null)
            {
                refusal = "a test transmission is already running";
                run.Withdrawal.Dispose();
                return null;
            }

            _running = run;
        }

        return (run, text, audioHz);
    }

    /// <summary>
    /// Reads a probe request into the audio that follows the tone, or returns null with the
    /// reason. Everything about it is refused rather than adjusted: an unknown kind (a newer head
    /// end asking for a probe this station cannot make), a gap that is not a length, a centre
    /// that puts the band outside the channel, and a keyup that would run past the cap.
    /// </summary>
    /// <remarks>
    /// The cap is the same <c>txTest.maxSeconds</c> a tone alone is held to, applied to the whole
    /// keyup. A tone alone is cut to it; a tone with a probe is refused instead, because cutting
    /// either part would hand the head end a measurement it did not ask for.
    /// </remarks>
    private ProbeBurst? PrepareProbe(TxTestProbe asked, double toneSeconds, double cap, out string? refusal)
    {
        refusal = ProbeProblem(asked);
        if (refusal is not null || ProbeSignal.ForKind(asked.Kind) is not { } descriptor)
        {
            return null;
        }

        int rate = _options.Channel.SampleRate;

        double total = toneSeconds + asked.GapSeconds + descriptor.DurationSeconds;
        if (total > cap)
        {
            refusal =
                $"the tone ({Seconds(toneSeconds)} s), gap ({Seconds(asked.GapSeconds)} s) and "
                + $"{descriptor.Id} probe ({Seconds(descriptor.DurationSeconds)} s) come to {Seconds(total)} s, "
                + $"over this station's {Seconds(cap)} s limit (txTest.maxSeconds), so nothing was transmitted";
            return null;
        }

        // Rendered here, before anything is queued, so the keyup never waits on it: on a small
        // processor a 6.5 s probe at 48 kHz is a noticeable fraction of a second.
        float[] audio = ProbeSignal.Render(descriptor, asked.AudioHz, _options.Amplitude, rate);
        return new ProbeBurst(descriptor, (int)Math.Round(asked.GapSeconds * rate), audio);
    }

    /// <summary>
    /// What is wrong with a probe request in itself, or null when nothing is: an unknown kind, a
    /// gap that is not a length, or a centre that puts the band outside this channel. These are
    /// the caller's mistakes, which <c>/api/txtest</c> answers 400 before anything is prepared;
    /// the cap is the station's limit, not a mistake, and is checked when the test is.
    /// </summary>
    internal string? ProbeProblem(TxTestProbe asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        if (ProbeSignal.ForKind(asked.Kind) is not { } descriptor)
        {
            return $"unknown probe kind \"{Printable(asked.Kind)}\"; this station sends "
                + string.Join(", ", ProbeSignal.Known.Select(k => $"\"{k.Kind}\" ({k.Id})"));
        }

        if (!double.IsFinite(asked.GapSeconds) || asked.GapSeconds < 0)
        {
            return "a probe's gapSeconds must be zero or more";
        }

        return ProbeSignal.BandProblem(descriptor, asked.AudioHz, _options.Channel.SampleRate);
    }

    private static string Seconds(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A caller's string, made safe for a journal line: printable ASCII, and short.</summary>
    internal static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var printable = new StringBuilder(Math.Min(text.Length, 32));
        foreach (char c in text.AsSpan(0, Math.Min(text.Length, 32)))
        {
            printable.Append(c is >= ' ' and <= '~' and not '"' ? c : '?');
        }

        return printable.ToString();
    }

    private TxTestOutcome Refuse(string why)
    {
        Say($"tx test: refused, {why}");
        _options.Report?.Invoke(new TxTestStatus("refused", $"refused, {why}"));
        return new TxTestOutcome(false, "", why);
    }

    private TxTestOutcome Fail(Exception failure)
    {
        string why = failure.GetBaseException().Message;
        Say($"tx test: failed: {why}");
        _options.Report?.Invoke(new TxTestStatus("failed", $"failed: {why}"));
        return new TxTestOutcome(false, "", why, Failed: true);
    }

    /// <summary>
    /// Writes one problem line, at most once a minute per distinct reason, with the repeats
    /// counted into the next one. Same shape and same wording as the transmit-drop suppressor in
    /// the daemon, and for the same reason: the answer still goes back to whoever asked every
    /// time, so nothing is hidden from the caller - only from the journal.
    /// </summary>
    private void Say(string line)
    {
        bool report;
        int suppressed = 0;
        lock (_gate)
        {
            long now = _options.Time.GetTimestamp();
            report = !_saidAt.TryGetValue(line, out long last)
                || _options.Time.GetElapsedTime(last, now) >= RefusalLogInterval;
            if (report)
            {
                _saidAt[line] = now;
                _suppressed.Remove(line, out suppressed);
            }
            else
            {
                _suppressed[line] = _suppressed.GetValueOrDefault(line) + 1;
            }
        }

        if (report)
        {
            _options.Journal.WriteError(
                line + (suppressed > 0 ? $" (and {suppressed} more like it in the last minute)" : ""));
        }
    }
}

/// <summary>Everything a <see cref="TxTestRunner"/> needs; a record so a test can vary one field.</summary>
internal sealed record TxTestOptions
{
    /// <summary>The channel the tones are queued on - the station's own transmit path.</summary>
    public required SoundModemChannel Channel { get; init; }

    /// <summary>Where the two lines a test writes go.</summary>
    public required StationJournal Journal { get; init; }

    /// <summary>How long a test runs when the request does not say.</summary>
    public double DefaultSeconds { get; init; } = TxTestRunner.DefaultSeconds;

    /// <summary>The cap on one test, clamped again at <see cref="TxTestRunner.CeilingSeconds"/>.</summary>
    public double MaxSeconds { get; init; } = TxTestRunner.DefaultMaxSeconds;

    /// <summary>
    /// What the burst peaks at. The modulators' own 0.8 by default, so the transmitter is
    /// presented with the same drive the station's data presents it with and the reading means
    /// something for the frames that follow.
    /// </summary>
    public double Amplitude { get; init; } = 0.8;

    /// <summary>
    /// Why this station cannot run one, or null when it can. Set at start-up (no PTT configured,
    /// no transmitter at all), because those are not conditions that come and go.
    /// </summary>
    public string? Refusal { get; init; }

    /// <summary>The sub-channel a test is filed under in the frame log and the frames panel.</summary>
    public int SubChannel { get; init; }

    /// <summary>
    /// How long a test may wait for a busy channel before it gives up. It defers to traffic
    /// exactly as a frame does, and this is the point at which waiting stops being useful.
    /// </summary>
    public TimeSpan ChannelWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The clock the wait above is measured on (injected; FakeTimeProvider under test).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Where a state change goes - the operator's page, and anything else watching.</summary>
    public Action<TxTestStatus>? Report { get; init; }

    /// <summary>
    /// Called once a test has actually been on the air, to write it down where transmissions are
    /// written down: the frame log and the frames panel, which is what puts it in front of
    /// somebody watching the public monitor of a station that publishes to one. It is also what
    /// arms the station's Morse identification, a test being a transmission like any other.
    /// </summary>
    public Action<TxTestRecord>? Recorded { get; init; }
}

/// <summary>A test that went out, for whatever keeps a record of transmissions.</summary>
/// <param name="SubChannel">The sub-channel it is filed under.</param>
/// <param name="Text">What was sent, in the journal's own wording.</param>
/// <param name="AudioHz">Where the energy was: the tone, or the midpoint of the pair.</param>
internal sealed record TxTestRecord(int SubChannel, string Text, double AudioHz)
{
    /// <summary>
    /// The record's own bytes, for a log whose rows are frames. Its text, in ASCII: the frame log
    /// stores a payload for every row and a test transmission has no frame to store, so what it
    /// keeps is the sentence describing what went out. See docs/reference/config.md under <c>frameLog</c>.
    /// </summary>
    /// <remarks>
    /// <b>The <c>tx test: </c> prefix is load-bearing.</b> Everything that reads a payload as an
    /// AX.25 frame - the frames panel, the link observer, the frame log's own backlog - shifts
    /// each byte right by one and accepts <c>[A-Z0-9]</c>. The ASCII range that shifts into a
    /// digit is <c>`</c> to <c>s</c>, so a sentence beginning with a lower-case letter in a to s
    /// could be read as a numeric callsign and mint a station that does not exist. <c>'t' &gt;&gt; 1</c>
    /// is <c>':'</c>, which is not, so this row reads as unattributed - which is what it is.
    /// </remarks>
    public byte[] Payload => Encoding.ASCII.GetBytes(Text);
}

/// <summary>What became of one test.</summary>
/// <param name="Ran">True when tones actually went on the air.</param>
/// <param name="Text">What was asked for, in the journal's wording; empty on a refusal.</param>
/// <param name="Refusal">Why not, or null when it ran.</param>
/// <param name="Failed">
/// True when it did not run because something threw rather than because the station said no. The
/// distinction is the caller's to act on: a refusal is an answer, a failure is a fault.
/// </param>
internal sealed record TxTestOutcome(bool Ran, string Text, string? Refusal, bool Failed = false)
{
    /// <summary>The id of the probe that went out after the tone, whole, or null when none did.</summary>
    public string? Probe { get; init; }

    /// <summary>Whether all of the probe asked for went out: true when it did, false when a stop
    /// cut it short or kept it off the air, null when no probe was asked for.</summary>
    public bool? ProbeComplete { get; init; }
}

/// <summary>A probe ready to follow the tone: which one, the silence before it, and its audio.</summary>
internal sealed record ProbeBurst(ProbeDescriptor Descriptor, int GapSamples, float[] Audio);
