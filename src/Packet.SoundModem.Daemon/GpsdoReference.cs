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
internal sealed record ReferenceVerdict(ReferenceState State, string Detail, DateTimeOffset Since)
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

    /// <summary>What the page's top bar shows: <c>10 MHz OK</c>, <c>10 MHz unreliable: GPS LOST</c>, <c>10 MHz unknown</c>.</summary>
    internal string Chip => State == ReferenceState.Unreliable ? $"10 MHz unreliable: {Detail}" : $"10 MHz {Spoken(State)}";
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
/// <para><b>A retained state is not a fresh one.</b> The broker hands a new subscriber the last
/// state the display published, however long ago that was. Its text is kept, but only a state
/// published while this station is listening counts as a report, so a display that died without
/// its last will reads unknown rather than whatever it said last.</para>
/// <para><b>Re-evaluated on a clock</b>, not only when a message arrives, because the case that
/// matters most is the one where no message arrives at all.</para>
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

    private string? _status;
    private bool _stateHeard;
    private string? _stateText;
    private bool? _referenceOk;
    private DateTimeOffset? _reportedAt;
    private bool _brokerDown;
    private bool _saidUnreadable;

    private ReferenceState _state = ReferenceState.Unknown;
    private string _detail = "no message yet";
    private DateTimeOffset _since;

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
        _check = time.CreateTimer(_ => Evaluate(), null, CheckEvery, CheckEvery);
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
                (ReferenceState state, string detail, _) = Judge(_time.GetUtcNow());
                return new ReferenceVerdict(state, detail, _since);
            }
        }
    }

    /// <summary>The <c>reference</c> object <c>/api/txlease</c> carries.</summary>
    internal JsonObject Describe()
    {
        ReferenceVerdict now = Current;
        return new JsonObject
        {
            ["state"] = now.Word,
            ["detail"] = now.Detail,
            ["since"] = TxLeaseApi.Utc(now.Since),
        };
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
                if (!retained)
                {
                    _reportedAt = _time.GetUtcNow();
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
    /// any change it shows. Called on every message and once a second.</summary>
    internal void Evaluate()
    {
        ReferenceVerdict changed;
        string? line = null;
        bool problem = false;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            (ReferenceState state, _, string shown) = Judge(now);
            if (state == _state && shown == _detail)
            {
                return;
            }

            if (state != _state)
            {
                line = $"10 MHz reference {ReferenceVerdict.Spoken(state)}: {shown} "
                    + $"(was {ReferenceVerdict.Spoken(_state)} for {Span(now - _since)})";
                problem = state != ReferenceState.Ok;
                _since = now;
            }

            _state = state;
            _detail = shown;
            changed = new ReferenceVerdict(state, shown, _since);
        }

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
    /// in seconds) and <c>Shown</c>, which only changes when something does.
    /// </summary>
    private (ReferenceState State, string Detail, string Shown) Judge(DateTimeOffset now)
    {
        if (_status == "offline")
        {
            return Unknown("display offline");
        }

        if (!_stateHeard)
        {
            return Unknown(_brokerDown ? "MQTT broker unreachable" : _status == "online" ? "no state report yet" : "no message yet");
        }

        if (_reportedAt is not DateTimeOffset reported)
        {
            return Unknown(_brokerDown ? "MQTT broker unreachable" : "waiting for a fresh report");
        }

        TimeSpan age = now - reported;
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
/// a line every 30 s until morning. Retries back off from 1 s to 30 s.</para>
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
        _session = session ?? ((connected, cancel) => MqttSessionAsync(config, reference, connected, cancel));
    }

    /// <summary>Keeps the connection up until <paramref name="cancel"/> fires. Never throws.</summary>
    internal async Task RunAsync(CancellationToken cancel)
    {
        bool down = false;
        bool ever = false;
        TimeSpan retry = TimeSpan.FromSeconds(1);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                string ended = await _session(
                    () =>
                    {
                        _reference.BrokerReachable(true);
                        _journal.Write(down
                            ? $"gpsdo: back on the MQTT broker at {_where}"
                            : ever ? $"gpsdo: reconnected to the MQTT broker at {_where}" : $"gpsdo: connected to the MQTT broker at {_where}");
                        down = false;
                        ever = true;
                        retry = TimeSpan.FromSeconds(1);
                    },
                    cancel).ConfigureAwait(false);
                if (cancel.IsCancellationRequested)
                {
                    break;
                }

                _reference.BrokerReachable(false);
                _journal.WriteError($"gpsdo: lost the MQTT broker at {_where} ({ended}); reconnecting");
                down = true;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failed)
            {
                _reference.BrokerReachable(false);
                if (!down)
                {
                    _journal.WriteError(
                        $"gpsdo: cannot reach the MQTT broker at {_where} ({failed.GetBaseException().Message}); "
                        + "retrying quietly");
                    down = true;
                }
            }

            try
            {
                await Task.Delay(retry, _time, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retry = retry * 2 > MaxRetry ? MaxRetry : retry * 2;
        }
    }

    /// <summary>One MQTT connection: connect, subscribe, pass messages on, and return why it ended.</summary>
    private static async Task<string> MqttSessionAsync(
        GpsdoConfig config, GpsdoReference reference, Action connected, CancellationToken cancel)
    {
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

            await client.SubscribeAsync(subscribe.Build(), cancel).ConfigureAwait(false);
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
