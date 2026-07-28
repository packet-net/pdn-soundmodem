using System.Globalization;
using M0LTE.Flex;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Bring-up and safety parameters shared by both transmit routes —
/// <see cref="FlexIqTransmitter"/> (waveform IQ, the bench/instrument path) and
/// <see cref="FlexDaxTransmitter"/> (DAX audio, the deployment path). Everything from
/// <see cref="RfPower"/> down is policy and applies to either; the frequency/bandwidth
/// knobs say which route they belong to.
/// </summary>
public sealed record FlexTransmitterOptions
{
    /// <summary>Radio address: an IP/hostname, or <c>discover</c> for the UDP :4992
    /// broadcast, or <c>mock</c> handled by the caller.</summary>
    public string Radio { get; init; } = "discover";

    /// <summary>
    /// The RF frequency where the transmitted baseband's 0 Hz lands, MHz in the six-decimal
    /// Flex form. A component at +f Hz in the samples (a tone, the modem's suppressed
    /// carrier offset) is transmitted at this frequency + f.
    /// </summary>
    /// <remarks>On the DAX route this is simply the slice dial (DIGU: audio lands above it).
    /// On the waveform-IQ route it is the lower edge of the <see cref="IqBand"/> the library
    /// places: the slice is <em>derived</em> at this + <see cref="OccupiedBandwidthHz"/>,
    /// because the waveform path transmits only the negative half of its baseband — see
    /// <see cref="FlexIqTransmitter"/>. Any LO/carrier leakage lands at the derived slice,
    /// i.e. <see cref="OccupiedBandwidthHz"/> above here — outside the occupied band.</remarks>
    public string FrequencyMHz { get; init; } = "18.098000";

    /// <summary>Antenna port. The dummy load is on ANT1.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>TX RF power, 0–100. <b>Required</b> — there is deliberately no default, so a
    /// power level is always a decision rather than an accident.</summary>
    public required int RfPower { get; init; }

    /// <summary>
    /// Waveform-IQ route only: the width of the band declared to the library's
    /// <see cref="IqBand"/> placement, in Hz above <see cref="FrequencyMHz"/>. Everything the
    /// session transmits must sit inside 0…this in the samples' own baseband; content outside
    /// it does not reach the air (the radio's global transmit filter is sized to exactly this
    /// width, and the placement shift puts only this span in the transmitted half).
    /// </summary>
    /// <remarks>The radio clamps its transmit filter at 10 kHz, so this cannot exceed 10000 —
    /// the waveform path is single-sideband and ≤10 kHz one-sided; ±12 kHz two-sided transmit
    /// does not exist on this hardware. The default clears the MS110D band at its +2000 Hz
    /// offset (2150–5450 Hz) plus the test tones, with margin.</remarks>
    public int OccupiedBandwidthHz { get; init; } = 6000;

    /// <summary>
    /// DAX route only: the high cut applied to the radio's global transmit filter, Hz.
    /// </summary>
    /// <remarks>The factory value is a 3 kHz SSB passband, which would truncate the top of the
    /// MS110D occupied band (skirts to ≈3450 Hz on the 1800 Hz sub-carrier) — and whatever a
    /// previous waveform session left behind would otherwise apply silently, since the filter
    /// is global and persistent. So the DAX route always states it. The default clears the
    /// full MS110D band and nothing more.</remarks>
    public int DaxTransmitFilterHighHz { get; init; } = 3450;

    /// <summary>Abort threshold for SWR. 1.5:1 is a cautious sanity check into a 1 kW dummy
    /// load fed by a 100 W radio — it is there to catch a disconnected or wrong load, not to
    /// protect a PA that is in no danger.</summary>
    public double MaxSwr { get; init; } = 1.5;

    /// <summary>Refuse to transmit anything longer than this.</summary>
    public double MaxBurstSeconds { get; init; } = 60;

    /// <summary>
    /// Highest <see cref="RfPower"/> accepted without an explicit override.
    /// </summary>
    /// <remarks>The binding limit is the <b>receiver</b>, not the transmitter. The dummy load
    /// is a kilowatt part fed by a 100 W radio, so the PA is in no danger — but the UberSDR's
    /// active loop is metres away, its IQ channel has no per-user gain, and a capture peaking
    /// at −11.4 dBFS at 10 W reaches full scale somewhere near 100 W. The reason that matters
    /// is <em>measurement validity</em>, not courtesy: a clipped front end generates its own
    /// intermodulation, which would be indistinguishable from the transmitter's, and compresses
    /// exactly the levels a linearity sweep exists to measure. 30 W keeps ~6 dB of headroom.
    /// Raising this is reasonable once the receiver's front-end gain has been reduced to match
    /// — the ceiling is there so the two get changed together rather than one silently
    /// invalidating the other.</remarks>
    public int RfPowerCeiling { get; init; } = 30;

    /// <summary>
    /// Hard forward-power ceiling in <b>watts</b> — the interlock aborts any transmission the
    /// instant measured FWDPWR exceeds it — or null for no measured-power limit.
    /// </summary>
    /// <remarks>Distinct from <see cref="RfPowerCeiling"/>, which caps the commanded 0–100
    /// <c>rfpower</c> level: that level is not watts and its mapping to power is the radio's, so a
    /// strict power limit has to be held against what the radio actually measures. Enforced on
    /// every FWDPWR sample regardless of envelope (a data burst counts too). Set for the attenuated
    /// RSP1 rig, whose off-air chain has a strict 5 W ceiling; null elsewhere (the dummy load needs
    /// no such limit).</remarks>
    public double? MaxForwardWatts { get; init; }

    /// <summary>Silence written before the tone/burst to absorb the PTT→TRANSMITTING settle
    /// (measured at 139 ms on M0LTE's 6500).</summary>
    public double LeadInSeconds { get; init; } = 0.3;

    /// <summary>Silence written after the burst, before draining and unkeying.</summary>
    public double LeadOutSeconds { get; init; } = 0.1;

    /// <summary>
    /// IQ ring depth, in seconds. Sized so an entire burst pre-fills before keying.
    /// </summary>
    /// <remarks>Anything longer than the ring has to be streamed while the radio drains it,
    /// and a momentary scheduling delay then empties the ring and starves — which is a phase
    /// discontinuity on the air. A 30 wpm identification is already ~6 s, so 4 s was not
    /// enough; 12 s costs ~2 MB and removes the failure mode for every burst we send.</remarks>
    public double BufferSeconds { get; init; } = 12.0;

    /// <summary>
    /// Station callsign for Morse identification. Null reads it from the radio
    /// (<c>radio.callsign</c>), which is why identification is hard to forget: it happens
    /// unless <see cref="Identify"/> is explicitly turned off.
    /// </summary>
    public string? Callsign { get; init; }

    /// <summary>Mode name sent after the callsign, so a listener knows what the unfamiliar
    /// signal they just heard was.</summary>
    public string? IdMode { get; init; } = "MS110D";

    /// <summary>Send a Morse identification at session start and at
    /// <see cref="IdentifyInterval"/> thereafter. On by default.</summary>
    public bool Identify { get; init; } = true;

    /// <summary>
    /// Longest gap between identifications. Ten minutes: often enough to satisfy the licence
    /// condition comfortably, rare enough that the airtime cost (~3 s at 30 wpm) is nothing.
    /// </summary>
    public TimeSpan IdentifyInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Minimum gap between the end of one transmission and the key-up of the next.
    /// </summary>
    /// <remarks>Empirical, and load-bearing: keying too soon after a long transmission is
    /// silently ignored by the radio. 1 s was observed to work between short bursts and 0.5 s
    /// to fail after a 6 s one, so 2 s buys margin at negligible cost.</remarks>
    public TimeSpan InterBurstSettle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Morse speed, words per minute.</summary>
    public double IdWpm { get; init; } = 30;

    /// <summary>Carrier offset for the identification, relative to the waveform centre.</summary>
    public double IdToneHz { get; init; } = 1000;

    /// <summary>Fail bring-up if meter telemetry cannot be subscribed. <b>True by default and
    /// should stay true against a real radio</b> — without meters there is no SWR interlock,
    /// and without the interlock there is nothing at all watching a deaf transmitter. Offline
    /// tests against the mock (which has no meter surface) set it false.</summary>
    public bool RequireMeters { get; init; } = true;

    /// <summary>
    /// Clock for every interval this class enforces — the identification period, the inter-burst
    /// settle, the post-transmit observation.
    /// </summary>
    /// <remarks>Injected so those policies can be verified by advancing a clock instead of
    /// waiting on one: proving the ten-minute identification rule with the system clock costs
    /// ten minutes of a test run, and a ladder pass paced in real time cannot be rehearsed at
    /// all. House rule (CLAUDE.md): wall-clock via <see cref="TimeProvider"/> only.</remarks>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The power and bandwidth gates, applied identically by both transmit routes before
    /// anything touches the radio.
    /// </summary>
    public void Validate()
    {
        if (RfPower is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(RfPower), RfPower, "RF power must be 0–100");
        }

        if (RfPower > RfPowerCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RfPower), RfPower,
                $"RF power {RfPower} exceeds the {RfPowerCeiling} ceiling. The limit is the " +
                "RECEIVER, not the transmitter: the loop is metres from the dummy load and its ADC clips " +
                "near 100 W, at which point the front end makes its own intermodulation and compresses the " +
                "very levels a sweep measures. Raise RfPowerCeiling once RX gain has been reduced to suit, " +
                "checking captured peak dBFS.");
        }

        if (OccupiedBandwidthHz is <= 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OccupiedBandwidthHz), OccupiedBandwidthHz,
                "the radio's transmit filter clamps at 10 kHz, so the declared band must be 1–10000 Hz");
        }
    }
}

/// <summary>What one keyed transmission did, on either transmit route.</summary>
/// <param name="KeyUtc">When <c>xmit 1</c> was issued.</param>
/// <param name="UnkeyUtc">When <c>xmit 0</c> was issued.</param>
/// <param name="Samples">Samples handed to the radio (complex on the waveform route, mono
/// audio on the DAX route).</param>
/// <param name="PacketsReflected">Waveform TX buffers reflected during the burst. The DAX
/// route is push rather than reflection-driven, so there it counts DAX packets sent.</param>
/// <param name="SamplesStarved">Complex samples the radio pulled that the ring could not
/// supply. <b>Must be zero</b> — a non-zero count is a phase discontinuity on the air and
/// invalidates the spectral measurement. Always zero on the DAX route, where nothing
/// pulls.</param>
/// <param name="Drained">Whether the ring drained before the unkey.</param>
/// <param name="Meters">Every meter sample seen while keyed.</param>
/// <param name="Aborted">Set when the safety interlock cut the transmission short.</param>
/// <param name="AbortReason">Why, when <paramref name="Aborted"/>.</param>
public sealed record TransmitReport(
    DateTime KeyUtc,
    DateTime UnkeyUtc,
    int Samples,
    long PacketsReflected,
    long SamplesStarved,
    bool Drained,
    IReadOnlyList<FlexMeterReading> Meters,
    bool Aborted,
    string? AbortReason)
{
    /// <summary>Keyed duration.</summary>
    public TimeSpan Duration => UnkeyUtc - KeyUtc;

    /// <summary>Highest SWR seen while keyed, from whichever source was trustworthy.</summary>
    public double? PeakSwr { get; init; }

    /// <summary>Highest forward power seen while keyed, in dBm.</summary>
    public double? PeakForwardDbm { get; init; }

    /// <summary>Radio fault/warning messages (the <c>M…</c> channel) received while keyed.</summary>
    public IReadOnlyList<string> Faults { get; init; } = [];
}

/// <summary>
/// Complex IQ transmit through a FlexRadio 6000-series waveform, with transmitter
/// health metering and a safety interlock — the bench/instrument route.
/// </summary>
/// <remarks>
/// <para>The transmit half of the MS110D OTA chain (ota-execution-plan §T2/§T4), on
/// <c>M0LTE.Flex</c>'s <see cref="FlexWaveform"/> headless bring-up with
/// <c>underlying_mode=RAW</c>.</para>
/// <para><b>The waveform path is single-sideband</b> (measured on M0LTE's 6500, fw 4.1.5,
/// 2026-07-26 — this retracts the earlier "true wideband both-sidebands" claim, which came
/// from a symmetric comb that could not distinguish the cases): only the <b>negative</b>
/// half of the baseband ever reaches the air, in every <c>underlying_mode</c>, and the
/// occupied width is capped by the radio's <b>global</b> transmit filter — 3 kHz factory,
/// 10 kHz hard clamp, <c>filter_low</c> cannot go negative. Usable width is therefore
/// ≤10 kHz one-sided; ±12 kHz two-sided transmit does not exist on this hardware. Band
/// placement is owned by the library: this class declares an
/// <see cref="IqBand"/> (<see cref="FlexTransmitterOptions.FrequencyMHz"/> +
/// <see cref="FlexTransmitterOptions.OccupiedBandwidthHz"/>,
/// <see cref="IqBandReference.LowerEdge"/> — our generators emit one-sided analytic
/// baseband), and the library derives the slice, translates the samples below DC, opens the
/// filter, and throws rather than transmitting truncated.</para>
/// <para>This route bypasses the radio's SSB modulator, ALC and TX DSP entirely, which is
/// why it is the reference/instrument leg (§E1–§E2). The modem's own deployment path is DAX
/// audio — see <see cref="FlexDaxTransmitter"/> and the §E3 A/B.</para>
/// <para>The counters (<c>PacketsReflected</c>, <c>SamplesStarved</c>) are reported on every
/// burst and a starve is treated as a failed measurement, not a warning.</para>
/// </remarks>
public sealed class FlexIqTransmitter : IOtaTransmitter
{
    /// <summary>The waveform stream's complex sample rate (24 kHz on the 6000 series).</summary>
    public const int SampleRate = FlexWaveformIqOutput.SampleRate;

    /// <summary>
    /// Complex samples the radio pulls per waveform TX buffer — 128, from the measured
    /// cadence of 187.5 packets/s at 24 kHz on M0LTE's 6500 (docs/flex-integration.md §9.2).
    /// </summary>
    /// <remarks>Bursts are padded up to a whole number of these. Otherwise the final buffer
    /// is a partial one, the ring zero-pads the shortfall, and <c>SamplesStarved</c> comes
    /// back non-zero on a perfectly healthy transmission — which would blunt the one clean
    /// signal we have that the reflection loop kept up.</remarks>
    public const int PacketSamples = 128;

    private readonly FlexTransmitterOptions _options;
    private readonly FlexClient _client;
    private readonly FlexWaveform _waveform;
    private readonly FlexWaveformIqOutput _iq;
    private readonly FlexPtt _ptt;
    private readonly FlexMeters _meters;
    private readonly Action<string> _log;
    private readonly List<string> _faults = [];
    private readonly Lock _faultGate = new();

    private readonly bool _ownsClient;

    /// <summary>Now, on the injected clock — never the system one directly.</summary>
    private DateTime UtcNow => _options.Time.GetUtcNow().UtcDateTime;

    private FlexIqTransmitter(
        FlexTransmitterOptions options, FlexClient client, FlexWaveform waveform,
        FlexWaveformIqOutput iq, FlexPtt ptt, FlexMeters meters, Action<string> log, bool ownsClient)
    {
        _options = options;
        _client = client;
        _waveform = waveform;
        _iq = iq;
        _ptt = ptt;
        _meters = meters;
        _log = log;
        _ownsClient = ownsClient;
        _client.MessageReceived += OnMessage;
        _client.StatusUpdated += OnStatus;
    }

    /// <summary>Interlock/transmit state transitions, timestamped — the radio's own account of
    /// whether it actually keyed the PA. Without this a transmission that never left
    /// PTT_REQUESTED looks identical to a healthy one from the client side.</summary>
    public IReadOnlyList<string> StateLog
    {
        get
        {
            lock (_faultGate)
            {
                return [.. _stateLog];
            }
        }
    }

    private readonly List<string> _stateLog = [];

    /// <summary>The interlock state last reported by the radio.</summary>
    public string InterlockState => _interlockState;

    private volatile string _interlockState = "";

    /// <summary>
    /// Blocks until the radio is out of its transmit cycle, so it will honour the next key.
    /// </summary>
    /// <remarks>
    /// <para>Keying while the radio is still finishing the previous transmission is silently
    /// ignored: it never re-enters TRANSMITTING, the burst goes out truncated, and the starve
    /// counter reads a healthy zero because the radio simply stopped asking. Observed live —
    /// a burst following an identification delivered 148 of 639 buffers with no error
    /// anywhere.</para>
    /// <para>This settles on elapsed time rather than on interlock state <b>because the
    /// waveform path does not report the state we would need</b>: transitions up to
    /// <c>UNKEY_REQUESTED</c> arrive, but the return to <c>RECEIVE</c> never does, so a
    /// state-based wait times out every time even though the radio is demonstrably idle
    /// (forward power zero, SWR back to 1.00). Waiting before keying rather than after
    /// unkeying also keeps the settle out of the previous burst's telemetry — measuring it
    /// there is what produced an 81792-sample "starve" that was purely an artefact of the
    /// wait itself.</para>
    /// </remarks>
    private async Task WaitForTransmitIdleAsync()
    {
        TimeSpan since = UtcNow - _lastUnkeyUtc;
        if (since < _options.InterBurstSettle)
        {
            await Task.Delay(_options.InterBurstSettle - since, _options.Time).ConfigureAwait(false);
        }
    }

    private DateTime _lastUnkeyUtc = DateTime.MinValue;

    private void OnStatus(FlexStatusUpdate update)
    {
        if (update.Object.StartsWith("interlock", StringComparison.OrdinalIgnoreCase)
            && update.Updated.TryGetValue("state", out string? interlock))
        {
            _interlockState = interlock;
        }

        if (!update.Object.StartsWith("interlock", StringComparison.OrdinalIgnoreCase)
            && !update.Object.StartsWith("transmit", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach ((string key, string value) in update.Updated)
        {
            if (key is "state" or "reason" or "source" or "tx_allowed" or "tx_client_handle"
                or "rfpower" or "band_zero_disable")
            {
                lock (_faultGate)
                {
                    _stateLog.Add($"{UtcNow:HH:mm:ss.fff} {update.Object}.{key}={value}");
                }
            }
        }
    }

    /// <summary>Meter telemetry for this session.</summary>
    public FlexMeters Meters => _meters;

    /// <summary>When the station last identified, or null if it has not yet.</summary>
    public DateTime? LastIdentifiedUtc { get; private set; }

    /// <summary>The callsign identification will use — the configured one, else the radio's own.</summary>
    public string? ResolvedCallsign =>
        _options.Callsign
        ?? (_client.TryGetObject("radio", out IReadOnlyDictionary<string, string>? radio)
            && radio.TryGetValue("callsign", out string? c) && !string.IsNullOrWhiteSpace(c)
                ? c
                : null);

    /// <summary>
    /// Sends a Morse station identification: callsign then mode, at
    /// <see cref="FlexTransmitterOptions.IdWpm"/>.
    /// </summary>
    /// <remarks>A data waveform carries nothing a listener can read, so on a real antenna the
    /// station has to say who it is in a form decodable without our software.</remarks>
    public async Task<TransmitReport?> IdentifyAsync(CancellationToken cancellation = default)
    {
        string? call = ResolvedCallsign;
        if (string.IsNullOrWhiteSpace(call))
        {
            throw new InvalidOperationException(
                "no callsign to identify with: the radio reported none, so set Callsign explicitly");
        }

        string text = MorseGenerator.IdText(call, _options.IdMode);
        _log($"identifying: \"{text}\" at {_options.IdWpm:F0} wpm " +
             $"({MorseGenerator.DurationSeconds(text, _options.IdWpm):F1} s)");
        float[] iq = MorseGenerator.Complex(
            text, _options.IdToneHz, 0.9, _options.IdWpm, SampleRate);
        TransmitReport report = await TransmitAsync(iq, cancellation).ConfigureAwait(false);
        LastIdentifiedUtc = UtcNow;
        return report;
    }

    /// <summary>
    /// Whether an identification is owed right now — the licence-condition decision by itself,
    /// separated from the transmitting that follows it.
    /// </summary>
    /// <remarks>Exposed so the policy can be asserted against a clock without keying anything,
    /// and so a ladder can see an identification coming and put it in a gap rather than
    /// discovering it mid-pass.</remarks>
    public bool IdentificationDue =>
        _options.Identify
        && (LastIdentifiedUtc is not DateTime last || UtcNow - last >= _options.IdentifyInterval);

    /// <summary>
    /// Identifies if the station has not done so yet, or if
    /// <see cref="FlexTransmitterOptions.IdentifyInterval"/> has elapsed since it last did.
    /// </summary>
    /// <remarks>Call this before every transmission. It is a no-op almost always, and the one
    /// time it is not is the time it would otherwise have been forgotten.</remarks>
    public async Task EnsureIdentifiedAsync(CancellationToken cancellation = default)
    {
        if (!IdentificationDue)
        {
            return;
        }

        await IdentifyAsync(cancellation).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(500), _options.Time, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The radio's own view of the slice we are transmitting on — antenna, mode, frequency,
    /// TX flag.
    /// </summary>
    /// <remarks>Read back rather than assumed: the transmitter is deaf into a dummy load, so
    /// "is this actually going out of the port the load is on" cannot be answered by
    /// listening, only by asking the radio what it thinks it is doing.</remarks>
    public IReadOnlyDictionary<string, string> SliceState()
    {
        if (_client.TryFindObject(
                "slice",
                s => s.TryGetValue("index_letter", out string? _) || s.ContainsKey("RF_frequency"),
                out string? name)
            && _client.TryGetObject(name, out IReadOnlyDictionary<string, string>? state))
        {
            return state;
        }

        return new Dictionary<string, string>();
    }

    /// <summary>The underlying session, for callers that need raw commands.</summary>
    public FlexClient Client => _client;

    /// <summary>A warning if the created slice could not be verified on the requested
    /// frequency (the band-persistence fix); null when it verified.</summary>
    public string? TuneWarning => _waveform.TuneWarning;

    /// <summary>Connects, registers the waveform, owns a slice, subscribes to meters and
    /// prepares the IQ sink and PTT.</summary>
    public static async Task<FlexIqTransmitter> OpenAsync(
        FlexTransmitterOptions options, Action<string>? log = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Action<string> write = log ?? (_ => { });
        options.Validate();

        write($"connecting to radio '{options.Radio}'…");
        FlexClient client = string.Equals(options.Radio, "discover", StringComparison.OrdinalIgnoreCase)
            ? await FlexClient.DiscoverAndConnectAsync("", TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false)
            : await FlexClient.ConnectAsync(options.Radio, cancellation: cancellation).ConfigureAwait(false);

        try
        {
            return await AttachAsync(client, options, write, ownsClient: true, cancellation).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Brings the waveform up over an <b>already-connected</b> session — the seam the offline
    /// mock tests use, and the way to share one session with other consumers.
    /// </summary>
    public static async Task<FlexIqTransmitter> AttachAsync(
        FlexClient client, FlexTransmitterOptions options, Action<string>? log = null,
        bool ownsClient = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        Action<string> write = log ?? (_ => { });
        options.Validate();

        {
            write($"connected: version {client.Version}, handle {client.Handle}");

            // Band placement is the library's job, not ours: our generators emit one-sided
            // analytic baseband (0…+OccupiedBandwidthHz), and the radio transmits only the
            // NEGATIVE half of a waveform's baseband — so M0LTE.Flex derives the slice at the
            // band's top edge, translates the samples below DC (a true frequency shift, never
            // a mirror), sizes the global transmit filter to the declared width, and throws
            // rather than putting a silently truncated signal on air. The hand-rolled
            // slice-centre-plus-offset scheme this replaces put the signal in the half that
            // never transmits under the corrected model.
            FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
                client,
                new FlexWaveformOptions
                {
                    UnderlyingMode = "RAW",
                    Band = new IqBand(
                        double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture),
                        options.OccupiedBandwidthHz,
                        IqBandReference.LowerEdge),
                    Antenna = options.Antenna,
                    RfPower = options.RfPower,
                },
                cancellation).ConfigureAwait(false);

            (double lowMhz, double highMhz) = waveform.OccupiedBand ?? (0, 0);
            write($"waveform '{waveform.WaveformName}' on slice {waveform.SliceIndex}: band " +
                  $"{lowMhz:F6}–{highMhz:F6} MHz, slice derived at {waveform.SliceFrequencyMhz:F6} MHz, " +
                  $"{options.Antenna} RAW, rfpower {options.RfPower}");
            if (waveform.TransmitFilter is (int filterLow, int filterHigh))
            {
                write($"transmit filter {filterLow}–{filterHigh} Hz (global; caps the occupied width)");
            }

            if (waveform.TransmitFilterWarning is not null)
            {
                write($"WARNING filter: {waveform.TransmitFilterWarning}");
            }

            if (waveform.TuneWarning is not null)
            {
                write($"WARNING tune: {waveform.TuneWarning}");
            }

            FlexMeters meters;
            try
            {
                meters = await FlexMeters.SubscribeAsync(client, cancellation).ConfigureAwait(false);
                write($"meters: {meters.Descriptors.Count} described");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                if (options.RequireMeters)
                {
                    throw new InvalidOperationException(
                        "meter telemetry unavailable, so there would be no SWR interlock watching a " +
                        $"transmitter that cannot hear itself — refusing to continue ({ex.Message}). " +
                        "Set RequireMeters=false only for offline tests.", ex);
                }

                write($"meters unavailable ({ex.Message}) — continuing WITHOUT an SWR interlock");
                meters = FlexMeters.None(client);
            }

            return new FlexIqTransmitter(
                options, client, waveform,
                waveform.CreateIqOutput(options.BufferSeconds),
                waveform.CreatePtt(confirmInterlock: true),
                meters, write, ownsClient);
        }
    }

    /// <summary>
    /// Pre-flight check: a short low-level tone while watching SWR, run before anything else
    /// each session.
    /// </summary>
    /// <remarks>The transmitter is deaf into a dummy load and its own receiver is blanked
    /// while keyed, so this is the only thing standing between a mis-cabled session and full
    /// power into an open antenna port a few metres from the receive loop.</remarks>
    /// <param name="seconds">Length of the pre-flight tone.</param>
    /// <param name="toneHz">Its offset from the waveform centre.</param>
    /// <param name="amplitude">Its IQ amplitude. <b>Defaults near full scale deliberately.</b>
    /// Radiated power is set by <em>both</em> the radio's <c>rfpower</c> and the drive level we
    /// hand the waveform — roughly rfpower × |IQ|² — so a quiet pre-flight tone reads low
    /// forward power and would fail to measure SWR for reasons that have nothing to do with the
    /// antenna. Keep the drive high and let <c>rfpower</c> set the watts; that also keeps a
    /// constant-envelope single tone using the waveform's full dynamic range.</param>
    /// <param name="requireSwrReading">Refuse to pass pre-flight unless SWR could actually be
    /// measured. Keep this true: a transmitter that cannot report SWR has no interlock at all,
    /// and passing pre-flight is what licenses the (louder) bursts that follow. Setting it
    /// false is a deliberate low-power diagnostic — see the caller's power guard.</param>
    /// <param name="cancellation">Cancels the transmission.</param>
    public async Task<TransmitReport> PreflightAsync(
        double seconds = 2.0, double toneHz = 1000, double amplitude = 0.9,
        bool requireSwrReading = true, CancellationToken cancellation = default)
    {
        _log($"pre-flight: {seconds:F1} s tone at {toneHz:F0} Hz, amplitude {amplitude:F2}, " +
             $"SWR limit {_options.MaxSwr:F2}");
        float[] tone = ToneGenerator.Complex([toneHz], amplitude, seconds, SampleRate);
        TransmitReport report = await TransmitAsync(tone, cancellation).ConfigureAwait(false);

        if (report.Aborted)
        {
            throw new InvalidOperationException($"pre-flight aborted: {report.AbortReason}");
        }

        if (report.PeakSwr is not null)
        {
            _log($"pre-flight OK: peak SWR {report.PeakSwr:F2}, peak forward " +
                 $"{report.PeakForwardDbm:F1} dBm ({FlexMeters.DbmToWatts(report.PeakForwardDbm ?? 0):F1} W)");
            return report;
        }

        string detail = report.PeakForwardDbm is null
            ? "no FWDPWR samples arrived while keyed"
            : $"forward power {report.PeakForwardDbm:F1} dBm is below the trust floor";
        if (requireSwrReading)
        {
            throw new InvalidOperationException(
                $"pre-flight could not measure SWR ({detail}), so nothing would be watching the " +
                "transmitter. Raise --rf-power until FWDPWR reads, or pass --allow-no-swr for a " +
                "deliberate low-power diagnostic.");
        }

        _log($"pre-flight: NO usable SWR reading ({detail}) — continuing because the SWR gate " +
             "was explicitly waived at low power.");
        return report;
    }

    /// <summary>
    /// Transmits one burst of interleaved I,Q at <see cref="SampleRate"/>: pre-fill, key,
    /// write, drain, unkey — with the meter interlock live throughout.
    /// </summary>
    public async Task<TransmitReport> TransmitAsync(
        float[] interleavedIq, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(interleavedIq);

        float[] payload = ToneGenerator.Concat(
            ToneGenerator.Silence(_options.LeadInSeconds, SampleRate),
            interleavedIq,
            ToneGenerator.Silence(_options.LeadOutSeconds, SampleRate));

        // Pad the tail with silence to a whole number of waveform buffers, plus one spare, so
        // the radio never asks for a partial buffer and the starve counter stays a sharp
        // oracle for "did the reflection loop keep up mid-burst".
        int wholePackets = ((payload.Length / 2) + PacketSamples - 1) / PacketSamples;
        int paddedSamples = (wholePackets + 1) * PacketSamples;
        if (paddedSamples * 2 > payload.Length)
        {
            var padded = new float[paddedSamples * 2];
            payload.CopyTo(padded, 0);
            payload = padded;
        }

        int complexSamples = payload.Length / 2;
        double seconds = complexSamples / (double)SampleRate;
        if (seconds > _options.MaxBurstSeconds)
        {
            throw new ArgumentException(
                $"burst is {seconds:F1} s, over the {_options.MaxBurstSeconds:F0} s ceiling",
                nameof(interleavedIq));
        }

        double peak = ToneGenerator.PeakMagnitude(interleavedIq);
        if (peak > 1.0)
        {
            throw new ArgumentException(
                $"IQ peak magnitude {peak:F3} exceeds 1.0 — it would clip before it reaches the PA",
                nameof(interleavedIq));
        }

        // SWR is only meaningful on a constant envelope — the trust rules live on
        // SwrInterlock. On this route the envelope is directly measurable from the IQ.
        bool constantEnvelope = IsConstantEnvelope(interleavedIq);
        if (!constantEnvelope)
        {
            _log("SWR not evaluated: the burst envelope is modulated, and forward/reflected " +
                 "samples from different instants cannot be compared. The pre-flight tone is " +
                 "the SWR measurement.");
        }

        int faultsBefore;
        lock (_faultGate)
        {
            faultsBefore = _faults.Count;
        }

        using var interlock = new SwrInterlock(
            _meters, _options.MaxSwr, constantEnvelope, _options.MaxForwardWatts);
        long reflectedBefore = _iq.PacketsReflected;
        long starvedBefore = _iq.SamplesStarved;
        long reflected = 0;
        long starved = 0;
        DateTime keyUtc;
        bool drained;

        try
        {
            // Pre-fill the ring before keying: the radio only pulls while keyed, so a burst
            // that fits entirely in the buffer is on the air with no chance of a starve at
            // key-on — which is what keeps SamplesStarved a meaningful oracle.
            int ringSamples = (int)(_options.BufferSeconds * SampleRate) * 2;
            int prefill = Math.Min(payload.Length, Math.Max(0, ringSamples - (SampleRate / 2)));
            prefill -= prefill % 2;
            if (prefill > 0)
            {
                _iq.Write(payload.AsSpan(0, prefill));
            }

            await WaitForTransmitIdleAsync().ConfigureAwait(false);
            keyUtc = UtcNow;
            _ptt.Key();

            // Chunked so the interlock can cut a transmission short mid-burst.
            const int chunk = SampleRate / 20 * 2; // 50 ms of interleaved I,Q
            for (int offset = prefill; offset < payload.Length;)
            {
                if (interlock.AbortReason is not null || cancellation.IsCancellationRequested)
                {
                    break;
                }

                int take = Math.Min(chunk, payload.Length - offset);
                _iq.Write(payload.AsSpan(offset, take));
                offset += take;
            }

            // Read the starve counter HERE, the moment the last sample is written — not after
            // the drain. A starve that matters is the producer falling behind the radio while
            // there are still samples to send; once the whole burst is in the ring it cannot
            // underrun on real data. The radio keeps pulling through the drain-and-unkey window
            // and the sink zero-pads those empty pulls — benign tail silence, not a phase
            // discontinuity — and M0LTE.Flex 0.8.0 counts them where 0.5.0 did not (a library
            // behaviour change, worth an upstream note). Snapshotting before the drain keeps
            // SamplesStarved a sharp oracle for the real failure through the version bump.
            starved = _iq.SamplesStarved - starvedBefore;

            drained = interlock.AbortReason is null
                && _iq.Drain(TimeSpan.FromSeconds(seconds + 5));
            reflected = _iq.PacketsReflected - reflectedBefore;
        }
        finally
        {
            _ptt.Unkey();
            interlock.Dispose(); // stop collecting at unkey (idempotent with the using)
        }

        DateTime unkeyUtc = UtcNow;
        _lastUnkeyUtc = unkeyUtc;
        string[] faults;
        lock (_faultGate)
        {
            faults = [.. _faults.Skip(faultsBefore)];
        }

        string? abortReason = interlock.AbortReason;
        if (abortReason is not null)
        {
            _log($"ABORTED: {abortReason}");
        }

        var report = new TransmitReport(
            keyUtc, unkeyUtc, complexSamples,
            reflected,
            starved,
            drained,
            interlock.Collected,
            abortReason is not null,
            abortReason)
        {
            PeakSwr = interlock.PeakSwr,
            PeakForwardDbm = interlock.PeakForwardDbm,
            Faults = faults,
        };

        _log($"tx: {seconds:F2} s, {report.PacketsReflected} buffers reflected, " +
             $"{report.SamplesStarved} starved, drained={report.Drained}");
        if (report.SamplesStarved > 0)
        {
            _log("WARNING: the radio pulled samples the ring could not supply — the transmitted " +
                 "signal has a phase discontinuity and any spectral measurement from it is void.");
        }

        foreach (string f in faults)
        {
            _log($"radio message while keyed: {f}");
        }

        return report;
    }

    /// <summary>
    /// Watches, after a transmission has ended, whether the radio is still asking for TX
    /// buffers — and whether it ever stops.
    /// </summary>
    /// <remarks>
    /// <para>A waveform is reflection-driven, so "has the transmission ended" is not a
    /// question the client can answer from its own side: the only evidence is whether the
    /// radio is still pulling.</para>
    /// <para>This is the diagnostic that found the root cause of bursts being truncated, and
    /// the answer was not what either transmit strategy assumed: <b>after any transmission the
    /// radio keeps pulling TX buffers at the full 187.5/s indefinitely, with the interlock
    /// parked in UNKEY_REQUESTED</b> — measured unchanged over 8 s following a 2 s burst. The
    /// stream is therefore drained whether or not we are keyed, so samples written before the
    /// PA comes up are silently discarded, and samples written after it are in a race with a
    /// consumer that never pauses.</para>
    /// </remarks>
    public async Task ObserveAfterTransmitAsync(double seconds, CancellationToken cancellation = default)
    {
        long last = _iq.PacketsReflected;
        long lastStarved = _iq.SamplesStarved;
        for (int t = 1; t <= (int)Math.Ceiling(seconds); t++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _options.Time, cancellation).ConfigureAwait(false);
            long now = _iq.PacketsReflected;
            long starvedNow = _iq.SamplesStarved;
            _log($"  +{t,2}s  buffers pulled this second: {now - last,5}   " +
                 $"starved: {starvedNow - lastStarved,7}   interlock: {_interlockState}");
            last = now;
            lastStarved = starvedNow;
        }
    }

    /// <summary>
    /// Whether a burst holds a steady envelope while it is on — the precondition for the
    /// radio's SWR meter to mean anything.
    /// </summary>
    /// <remarks>Measured over the samples above half the peak, so the lead-in silence and the
    /// cosine ramps do not count against it. A single complex tone is exactly constant; a
    /// two-tone or a data waveform is not.</remarks>
    internal static bool IsConstantEnvelope(ReadOnlySpan<float> interleavedIq)
    {
        double peak = 0;
        for (int k = 0; k + 1 < interleavedIq.Length; k += 2)
        {
            double m = (interleavedIq[k] * (double)interleavedIq[k])
                + (interleavedIq[k + 1] * (double)interleavedIq[k + 1]);
            peak = Math.Max(peak, m);
        }

        if (peak <= 0)
        {
            return false;
        }

        double threshold = peak * 0.25; // half the peak amplitude, in power terms
        double sum = 0;
        long count = 0;
        for (int k = 0; k + 1 < interleavedIq.Length; k += 2)
        {
            double m = (interleavedIq[k] * (double)interleavedIq[k])
                + (interleavedIq[k + 1] * (double)interleavedIq[k + 1]);
            if (m >= threshold)
            {
                sum += m;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        // Peak-to-average power ratio of the "on" portion; 1.0 is a pure carrier.
        return peak / (sum / count) < 1.10;
    }

    /// <summary>Sets the radio's TX power (0–100) mid-session, for a linearity sweep.</summary>
    public async Task SetRfPowerAsync(int power, CancellationToken cancellation = default)
    {
        if (power is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(power), power, "RF power must be 0–100");
        }

        await _client.SendCommandExpectOkAsync(
            string.Create(CultureInfo.InvariantCulture, $"transmit set rfpower={power}"),
            cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.MessageReceived -= OnMessage;
        _client.StatusUpdated -= OnStatus;
        try
        {
            _ptt.Unkey();
        }
        catch (IOException)
        {
            // best effort — we are tearing down anyway
        }

        _meters.Dispose();
        _iq.Dispose();

        // Remove the headless slice this waveform created (see FlexDaxTransmitter.DisposeAsync for
        // the mechanism): the radio does not auto-remove it on disconnect, so without this each pass
        // leaks a slice toward the 6500's 4-slice limit. Best-effort, while the client is alive and
        // before the waveform is torn down.
        if (!string.IsNullOrEmpty(_waveform.SliceIndex))
        {
            try
            {
                await _client.SendCommandExpectOkAsync($"slice remove {_waveform.SliceIndex}")
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or InvalidOperationException
                    or ObjectDisposedException or FlexProtocolException)
            {
                // best effort — tearing down anyway
            }
        }

        await _waveform.DisposeAsync().ConfigureAwait(false);
        if (_ownsClient)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnMessage(string handle, string text)
    {
        lock (_faultGate)
        {
            _faults.Add(text);
        }
    }
}
