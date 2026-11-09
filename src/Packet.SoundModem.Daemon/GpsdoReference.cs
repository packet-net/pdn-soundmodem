using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;

namespace Packet.SoundModem.Daemon;

/// <summary>What the station makes of its 10 MHz reference.</summary>
internal enum ReferenceState
{
    /// <summary>Nothing to go on: no message yet, the display offline, or its last report stale.</summary>
    Unknown,

    /// <summary>The display says the reference can be trusted (<c>reference_ok</c> true).</summary>
    Ok,

    /// <summary>The display says it cannot (<c>reference_ok</c> false).</summary>
    Unreliable,
}

/// <summary>One reading of the reference: the verdict, why, and since when.</summary>
/// <param name="State">The verdict.</param>
/// <param name="Detail">
/// The display's own state text (<c>LOCKED</c>, <c>GPS LOST</c>) for a verdict it gave, or why the
/// verdict is unknown (<c>display offline</c>, <c>last report 47 s old</c>).
/// </param>
/// <param name="Since">When the verdict last changed, or when watching began.</param>
/// <param name="Radio">
/// What a Flex says it is running on, for the chip (<c>Flex on TCXO</c>, <c>Flex ref not
/// locked</c>), or null for a station that is not a Flex.
/// </param>
internal sealed record ReferenceVerdict(ReferenceState State, string Detail, DateTimeOffset Since, string? Radio = null)
{
    /// <summary>The verdict as <c>/api/txlease</c> spells it: <c>ok</c>, <c>unreliable</c> or <c>unknown</c>.</summary>
    internal string Word => Words(State);

    internal static string Words(ReferenceState state) => state switch
    {
        ReferenceState.Ok => "ok",
        ReferenceState.Unreliable => "unreliable",
        _ => "unknown",
    };

    /// <summary>The verdict in a sentence's own case, for the journal and the page.</summary>
    internal static string Spoken(ReferenceState state) => state == ReferenceState.Ok ? "OK" : Words(state);

    /// <summary>What the page's top bar shows: <c>10 MHz OK</c>, <c>10 MHz unreliable: GPS LOST</c>,
    /// <c>10 MHz unknown</c>, with a Flex's part after it: <c>10 MHz OK (Flex on TCXO)</c>.</summary>
    internal string Chip =>
        (State == ReferenceState.Unreliable ? $"10 MHz unreliable: {Detail}" : $"10 MHz {Spoken(State)}")
        + (Radio is null ? "" : $" ({Radio})");
}

/// <summary>
/// What a Flex says about the oscillator it is running on, from its <c>radio oscillator</c>
/// status (<c>state=tcxo setting=tcxo locked=1</c>, <c>state=external setting=external locked=1</c>).
/// </summary>
/// <remarks>
/// A 6000-series Flex takes the GPSDO's 10 MHz only when it found it on REF IN at power-up;
/// otherwise it runs on its own calibrated TCXO, which is fine to transmit on whatever the GPSDO
/// is doing. So the GPSDO's verdict only counts while the Flex says it is on it.
/// </remarks>
/// <param name="Source">
/// The source in use as <c>/api/txlease</c> spells it: <c>tcxo</c>, <c>ocxo</c>, <c>external</c>,
/// <c>gpsdo</c>, or null when the radio has not said or said something not recognised.
/// </param>
/// <param name="Locked">Whether the radio says its reference is locked.</param>
internal sealed record RadioOscillator(string? Source, bool Locked)
{
    /// <summary>What the radio has said, as M0LTE.Flex read it from its status.</summary>
    internal static RadioOscillator FromFlex(M0LTE.Flex.FlexReferenceStatus status) => new(
        status.State switch
        {
            M0LTE.Flex.FlexOscillatorSource.Tcxo => "tcxo",
            M0LTE.Flex.FlexOscillatorSource.Ocxo => "ocxo",
            M0LTE.Flex.FlexOscillatorSource.External => "external",
            M0LTE.Flex.FlexOscillatorSource.Gpsdo => "gpsdo",
            _ => null,
        },
        status.Locked);

    /// <summary>On its own internal oscillator and locked: the GPSDO is not in use.</summary>
    internal bool OnItsOwn => Locked && Source is "tcxo" or "ocxo";

    /// <summary>On the external reference (REF IN, or a built-in GPSDO) and locked: the GPSDO's verdict counts.</summary>
    internal bool OnTheGpsdo => Locked && Source is "external" or "gpsdo";

    /// <summary>The source in the radio's own case: <c>TCXO</c>, <c>external</c>.</summary>
    internal string Named => Source switch
    {
        "tcxo" => "TCXO",
        "ocxo" => "OCXO",
        "gpsdo" => "GPSDO",
        string other => other,
        null => "unknown",
    };

    /// <summary>The Flex's part of the page's chip.</summary>
    internal string Chip =>
        OnItsOwn || OnTheGpsdo ? $"Flex on {Named}"
        : Source is null ? "Flex ref unknown"
        : "Flex ref not locked";

    /// <summary>The one journal line for the Flex moving to this.</summary>
    internal string Line =>
        OnItsOwn ? $"flex: now on its {Named}, the GPSDO is not in use; the reference is judged OK"
        : OnTheGpsdo ? $"flex: now on its {(Source == "gpsdo" ? "GPSDO" : "external 10 MHz reference")}; the GPSDO's verdict counts"
        : Source is null ? "flex: its oscillator state is unknown; the reference is judged unknown unless the GPSDO says unreliable"
        : $"flex: its {Named} reference is not locked; the reference is judged unknown unless the GPSDO says unreliable";
}

/// <summary>
/// The GPSDO's 10 MHz reference state, from the topics a display watching it publishes (issue
/// #789): <c>&lt;prefix&gt;/status</c> (<c>online</c>, or <c>offline</c> as its MQTT last will) and
/// <c>&lt;prefix&gt;/state</c> (JSON, at least every 10 s).
/// </summary>
/// <remarks>
/// <para><b>Unknown is not good.</b> The verdict is unknown until there is something to go on,
/// while the display is offline, and once its last report is more than <see cref="StaleAfter"/>
/// old. Otherwise it is the display's own <c>reference_ok</c>, which is the one field the spec says
/// to act on; an absent one is unknown, never false. The state text is shown, not parsed.</para>
/// <para><b>A retained state counts only beside an online status.</b> The broker hands a new
/// subscriber the last state the display published, however long ago. While the retained status
/// is <c>online</c> that state is taken as fresh at receipt (a display that dies has its last will,
/// <c>offline</c>, published within about 1.5 keepalives), so a restart just before a mailcast slot
/// is not ten seconds of unknown. With the status absent it waits for one, or for a live report.</para>
/// <para><b>Ages are monotonic.</b> A report's age and how long a verdict lasted are measured on
/// <see cref="TimeProvider.GetTimestamp"/>; only <c>since</c> is wall time.</para>
/// <para><b>Re-evaluated on a clock</b>, not only when a message arrives, because the case that
/// matters most is the one where no message arrives at all.</para>
/// <para><b>A Flex has the last word on whether the GPSDO matters.</b> Once a Flex reports its
/// oscillator (<see cref="FlexOscillator"/>), the verdict follows what it is running on: its own
/// TCXO, locked, is OK whatever the GPSDO says; the external reference, locked, is the GPSDO's
/// verdict; not locked or not said is unknown, or unreliable if the GPSDO says so. A station that
/// is not a Flex never calls it, and its verdict is the GPSDO's alone.</para>
/// <para>The MQTT side is <see cref="GpsdoFeed"/>; this class has no network, so a test drives it
/// with topics and payloads and a fake clock.</para>
/// </remarks>
internal sealed class GpsdoReference : IDisposable
{
    /// <summary>How old the last report may be before the verdict is unknown. The display
    /// republishes every 10 s, so this is three missed reports.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>How often staleness is checked between messages.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);

    private readonly string _statusTopic;
    private readonly string _stateTopic;
    private readonly StationJournal _journal;
    private readonly TimeProvider _time;
    private readonly ITimer _check;
    private readonly Lock _gate = new();

    /// <summary>
    /// Held across working a verdict out and publishing it, so two evaluations (the timer and a
    /// message) cannot publish out of order and leave the page showing the older one.
    /// </summary>
    private readonly Lock _publish = new();

    private string? _status;
    private bool _stateHeard;
    private string? _stateText;
    private bool? _referenceOk;
    /// <summary>When the last report that counts arrived, on the monotonic clock.</summary>
    private long? _reportedAt;

    /// <summary>When a retained state arrived that could not count yet (no status heard).</summary>
    private long? _retainedAt;
    private bool _brokerDown;
    private bool _saidUnreadable;
    private bool _saidSubscriberFault;

    /// <summary>What the Flex last said it runs on; null for a station that is not a Flex.</summary>
    private RadioOscillator? _flex;
    private bool _saidFlexFault;

    private ReferenceState _state = ReferenceState.Unknown;
    private string _detail = "no message yet";
    private string? _radio;
    private DateTimeOffset _since;
    private long _sinceStamp;

    /// <param name="topicPrefix">What the display's topics start with, without a trailing slash.</param>
    /// <param name="journal">Where the one line per change goes.</param>
    /// <param name="time">The clock (FakeTimeProvider under test).</param>
    internal GpsdoReference(string topicPrefix, StationJournal journal, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(topicPrefix);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(time);
        _statusTopic = $"{topicPrefix}/status";
        _stateTopic = $"{topicPrefix}/state";
        _journal = journal;
        _time = time;
        _since = time.GetUtcNow();
        _sinceStamp = time.GetTimestamp();
        _check = time.CreateTimer(_ => Tick(), null, CheckEvery, CheckEvery);
    }

    /// <summary>The two topics to subscribe to.</summary>
    internal IReadOnlyList<string> Topics => [_statusTopic, _stateTopic];

    /// <summary>
    /// Raised when the verdict or its shown reason changes, from whichever thread noticed, outside
    /// the lock. Not raised for the age of a stale report ticking up.
    /// </summary>
    internal event Action<ReferenceVerdict>? Changed;

    /// <summary>The verdict now, with a stale report's age as of this moment.</summary>
    internal ReferenceVerdict Current
    {
        get
        {
            lock (_gate)
            {
                (ReferenceState state, string detail, _) = Judge();
                return new ReferenceVerdict(state, detail, _since, _flex?.Chip);
            }
        }
    }

    /// <summary>The <c>reference</c> object <c>/api/txlease</c> carries, with <c>oscillator</c>
    /// on a Flex only.</summary>
    internal JsonObject Describe()
    {
        ReferenceVerdict now;
        RadioOscillator? flex;
        lock (_gate)
        {
            (ReferenceState state, string detail, _) = Judge();
            now = new ReferenceVerdict(state, detail, _since);
            flex = _flex;
        }

        var described = new JsonObject
        {
            ["state"] = now.Word,
            ["detail"] = now.Detail,
            ["since"] = TxLeaseApi.Utc(now.Since),
        };
        if (flex is not null)
        {
            described["oscillator"] = flex.Source;
        }

        return described;
    }

    /// <summary>
    /// What the Flex says it is running on, from its live status: at bring-up and on every change
    /// M0LTE.Flex reports. One journal line when it moves (not for the first report, which the
    /// bring-up's own <c>flex: reference</c> line already gave), then the verdict is worked out
    /// again. Never throws: it runs on the radio's read loop.
    /// </summary>
    internal void FlexOscillator(RadioOscillator oscillator)
    {
        ArgumentNullException.ThrowIfNull(oscillator);
        lock (_publish)
        {
            string? line = null;
            bool problem = false;
            lock (_gate)
            {
                if (oscillator == _flex)
                {
                    return;
                }

                if (_flex is not null)
                {
                    line = oscillator.Line;
                    problem = !oscillator.OnItsOwn && !oscillator.OnTheGpsdo;
                }

                _flex = oscillator;
            }

            if (line is not null)
            {
                try
                {
                    if (problem)
                    {
                        _journal.WriteError(line);
                    }
                    else
                    {
                        _journal.Write(line);
                    }
                }
                catch (Exception fault)
                {
                    bool say;
                    lock (_gate)
                    {
                        say = !_saidFlexFault;
                        _saidFlexFault = true;
                    }

                    if (say)
                    {
                        Console.Error.WriteLine($"gpsdo: journalling the Flex's oscillator failed: {fault.GetBaseException().Message}");
                    }
                }
            }

            PublishGuarded();
        }
    }

    /// <summary>One message from the broker. Topics other than the two are ignored.</summary>
    /// <param name="topic">The topic it arrived on.</param>
    /// <param name="payload">Its payload, as text.</param>
    /// <param name="retained">Whether the broker sent a stored copy rather than a fresh publish.</param>
    internal void Receive(string topic, string payload, bool retained)
    {
        if (topic == _statusTopic)
        {
            string status = payload.Trim();
            if (status is "online" or "offline")
            {
                lock (_gate)
                {
                    _status = status;

                    // A retained state that came before the status counts from when it came,
                    // now the display is known to be online.
                    if (status == "online" && _reportedAt is null && _retainedAt is long held)
                    {
                        _reportedAt = held;
                    }

                    _retainedAt = null;
                }
            }
        }
        else if (topic == _stateTopic)
        {
            if (!TryRead(payload, out string? text, out bool? referenceOk))
            {
                bool say;
                lock (_gate)
                {
                    say = !_saidUnreadable;
                    _saidUnreadable = true;
                }

                if (say)
                {
                    // Once until a readable one arrives: a display sending nonsense every 10 s
                    // would otherwise be a line every 10 s. Staleness turns the verdict unknown.
                    _journal.WriteError(
                        $"gpsdo: ignoring a {_stateTopic} message that is not a JSON object: {TxTestRunner.Printable(payload)}");
                }

                return;
            }

            lock (_gate)
            {
                _saidUnreadable = false;
                _stateHeard = true;
                _stateText = text;
                _referenceOk = referenceOk;
                // A retained state is fresh at receipt only while the display's status is online:
                // a display that dies has its last will published as "offline" within about 1.5
                // keepalives, so an "online" status vouches for the state beside it. Without one
                // it waits (for the status, or for a live report).
                long arrived = _time.GetTimestamp();
                if (!retained || _status == "online")
                {
                    _reportedAt = arrived;
                    _retainedAt = null;
                }
                else if (_status is null)
                {
                    _retainedAt = arrived;
                }
            }
        }
        else
        {
            return;
        }

        Evaluate();
    }

    /// <summary>Whether the broker is reachable, from <see cref="GpsdoFeed"/>, for the reason an
    /// unknown verdict gives.</summary>
    internal void BrokerReachable(bool reachable)
    {
        lock (_gate)
        {
            _brokerDown = !reachable;
        }

        Evaluate();
    }

    /// <summary>Works the verdict out again, journals a change of verdict and tells the page about
    /// any change it shows. Called on every message and once a second. Never throws: it runs on
    /// the timer and on the MQTT client's thread, and neither may be taken down by a subscriber.</summary>
    internal void Evaluate()
    {
        lock (_publish)
        {
            PublishGuarded();
        }
    }

    /// <summary>
    /// The timer's evaluation: skipped, returning false, when another evaluation holds the
    /// publish lock, so ticks can never pile up behind a journal write that is stuck. The next
    /// tick, a second later, tries again. Messages still wait their turn (<see cref="Evaluate"/>).
    /// </summary>
    internal bool Tick()
    {
        if (!_publish.TryEnter())
        {
            return false;
        }

        try
        {
            PublishGuarded();
            return true;
        }
        finally
        {
            _publish.Exit();
        }
    }

    private void PublishGuarded()
    {
        try
        {
            Publish();
        }
        catch (Exception fault)
        {
            bool say;
            lock (_gate)
            {
                say = !_saidSubscriberFault;
                _saidSubscriberFault = true;
            }

            if (say)
            {
                _journal.WriteError($"gpsdo: showing the reference failed: {fault.GetBaseException().Message}");
            }
        }
    }

    private void Publish()
    {
        ReferenceVerdict changed;
        string? line = null;
        bool problem = false;
        lock (_gate)
        {
            (ReferenceState state, _, string shown) = Judge();
            string? radio = _flex?.Chip;
            if (state == _state && shown == _detail && radio == _radio)
            {
                return;
            }

            if (state != _state)
            {
                line = $"10 MHz reference {ReferenceVerdict.Spoken(state)}: {shown} "
                    + $"(was {ReferenceVerdict.Spoken(_state)} for {Span(_time.GetElapsedTime(_sinceStamp))})";
                problem = state != ReferenceState.Ok;
                _since = _time.GetUtcNow();
                _sinceStamp = _time.GetTimestamp();
            }

            _state = state;
            _detail = shown;
            _radio = radio;
            changed = new ReferenceVerdict(state, shown, _since, radio);
        }

        // Outside _gate (a subscriber may read Current) but inside _publish, so lines and page
        // updates go out in the order the verdicts were reached.
        if (line is not null)
        {
            if (problem)
            {
                _journal.WriteError(line);
            }
            else
            {
                _journal.Write(line);
            }
        }

        Changed?.Invoke(changed);
    }

    /// <summary>
    /// The verdict, with two wordings of its reason: <c>Detail</c> as of now (a stale report's age
    /// in seconds) and <c>Shown</c>, which only changes when something does. The GPSDO's, then
    /// what a Flex makes of it.
    /// </summary>
    private (ReferenceState State, string Detail, string Shown) Judge() => Combine(JudgeGpsdo(), _flex);

    /// <summary>
    /// The GPSDO's verdict as the Flex's oscillator makes it count. No Flex: the GPSDO's alone.
    /// The Flex on its own TCXO (or OCXO), locked: OK, whatever the GPSDO says. On the external
    /// reference, locked: the GPSDO's. Not locked, or not said: unknown, unless the GPSDO says
    /// unreliable, which stands.
    /// </summary>
    internal static (ReferenceState State, string Detail, string Shown) Combine(
        (ReferenceState State, string Detail, string Shown) gpsdo, RadioOscillator? flex)
    {
        if (flex is null || flex.OnTheGpsdo)
        {
            return gpsdo;
        }

        if (flex.OnItsOwn)
        {
            string own = $"Flex on its own {flex.Named} (GPSDO not in use)";
            return (ReferenceState.Ok, own, own);
        }

        if (gpsdo.State == ReferenceState.Unreliable)
        {
            return gpsdo;
        }

        string why = flex.Source is null
            ? "Flex has not said which oscillator it is on"
            : $"Flex reports its {flex.Named} reference not locked";
        return (ReferenceState.Unknown, why, why);
    }

    /// <summary>The GPSDO display's verdict on its own.</summary>
    private (ReferenceState State, string Detail, string Shown) JudgeGpsdo()
    {
        if (_status == "offline")
        {
            return Unknown("display offline");
        }

        if (!_stateHeard)
        {
            return Unknown(_brokerDown ? "MQTT broker unreachable" : _status == "online" ? "no state report yet" : "no message yet");
        }

        if (_reportedAt is not long reported)
        {
            return Unknown(_brokerDown ? "MQTT broker unreachable" : "waiting for a fresh report");
        }

        TimeSpan age = _time.GetElapsedTime(reported);
        if (age > StaleAfter)
        {
            string old = $"last report {Math.Floor(age.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} s old";
            return _brokerDown
                ? (ReferenceState.Unknown, $"MQTT broker unreachable, {old}", "MQTT broker unreachable")
                : (ReferenceState.Unknown, old, $"no report for over {StaleAfter.TotalSeconds:0} s");
        }

        string text = _stateText ?? "no state text";
        return _referenceOk switch
        {
            true => (ReferenceState.Ok, text, text),
            false => (ReferenceState.Unreliable, text, text),
            null => Unknown($"{text}, reference_ok not reported"),
        };

        static (ReferenceState, string, string) Unknown(string why) => (ReferenceState.Unknown, why, why);
    }

    /// <summary>
    /// Reads a state payload's two fields of interest. False for anything that is not a JSON
    /// object. A field that is absent, or not of its type, is null: absent means unknown.
    /// </summary>
    internal static bool TryRead(string payload, out string? stateText, out bool? referenceOk)
    {
        stateText = null;
        referenceOk = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String)
            {
                string printable = TxTestRunner.Printable(state.GetString());
                stateText = printable.Length > 0 ? printable : null;
            }

            if (document.RootElement.TryGetProperty("reference_ok", out JsonElement ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                referenceOk = ok.GetBoolean();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A length of time the way the journal says it: <c>3h12m</c>, <c>5m03s</c>, <c>12s</c>.</summary>
    internal static string Span(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
                : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    public void Dispose() => _check.Dispose();
}

/// <summary>
/// The MQTT connection behind <see cref="GpsdoReference"/>: connects, subscribes to the display's
/// two topics, passes every message on, and reconnects by itself after an outage.
/// </summary>
/// <remarks>
/// <para><b>Quiet about outages.</b> One line when the broker goes or cannot be reached, one when
/// it is back, and nothing for the retries between: a broker down overnight would otherwise write
/// a line every 30 s until morning. Retries back off from 1 s to 30 s. The outage is over only
/// once a connection has lasted <see cref="SteadyAfter"/>: a broker that accepts and drops at once
/// is still down, so it costs no lines and keeps the backoff.</para>
/// <para><b>MQTTnet</b>, which the solution already pins for the NinoCompare tool, so no new
/// package. The session is a delegate so the reconnect loop can be tested without a network.</para>
/// </remarks>
internal sealed class GpsdoFeed
{
    /// <summary>The longest wait between two connection attempts.</summary>
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly GpsdoReference _reference;
    private readonly StationJournal _journal;
    private readonly TimeProvider _time;
    private readonly string _where;
    private readonly Func<Action, CancellationToken, Task<string>> _session;

    /// <param name="config">The broker and the topic prefix.</param>
    /// <param name="reference">Where messages and reachability go.</param>
    /// <param name="journal">Where the outage lines go.</param>
    /// <param name="time">The clock the retry waits are measured on.</param>
    /// <param name="session">
    /// One connection, for tests: connects (throwing if it cannot), calls the action once it is
    /// subscribed, and returns why it ended once it does. Null is the real MQTT one.
    /// </param>
    internal GpsdoFeed(
        GpsdoConfig config, GpsdoReference reference, StationJournal journal, TimeProvider time,
        Func<Action, CancellationToken, Task<string>>? session = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _reference = reference;
        _journal = journal;
        _time = time;
        _where = $"{config.Host}:{config.Port}";
        _session = session ?? ((connected, cancel) => MqttSessionAsync(config, connected, cancel));
    }

    /// <summary>How long a connection must last before an outage is over: until then a broker
    /// that accepts and drops at once is still an outage, backed off and kept quiet.</summary>
    internal static readonly TimeSpan SteadyAfter = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private bool _down;
    private bool _ever;
    private bool _saidRefused;
    private TimeSpan _retry = TimeSpan.FromSeconds(1);

    /// <summary>Keeps the connection up until <paramref name="cancel"/> fires. Never throws.</summary>
    internal async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            using var steady = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                string ended = await _session(
                    () =>
                    {
                        _reference.BrokerReachable(true);
                        bool first;
                        lock (_gate)
                        {
                            first = !_ever && !_down;
                            _ever = true;
                        }

                        if (first)
                        {
                            _journal.Write($"gpsdo: connected to the MQTT broker at {_where}");
                        }

                        _ = SteadyAsync(steady.Token);
                    },
                    cancel).ConfigureAwait(false);
                await steady.CancelAsync().ConfigureAwait(false);
                if (cancel.IsCancellationRequested)
                {
                    break;
                }

                _reference.BrokerReachable(false);
                if (Down())
                {
                    _journal.WriteError($"gpsdo: lost the MQTT broker at {_where} ({ended}); reconnecting");
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failed)
            {
                await steady.CancelAsync().ConfigureAwait(false);
                _reference.BrokerReachable(false);
                if (Down())
                {
                    _journal.WriteError(
                        $"gpsdo: cannot reach the MQTT broker at {_where} ({failed.GetBaseException().Message}); "
                        + "retrying quietly");
                }
            }

            TimeSpan wait;
            lock (_gate)
            {
                wait = _retry;
                _retry = _retry * 2 > MaxRetry ? MaxRetry : _retry * 2;
            }

            try
            {
                await Task.Delay(wait, _time, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Marks an outage, and says whether it is a new one worth a line.</summary>
    private bool Down()
    {
        lock (_gate)
        {
            bool fresh = !_down;
            _down = true;
            return fresh;
        }
    }

    /// <summary>Once a connection has lasted <see cref="SteadyAfter"/>, the outage is over: the
    /// backoff starts again from 1 s, and the next loss is a new line.</summary>
    private async Task SteadyAsync(CancellationToken connection)
    {
        try
        {
            await Task.Delay(SteadyAfter, _time, connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool back;
        lock (_gate)
        {
            back = _down;
            _down = false;
            _retry = TimeSpan.FromSeconds(1);
        }

        if (back)
        {
            _journal.Write($"gpsdo: back on the MQTT broker at {_where}");
        }
    }

    /// <summary>Says once, for the life of the feed, that the broker would not let it subscribe.</summary>
    private void SubscriptionRefused(string what)
    {
        lock (_gate)
        {
            if (_saidRefused)
            {
                return;
            }

            _saidRefused = true;
        }

        _journal.WriteError($"gpsdo: the MQTT broker at {_where} refused the subscription: {what}; check the broker's access rules");
    }

    /// <summary>One MQTT connection: connect, subscribe, pass messages on, and return why it ended.</summary>
    private async Task<string> MqttSessionAsync(GpsdoConfig config, Action connected, CancellationToken cancel)
    {
        GpsdoReference reference = _reference;
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        var gone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += e =>
        {
            gone.TrySetResult(e.Exception?.GetBaseException().Message ?? e.Reason.ToString());
            return Task.CompletedTask;
        };
        client.ApplicationMessageReceivedAsync += e =>
        {
            reference.Receive(
                e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? "", e.ApplicationMessage.Retain);
            return Task.CompletedTask;
        };

        MqttClientOptionsBuilder options = new MqttClientOptionsBuilder()
            .WithTcpServer(config.Host, config.Port)
            .WithClientId($"pdn-soundmodem-{Guid.NewGuid().ToString("N")[..8]}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrEmpty(config.Username))
        {
            options = options.WithCredentials(config.Username, config.Password ?? "");
        }

        await client.ConnectAsync(options.Build(), cancel).ConfigureAwait(false);
        try
        {
            MqttClientSubscribeOptionsBuilder subscribe = factory.CreateSubscribeOptionsBuilder();
            foreach (string topic in reference.Topics)
            {
                subscribe = subscribe.WithTopicFilter(f => f.WithTopic(topic));
            }

            MqttClientSubscribeResult subscribed = await client.SubscribeAsync(subscribe.Build(), cancel).ConfigureAwait(false);
            string refused = string.Join(", ", subscribed.Items
                .Where(i => i.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                    or MqttClientSubscribeResultCode.GrantedQoS1 or MqttClientSubscribeResultCode.GrantedQoS2))
                .Select(i => $"{i.TopicFilter.Topic} ({i.ResultCode})"));
            if (refused.Length > 0)
            {
                // Connected but deaf: the verdict stays unknown on its own, and this says why.
                SubscriptionRefused(refused);
            }

            connected();
            using (cancel.Register(() => gone.TrySetCanceled(cancel)))
            {
                return await gone.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down or already gone; either way there is nothing to tell anyone.
                }
            }
        }
    }
}
