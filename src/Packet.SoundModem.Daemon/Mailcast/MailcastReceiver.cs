using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Packet.Mailcast;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The built-in pdn-mailcast receiver: an MS110D modem on GB7RDG's signal, the store its frames
/// feed, the slots and tones it hears, and the delivery of rebuilt bulletins into the BBS.
/// </summary>
/// <remarks>
/// <para>The modem is a receive tap on the station's channel, not one of its modems, the same as
/// the id-beacon ghosts: it has no KISS sub-channel, so nothing a host sends can reach it and
/// nothing it hears reaches a host; it never transmits; and it takes no part in carrier
/// sense.</para>
/// <para>When the rig is retuned for it (<see cref="Retuner"/> set), the modem and the tone
/// detector are only fed while the rig is on the mailcast dial, and start afresh each time it
/// gets there: the rest of the time the audio is the station's own frequency.</para>
/// <para>A modem locked on one burst for longer than <see cref="MailcastOnAir.LongestBurst"/> is
/// made to listen afresh (see there for why it would not by itself).</para>
/// </remarks>
internal sealed class MailcastReceiver : IAsyncDisposable
{
    private readonly MailcastConfig _config;
    private readonly MailcastPlacement _placement;
    private readonly int _rate;
    private readonly IModem _modem;
    private readonly MailcastToneDetector _tone;
    private readonly long _lockLimit;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private long _locked;
    private int _locksReleased;
    private volatile bool _resetOwed;
    private bool _wasListening;

    private MailcastReceiver(
        MailcastConfig config, MailcastPlacement placement, int dspRate, string directory,
        TimeProvider time, Action<string> log, IMailcastBbs? bbs, TimeSpan? longestBurst)
    {
        _config = config;
        _placement = placement;
        _rate = dspRate;
        _time = time;
        _log = log;
        Directory = directory;
        Intake = new MailcastIntake(Path.Combine(directory, "store"), time, log);
        Ledger = new MailcastLedger(directory);
        Slots = new MailcastSlots(time, log, Intake.HeardTimetable);
        Delivery = new MailcastDelivery(Intake, bbs ?? new MailcastBbsClient(config.BbsInUse, time), Ledger, time, log);
        Hooks = new MailcastHooks(config, directory, time, log);
        Intake.FrameHeard += Slots.OnFrame;
        Intake.TimetableHeard += Slots.Heard;

        // The modem at the signal's centre in this station's audio: 1800 Hz, the standard's own,
        // on a retuned rig; wherever the dial puts it otherwise, shifted the way a band plan
        // places any MS110D modem.
        double centre = placement.AudioCentreHz;
        _modem = ModemCatalog.Create(
            MailcastOnAir.Mode, dspRate, frame => Intake.Offer(frame),
            Math.Abs(centre - MailcastOnAir.CentreAudioHz) < 0.5 ? default : new ModemOptions(CentreFrequencyHz: centre));
        _tone = new MailcastToneDetector(dspRate, centre);
        _tone.Measured += Slots.OnTone;
        _lockLimit = (long)((longestBurst ?? MailcastOnAir.LongestBurst).TotalSeconds * dspRate);
    }

    /// <summary>Where the store, the outbox and the record of deliveries are.</summary>
    internal string Directory { get; }

    /// <summary>The store the frames feed.</summary>
    internal MailcastIntake Intake { get; }

    /// <summary>What the BBS has said.</summary>
    internal MailcastLedger Ledger { get; }

    /// <summary>The timetable and the latest slot.</summary>
    internal MailcastSlots Slots { get; }

    /// <summary>The delivery loop.</summary>
    internal MailcastDelivery Delivery { get; }

    /// <summary>The config's <c>hooks</c>, run around each slot listened to: by the retuner when
    /// there is one, else by <see cref="RunAsync"/>.</summary>
    internal MailcastHooks Hooks { get; }

    /// <summary>The rig retuner, on a station whose passband does not reach the signal; null on
    /// one that hears it where it is. Set before audio flows, made with <see cref="Hooks"/>.</summary>
    internal MailcastRetuner? Retuner { get; set; }

    /// <summary>How many times a lock that outlasted any burst has been let go.</summary>
    internal int LocksReleased => Volatile.Read(ref _locksReleased);

    /// <summary>Builds the receiver, opening (or creating) its files in <paramref name="directory"/>.</summary>
    /// <param name="bbs">For tests: the BBS to deliver to in place of the configured one.</param>
    /// <param name="longestBurst">For tests: a shorter stuck-lock limit.</param>
    internal static MailcastReceiver Create(
        MailcastConfig config, MailcastPlacement placement, int dspRate, string directory,
        TimeProvider time, Action<string> log, IMailcastBbs? bbs = null, TimeSpan? longestBurst = null) =>
        new(config, placement, dspRate, directory, time, log, bbs, longestBurst);

    /// <summary>Puts the receiver on <paramref name="channel"/>'s audio.</summary>
    internal void Attach(SoundModemChannel channel)
    {
        channel.AddReceiveTap(Process);

        // A tap is not one of the channel's modems, so the reset that follows our own keyups
        // misses it; done on the receive thread, at the next block.
        channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                _resetOwed = true;
            }
        };
    }

    /// <summary>One block of the channel's audio, on the receive thread.</summary>
    internal void Process(ReadOnlySpan<float> samples)
    {
        bool listening = Retuner?.Listening ?? true;
        if (listening != _wasListening)
        {
            _wasListening = listening;
            _resetOwed = true;
        }

        if (!listening)
        {
            return;
        }

        if (_resetOwed)
        {
            _resetOwed = false;
            _modem.ResetCarrierState();
            _tone.Reset();
            _locked = 0;
        }

        _modem.Process(samples);
        _tone.Process(samples);
        ReleaseStuckLock(samples.Length);
    }

    private void ReleaseStuckLock(int samples)
    {
        if (!_modem.CarrierDetect)
        {
            _locked = 0;
            return;
        }

        _locked += samples;
        if (_locked < _lockLimit)
        {
            return;
        }

        _modem.ResetCarrierState();
        Interlocked.Increment(ref _locksReleased);
        _log($"mailcast: the modem had been locked on one burst for {_locked / _rate} s, longer than any GB7RDG sends, "
            + "so it was a signal too weak to read; listening afresh");
        _locked = 0;
    }

    /// <summary>
    /// Runs delivery, and the retuner or (on a station that hears the signal where it is) the
    /// hooks, until <paramref name="cancellation"/> is cancelled; then, once the retuner has put
    /// the rig back, the "after" hook if it is still owed.
    /// </summary>
    internal async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            Task delivery = Task.Run(() => Delivery.RunAsync(cancellation), CancellationToken.None);
            Task slots = Retuner is { } retuner
                ? Task.Run(() => retuner.RunAsync(cancellation), CancellationToken.None)
                : Task.Run(() => Hooks.RunAsync(at => MailcastHooks.PassbandWindowAt(at, Slots.Timetable), cancellation), CancellationToken.None);
            try
            {
                await Task.WhenAll(delivery, slots).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            // "after" always runs once "before" has, whatever stopped the station.
            await Hooks.FinishAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The start-up lines: where it listens, the timetable, the BBS and the files.</summary>
    internal IEnumerable<string> Describe()
    {
        yield return _placement.Describe(_config);
        yield return $"mailcast: GB7RDG's slots are {MailcastSlots.Describe(Slots.Timetable)}"
            + (Slots.FromDirectory ? ", as its directory gives them" : " (its own; its directory updates them once heard)");
        yield return $"mailcast: delivering rebuilt bulletins to {_config.BbsInUse.Describe()}; state in {Directory}";
        if (Hooks.DescribeConfig() is { } hooks)
        {
            yield return hooks;
        }
    }

    /// <summary>What the page and <c>GET /api/mailcast</c> show. Never the BBS password.</summary>
    internal JsonObject Status()
    {
        (BroadcastDirectory? directory, IReadOnlyList<ObjectProgress> progress, int partial) = Intake.Progress();
        MailcastSlotSummary? last = Slots.Last;
        int waiting = Intake.Pending().Count;
        string? failure = Delivery.LastFailure;
        return new JsonObject
        {
            ["placement"] = _placement.Retunes ? "retune" : "passband",
            ["dialMhz"] = Math.Round(_config.DialHz / 1e6, 6),
            ["centreMhz"] = Math.Round(_config.CentreHz / 1e6, 6),
            ["audioCentreHz"] = Math.Round(_placement.AudioCentreHz, 1),
            ["timetable"] = MailcastSlots.Describe(Slots.Timetable),
            ["timetableFromDirectory"] = Slots.FromDirectory,
            ["nextSlot"] = Iso(Slots.Next),
            ["lastSlot"] = last is null ? null : new JsonObject
            {
                ["slot"] = Iso(last.Slot),
                ["frames"] = last.Frames,
                ["lastFrame"] = Iso(last.LastFrame),
                ["toneOffsetHz"] = last.Tone is { } tone ? Math.Round(tone.OffsetHz, 1) : null,
                ["toneSnrDb"] = last.Tone is { } snr ? Math.Round(snr.SnrDb, 1) : null,
            },
            ["framesHeard"] = Intake.FramesHeard,
            ["bulletins"] = new JsonObject
            {
                ["inRotation"] = directory?.Entries.Count(e => e.Type == (byte)ObjectKind.Bulletin) ?? 0,
                ["complete"] = progress.Count(p => p.Complete && p.Entry.Type == (byte)ObjectKind.Bulletin),
                ["partial"] = partial,
                ["waitingForBbs"] = waiting,
                ["delivered"] = Ledger.Delivered,
            },
            ["bbs"] = new JsonObject
            {
                ["at"] = _config.BbsInUse.Describe(),
                ["state"] = failure is not null ? "failing" : Delivery.LastSession is null ? "not tried yet" : "ok",
                ["lastFailure"] = failure,
                ["lastSession"] = Iso(Delivery.LastSession),
                ["nextAttempt"] = Iso(Delivery.NextAttempt),
            },
            ["retune"] = Retuner is null ? null : new JsonObject
            {
                ["listening"] = Retuner.Listening,
                ["state"] = Retuner.State,
            },
            ["locksReleased"] = LocksReleased,
        };
    }

    /// <summary>
    /// <c>GET /api/mailcast</c> in front of whatever else answers under <c>/api/</c>: read-only
    /// and keyless, like the operator's page it is drawn on, and never on a public page.
    /// </summary>
    internal Func<HttpListenerContext, string, Task<bool>> Serve(Func<HttpListenerContext, string, Task<bool>>? others) =>
        async (context, path) =>
        {
            if (path is "/api/mailcast")
            {
                if (context.Request.HttpMethod != "GET")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Headers["Allow"] = "GET";
                    context.Response.Close();
                    return true;
                }

                byte[] body = System.Text.Encoding.UTF8.GetBytes(Status().ToJsonString());
                context.Response.ContentType = "application/json";
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                context.Response.Close();
                return true;
            }

            return others is not null && await others(context, path).ConfigureAwait(false);
        };

    private static string? Iso(DateTimeOffset? at) =>
        at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Intake.DisposeAsync().ConfigureAwait(false);
        Hooks.Dispose();
    }
}
