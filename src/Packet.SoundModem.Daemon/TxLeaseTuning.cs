using System.Text.Json.Nodes;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Issue #760: a transmit lease that tunes the rig for its holder's modem. The rig is moved
/// before the lease is granted, held there while the lease is, and put back through the same
/// tuning window every other caller uses when the lease is released, runs out or the station
/// stops.
/// </summary>
/// <remarks>
/// The window is the rig's own (<see cref="RigControl.Tune"/>), opened for
/// <see cref="Owner"/> and not holding the transmitter, so the holder can transmit inside it
/// while a receive window and the API's windows are still refused by the rig's owner check. It
/// is kept alive by <see cref="RigControl.Extend"/>, which works in the middle of a transmission
/// where a retune is refused, and ended when the lease finishes (after any closing ident).
/// </remarks>
internal sealed class TxLeaseTuning
{
    /// <summary>Who the rig's window is held by, in the rig's journal lines.</summary>
    internal const string Owner = "the transmit lease";

    private readonly RigControl _rig;
    private readonly Func<int, (RigTuning? Tuning, string? Refusal)> _resolve;

    /// <param name="rig">The station's rig.</param>
    /// <param name="lease">The station's lease, whose end puts the rig back.</param>
    /// <param name="resolve">Where the rig goes for a sub-channel's modem, or why it cannot.</param>
    internal TxLeaseTuning(
        RigControl rig, TransmitLease lease, Func<int, (RigTuning? Tuning, string? Refusal)> resolve)
    {
        _rig = rig;
        _resolve = resolve;
        lease.Changed += change =>
        {
            if (change.Change is TransmitLeaseChange.Released or TransmitLeaseChange.Expired)
            {
                _rig.Release(Owner);
            }
        };
    }

    /// <summary>What the rig is tuned to for the lease right now, or null when it is not.</summary>
    internal RigTuning? Held =>
        _rig.Snapshot().Window is { } window && window.Owner == Owner ? window.Tuning : null;

    /// <summary>Keeps an open tuning alive for another <paramref name="seconds"/>.</summary>
    internal bool Extend(double seconds) => _rig.Extend(Owner, WindowFor(seconds));

    /// <summary>Puts the rig back now, if the lease holds a tuning.</summary>
    internal bool Release() => _rig.Release(Owner);

    /// <summary>
    /// Tunes the rig for <paramref name="subChannel"/>'s modem, or keeps the tuning it already
    /// has. <paramref name="status"/> is 0 on success.
    /// </summary>
    internal RigTuning? Apply(int subChannel, double seconds, out int status, out string? why)
    {
        status = 0;
        why = null;
        (RigTuning? target, string? refusal) = _resolve(subChannel);
        if (target is null)
        {
            status = 400;
            why = refusal;
            return null;
        }

        if (Held == target && _rig.Extend(Owner, WindowFor(seconds)))
        {
            return target;
        }

        RigTuneResult result = _rig.Tune(
            target, WindowFor(seconds), Owner, holdsTransmitter: false);
        if (result.Granted)
        {
            return target;
        }

        status = result.Outcome switch
        {
            RigTuneOutcome.Invalid => 400,
            RigTuneOutcome.Failed => 500,
            _ => 409,
        };
        why = result.Why;
        return null;
    }

    /// <summary>The window outlives the lease by its closing ident's allowance.</summary>
    private static TimeSpan WindowFor(double seconds) =>
        TimeSpan.FromSeconds(Math.Min(seconds, TransmitLease.MaxDuration.TotalSeconds))
        + TransmitLease.ClosingTimeout;

    /// <summary>
    /// The plan a sub-channel's transmissions are placed by: its own alone-plan for a modem that
    /// transmits only under a tuned lease (so its ident tone is worked out against the tuned
    /// dial), the station's everyday plan for any other.
    /// </summary>
    internal static RfPlan.Result? PlanFor(
        int subChannel, RfPlan.Result? everyday, IReadOnlyDictionary<int, RfPlan.Result> leaseOnlyPlans) =>
        leaseOnlyPlans.TryGetValue(subChannel, out RfPlan.Result? own) ? own : everyday;

    /// <summary>The tuning as an answer's fields.</summary>
    internal static void Describe(JsonObject answer, RigTuning? tuning)
    {
        answer["tuned"] = tuning is not null;
        if (tuning is not null)
        {
            answer["dialHz"] = tuning.DialHz;
            answer["mode"] = tuning.Mode;
            answer["passbandHz"] = tuning.PassbandHz;
        }
    }

    /// <summary>
    /// Where the rig has to be for one modem. A <see cref="ModemConfig.TransmitOnlyUnderLease"/>
    /// modem has its own plan, made as if it were alone: the dial that puts its
    /// <c>rfFrequency</c> at the audio centre it was fixed at, with a passband wide enough for it.
    /// Any other modem is already on the station's everyday plan, which is what it is given.
    /// </summary>
    /// <param name="subChannel">The lease holder.</param>
    /// <param name="modems">The station's modems as configured.</param>
    /// <param name="plan">The everyday band plan, or null without one.</param>
    /// <param name="leaseOnlyPlans">The alone-plan of each lease-only modem, by sub-channel.</param>
    /// <param name="dialOffsetHz">The station device's rig dial offset for the plan's sideband.</param>
    /// <param name="mode">The rig section's own mode, or null for the sideband's data mode.</param>
    internal static (RigTuning? Tuning, string? Refusal) Resolve(
        int subChannel, IReadOnlyList<ModemConfig> modems, RfPlan.Result? plan,
        IReadOnlyDictionary<int, RfPlan.Result> leaseOnlyPlans, double dialOffsetHz, string? mode)
    {
        ModemConfig? modem = modems.FirstOrDefault(m => m.SubChannel == subChannel);
        if (modem is null)
        {
            return (null, $"no modem transmits on sub-channel {subChannel}");
        }

        if (modem.RfFrequency is null || (plan is null && !leaseOnlyPlans.ContainsKey(subChannel)))
        {
            return (null,
                $"sub-channel {subChannel} has no \"rfFrequency\", so there is no dial to tune the rig to for it");
        }

        bool alone = leaseOnlyPlans.TryGetValue(subChannel, out RfPlan.Result? own);
        RfPlan.Result used = alone ? own! : plan!;
        string rigMode = mode?.ToUpperInvariant() ?? RigModes.ForSideband(used.Sideband);
        if (used.IsFm)
        {
            return (new RigTuning((long)Math.Round(used.DialHz + dialOffsetHz), rigMode, 0), null);
        }

        PlannedModem? planned = used.Modems.FirstOrDefault(p => p.Slot.SubChannel == subChannel);
        if (planned is null)
        {
            return (null, $"sub-channel {subChannel} is not in the band plan");
        }

        int width = Math.Max(
            (int)Math.Round(used.Window.WidthHz),
            BandPlanner.HighCutClearing(planned.AudioCentreHz + (planned.Slot.BandwidthHz / 2)));
        return (new RigTuning(
            (long)Math.Round(used.DialHz + dialOffsetHz), rigMode, Math.Clamp(width, 0, RigModes.MaxPassbandHz)), null);
    }
}
