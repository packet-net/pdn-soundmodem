using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Channel;

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

    /// <summary>What a GET or a malformed request is told about the shape of a good one.</summary>
    internal const string Usage =
        "POST {\"subChannel\": 3, \"seconds\": 60} to take or renew the transmit lease for that "
        + "sub-channel (at most 300 s at a time), {\"release\": true, \"subChannel\": 3} to give it back; GET to read it";

    /// <summary>Answers one request.</summary>
    /// <param name="lease">The station's lease.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="body">The request body, possibly empty.</param>
    /// <param name="hasModem">Whether a sub-channel carries a modem that can transmit.</param>
    /// <param name="cannot">Why this station cannot transmit at all, or null.</param>
    internal static (int Status, JsonObject Answer) Handle(
        TransmitLease lease, string method, string body, Func<int, bool> hasModem, string? cannot)
    {
        if (method == "GET")
        {
            return (200, Describe(lease));
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
        int? subChannel;
        double seconds;
        try
        {
            release = asked?["release"]?.GetValue<bool>() == true;
            subChannel = asked?["subChannel"]?.GetValue<int>();
            seconds = asked?["seconds"]?.GetValue<double>() ?? DefaultSeconds;
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            return (400, Error(
                $"\"subChannel\" is a whole number, \"seconds\" a number and \"release\" true or false: {wrongType.Message}"));
        }

        if (release)
        {
            // Checked and released in one step, so a release arriving late cannot free a lease
            // somebody else has taken since. Naming the sub-channel is what makes that so, and
            // what the head end should always do.
            bool released = lease.Release(subChannel);
            if (!released && subChannel is int named && lease.Holder is int holder && holder != named)
            {
                return (409, Conflict(lease, $"sub-channel {holder} holds the transmit lease, not {named}"));
            }

            JsonObject answer = Describe(lease);
            answer["released"] = released;
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

        if (!hasModem(sub))
        {
            return (400, Error($"no modem transmits on sub-channel {sub}"));
        }

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return (400, Error("\"seconds\" must be more than 0"));
        }

        double max = TransmitLease.MaxDuration.TotalSeconds;
        bool capped = seconds > max;
        TransmitLeaseGrant grant = lease.Take(sub, TimeSpan.FromSeconds(Math.Min(seconds, max)));
        if (!grant.Granted)
        {
            return (409, Conflict(lease, $"sub-channel {grant.SubChannel} holds the transmit lease until {Utc(grant.Expires)}"));
        }

        JsonObject granted = Describe(lease);
        granted["renewed"] = grant.Renewed;
        granted["seconds"] = Math.Min(seconds, max);
        granted["capped"] = capped;
        return (200, granted);
    }

    /// <summary>The lease as it stands: who holds it and until when.</summary>
    internal static JsonObject Describe(TransmitLease lease)
    {
        int? holder = lease.Holder;
        DateTimeOffset? expires = lease.Expires;
        return new JsonObject
        {
            ["held"] = holder is not null,
            ["subChannel"] = holder,
            ["expires"] = expires is { } at ? Utc(at) : null,
        };
    }

    private static JsonObject Conflict(TransmitLease lease, string why)
    {
        JsonObject answer = Describe(lease);
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
                    + $"{TxLeaseApi.Utc(change.Expires)}, after {Span(change.HeldFor)}; "
                    + $"{Refused(change.Refused)}; normal service resumes");
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
