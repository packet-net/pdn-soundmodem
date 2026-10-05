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
/// </remarks>
internal sealed class MailcastRetuner
{
    /// <summary>Who the rig's windows are opened for, in its journal lines.</summary>
    internal const string Owner = "mailcast";

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
    private volatile bool _listening;
    private volatile string _state = "starting";

    /// <param name="rig">The station's rig.</param>
    /// <param name="config">The mailcast section.</param>
    /// <param name="rigMode">The rig section's own mode, used when it is an upper-sideband one
    /// (PKTUSB for a rig whose data jack needs it); otherwise USB.</param>
    /// <param name="timetable">GB7RDG's timetable as it stands, asked afresh each time.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The journal.</param>
    internal MailcastRetuner(
        RigControl rig, MailcastConfig config, string? rigMode, Func<SlotTimetable> timetable, TimeProvider time, Action<string> log)
    {
        _rig = rig;
        string mode = rigMode is not null && RigModes.SidebandOf(rigMode) == "usb" ? rigMode.ToUpperInvariant() : "USB";
        _tuning = new RigTuning((long)Math.Round(config.DialHz), mode, 0);
        _timetable = timetable;
        _time = time;
        _log = log;
        rig.Changed += state => SetListening(state.Window is { Owner: Owner });
    }

    /// <summary>Raised when the rig goes onto the mailcast dial (true) or leaves it (false).</summary>
    internal event Action<bool>? ListeningChanged;

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
    internal static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot)? WindowAt(DateTimeOffset now, SlotTimetable timetable)
    {
        if (timetable.ActiveAtOrBefore(now + MailcastOnAir.ListenBefore) is { } started
            && now < started + MailcastOnAir.ListenAfter)
        {
            return (started - MailcastOnAir.ListenBefore, started + MailcastOnAir.ListenAfter, started);
        }

        return timetable.NextActiveAtOrAfter(now + MailcastOnAir.ListenBefore) is { } next
            ? (next - MailcastOnAir.ListenBefore, next + MailcastOnAir.ListenAfter, next)
            : null;
    }

    /// <summary>Retunes around each slot until <paramref name="cancellation"/> is cancelled, then
    /// lets the rig go back.</summary>
    internal async Task RunAsync(CancellationToken cancellation)
    {
        DateTimeOffset? saidRefusalFor = null;
        DateTimeOffset? tunedFor = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                DateTimeOffset now = _time.GetUtcNow();
                if (WindowAt(now, _timetable()) is not { } window)
                {
                    _state = "no slot of GB7RDG's runs in the coming year by its timetable";
                    await DelayAsync(ClockCheck, cancellation).ConfigureAwait(false);
                    continue;
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
                    continue;
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

                    continue;
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
            }
        }
        finally
        {
            // Put back at once rather than at the window's own end: the station is stopping.
            _rig.Release(Owner);
            SetListening(false);
        }
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
        ListeningChanged?.Invoke(listening);
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : b;

    /// <summary>For tests: when the loop's current timer fires, so a fake clock can be moved on
    /// no further than that until the loop has acted and set its next one.</summary>
    internal DateTimeOffset NextWake { get; private set; } = DateTimeOffset.MinValue;

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellation)
    {
        NextWake = _time.GetUtcNow() + delay;
        Task tick = Task.Delay(delay, _time, cancellation);
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
