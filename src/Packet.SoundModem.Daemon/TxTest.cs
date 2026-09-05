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
/// Action{Exception}, bool, object, TimeSpan?, CancellationToken)"/>), so it waits out a busy
/// channel on the same p-persistence roll, defers to the same
/// <see cref="SoundModemChannel.TransmitInhibit"/> an ARQ session sets, and goes out through
/// whichever transmitter the station has - the sound card and its serial or CM108 PTT, or the
/// Flex DAX path. What is measured is therefore what a frame gets. The one thing it does not
/// share is a modem: the tones are generated here, because a modem modulates frames and there is
/// no frame.</para>
/// <para><b>One keyup, one array.</b> The burst is rendered whole and handed to the channel as a
/// single transmission rather than a stream of blocks. That is not laziness: the transmitter
/// drains the output device between queued items, which on a real card stops and re-primes the
/// PCM, and a test signal with a hole in it every few hundred milliseconds is a poor instrument.
/// The cost is that once the burst has been rendered it goes out to its full length - which is
/// why the length is capped rather than merely defaulted.</para>
/// <para><b>A cancelled test transmits nothing, ever.</b> <see cref="Stop"/> and the channel-wait
/// timeout both withdraw the transmission from the channel's queue, so a test that has been given
/// up on cannot key the radio minutes later when a busy channel finally clears - and the run is
/// held open until the channel has actually given it back, so a retry cannot queue behind a stale
/// one. Where the burst is already on the air the withdrawal cannot reach it and the tone fades
/// out instead, which is the only case in which a stopped test is heard at all.</para>
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
    private sealed class Run(TestTone tone)
    {
        internal TestTone Tone { get; } = tone;

        internal CancellationTokenSource Withdrawal { get; } = new();

        internal bool Cancelled => Withdrawal.IsCancellationRequested;

        internal void Cancel()
        {
            // The tone first, so a burst already on the air fades rather than stopping dead; the
            // withdrawal then takes it off the queue if it has not been reached yet.
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
    };

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

        if (Prepare(request, out string? why) is not { } prepared)
        {
            return Refuse(why ?? "a test transmission is already running");
        }

        (Run run, string text, double audioHz) = prepared;
        try
        {
            _options.Journal.Write($"tx test: {text}");
            _options.Report?.Invoke(new TxTestStatus("running", text));

            string? rejection = null;
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
                    float[] burst = run.Tone.Render();
                    int lead = (int)Math.Round(txDelay / 1000.0 * _options.Channel.SampleRate);
                    var audio = new float[lead + burst.Length];
                    burst.CopyTo(audio, lead);
                    return audio;
                },
                rejected: refusal => rejection = refusal.Message,
                // Its own identity, so the test takes its own keyup rather than being appended to
                // a modem's - the operator wants to measure the tones, not the frame in front of
                // them.
                source: this,
                withdraw: run.Withdrawal.Token);

            // A bound on the wall clock as well as on the airtime. The airtime is bounded by the
            // burst itself; this is the other half - the transmitter's own wait for a clear
            // channel has no timeout, so a channel busy for minutes would otherwise leave the
            // page saying "running" for ever.
            Task waited = Task.Delay(
                TimeSpan.FromSeconds(run.Tone.Remaining / (double)_options.Channel.SampleRate)
                    + _options.ChannelWait,
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

            double onAir = run.Tone.Produced / (double)_options.Channel.SampleRate;
            if (onAir <= 0)
            {
                return Refuse("stopped before it reached the air, so nothing was transmitted");
            }

            string done = run.Cancelled
                ? $"stopped after {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s"
                : $"done, {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s on air";
            _options.Journal.Write($"tx test: {done}");
            _options.Report?.Invoke(new TxTestStatus("done", $"{text} - {done}"));

            // Written down like a transmission, because it was one. See TxTestOptions.Recorded.
            _options.Recorded?.Invoke(new TxTestRecord(
                _options.SubChannel, $"tx test: {text} - {done}", audioHz));

            return new TxTestOutcome(true, text, null);
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

        var run = new Run(
            new TestTone(tones, _options.Amplitude, _options.Channel.SampleRate, seconds));
        string text =
            $"{what}, {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
            + (capped ? $" (capped from {request.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s)" : "")
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
    /// keeps is the sentence describing what went out. See CONFIG.md under <c>frameLog</c>.
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
internal sealed record TxTestOutcome(bool Ran, string Text, string? Refusal, bool Failed = false);
