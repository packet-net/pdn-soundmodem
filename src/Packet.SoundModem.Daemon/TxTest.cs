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
/// Action{Exception}, bool, object, TimeSpan?)"/>), so it waits out a busy channel on the same
/// p-persistence roll, defers to the same <see cref="SoundModemChannel.TransmitInhibit"/> an ARQ
/// session sets, and goes out through whichever transmitter the station has - the sound card and
/// its serial or CM108 PTT, or the Flex DAX path. What is measured is therefore what a frame
/// gets. The one thing it does not share is a modem: the tones are generated here, because a
/// modem modulates frames and there is no frame.</para>
/// <para><b>One keyup, one array.</b> The burst is rendered whole and handed to the channel as a
/// single transmission rather than a stream of blocks. That is not laziness: the transmitter
/// drains the output device between queued items, which on a real card stops and re-primes the
/// PCM, and a test signal with a hole in it every few hundred milliseconds is a poor instrument.
/// The cost is that once the burst has been rendered it goes out to its full length - which is
/// why the length is capped rather than merely defaulted. <see cref="Stop"/> before then (while
/// it is still waiting for the channel) sends nothing at all.</para>
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

    private readonly TxTestOptions _options;
    private readonly Lock _gate = new();
    private TestTone? _running;
    private bool _stopRequested;

    internal TxTestRunner(TxTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>What the operator's page is offered, or null when this station offers none.</summary>
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
        TestTone? tone;
        lock (_gate)
        {
            _stopRequested = true;
            tone = _running;
        }

        tone?.Stop();
    }

    /// <summary>
    /// Runs one test to its end, and says what happened. Never throws for an ordinary refusal -
    /// the refusal is the answer.
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

        (TestTone tone, string text, double audioHz) = prepared;
        try
        {
            _options.Journal.Write($"tx test: {text}");
            _options.Report?.Invoke(new TxTestStatus("running", text));

            string? rejection = null;
            Task send = _options.Channel.EnqueueTransmit(
                txDelay =>
                {
                    lock (_gate)
                    {
                        if (_stopRequested)
                        {
                            // Cancelled while it waited for the channel: the keyup happens (the
                            // transmitter has already taken it) and carries nothing, so PTT goes
                            // straight back down and no tone reaches the air.
                            return [];
                        }
                    }

                    // TXDELAY spent on silence, as the CW ident spends it: the PTT settling time
                    // wants the transmitter keyed and quiet, and an SSB rig radiates nothing
                    // without audio.
                    float[] burst = tone.Render();
                    int lead = (int)Math.Round(txDelay / 1000.0 * _options.Channel.SampleRate);
                    var audio = new float[lead + burst.Length];
                    burst.CopyTo(audio, lead);
                    return audio;
                },
                rejected: refusal => rejection = refusal.Message,
                // Its own identity, so the test takes its own keyup rather than being appended to
                // a modem's - the operator wants to measure the tones, not the frame in front of
                // them.
                source: this);

            // A bound on the wall clock as well as on the airtime. The airtime is bounded by the
            // burst itself; this is the other half - a test queued behind a channel that never
            // clears would otherwise leave the page saying "running" for ever and the station
            // unable to start another.
            Task waited = Task.Delay(
                TimeSpan.FromSeconds(tone.Remaining / (double)_options.Channel.SampleRate)
                    + _options.ChannelWait,
                _options.Time);
            if (await Task.WhenAny(send, waited).ConfigureAwait(false) == waited)
            {
                // Cancelled rather than waited out. The transmission cannot be taken off the
                // queue, but it can be emptied, and Stop does that: whenever the channel does
                // free up it renders a few milliseconds of fade and nothing else. Not awaited,
                // because on a channel that is genuinely wedged it would never return - and then
                // the operator could not ask again either, which is the failure this bound exists
                // to prevent. Its fault, if it has one, is observed and dropped.
                Stop();
                _ = send.ContinueWith(
                    faulted => _ = faulted.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);

                return Refuse(
                    $"the channel did not clear within {_options.ChannelWait.TotalSeconds:F0} s, "
                    + "so the test was cancelled and sends nothing");
            }

            try
            {
                await send.ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return Refuse(rejection ?? e.Message);
            }

            bool cut;
            lock (_gate)
            {
                cut = _stopRequested;
            }

            double onAir = tone.Produced / (double)_options.Channel.SampleRate;
            string done = cut
                ? $"stopped after {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s"
                : $"done, {onAir.ToString("0.0", CultureInfo.InvariantCulture)} s on air";
            _options.Journal.Write($"tx test: {done}");
            _options.Report?.Invoke(new TxTestStatus("done", $"{text} - {done}"));

            if (onAir > 0)
            {
                // Written down like a transmission, because it was one. See TxTestOptions.Recorded.
                _options.Recorded?.Invoke(new TxTestRecord(
                    _options.SubChannel, $"tx test: {text} - {done}", audioHz));
            }

            return new TxTestOutcome(onAir > 0, text, null);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _stopRequested = false;
            }
        }
    }

    /// <summary>
    /// Reads the request into a burst, or returns null when one is already running. The clamping
    /// happens here rather than at each of the three entry points, so the page, the API and the
    /// command line cannot grow three opinions about the cap.
    /// </summary>
    private (TestTone Tone, string Text, double AudioHz)? Prepare(
        TxTestRequest request, out string? refusal)
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

        var tone = new TestTone(tones, _options.Amplitude, _options.Channel.SampleRate, seconds);
        string text =
            $"{what}, {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
            + (capped ? $" (capped from {request.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s)" : "")
            + $", peak level {_options.Amplitude.ToString("0.00", CultureInfo.InvariantCulture)}";

        lock (_gate)
        {
            if (_running is not null)
            {
                refusal = "a test transmission is already running";
                return null;
            }

            _running = tone;
            _stopRequested = false;
        }

        return (tone, text, audioHz);
    }

    private TxTestOutcome Refuse(string why)
    {
        _options.Journal.WriteError($"tx test: refused, {why}");
        _options.Report?.Invoke(new TxTestStatus("refused", $"refused, {why}"));
        return new TxTestOutcome(false, "", why);
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
    /// somebody watching the public monitor of a station that publishes to one.
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
    public byte[] Payload => Encoding.ASCII.GetBytes(Text);
}

/// <summary>What became of one test.</summary>
/// <param name="Ran">True when tones actually went on the air.</param>
/// <param name="Text">What was asked for, in the journal's wording; empty on a refusal.</param>
/// <param name="Refusal">Why not, or null when it ran.</param>
internal sealed record TxTestOutcome(bool Ran, string Text, string? Refusal);
