using System.Globalization;
using Packet.SoundModem.Ident;
using M0LTE.Flex;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Real-audio transmit through a FlexRadio 6000-series DAX-TX stream, with transmitter health
/// metering and a safety interlock - the modem's real deployment route.
/// </summary>
/// <remarks>
/// <para>Where <see cref="FlexIqTransmitter"/> synthesises single-sideband in software and hands
/// the radio complex IQ (bypassing the radio's SSB modulator, ALC and TX DSP - the bench
/// instrument), this route hands the radio <b>real audio through DAX</b> and lets the radio's own
/// DIGU SSB modulator place it. On DIGU the radio transmits audio frequency <c>f</c> at
/// <c>dial + f</c> with the carrier suppressed at the dial, so the MS110D waveform - native
/// 9600 Hz, an 1800 Hz sub-carrier, occupied ≈180-3420 Hz - needs <b>no offset, no NCO and no
/// SSB synthesis</b>: it is just resampled to the DAX rate and pushed. That is exactly the modem's
/// production path (<see cref="Packet.SoundModem.FlexRadio.FlexDevice"/>), which is why the OTA
/// campaign exercises it by default and reserves the IQ route for the reference/instrument leg.</para>
/// <para><b>Bring-up points the transmitter at DAX and reads the selection back.</b> Until Flex
/// 0.7.0 the transmitter defaulted to the mic and every DAX enable step returned <c>err=0</c>
/// regardless, so a mic-sourced transmitter would key and send silence - a dead modem, not a
/// degraded one. <see cref="FlexStationOptions.SelectDaxAsTransmitSource"/> issues
/// <c>transmit set dax=1</c> and confirms it; if the radio does not agree, bring-up throws rather
/// than run deaf (mirrors <c>FlexDevice.OpenAsync</c> in the main repo).</para>
/// <para>This is a <b>push</b> sink - we write audio and the sink meters it out at the DAX sample
/// rate - so unlike the reflection-driven waveform path there is no ring to pre-fill and no starve
/// to guard against (<see cref="TransmitReport.SamplesStarved"/> is always zero here, and
/// <see cref="TransmitReport.PacketsReflected"/> counts DAX packets sent). Everything from the
/// shared <see cref="FlexTransmitterOptions"/> down - power ceiling, SWR limit, burst ceiling,
/// lead-in/out, identification and the inter-burst settle - is the same policy the IQ route
/// applies, on the same <see cref="SwrInterlock"/> and generators.</para>
/// </remarks>
public sealed class FlexDaxTransmitter : IOtaTransmitter
{
    /// <summary>The DAX transport this route uses: reduced-bandwidth 24 kHz s16. MS110D is
    /// ≤3450 Hz so 24 kHz is ample, and it is the radio's native DAX data mode.</summary>
    private static readonly DaxStreamFormat Format = DaxStreamFormat.ReducedBandwidth;

    /// <summary>The DAX audio sample rate (24 kHz on the reduced-bandwidth transport). Callers
    /// resample the modem's 9600 Hz audio to this before handing it over.</summary>
    public const int SampleRate = 24000;

    /// <summary>The DAX channel the transmitter claims. SmartSDR grabs channel 1 when it is
    /// running, but the headless deployment owns the radio, so channel 1 is ours.</summary>
    private const string DaxChannel = "1";

    private readonly FlexTransmitterOptions _options;
    private readonly FlexClient _client;
    private readonly FlexStation _station;
    private readonly FlexAudioOutput _output;
    private readonly FlexPtt _ptt;
    private readonly FlexMeters _meters;
    private readonly Action<string> _log;
    private readonly List<string> _faults = [];
    private readonly Lock _faultGate = new();
    private readonly bool _ownsClient;

    /// <summary>Now, on the injected clock - never the system one directly.</summary>
    private DateTime UtcNow => _options.Time.GetUtcNow().UtcDateTime;

    private DateTime _lastUnkeyUtc = DateTime.MinValue;

    private FlexDaxTransmitter(
        FlexTransmitterOptions options, FlexClient client, FlexStation station,
        FlexAudioOutput output, FlexPtt ptt, FlexMeters meters, Action<string> log, bool ownsClient)
    {
        _options = options;
        _client = client;
        _station = station;
        _output = output;
        _ptt = ptt;
        _meters = meters;
        _log = log;
        _ownsClient = ownsClient;
        _client.MessageReceived += OnMessage;
    }

    /// <summary>The underlying session, for callers that need raw commands.</summary>
    public FlexClient Client => _client;

    /// <summary>Meter telemetry for this session.</summary>
    public FlexMeters Meters => _meters;

    /// <summary>Whether the radio reported DAX as the transmitter's audio source at bring-up -
    /// the read-back that separates a live modem from one keying silence.</summary>
    public bool? TransmitSourceIsDax => _station.TransmitSourceIsDax;

    /// <summary>The transmit passband the radio reports, as (low, high) in Hz - the global filter
    /// that actually caps transmitted audio bandwidth, opened to
    /// <see cref="FlexTransmitterOptions.DaxTransmitFilterHighHz"/> at bring-up.</summary>
    public (int Low, int High)? TransmitFilter => _station.TransmitFilter;

    /// <summary>A warning if the created slice could not be verified on the requested frequency
    /// (the band-persistence fix); null when it verified.</summary>
    public string? TuneWarning => _station.TuneWarning;

    /// <summary>When the station last identified, or null if it has not yet.</summary>
    public DateTime? LastIdentifiedUtc { get; private set; }

    /// <summary>The callsign identification will use - the configured one, else the radio's own.</summary>
    public string? ResolvedCallsign =>
        _options.Callsign
        ?? (_client.TryGetObject("radio", out IReadOnlyDictionary<string, string>? radio)
            && radio.TryGetValue("callsign", out string? c) && !string.IsNullOrWhiteSpace(c)
                ? c
                : null);

    /// <summary>Connects, brings up a DIGU slice with DAX enabled and pointed at the transmitter,
    /// subscribes to meters and prepares the audio sink and PTT.</summary>
    public static async Task<FlexDaxTransmitter> OpenAsync(
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
    /// Brings the DAX station up over an <b>already-connected</b> session - the seam the offline
    /// mock tests use, and the way to share one session with other consumers.
    /// </summary>
    public static async Task<FlexDaxTransmitter> AttachAsync(
        FlexClient client, FlexTransmitterOptions options, Action<string>? log = null,
        bool ownsClient = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        Action<string> write = log ?? (_ => { });
        options.Validate();

        write($"connected: version {client.Version}, handle {client.Handle}");

        // Headless DIGU slice + the eight-step DAX enable, then point the transmitter at DAX and
        // open the transmit filter. On DIGU the radio does the SSB placement (audio f → dial + f,
        // carrier suppressed at the dial), so there is nothing to offset here - see the class
        // remarks. The transmit filter is a GLOBAL, persistent setting, so a previous waveform
        // session could otherwise leave it truncating the top of the MS110D band; we always state
        // it (DaxTransmitFilterHighHz, default 3450).
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client,
            Format,
            new FlexStationOptions
            {
                Frequency = options.FrequencyMHz,
                Antenna = options.Antenna,
                // DIGU for the SSB modes (the deployment default); FM/NFM for the FM-native modes,
                // where the DAX audio frequency-modulates the carrier and the drive is calibrated to
                // the mode's target peak deviation. The transmit-filter high cut still applies (it
                // caps the audio bandwidth reaching the modulator on either mode).
                SliceMode = options.SliceMode,
                DaxChannel = DaxChannel,
                TransmitFilterHighHz = options.DaxTransmitFilterHighHz,
                SelectDaxAsTransmitSource = true,
                // Name the harness: the production daemon registers as "pdn-soundmodem", and
                // until both named themselves, two transmitting clients on one radio were
                // indistinguishable "Flex"es in each other's diagnostics.
                HeadlessStationName = "sm-ota",
            },
            cancellation).ConfigureAwait(false);

        // Any throw from here propagates to the client's owner - OpenAsync (ownsClient) or the
        // caller that passed the session in - which disposes the client and thereby the session,
        // releasing the DAX streams the station created. FlexStation holds nothing else, so there
        // is no separate station teardown to do (mirrors FlexIqTransmitter.AttachAsync).

        // If the transmitter could not be pointed at DAX it would key and send silence - a dead
        // modem, not a degraded one - so fail bring-up loudly rather than run with it. The
        // read-back is the whole point of Flex 0.7.0; mirrors FlexDevice.OpenAsync in the main repo.
        if (station.TransmitSourceWarning is string warning)
        {
            throw new InvalidOperationException($"flex: {warning}");
        }

        write($"DAX station on slice {station.SliceIndex}: {options.FrequencyMHz} MHz {options.Antenna} " +
              $"{options.SliceMode}, dax ch {DaxChannel}");
        write($"transmit source is DAX: {station.TransmitSourceIsDax?.ToString() ?? "unreported"}");
        if (station.TransmitFilter is (int filterLow, int filterHigh))
        {
            write($"transmit filter {filterLow}-{filterHigh} Hz (global; caps transmitted audio bandwidth)");
        }

        if (station.TuneWarning is not null)
        {
            write($"WARNING tune: {station.TuneWarning}");
        }

        // DAX carries no power setting of its own (unlike the waveform's IqBand), so state it
        // explicitly. Radiated power is the radio's rfpower × the audio drive level, exactly as it
        // is on the IQ route.
        await client.SendCommandExpectOkAsync(
            string.Create(CultureInfo.InvariantCulture, $"transmit set rfpower={options.RfPower}"),
            cancellation).ConfigureAwait(false);
        write($"rfpower {options.RfPower}");

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
                    $"transmitter that cannot hear itself - refusing to continue ({ex.Message}). " +
                    "Set RequireMeters=false only for offline tests.", ex);
            }

            write($"meters unavailable ({ex.Message}) - continuing WITHOUT an SWR interlock");
            meters = FlexMeters.None(client);
        }

        return new FlexDaxTransmitter(
            options, client, station,
            station.CreateAudioOutput(paceRealTime: true),
            station.CreatePtt(confirmInterlock: true),
            meters, write, ownsClient);
    }

    /// <summary>
    /// Whether an identification is owed right now - the licence-condition decision by itself,
    /// separated from the transmitting that follows it.
    /// </summary>
    public bool IdentificationDue =>
        _options.Identify
        && (LastIdentifiedUtc is not DateTime last || UtcNow - last >= _options.IdentifyInterval);

    /// <summary>
    /// Sends a Morse station identification as real keyed audio: callsign then mode, at
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
        float[] audio = MorseGenerator.Real(text, _options.IdToneHz, 0.9, _options.IdWpm, SampleRate);

        // A keyed Morse carrier has gaps and ramps, so its RF envelope is not constant - SWR is
        // not evaluated here (the pre-flight tone is the SWR measurement).
        TransmitReport report = await TransmitCoreAsync(audio, constantEnvelope: false, cancellation)
            .ConfigureAwait(false);
        LastIdentifiedUtc = UtcNow;
        return report;
    }

    /// <summary>
    /// Identifies if the station has not done so yet, or if
    /// <see cref="FlexTransmitterOptions.IdentifyInterval"/> has elapsed since it last did.
    /// </summary>
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
    /// Pre-flight check: a short single tone while watching SWR, run before anything else each
    /// session.
    /// </summary>
    /// <remarks>The transmitter is deaf into a dummy load and its own receiver is blanked while
    /// keyed, so this is the only thing standing between a mis-cabled session and full power into
    /// an open antenna port a few metres from the receive loop. A single audio tone through DIGU
    /// SSB is a <b>constant-envelope</b> RF carrier, so its forward/reflected samples are
    /// comparable and the SWR reading is meaningful - this tone is the session's SWR measurement.
    /// The modulated data bursts that follow are not constant-envelope and do not attempt one.</remarks>
    public async Task<TransmitReport> PreflightAsync(
        double seconds = 2.0, double toneHz = 1000, double amplitude = 0.9,
        bool requireSwrReading = true, CancellationToken cancellation = default)
    {
        _log($"pre-flight: {seconds:F1} s tone at {toneHz:F0} Hz, amplitude {amplitude:F2}, " +
             $"SWR limit {_options.MaxSwr:F2}");
        float[] tone = ToneGenerator.Real([toneHz], amplitude, seconds, SampleRate);
        TransmitReport report = await TransmitCoreAsync(tone, constantEnvelope: true, cancellation)
            .ConfigureAwait(false);

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

        _log($"pre-flight: NO usable SWR reading ({detail}) - continuing because the SWR gate " +
             "was explicitly waived at low power.");
        return report;
    }

    /// <summary>
    /// Transmits one modulated data burst of mono audio at <see cref="SampleRate"/>: lead-in
    /// silence, audio, lead-out silence - key, write, drain, unkey - with the meter interlock live
    /// throughout. A data burst is not constant-envelope, so SWR is not evaluated during it.
    /// </summary>
    public Task<TransmitReport> TransmitAsync(float[] monoAudio, CancellationToken cancellation = default) =>
        TransmitCoreAsync(monoAudio, constantEnvelope: false, cancellation);

    /// <summary>
    /// The shared keyed-transmission core. <paramref name="constantEnvelope"/> is decided by the
    /// caller by construction - true only for the pre-flight tone - rather than measured from the
    /// samples: on real audio there is nothing analytic to inspect, and the transmitter already
    /// knows which of its transmissions is a carrier and which is modulated.
    /// </summary>
    private async Task<TransmitReport> TransmitCoreAsync(
        float[] monoAudio, bool constantEnvelope, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(monoAudio);

        float[] payload = ToneGenerator.Concat(
            ToneGenerator.SilenceReal(_options.LeadInSeconds, SampleRate),
            monoAudio,
            ToneGenerator.SilenceReal(_options.LeadOutSeconds, SampleRate));

        double seconds = payload.Length / (double)SampleRate;
        if (seconds > _options.MaxBurstSeconds)
        {
            throw new ArgumentException(
                $"burst is {seconds:F1} s, over the {_options.MaxBurstSeconds:F0} s ceiling",
                nameof(monoAudio));
        }

        double peak = ToneGenerator.PeakReal(monoAudio);
        if (peak > 1.0)
        {
            throw new ArgumentException(
                $"audio peak {peak:F3} exceeds 1.0 - it would clip in the DAX s16 transport before " +
                "it reaches the SSB modulator", nameof(monoAudio));
        }

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
        long packetsBefore = _output.PacketsSent;
        DateTime keyUtc;
        bool drained;

        try
        {
            await WaitForTransmitIdleAsync().ConfigureAwait(false);
            keyUtc = UtcNow;
            _ptt.Key();

            // Chunked so the interlock can cut a transmission short mid-burst. The push sink paces
            // itself at the DAX sample rate, so each chunk write also meters out real airtime -
            // there is no ring to pre-fill and no starve to guard against, unlike the waveform path.
            const int chunk = SampleRate / 20; // 50 ms of mono audio
            for (int offset = 0; offset < payload.Length;)
            {
                if (interlock.AbortReason is not null || cancellation.IsCancellationRequested)
                {
                    break;
                }

                int take = Math.Min(chunk, payload.Length - offset);
                _output.Write(payload.AsSpan(offset, take));
                offset += take;
            }

            drained = interlock.AbortReason is null;

            // Always drain: it flushes the final partial packet and resets the sink's airtime
            // clock and accumulator, so an aborted burst cannot bleed a stale partial packet into
            // the next transmission. The airtime it waits out is at most one packet.
            _output.Drain();
        }
        finally
        {
            _ptt.Unkey();
            interlock.Dispose(); // stop collecting at unkey (idempotent with the using)
        }

        long packetsSent = _output.PacketsSent - packetsBefore;
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
            keyUtc, unkeyUtc, payload.Length,
            packetsSent,
            SamplesStarved: 0, // nothing pulls on the push route - see TransmitReport.SamplesStarved
            drained,
            interlock.Collected,
            abortReason is not null,
            abortReason)
        {
            PeakSwr = interlock.PeakSwr,
            PeakForwardDbm = interlock.PeakForwardDbm,
            Faults = faults,
        };

        _log($"tx: {seconds:F2} s, {report.PacketsReflected} DAX packets sent, drained={report.Drained}");
        foreach (string f in faults)
        {
            _log($"radio message while keyed: {f}");
        }

        return report;
    }

    /// <summary>
    /// Blocks until enough time has passed since the last unkey that the radio will honour the
    /// next key - the same inter-burst settle the IQ route enforces, on the injected clock.
    /// </summary>
    private async Task WaitForTransmitIdleAsync()
    {
        TimeSpan since = UtcNow - _lastUnkeyUtc;
        if (since < _options.InterBurstSettle)
        {
            await Task.Delay(_options.InterBurstSettle - since, _options.Time).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.MessageReceived -= OnMessage;
        try
        {
            _ptt.Unkey();
        }
        catch (IOException)
        {
            // best effort - we are tearing down anyway
        }

        _meters.Dispose();

        // Remove the headless slice this station created. The radio does NOT auto-remove it when
        // the client disconnects, and FlexStation.DisposeAsync only disposes the client - so
        // without this every pass leaks a slice, and they pile up to the 6500's 4-slice limit until
        // `slice create` starts failing (0x50000003). Done here (regardless of _ownsClient) while
        // the client is still alive - the dispose order is transmitter-before-client - and
        // best-effort, since we are tearing down. The transmitter always brings the station up
        // headless, so the slice is always ours to remove.
        if (!string.IsNullOrEmpty(_station.SliceIndex))
        {
            try
            {
                await _client.SendCommandExpectOkAsync($"slice remove {_station.SliceIndex}")
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or InvalidOperationException
                    or ObjectDisposedException or FlexProtocolException)
            {
                // best effort - tearing down anyway
            }
        }

        // FlexStation.DisposeAsync does nothing but dispose the client, so tearing the station
        // down here would dispose a session we may not own. Dispose the client only when we own
        // it (which releases the station's DAX streams with the session); otherwise leave it to
        // the caller that passed it in.
        if (_ownsClient)
        {
            await _station.DisposeAsync().ConfigureAwait(false);
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
