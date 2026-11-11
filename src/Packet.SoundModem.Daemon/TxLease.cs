using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// <c>/api/txlease</c>: one sub-channel takes the station's transmitter for a while, and every
/// other transmission is refused until it gives it back or stops renewing.
/// </summary>
/// <remarks>
/// <para>The request handling, without the HTTP: <see cref="ConfigApi"/> reads the body and writes
/// the answer, and everything in between is here, where a test can reach it with a fake clock and
/// no listener. The lease itself is the channel's (<see cref="TransmitLease"/>), so a KISS frame,
/// an ident and a test tone are all refused by the same rule however they arrive.</para>
/// <para>Built for pdn-mailcast's daily slot: the head end takes a 60 s lease, renews it every
/// 30 s for the length of the broadcast and releases it at the end. If it dies, the lease runs
/// out on its own within a minute and the station's ordinary traffic resumes.</para>
/// </remarks>
internal static class TxLeaseApi
{
    /// <summary>How long a lease lasts when the request does not say.</summary>
    internal const double DefaultSeconds = 60;

    /// <summary>The longest carrier wait a lease may set.</summary>
    internal const double MaxCarrierWaitCeiling = 300;

    /// <summary>What a GET or a malformed request is told about the shape of a good one.</summary>
    internal const string Usage =
        "POST {\"subChannel\": 3, \"seconds\": 60} to take or renew the transmit lease for that "
        + "sub-channel (at most 300 s at a time, optionally with \"maxCarrierWaitSeconds\"), "
        + "\"tune\": true to tune the rig for that sub-channel's modem first, "
        + "{\"release\": true, \"subChannel\": 3} to give it back, \"dropQueued\": true with or "
        + "without a release to drop its unsent frames; GET to read it";

    /// <summary>Answers one request.</summary>
    /// <param name="channel">The station's channel, whose lease and carrier sense these are.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="body">The request body, possibly empty.</param>
    /// <param name="cannot">Why this station cannot transmit at all, or null.</param>
    /// <param name="rig">
    /// The station's rig, or null without one. A lease is refused while a receive window
    /// (issue #585) holds it, so a listener's receive window and a head end's broadcast lease
    /// can never both be granted; see <see cref="Waterfall.WaterfallWebServer.ReceiveWindowOwner"/>.
    /// </param>
    /// <param name="tuning">What tunes the rig for a take that asks for it (issue #760), or null
    /// without a rig; then <c>"tune": true</c> is accepted and ignored.</param>
    /// <param name="reference">The GPSDO's 10 MHz reference (issue #789), or null for a station
    /// with no <c>gpsdo</c> section; every answer that describes the lease then carries it.</param>
    /// <param name="audioCentreOf">Where a sub-channel's modem sits in the audio, in Hz, or null
    /// when it has no centre (a baseband mode); every answer that names a holder then carries the
    /// holder's as <c>audioCentreHz</c>, tuned or not, so a head end can put its calibration tone
    /// with its data.</param>
    internal static (int Status, JsonObject Answer) Handle(
        SoundModemChannel channel, string method, string body, string? cannot, RigControl? rig = null,
        TxLeaseTuning? tuning = null, GpsdoReference? reference = null, Func<int, double?>? audioCentreOf = null)
    {
        (int status, JsonObject answer) = Answer(channel, method, body, cannot, rig, tuning);

        // A tuned lease has already said where its modem sits in the audio on the tuned dial.
        if (audioCentreOf is not null && !answer.ContainsKey("audioCentreHz")
            && answer["subChannel"] is JsonValue named && named.TryGetValue(out int holder)
            && audioCentreOf(holder) is double centre)
        {
            answer["audioCentreHz"] = Math.Round(centre, 1);
        }

        // On every answer that describes the lease, and on nothing else: a head end reads it
        // before each slot from the GET, and the take that follows reads the same. Left out
        // entirely without a "gpsdo" section, so a head end can tell "no feed" from "unknown".
        if (reference is not null && answer.ContainsKey("held"))
        {
            answer["reference"] = reference.Describe();
        }

        return (status, answer);
    }

    /// <summary>
    /// Where a sub-channel's modem sits in the audio, as it is built: the centre the band plan wrote
    /// back (a lease-only modem's own, planned alone), else its mode's own centre (ARDOP's native
    /// one). Null with no such modem, or for a baseband mode, which has no centre.
    /// </summary>
    internal static double? AudioCentreOf(IReadOnlyList<ModemConfig> modems, int subChannel) =>
        modems.FirstOrDefault(m => m.SubChannel == subChannel) is { } modem
            ? modem.Frequency ?? (DaemonConfig.IsArdop(modem.Mode)
                ? ArdopChannelBridge.NativeCentreHz
                : ModemCatalog.DefaultCentreFrequencyFor(modem.Mode))
            : null;

    private static (int Status, JsonObject Answer) Answer(
        SoundModemChannel channel, string method, string body, string? cannot, RigControl? rig,
        TxLeaseTuning? tuning)
    {
        TransmitLease lease = channel.TransmitLease;
        if (method == "GET")
        {
            return (200, Describe(channel, tuning));
        }

        if (method != "POST")
        {
            return (405, Error(Usage));
        }

        JsonNode? asked;
        try
        {
            asked = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (JsonException bad)
        {
            return (400, Error($"the body is not JSON: {bad.Message}"));
        }

        bool release;
        bool dropQueued;
        int? subChannel;
        double seconds;
        double? maxCarrierWait;
        bool tune;
        try
        {
            release = asked?["release"]?.GetValue<bool>() == true;
            dropQueued = asked?["dropQueued"]?.GetValue<bool>() == true;
            subChannel = asked?["subChannel"]?.GetValue<int>();
            seconds = asked?["seconds"]?.GetValue<double>() ?? DefaultSeconds;
            maxCarrierWait = asked?["maxCarrierWaitSeconds"]?.GetValue<double>();
            tune = asked?["tune"]?.GetValue<bool>() == true;
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            return (400, Error(
                "\"subChannel\" is a whole number, \"seconds\" and \"maxCarrierWaitSeconds\" numbers, "
                + $"\"release\", \"dropQueued\" and \"tune\" true or false: {wrongType.Message}"));
        }

        if (release || dropQueued)
        {
            int? holder = lease.Holder;
            if (subChannel is int named && holder is int held && held != named)
            {
                return (409, Conflict(channel, tuning, $"sub-channel {held} holds the transmit lease, not {named}"));
            }

            if (!release && holder is null)
            {
                return (409, Conflict(channel, tuning, "no transmit lease is held, so there is nothing of its to drop"));
            }

            // Dropped first, so the closing ident a release may send is the last thing of the
            // holder's to go out, with nothing of the abandoned broadcast behind it.
            int dropped = dropQueued && holder is int dropping ? channel.DropQueued(dropping) : 0;
            // Released only when a holder was actually read, and only that one: never a bare
            // Release() that would free whoever happens to hold it by the time it runs.
            bool released = release && holder is int releasing && lease.Release(subChannel ?? releasing);
            JsonObject answer = Describe(channel, tuning);
            if (release)
            {
                answer["released"] = released;
            }

            if (dropQueued)
            {
                answer["dropped"] = dropped;
            }

            return (200, answer);
        }

        if (subChannel is not int sub)
        {
            return (400, Error(Usage));
        }

        if (cannot is not null)
        {
            return (409, Error(cannot));
        }

        if (!channel.Modems.ContainsKey(sub))
        {
            return (400, Error($"no modem transmits on sub-channel {sub}"));
        }

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return (400, Error("\"seconds\" must be more than 0"));
        }

        if (maxCarrierWait is double wait && (!double.IsFinite(wait) || wait < 1 || wait > MaxCarrierWaitCeiling))
        {
            return (400, Error(
                $"\"maxCarrierWaitSeconds\" must be 1 to {MaxCarrierWaitCeiling:0}, or left out for ordinary carrier sense"));
        }

        // Issue #760. A sub-channel whose modem transmits only under a tuned lease can have no
        // other kind, and a tuned lease is shorter so that its window outlives the closing ident.
        bool leaseOnly = tuning is not null && tuning.IsLeaseOnly(sub);
        double max = leaseOnly
            ? TxLeaseTuning.MaxTunedLease.TotalSeconds
            : TransmitLease.MaxDuration.TotalSeconds;
        bool capped = seconds > max;
        if (leaseOnly && !tune)
        {
            JsonObject untuned = Describe(channel, tuning);
            untuned["refused"] =
                $"sub-channel {sub} only transmits under a tuned transmit lease: take it with \"tune\": true";
            untuned["tuneFailed"] = true;
            return (409, untuned);
        }

        // A receive window (issue #585) and a transmit lease can never both be held: the window
        // takes the station off the air for another program's purposes, and a lease granted
        // underneath it would be a promise this station could not keep. The lease decides the two
        // against each other, so neither can slip in while the other is deciding.
        TransmitLeaseGrant? taken = lease.TakeUnlessWindow(
            () => rig?.Snapshot().Window is { Owner: Waterfall.WaterfallWebServer.ReceiveWindowOwner },
            sub, TimeSpan.FromSeconds(Math.Min(seconds, max)),
            maxCarrierWait is double limit ? TimeSpan.FromSeconds(limit) : null);
        if (taken is not TransmitLeaseGrant grant)
        {
            return (409, Conflict(channel, tuning,
                rig?.Snapshot().Window is { Owner: Waterfall.WaterfallWebServer.ReceiveWindowOwner } open
                    ? $"a receive window holds the rig on {open.Tuning} until {Utc(open.Expires)}, so "
                      + "the transmit lease is refused until it ends"
                    : "a receive window is being opened, so the transmit lease is refused until it ends"));
        }

        if (!grant.Granted)
        {
            string why = !lease.IsClosing
                ? $"sub-channel {grant.SubChannel} holds the transmit lease until {Utc(grant.Expires)}"
                : grant.SubChannel == sub
                    ? "this lease is closing: it was released or ran out and its closing ident is "
                        + "going out. It cannot be renewed; take a new one once \"closing\" reads false"
                    : $"sub-channel {grant.SubChannel}'s lease is closing and sending its closing ident; "
                        + "try again once \"closing\" reads false";
            return (409, Conflict(channel, tuning, why));
        }

        // Tuned only once the lease is ours, so no other modem's frame can slip onto the new dial
        // in a gap before it (the channel also refuses everything while a tuning is open and
        // nobody holds the lease), and a keyed rig can finish its transmission first. A renewal
        // keeps the tuning without retuning it, in the middle of a transmission too. Only a
        // lease-only modem is ever retuned.
        string? note = null;
        if (tuning is not null && leaseOnly)
        {
            RigTuning? tuned = tuning.Apply(sub, Math.Min(seconds, max), out int tuneStatus, out string? tuneWhy);
            if (tuned is null)
            {
                if (!grant.Renewed)
                {
                    lease.Release(sub);
                }

                JsonObject failed = Describe(channel, tuning);
                failed["refused"] = $"the rig could not be tuned for sub-channel {sub}: {tuneWhy}"
                    + (grant.Renewed ? "; the lease is still held" : "");
                failed["tuneFailed"] = true;
                return (tuneStatus, failed);
            }
        }
        else if (tune)
        {
            note = tuning is null
                ? "this station has no \"rig\" section, so there is no rig to tune; \"tune\" was ignored"
                : $"sub-channel {sub} is already on its everyday dial; \"tune\" was ignored";
        }

        JsonObject granted = Describe(channel, tuning);
        if (note is not null)
        {
            granted["tuned"] = false;
            granted["note"] = note;
        }

        granted["renewed"] = grant.Renewed;
        granted["seconds"] = Math.Min(seconds, max);
        granted["capped"] = capped;
        return (200, granted);
    }

    /// <summary>
    /// The lease as it stands - who holds it, until when, and how - and the channel as carrier
    /// sense sees it right now, so a caller can decide without trying to transmit.
    /// </summary>
    internal static JsonObject Describe(SoundModemChannel channel, TxLeaseTuning? tuning = null)
    {
        TransmitLease lease = channel.TransmitLease;
        int? holder = lease.Holder;
        DateTimeOffset? expires = lease.Expires;
        var described = new JsonObject
        {
            ["held"] = holder is not null,
            ["subChannel"] = holder,
            ["expires"] = expires is { } at ? Utc(at) : null,
            ["closing"] = lease.IsClosing,
            ["maxCarrierWaitSeconds"] = lease.MaxCarrierWait?.TotalSeconds,
            // For the holder's own passband when there is one, the whole station otherwise: the
            // same question the transmitter asks before it keys.
            ["channelBusy"] = holder is int sub ? channel.ChannelBusyFor(sub) : channel.ChannelBusy,
            ["carrierDetect"] = channel.CarrierDetect,
        };
        tuning?.Describe(described, holder);
        if (tuning is null)
        {
            described["tuned"] = false;
        }
        return described;
    }

    private static JsonObject Conflict(SoundModemChannel channel, TxLeaseTuning? tuning, string why)
    {
        JsonObject answer = Describe(channel, tuning);
        answer["refused"] = why;
        return answer;
    }

    private static JsonObject Error(string why) => new() { ["refused"] = why };

    /// <summary>An instant as ISO 8601 UTC to the second, which is what a script parses and a
    /// person reads.</summary>
    internal static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// The journal's account of the transmit lease: one line when it is taken, released or runs out,
/// and a renewal line now and then rather than every 30 s.
/// </summary>
internal sealed class TxLeaseJournal
{
    /// <summary>The least time between two renewal lines. A slot renewing every 30 s would
    /// otherwise write thirty identical lines in a quarter of an hour.</summary>
    internal static readonly TimeSpan RenewalLineInterval = TimeSpan.FromMinutes(5);

    private readonly StationJournal _journal;
    private readonly Func<int, string> _describe;
    private readonly Lock _gate = new();
    private TimeSpan _lastRenewalLine;

    /// <param name="journal">Where the lines go.</param>
    /// <param name="describe">How to name a sub-channel's modem, e.g. its mode.</param>
    internal TxLeaseJournal(StationJournal journal, Func<int, string> describe)
    {
        _journal = journal;
        _describe = describe;
    }

    /// <summary>Says that the holder's transmission went after waiting its lease's limit for a
    /// clear channel.</summary>
    internal void NoteCarrierWaitCutShort(int subChannel, TimeSpan waited) =>
        _journal.Write(
            $"tx lease: sub-channel {subChannel} ({_describe(subChannel)}) transmitted after "
            + $"{Span(waited)} without a clear channel (maxCarrierWaitSeconds)");

    /// <summary>Writes the line, if any, one lease change deserves.</summary>
    internal void Note(TransmitLeaseEvent change)
    {
        string who = $"sub-channel {change.SubChannel} ({_describe(change.SubChannel)})";
        switch (change.Change)
        {
            case TransmitLeaseChange.Taken:
                lock (_gate)
                {
                    _lastRenewalLine = TimeSpan.Zero;
                }

                _journal.Write(
                    $"tx lease: {who} holds the transmitter until {TxLeaseApi.Utc(change.Expires)}; "
                    + "transmissions from other sub-channels are refused until it ends");
                break;

            case TransmitLeaseChange.Renewed:
                lock (_gate)
                {
                    if (change.HeldFor - _lastRenewalLine < RenewalLineInterval)
                    {
                        return;
                    }

                    _lastRenewalLine = change.HeldFor;
                }

                _journal.Write(
                    $"tx lease: {who} still holds the transmitter after {Span(change.HeldFor)} "
                    + $"({change.Renewals} renewals), now until {TxLeaseApi.Utc(change.Expires)}; "
                    + $"{Refused(change.Refused)} so far");
                break;

            case TransmitLeaseChange.Released:
                _journal.Write(
                    $"tx lease: {who} released the transmitter after {Span(change.HeldFor)}; "
                    + $"{Refused(change.Refused)}; normal service resumes");
                break;

            case TransmitLeaseChange.Expired:
                _journal.Write(
                    $"tx lease: {who} stopped renewing and its lease ran out at "
                    + $"{TxLeaseApi.Utc(change.Expires)}, after {Span(change.HeldFor)}; its unsent "
                    + $"frames were dropped; {Refused(change.Refused)}; normal service resumes");
                break;
        }
    }

    private static string Refused(long count) =>
        count == 1 ? "1 transmission from others refused" : $"{count} transmissions from others refused";

    private static string Span(TimeSpan held) =>
        held.TotalHours >= 1
            ? $"{(int)held.TotalHours}h{held.Minutes:00}m{held.Seconds:00}s"
            : held.TotalMinutes >= 1
                ? $"{(int)held.TotalMinutes}m{held.Seconds:00}s"
                : $"{held.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s";
}

/// <summary>
/// A line said the first time something is held back by the transmit lease, and then at most
/// once a minute with a count of the repeats, so a service that keeps asking during a fifteen
/// minute lease (an ARDOP session replying) costs a handful of lines rather than hundreds.
/// </summary>
internal sealed class LeaseQuietLine(TimeProvider time, Action<int, int> say)
{
    /// <summary>The least time between two lines.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private long _saidAt;
    private bool _said;
    private int _heldBack;

    /// <summary>Notes one thing held back by <paramref name="holder"/>'s lease.</summary>
    internal void Note(int holder)
    {
        int more;
        lock (_gate)
        {
            if (_said && time.GetElapsedTime(_saidAt) < Interval)
            {
                _heldBack++;
                return;
            }

            _said = true;
            _saidAt = time.GetTimestamp();
            more = _heldBack;
            _heldBack = 0;
        }

        say(holder, more);
    }
}
