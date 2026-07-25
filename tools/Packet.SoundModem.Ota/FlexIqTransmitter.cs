using System.Globalization;
using M0LTE.Flex;

namespace Packet.SoundModem.Ota;

/// <summary>Bring-up and safety parameters for <see cref="FlexIqTransmitter"/>.</summary>
public sealed record FlexIqTransmitterOptions
{
    /// <summary>Radio address: an IP/hostname, or <c>discover</c> for the UDP :4992
    /// broadcast, or <c>mock</c> handled by the caller.</summary>
    public string Radio { get; init; } = "discover";

    /// <summary>Waveform slice frequency, MHz in the six-decimal Flex form. This is the
    /// <b>waveform centre</b>: any LO/carrier leakage lands here, so the plan places it
    /// deliberately below the occupied band (18.098000 → effective dial 18.100000 with a
    /// +2000 Hz offset).</summary>
    public string FrequencyMHz { get; init; } = "18.098000";

    /// <summary>Antenna port. The dummy load is on ANT1.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>TX RF power, 0–100. <b>Required</b> — there is deliberately no default, so a
    /// power level is always a decision rather than an accident.</summary>
    public required int RfPower { get; init; }

    /// <summary>TX filter edges. The defaults are the ±12 kHz limit of the 24 kHz waveform
    /// rate.</summary>
    public int TxFilterLowHz { get; init; } = -12000;

    /// <summary>TX filter high cut.</summary>
    public int TxFilterHighHz { get; init; } = 12000;

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

    /// <summary>Silence written before the tone/burst to absorb the PTT→TRANSMITTING settle
    /// (measured at 139 ms on M0LTE's 6500).</summary>
    public double LeadInSeconds { get; init; } = 0.3;

    /// <summary>Silence written after the burst, before draining and unkeying.</summary>
    public double LeadOutSeconds { get; init; } = 0.1;

    /// <summary>IQ ring depth. Must exceed lead-in + burst for the pre-fill to work
    /// without back-pressure on short bursts.</summary>
    public double BufferSeconds { get; init; } = 4.0;

    /// <summary>Fail bring-up if meter telemetry cannot be subscribed. <b>True by default and
    /// should stay true against a real radio</b> — without meters there is no SWR interlock,
    /// and without the interlock there is nothing at all watching a deaf transmitter. Offline
    /// tests against the mock (which has no meter surface) set it false.</summary>
    public bool RequireMeters { get; init; } = true;
}

/// <summary>What one keyed transmission did.</summary>
/// <param name="KeyUtc">When <c>xmit 1</c> was issued.</param>
/// <param name="UnkeyUtc">When <c>xmit 0</c> was issued.</param>
/// <param name="Samples">Complex samples handed to the radio.</param>
/// <param name="PacketsReflected">Waveform TX buffers reflected during the burst.</param>
/// <param name="SamplesStarved">Complex samples the radio pulled that the ring could not
/// supply. <b>Must be zero</b> — a non-zero count is a phase discontinuity on the air and
/// invalidates the spectral measurement.</param>
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
/// Wideband complex IQ transmit through a FlexRadio 6000-series waveform, with transmitter
/// health metering and a safety interlock.
/// </summary>
/// <remarks>
/// <para>The transmit half of the MS110D OTA chain (ota-execution-plan §T2/§T4). Uses
/// <c>M0LTE.Flex</c>'s <see cref="FlexWaveform"/> headless bring-up with
/// <c>underlying_mode=RAW</c>, which is the only path on a Flex that carries true wideband
/// complex IQ to RF (docs/flex-integration.md §9.5); DAX audio TX is clamped to the mode's
/// ~3 kHz SSB filter and DAX-IQ is receive-only.</para>
/// <para><b>Everything here is new code under test.</b> The waveform IQ path has had one
/// hardware session (a tone and a comb) and no consumer until now, so the counters
/// (<c>PacketsReflected</c>, <c>SamplesStarved</c>) are reported on every burst and a starve
/// is treated as a failed measurement, not a warning.</para>
/// </remarks>
public sealed class FlexIqTransmitter : IAsyncDisposable
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

    private readonly FlexIqTransmitterOptions _options;
    private readonly FlexClient _client;
    private readonly FlexWaveform _waveform;
    private readonly FlexWaveformIqOutput _iq;
    private readonly FlexPtt _ptt;
    private readonly FlexMeters _meters;
    private readonly Action<string> _log;
    private readonly List<string> _faults = [];
    private readonly Lock _faultGate = new();

    private readonly bool _ownsClient;

    private FlexIqTransmitter(
        FlexIqTransmitterOptions options, FlexClient client, FlexWaveform waveform,
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

    private void OnStatus(FlexStatusUpdate update)
    {
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
                    _stateLog.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {update.Object}.{key}={value}");
                }
            }
        }
    }

    /// <summary>Meter telemetry for this session.</summary>
    public FlexMeters Meters => _meters;

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
        FlexIqTransmitterOptions options, Action<string>? log = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Action<string> write = log ?? (_ => { });
        if (options.RfPower is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RfPower, "RF power must be 0–100");
        }

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
        FlexClient client, FlexIqTransmitterOptions options, Action<string>? log = null,
        bool ownsClient = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        Action<string> write = log ?? (_ => { });
        if (options.RfPower is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RfPower, "RF power must be 0–100");
        }

        if (options.RfPower > options.RfPowerCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RfPower,
                $"RF power {options.RfPower} exceeds the {options.RfPowerCeiling} ceiling. The limit is the " +
                "RECEIVER, not the transmitter: the loop is metres from the dummy load and its ADC clips " +
                "near 100 W, at which point the front end makes its own intermodulation and compresses the " +
                "very levels a sweep measures. Raise RfPowerCeiling once RX gain has been reduced to suit, " +
                "checking captured peak dBFS.");
        }

        {
            write($"connected: version {client.Version}, handle {client.Handle}");
            FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
                client,
                new FlexWaveformOptions
                {
                    UnderlyingMode = "RAW",
                    Frequency = options.FrequencyMHz,
                    Antenna = options.Antenna,
                    TxFilterLowHz = options.TxFilterLowHz,
                    TxFilterHighHz = options.TxFilterHighHz,
                    RfPower = options.RfPower,
                },
                cancellation).ConfigureAwait(false);

            write($"waveform '{waveform.WaveformName}' on slice {waveform.SliceIndex}, " +
                  $"{options.FrequencyMHz} MHz {options.Antenna} RAW, rfpower {options.RfPower}");
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

        var collected = new List<FlexMeterReading>();
        double? peakSwr = null;
        double? peakFwd = null;
        string? abortReason = null;
        var gate = new Lock();

        void OnMeter(FlexMeterReading r)
        {
            lock (gate)
            {
                collected.Add(r);
                if (r.Descriptor.Name.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase)
                    && (peakFwd is null || r.Value > peakFwd))
                {
                    peakFwd = r.Value;
                }
            }

            // Derived SWR (forward/reflected, both dBm) is the trustworthy one — see
            // FlexMeters.SwrFromPowers. Null simply means "not transmitting hard enough to
            // measure", which is not a fault.
            double? swr = _meters.SwrFromPowers();
            if (swr is null)
            {
                return;
            }

            lock (gate)
            {
                if (peakSwr is null || swr > peakSwr)
                {
                    peakSwr = swr;
                }

                if (swr > _options.MaxSwr && abortReason is null)
                {
                    abortReason = $"SWR {swr:F2} exceeded the {_options.MaxSwr:F2} limit";
                }
            }
        }

        int faultsBefore;
        lock (_faultGate)
        {
            faultsBefore = _faults.Count;
        }

        _meters.Updated += OnMeter;
        long reflectedBefore = _iq.PacketsReflected;
        long starvedBefore = _iq.SamplesStarved;
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

            keyUtc = DateTime.UtcNow;
            _ptt.Key();

            // Chunked so the interlock can cut a transmission short mid-burst.
            const int chunk = SampleRate / 20 * 2; // 50 ms of interleaved I,Q
            for (int offset = prefill; offset < payload.Length;)
            {
                if (abortReason is not null || cancellation.IsCancellationRequested)
                {
                    break;
                }

                int take = Math.Min(chunk, payload.Length - offset);
                _iq.Write(payload.AsSpan(offset, take));
                offset += take;
            }

            drained = abortReason is null
                && _iq.Drain(TimeSpan.FromSeconds(seconds + 5));
        }
        finally
        {
            _ptt.Unkey();
            _meters.Updated -= OnMeter;
        }

        DateTime unkeyUtc = DateTime.UtcNow;
        string[] faults;
        lock (_faultGate)
        {
            faults = [.. _faults.Skip(faultsBefore)];
        }

        if (abortReason is not null)
        {
            _log($"ABORTED: {abortReason}");
        }

        var report = new TransmitReport(
            keyUtc, unkeyUtc, complexSamples,
            _iq.PacketsReflected - reflectedBefore,
            _iq.SamplesStarved - starvedBefore,
            drained,
            collected,
            abortReason is not null,
            abortReason)
        {
            PeakSwr = peakSwr,
            PeakForwardDbm = peakFwd,
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
