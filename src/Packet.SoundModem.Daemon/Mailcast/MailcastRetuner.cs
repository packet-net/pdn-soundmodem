using System.Globalization;
using Packet.Mailcast;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Retunes the rig to the mailcast dial around each of GB7RDG's slots, through the rig's own
/// tuning windows, and lets it go back afterwards.
/// </summary>
/// <remarks>
/// <para>Each slot's window runs from <see cref="MailcastOnAir.ListenBefore"/> before its start
/// to <see cref="MailcastOnAir.ListenAfter"/> after, on the timetable the directory gives (GB7RDG's
/// own until one has been heard). A rig window lasts at most <see cref="RigControl.MaxWindow"/>,
/// so it is renewed every <see cref="RenewEvery"/> for as long as the slot's window lasts, and
/// asked for no further than its end: the rig goes back when the window's time runs out, even
/// if this has gone away.</para>
/// <para>Everything that keeps the station quiet meanwhile is the rig's: while its window is open
/// or a restore is owed the transmitter is held (the channel's inhibit) and any keyup refused
/// (the PTT guard). A station stopped mid-window puts the rig back at its next start-up from the
/// rig's restore file, and this then retunes it if the slot's window is still open.</para>
/// <para>A refusal (the transmitter keyed, the API holding a window of its own, rigctld away) is
/// tried again every <see cref="RetryEvery"/> while the slot's window lasts, and said once per
/// slot.</para>
/// <para>With <c>mailcast.hooks</c>, the retuner runs them too (see <see cref="MailcastHooks"/>),
/// in order with its own steps: "before" first, started its timeout earlier than the window's
/// opening so it has finished by then, and only if it worked is the rig tuned and the transmitter
/// held; "after" once the window is over, the rig back and transmissions released, and as the
/// station stops. A slot whose window the station stopped in last time, after its "before", is
/// not listened to again: its "after" runs instead.</para>
/// </remarks>
internal sealed class MailcastRetuner
{
    /// <summary>Who the rig's windows are opened for, in its journal lines.</summary>
    internal const string Owner = "mailcast";

    /// <summary>The passband the rig is asked for on the mailcast dial.</summary>
    internal const int PassbandHz = 3000;

    /// <summary>How often an open window is renewed.</summary>
    internal static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(1);

    /// <summary>How soon a refused retune is asked for again.</summary>
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait on the clock before looking at it again, so a clock put right
    /// by NTP, or a timetable heard meanwhile, is noticed within a minute.</summary>
    internal static readonly TimeSpan ClockCheck = TimeSpan.FromMinutes(1);

    private readonly RigControl _rig;
    private readonly RigTuning _tuning;
    private readonly Func<SlotTimetable> _timetable;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly MailcastHooks? _hooks;
    private DateTimeOffset? _finished;
    private DateTimeOffset? _hookSlot;
    private DateTimeOffset _hookCloses;
    private bool _hookOk;
    private bool _saidRigOwed;
    private volatile bool _listening;
    private volatile string _state = "starting";

    /// <param name="rig">The station's rig.</param>
    /// <param name="config">The mailcast section.</param>
    /// <param name="rigMode">The rig section's own mode, used when it is an upper-sideband one
    /// (PKTUSB for a rig whose data jack needs it); otherwise USB.</param>
    /// <param name="timetable">GB7RDG's timetable as it stands, asked afresh each time.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The journal.</param>
    /// <param name="hooks">The hooks to run around each slot, if any.</param>
    internal MailcastRetuner(
        RigControl rig, MailcastConfig config, string? rigMode, Func<SlotTimetable> timetable, TimeProvider time, Action<string> log,
        MailcastHooks? hooks = null)
    {
        _hooks = hooks;
        _rig = rig;
        string mode = rigMode is not null && RigModes.SidebandOf(rigMode) == "usb" ? rigMode.ToUpperInvariant() : "USB";
        // A 3000 Hz passband, which holds nearly all of the signal; a rig that will not set it is
        // left on its normal width, with a warning, by the rig control itself.
        _tuning = new RigTuning((long)Math.Round(config.DialHz), mode, PassbandHz);
        _timetable = timetable;
        _time = time;
        _log = log;
        rig.Changed += state => SetListening(state.Window is { Owner: Owner });
    }

    /// <summary>For tests: raised each time the loop has set its timer on the clock.</summary>
    internal event Action? Waiting;

    /// <summary>Whether the rig is on the mailcast dial for a window of this station's now.
    /// Read on the receive thread, so it takes no lock.</summary>
    internal bool Listening => _listening;

    /// <summary>Where it is, for the page: "on 7.052 MHz for the 14:00 UTC slot until 14:12 UTC".</summary>
    internal string State => _state;

    /// <summary>Where the rig is tuned for a slot.</summary>
    internal RigTuning Tuning => _tuning;

    /// <summary>
    /// The listening window in progress at <paramref name="now"/>, or else the next one, for a
    /// timetable; null when no slot runs within a year (a daylight rule no day satisfies).
    /// </summary>
    internal static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot)? WindowAt(DateTimeOffset now, SlotTimetable timetable) =>
        MailcastOnAir.WindowAt(now, timetable, MailcastOnAir.ListenBefore, MailcastOnAir.ListenAfter);

    /// <summary>How much earlier than a window's opening its work starts: the "before" hook's timeout, so it is done by then.</summary>
    private TimeSpan HookLead => _hooks is { Configured: true } hooks ? hooks.BeforeLead : TimeSpan.Zero;

    /// <summary>The window in progress at <paramref name="now"/> or the next, past any slot not to be listened to again.</summary>
    private MailcastHookWindow? NextWindow(DateTimeOffset now, SlotTimetable timetable) =>
        MailcastHooks.Next(
            at => WindowAt(at, timetable) is { } w ? new MailcastHookWindow(w.Opens, w.Closes, w.Slot) : null,
            now, _finished);

    /// <summary>Retunes around each slot until <paramref name="cancellation"/> is cancelled, then
    /// lets the rig go back.</summary>
    internal async Task RunAsync(CancellationToken cancellation)
    {
        DateTimeOffset? saidRefusalFor = null;
        DateTimeOffset? tunedFor = null;
        string? saidFailure = null;
        if (_hooks is not null)
        {
            // A slot whose window the station stopped in last time is not listened to again: its
            // "after" runs instead, once the rig is back.
            _hooks.Recover();
            _finished = _hooks.RecoveredSlot;
        }

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    (saidRefusalFor, tunedFor) = await StepAsync(saidRefusalFor, tunedFor, cancellation).ConfigureAwait(false);
                }
                catch (Exception e) when (!cancellation.IsCancellationRequested)
                {
                    // Whatever it was, retuning carries on: a loop that died here would leave the
                    // station deaf to every later slot without a word. Said once per new reason.
                    string why = MailcastOnAir.Ascii(e.Message);
                    if (why != saidFailure)
                    {
                        saidFailure = why;
                        _log($"mailcast: WARNING - the retuning loop failed ({why}); trying again in {RetryEvery.TotalSeconds:F0} s");
                    }

                    _state = "retuning failed: " + why;
                    await DelayAsync(RetryEvery, cancellation).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Put back at once rather than at the window's own end: the station is stopping.
            _rig.Release(Owner);
            SetListening(false);
            if (_hooks is not null && _hooks.AfterOwedTo(this, unowned: true))
            {
                if (_rig.RestorePending)
                {
                    _log("mailcast: hooks: WARNING - the rig is not back where it was as the station stops (a restore is owed, or a tuning window is open); "
                        + "running \"after\" all the same, so whatever it starts may find the rig still retuned");
                }

                await _hooks.AfterAsync(this, unowned: true, " as the station stops").ConfigureAwait(false);
            }
        }
    }

    /// <summary>One look at the clock and what follows from it; returns the slot a refusal was
    /// last said for and the slot the rig is tuned for.</summary>
    private async Task<(DateTimeOffset? SaidRefusalFor, DateTimeOffset? TunedFor)> StepAsync(
        DateTimeOffset? saidRefusalFor, DateTimeOffset? tunedFor, CancellationToken cancellation)
    {
        DateTimeOffset now = _time.GetUtcNow();
        MailcastHookWindow? next = NextWindow(now, _timetable());
        if (_hooks is { } owing && owing.AfterOwedTo(this, unowned: true) && (_hookSlot is null || now >= _hookCloses))
        {
            if (_hookSlot is { } last && next is { } following && following.Slot > last && following.Opens - HookLead <= _hookCloses)
            {
                // A following window whose "before" would start before this one closes keeps the hooks' window open.
                _hookSlot = following.Slot;
                _hookCloses = following.Closes;
                owing.Extend(following.Slot);
                _log(string.Create(CultureInfo.InvariantCulture,
                    $"mailcast: hooks: the {following.Slot.UtcDateTime:HH:mm} UTC slot follows straight on, so \"after\" waits until {following.Closes.UtcDateTime:HH:mm} UTC"));
            }
            else
            {
                if (tunedFor is { } ended)
                {
                    Ended(ended);
                    tunedFor = null;
                }

                if (_rig.RestorePending)
                {
                    // Whatever "after" starts must find the rig where it was, and free to transmit:
                    // not while any restore is owed, nor while anyone's window has the rig.
                    _state = "waiting for the rig to be put back before running the \"after\" command";
                    if (!_saidRigOwed)
                    {
                        _saidRigOwed = true;
                        _log("mailcast: hooks: waiting for the rig to be put back before running \"after\"");
                    }

                    await DelayAsync(RetryEvery, cancellation).ConfigureAwait(false);
                    return (saidRefusalFor, tunedFor);
                }

                _saidRigOwed = false;
                _state = "running the \"after\" command";
                await owing.AfterAsync(this, unowned: true).ConfigureAwait(false);
                _hookSlot = null;
                return (saidRefusalFor, tunedFor);
            }
        }

        if (next is not { } window)
        {
            _state = "no slot of the broadcast's runs in the coming year by its timetable";
            await DelayAsync(ClockCheck, cancellation).ConfigureAwait(false);
            return (saidRefusalFor, tunedFor);
        }

        if (_hooks is { Configured: true } hooks && _hookSlot != window.Slot)
        {
            // No "before" yet for this window: it runs first, its timeout ahead of the opening.
            if (tunedFor is not null)
            {
                Ended(tunedFor.Value);
                tunedFor = null;
            }

            DateTimeOffset startAt = window.Opens - HookLead;
            if (now < startAt)
            {
                _state = string.Create(CultureInfo.InvariantCulture,
                    $"waiting for the {window.Slot.UtcDateTime:HH:mm} UTC slot; the \"before\" command runs at {startAt.UtcDateTime:HH:mm:ss} UTC and the rig is retuned at {window.Opens.UtcDateTime:HH:mm} UTC");
                await DelayAsync(Shorter(startAt - now, ClockCheck), cancellation).ConfigureAwait(false);
                return (saidRefusalFor, tunedFor);
            }

            _state = string.Create(CultureInfo.InvariantCulture, $"running the \"before\" command for the {window.Slot.UtcDateTime:HH:mm} UTC slot");
            MailcastBeforeOutcome outcome = await hooks.BeforeAsync(
                window.Slot, this,
                "; so the rig is not retuned and nothing is held for that slot, as whatever it was to stop may still be transmitting",
                cancellation).ConfigureAwait(false);
            if (outcome == MailcastBeforeOutcome.Busy)
            {
                // Another window's "after" is owed first; it is run at the top of the next step.
                _state = "the hooks are still running for another window";
                await DelayAsync(RetryEvery, cancellation).ConfigureAwait(false);
                return (saidRefusalFor, tunedFor);
            }

            _hookSlot = window.Slot;
            _hookCloses = window.Closes;
            _hookOk = outcome == MailcastBeforeOutcome.Ok;
            return (saidRefusalFor, tunedFor);
        }

        if (_hooks is { Configured: true } && !_hookOk)
        {
            _state = string.Create(CultureInfo.InvariantCulture,
                $"not retuning for the {window.Slot.UtcDateTime:HH:mm} UTC slot: the \"before\" command failed; \"after\" runs at {_hookCloses.UtcDateTime:HH:mm} UTC");
            await DelayAsync(Shorter(_hookCloses - now, ClockCheck), cancellation).ConfigureAwait(false);
            return (saidRefusalFor, tunedFor);
        }

        if (now < window.Opens)
        {
            if (tunedFor is not null)
            {
                Ended(tunedFor.Value);
                tunedFor = null;
            }

            _state = string.Create(CultureInfo.InvariantCulture,
                $"waiting for the {window.Slot.UtcDateTime:HH:mm} UTC slot; the rig is retuned at {window.Opens.UtcDateTime:HH:mm} UTC");
            await DelayAsync(Shorter(window.Opens - now, ClockCheck), cancellation).ConfigureAwait(false);
            return (saidRefusalFor, tunedFor);
        }

        if (tunedFor is { } previous && previous != window.Slot)
        {
            Ended(previous);
            tunedFor = null;
        }

        TimeSpan left = window.Closes - now;
        RigTuneResult result = _rig.Tune(_tuning, Shorter(left, RigControl.MaxWindow), Owner);
        if (result.Granted)
        {
            if (tunedFor is null)
            {
                tunedFor = window.Slot;
                _log(string.Create(CultureInfo.InvariantCulture,
                    $"mailcast: rig on {MailcastOnAir.Mhz(_tuning.DialHz)} MHz {_tuning.Mode} for the {window.Slot.UtcDateTime:HH:mm} UTC slot "
                    + $"until {window.Closes.UtcDateTime:HH:mm} UTC; nothing is transmitted until it is put back"));
            }

            SetListening(true);
            _state = string.Create(CultureInfo.InvariantCulture,
                $"on {MailcastOnAir.Mhz(_tuning.DialHz)} MHz for the {window.Slot.UtcDateTime:HH:mm} UTC slot until {window.Closes.UtcDateTime:HH:mm} UTC");
            await DelayAsync(Shorter(left, RenewEvery), cancellation).ConfigureAwait(false);
            if (_time.GetUtcNow() >= window.Closes)
            {
                Ended(window.Slot);
                tunedFor = null;
            }

            return (saidRefusalFor, tunedFor);
        }

        tunedFor = null;
        _state = string.Create(CultureInfo.InvariantCulture,
            $"the {window.Slot.UtcDateTime:HH:mm} UTC slot is on, but the rig could not be retuned: {result.Why}");
        if (saidRefusalFor != window.Slot)
        {
            saidRefusalFor = window.Slot;
            _log(string.Create(CultureInfo.InvariantCulture,
                $"mailcast: WARNING - cannot retune the rig for the {window.Slot.UtcDateTime:HH:mm} UTC slot yet ({result.Why}); trying again every {RetryEvery.TotalSeconds:F0} s"));
        }

        await DelayAsync(Shorter(left, RetryEvery), cancellation).ConfigureAwait(false);
        return (saidRefusalFor, tunedFor);
    }


    private void Ended(DateTimeOffset slot)
    {
        _rig.Release(Owner);
        SetListening(false);
        _log(string.Create(CultureInfo.InvariantCulture,
            $"mailcast: the {slot.UtcDateTime:HH:mm} UTC slot's listening window has ended; the rig goes back"));
    }

    private void SetListening(bool listening)
    {
        if (_listening == listening)
        {
            return;
        }

        _listening = listening;
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    /// <summary>For tests: when the loop's current timer fires, so a fake clock can be moved on
    /// no further than that until the loop has acted and set its next one.</summary>
    internal DateTimeOffset NextWake { get; private set; } = DateTimeOffset.MinValue;

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        // The timer first, then the note of when it fires: a test that moves the clock on once it
        // sees the note never moves it before the timer exists.
        DateTimeOffset due = _time.GetUtcNow() + delay;
        Task tick = Task.Delay(delay, _time, cancellation);
        NextWake = due;
        Waiting?.Invoke();
        try
        {
            await tick.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
