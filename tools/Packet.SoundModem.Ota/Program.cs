using M0LTE.Flex;
using System.Globalization;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

// sm-ota - the MS110D over-the-air harness. See docs/ms110d/ota-execution-plan.md.
//
// This increment is the tone-first bring-up (§E0.5): transmit a known tone through the Flex
// waveform IQ path into the dummy load, capture it on the UberSDR, and measure what came
// back. No modem, no scorer - deliberately, because a tone is a better detector of faults in
// the (all new) transmit stack than a data burst, and because the receiver has to be trusted
// before it can be used to judge a transmitter.

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    return args[0] switch
    {
        "tone" => await Commands.ToneAsync(args[1..]),
        "tune" => await Commands.TuneAsync(args[1..]),
        "sweep" => await Commands.SweepAsync(args[1..]),
        "rawmeters" => await Commands.RawMetersAsync(args[1..]),
        "radio" => await Commands.RadioStateAsync(args[1..]),
        "synth" => Commands.Synth(args[1..]),
        "burst" => await Commands.BurstAsync(args[1..]),
        "meters" => await Commands.MetersAsync(args[1..]),
        "measure" => Commands.Measure(args[1..]),
        "score" => ScoreCommand.Run(args[1..]),
        "sim" => SimCommand.Run(args[1..]),
        "sim-stream" => SimStreamCommand.Run(args[1..]),
        "replay" => ReplayCommand.Run(args[1..]),
        "ladder" => await LadderCommand.RunAsync(args[1..]),
        "fm-deviation" => FmLadderCommand.Deviation(args[1..]),
        "monitor" => await MonitorCommand.RunAsync(args[1..]),
        "-h" or "--help" or "help" => Usage(),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                               or IOException or FormatException or TimeoutException
                               or System.Net.Sockets.SocketException)
{
    // SocketException is not an IOException. The radio's network connection coming loose
    // mid-session is a bench event, not a defect, and deserves a line rather than a stack trace.
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'");
    Usage();
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine("""
        sm-ota - MS110D over-the-air harness (tone bring-up stage).

        sm-ota tone     transmit a test tone through the Flex waveform IQ path, optionally
                        capturing it on an UberSDR and measuring the result in one go.
        sm-ota meters   dump the radio's meter list and watch live readings (no transmit).
        sm-ota measure  analyse an IQ capture WAV: levels, spectrum, tone, image, spurs, IMD.
        sm-ota score    convert a captured pass and score every burst in it - acquisition,
                        WID, CFO, SNR, coded and uncoded BER, and what was MISSED.
        sm-ota sim      pure-software BER-vs-SNR baseline for any ModemCatalog mode: render a
                        burst, inject AWGN/Watterson at a known SNR, decode, tally frame/packet
                        success. No radio. Drives the FreeDV datac OFDM waterfalls.
        sm-ota sim-stream  continuous-stream datac measurement: N single-packet bursts through ONE
                        channel run, scored packets-received/sent (codec2's MPP currency). Injects a
                        managed WattersonChannel, or emits/decodes int16 for codec2's own `ch`.
        sm-ota ladder   §E2: inject the mask suite's own channel at the transmitter and run an
                        SNR ladder through real hardware. --dry-run rehearses it with no radio.
                        --mode freedv-datac<N> runs the OFDM ladder; --mode <fm-mode>
                        (afsk1200/fsk9600/…) runs the FM ladder (DAX FM route, discriminator
                        scorer); default (no --mode / --wn N) is the MS110D ladder.
        sm-ota fm-deviation  FM-discriminate an IQ capture and print the peak/RMS/mean deviation
                        in kHz - the instrument for calibrating an FM mode's drive to its target.
        sm-ota monitor  watch a receiver live - capture, convert, demodulate, print each burst
                        as it lands. Receive-only; nothing here transmits.

        Run any command with --help for its options.
        """);
    return 0;
}

internal static class Commands
{
    private const int WaveformRate = FlexIqTransmitter.SampleRate;

    public static async Task<int> ToneAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota tone --rf-power <0-100> [options]

                Radio:
                  --radio <ip|discover>     default discover
                  --freq <MHz>              where baseband 0 Hz lands on air, six decimals
                                            (default 18.098000). The slice is DERIVED above it -
                                            the waveform path transmits one sideband only
                  --obw <Hz>                declared occupied bandwidth above --freq (default
                                            6000, hard max 10000 - the radio's filter clamp).
                                            Tones outside 0..obw cannot reach the air
                  --antenna <port>          default ANT1
                  --rf-power <0-100>        REQUIRED - no default, by design
                  --max-swr <ratio>         abort threshold (default 1.5)
                  --rf-power-ceiling <n>    refuse above this (default 30) - protects the
                                            RECEIVER's ADC, not the transmitter

                Tone:
                  --tone-hz <Hz>            offset from the slice centre (default 2000)
                  --tone2-hz <Hz>           second tone; adding this makes it a two-tone IMD test
                  --amplitude <0-1>         per-tone IQ amplitude (default 0.7 single tone)
                  --seconds <s>             tone length (default 5)
                  --no-preflight            skip the SWR check (not advised)
                  --id-call <call>          Morse ID callsign (default: the radio's own)
                  --id-mode <name>          sent after the callsign (default MS110D)
                  --no-id                   suppress identification (dummy load only)
                  --allow-no-swr            proceed even if SWR could not be measured; only
                                            permitted at --rf-power 5 or below (diagnostic)

                Radiated power ≈ rfpower × |IQ|², so --rf-power and --amplitude BOTH set the
                power on the air. Keep --amplitude high and vary --rf-power for a power sweep,
                or the two confound each other.

                Capture (optional - omit to transmit only):
                  --capture-host <host>     e.g. ubersdr
                  --capture-freq <Hz>       RX tune frequency (default: slice centre + tone offset)
                  --capture-port <n>        default 80 with --capture-no-ssl, else 443
                  --capture-no-ssl          plain ws/http (a LAN instance)
                  --out-dir <dir>           where the capture WAV/sidecar go (default .)
                """);
            return a is null ? 2 : 0;
        }

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "discover"),
            FrequencyMHz = a.Str("freq", "18.098000"),
            OccupiedBandwidthHz = a.Int("obw", 6000),
            Antenna = a.Str("antenna", "ANT1"),
            RfPower = a.Int("rf-power", -1) is var p && p >= 0
                ? p
                : throw new ArgumentException("--rf-power <0-100> is required (there is deliberately no default)"),
            MaxSwr = a.Dbl("max-swr", 1.5),
            RfPowerCeiling = a.Int("rf-power-ceiling", 30),
            Callsign = a.Str("id-call", null),
            IdMode = a.Str("id-mode", "MS110D"),
            Identify = !a.Has("no-id"),
            InterBurstSettle = TimeSpan.FromSeconds(a.Dbl("settle", 2)),
            MaxBurstSeconds = a.Dbl("max-seconds", 60),
        };

        double toneHz = a.Dbl("tone-hz", 2000);
        double? tone2Hz = a.Has("tone2-hz") ? a.Dbl("tone2-hz", 0) : null;
        foreach (double? t in (double?[])[toneHz, tone2Hz])
        {
            // The placed band is 0..obw above --freq, and only that span reaches the air -
            // a tone outside it would be transmitted by no setting of anything.
            if (t is double hz && (hz <= 0 || hz >= options.OccupiedBandwidthHz))
            {
                throw new ArgumentException(
                    $"--tone-hz {hz:F0} is outside the declared band (0..{options.OccupiedBandwidthHz} Hz "
                    + "above --freq); move the tone or widen --obw (max 10000)");
            }
        }
        // A single complex tone is constant-envelope, so it can sit near full scale; two
        // tones peak at twice the per-tone amplitude, hence the lower default.
        double amplitude = a.Dbl("amplitude", tone2Hz is null ? 0.7 : 0.35);
        double seconds = a.Dbl("seconds", 5);

        bool allowNoSwr = a.Has("allow-no-swr");
        if (allowNoSwr && options.RfPower > 5)
        {
            throw new ArgumentException(
                $"--allow-no-swr waives the only check watching a transmitter that cannot hear " +
                $"itself, so it is limited to --rf-power 5 or below (got {options.RfPower}).");
        }

        double[] tones = tone2Hz is null ? [toneHz] : [toneHz, tone2Hz.Value];
        float[] tone = ToneGenerator.Complex(tones, amplitude, seconds, WaveformRate);
        double peak = ToneGenerator.PeakMagnitude(tone);
        Log($"tone: {string.Join(" + ", tones.Select(t => $"{t:F0} Hz"))} at amplitude " +
            $"{amplitude:F2}/tone, {seconds:F1} s, envelope peak {peak:F3}");

        string? captureHost = a.Str("capture-host", null);
        // Total capture window: pre-flight (~2 s + guards) + the tone + generous slack, so
        // the transmission is always well inside the recording.
        bool preflight = !a.Has("no-preflight");
        // The capture has to span everything that will be keyed, not just the burst under
        // test: the identification and each inter-burst settle are airtime too, and a capture
        // that stops early reads the burst as several dB low rather than failing outright.
        double idSeconds = options.Identify
            ? MorseGenerator.DurationSeconds(
                MorseGenerator.IdText(a.Str("id-call", "M0LTE"), a.Str("id-mode", "MS110D")), 30)
              + options.InterBurstSettle.TotalSeconds
            : 0;
        int captureSeconds = (int)Math.Ceiling(
            seconds + idSeconds + (preflight ? 6 : 2) + 8);

        await using FlexIqTransmitter tx = await FlexIqTransmitter.OpenAsync(options, Log);

        // Read back what the radio thinks it is doing - a deaf transmitter cannot confirm its
        // own antenna port by listening.
        IReadOnlyDictionary<string, string> slice = tx.SliceState();
        foreach (string key in (string[])["index_letter", "RF_frequency", "mode", "ant", "rxant", "tx", "active"])
        {
            if (slice.TryGetValue(key, out string? v))
            {
                Log($"slice {key} = {v}");
            }
        }

        // Tally EVERY VITA packet by class and stream id, not just the ones we accept. If a
        // described meter never arrives, the first question is whether its packet is being
        // received and discarded by our class filter rather than never sent at all.
        var vitaTally = new Dictionary<(ushort Class, uint Stream), long>();
        var vitaGate = new Lock();
        if (a.Has("dump-meters"))
        {
            tx.Client.VitaPacketReceived += p =>
            {
                lock (vitaGate)
                {
                    var key = (p.PacketClassCode, p.StreamId);
                    vitaTally[key] = vitaTally.TryGetValue(key, out long n) ? n + 1 : 1;
                }
            };
        }

        if (a.Has("dump-meters"))
        {
            Console.WriteLine($"{tx.Meters.Descriptors.Count} meters described after bring-up:");
            foreach (FlexMeterDescriptor d in tx.Meters.Descriptors.Values.OrderBy(d => d.Id))
            {
                Console.WriteLine($"{d.Id,4}  {d.Source,-5} {d.Name,-14} {d.Unit,-6} fps {d.Fps,3:F0}  {d.Description}");
            }
        }

        Task<CaptureResult>? capture = null;
        TransmitReport? mainBurst = null;
        using var captureCts = new CancellationTokenSource();
        if (captureHost is not null)
        {
            bool ssl = !a.Has("capture-no-ssl");
            // Tune the receiver to the BAND REFERENCE (--freq, where baseband 0 Hz lands), not
            // to the tone. That puts the tone at +tone-hz, any residual image at −tone-hz, and
            // LO/carrier leakage at +obw (the DERIVED slice sits at the top of the placed
            // band) - distinct, separately measurable things. Tuning to the tone itself would
            // sit it on the receiver's own DC, where a DC offset is indistinguishable from
            // signal.
            double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
            var capOpt = new UberSdrCaptureOptions
            {
                Host = captureHost,
                Port = a.Int("capture-port", ssl ? 443 : 80),
                Ssl = ssl,
                FrequencyHz = a.Int("capture-freq", (int)Math.Round(centreHz)),
                Name = a.Str("capture-name", "otatone"),
                OutputDir = a.Str("out-dir", "."),
                DurationSeconds = captureSeconds,
            };
            Log($"capture: {capOpt.Host} at {capOpt.FrequencyHz} Hz for {captureSeconds} s");
            var captureClient = new UberSdrIqClient(m => Log($"[rx] {m}"));
            capture = captureClient.CaptureAsync(capOpt, captureCts.Token);

            // Let the capture connect and clear its startup guard before any RF.
            await AwaitReceiverAsync(captureClient, capture, capOpt.Host);
        }

        try
        {
            await tx.EnsureIdentifiedAsync();

            if (preflight)
            {
                TransmitReport pre = await tx.PreflightAsync(
                    toneHz: toneHz, requireSwrReading: !allowNoSwr);
                Report(pre, tx);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            TransmitReport report = await tx.TransmitAsync(tone);
            Report(report, tx);
            mainBurst = report;

            if (a.Dbl("observe", 0) is var observe && observe > 0)
            {
                Log($"observing the radio for {observe:F0} s after unkey…");
                await tx.ObserveAfterTransmitAsync(observe);
            }

            lock (vitaGate)
            {
                if (vitaTally.Count > 0)
                {
                    Console.WriteLine("VITA packets by class/stream (class 0x8002 = meter):");
                    foreach (((ushort cls, uint stream), long n) in vitaTally.OrderBy(kv => kv.Key.Class))
                    {
                        Console.WriteLine($"  class 0x{cls:X4}  stream 0x{stream:X8}  {n,7} packets");
                    }
                }
            }
        }
        finally
        {
            if (capture is not null)
            {
                Log("waiting for the capture to finish…");
                CaptureResult result = await capture;
                Log($"capture written: {result.WavPath} ({result.Frames} frames, sample0 {result.Sample0Utc:O})");
                Console.WriteLine(result.WavPath);
                Log("analysing…");
                (DateTime, DateTime, DateTime)? mainWindow = mainBurst is null
                    ? null
                    : (result.Sample0Utc, mainBurst.KeyUtc, mainBurst.UnkeyUtc);
                Analyse(result.WavPath, tones, expectedOffsetHz: 0, window: mainWindow);
            }
        }

        return 0;
    }

    /// <summary>
    /// Diagnostic: engage the PA with the radio's own tune carrier on an ordinary slice, and
    /// report which meters stream.
    /// </summary>
    /// <remarks>
    /// This is the discriminator for "the hardware transmit meters (FWDPWR/REFPWR/SWR) never
    /// arrive". It removes the waveform, our IQ, and our reflection loop from the picture
    /// entirely - a plain carrier the radio generates itself, on a normal-mode slice, with the
    /// same client, the same subscription and the same decoder. If the meters appear here they
    /// are waveform-specific; if they are still absent the problem is in the client session.
    /// </remarks>
    /// <summary>
    /// Steps transmit power (or drive level) through a series of tones inside ONE capture,
    /// then measures each step - the operating-point and linearity instrument (§I3).
    /// </summary>
    public static async Task<int> SweepAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota sweep --capture-host <host> [options]

                  --rf-powers <list>     comma-separated, default 1,2,5,10,15
                  --amplitudes <list>    sweep drive instead of power (rf-power then fixed)
                  --rf-power <n>         the fixed power when sweeping amplitude
                  --seconds <s>          tone length per step (default 3)
                  --gap <s>              silence between steps (default 2)
                  --radio/--freq/--antenna/--tone-hz/--capture-*/--out-dir as for `tone`

                One capture spans every step, so the dummy-load-to-loop coupling - which is
                physical and uncalibrated - cannot drift between measurements and masquerade
                as non-linearity.
                """);
            return a is null ? 2 : 0;
        }

        string radio = a.Str("radio", "10.45.0.76");
        string freq = a.Str("freq", "18.106500");
        double toneHz = a.Dbl("tone-hz", 2000);
        double seconds = a.Dbl("seconds", 3);
        double gap = a.Dbl("gap", 2);
        bool sweepAmplitude = a.Has("amplitudes");

        double[] steps = (sweepAmplitude ? a.Req("amplitudes") : a.Str("rf-powers", "1,2,5,10,15"))
            .Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
        int fixedPower = a.Int("rf-power", 10);
        double fixedAmplitude = a.Dbl("amplitude", 0.9);

        var options = new FlexTransmitterOptions
        {
            Radio = radio,
            FrequencyMHz = freq,
            OccupiedBandwidthHz = a.Int("obw", 6000),
            Antenna = a.Str("antenna", "ANT1"),
            RfPower = sweepAmplitude ? fixedPower : (int)steps[0],
            MaxSwr = a.Dbl("max-swr", 1.5),
            RfPowerCeiling = a.Int("rf-power-ceiling", 30),
            Callsign = a.Str("id-call", null),
            IdMode = a.Str("id-mode", "MS110D"),
            Identify = !a.Has("no-id"),
        };

        await using FlexIqTransmitter tx = await FlexIqTransmitter.OpenAsync(options, Log);

        string captureHost = a.Req("capture-host");
        bool ssl = !a.Has("capture-no-ssl");
        int captureSeconds = (int)Math.Ceiling((steps.Length * (seconds + gap)) + 14);
        var capOpt = new UberSdrCaptureOptions
        {
            Host = captureHost,
            Port = a.Int("capture-port", ssl ? 443 : 80),
            Ssl = ssl,
            FrequencyHz = a.Int("capture-freq",
                (int)Math.Round(double.Parse(freq, CultureInfo.InvariantCulture) * 1e6)),
            Name = a.Str("capture-name", "otasweep"),
            OutputDir = a.Str("out-dir", "."),
            DurationSeconds = captureSeconds,
        };
        Log($"capture: {capOpt.Host} at {capOpt.FrequencyHz} Hz for {captureSeconds} s");
        using var cts = new CancellationTokenSource();
        var captureClient = new UberSdrIqClient(m => Log($"[rx] {m}"));
        Task<CaptureResult> capture = captureClient.CaptureAsync(capOpt, cts.Token);
        await AwaitReceiverAsync(captureClient, capture, capOpt.Host);

        var results = new List<(double Step, DateTime Start, DateTime End, TransmitReport Report)>();
        foreach (double step in steps)
        {
            int power = sweepAmplitude ? fixedPower : (int)step;
            double amplitude = sweepAmplitude ? step : fixedAmplitude;
            await tx.EnsureIdentifiedAsync();
            await tx.SetRfPowerAsync(power);
            await Task.Delay(300);

            Log($"step: rfpower {power}, amplitude {amplitude:F2}");
            float[] tone = ToneGenerator.Complex([toneHz], amplitude, seconds, WaveformRate);
            TransmitReport r = await tx.TransmitAsync(tone);
            results.Add((step, r.KeyUtc, r.UnkeyUtc, r));
            await Task.Delay(TimeSpan.FromSeconds(gap));
        }

        Log("waiting for the capture…");
        CaptureResult result = await capture;
        Log($"capture: {result.WavPath}");

        (float[] i, int rate) = WavFile.ReadMono(result.WavPath, channel: 0);
        (float[] q, _) = WavFile.ReadMono(result.WavPath, channel: 1);

        Console.WriteLine();
        Console.WriteLine($"=== sweep: {(sweepAmplitude ? "drive amplitude" : "rf power")} ===");
        // Supply voltage is in the table because on a battery a sagging rail compresses the
        // PA at high power and would read as non-linearity - the two must be separable.
        Console.WriteLine($"{"step",6} {"fwd dBm",9} {"fwd W",8} {"SWR",6} {"volts",7} " +
                          $"{"rx dBFS",9} {"SNR/3k",8} {"starve",7}");

        double? referenceStep = null;
        double? referenceRx = null;
        foreach ((double step, DateTime start, DateTime end, TransmitReport r) in results)
        {
            // Trim generously inside the keyed window so the cosine ramps and the PTT settle
            // never enter the measurement.
            double from = (start - result.Sample0Utc).TotalSeconds + 0.6;
            double length = (end - start).TotalSeconds - 1.0;
            string rx = "  (n/a)", snrText = "  (n/a)";
            if (from > 0 && length > 0.5 && from + length < i.Length / (double)rate)
            {
                IqSpectrum spectrum = IqAnalysis.WelchWindow(i, q, rate, from, length, 4096);
                (double hz, double power) = spectrum.FindPeak(toneHz - 500, toneHz + 500);
                double rxDb = IqAnalysis.Db(power);
                double snr = rxDb - IqAnalysis.Db(spectrum.NoiseInBandwidth(3000));
                rx = $"{rxDb,9:F1}";
                snrText = $"{snr,8:F1}";
                referenceStep ??= step;
                referenceRx ??= rxDb;
            }

            double? volts = r.Meters
                .Where(m => m.Descriptor.Name.StartsWith("+13.8", StringComparison.Ordinal))
                .Select(m => (double?)m.Value)
                .LastOrDefault();
            Console.WriteLine($"{step,6:F2} {r.PeakForwardDbm,9:F1} " +
                              $"{FlexMeters.DbmToWatts(r.PeakForwardDbm ?? 0),8:F2} {r.PeakSwr,6:F2} " +
                              $"{volts,7:F2} {rx} {snrText} {r.SamplesStarved,7}");
        }

        Console.WriteLine();
        Console.WriteLine("Linearity: for a linear chain, a step ratio of N in power should move both");
        Console.WriteLine("forward power and received level by 10·log10(N) dB. Divergence between the");
        Console.WriteLine("two is the transmitter compressing; divergence of both from the commanded");
        Console.WriteLine("ratio is the radio's own power control, which is not a fault.");
        Console.WriteLine(result.WavPath);
        return 0;
    }

    public static async Task<int> TuneAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota tune --rf-power <0-100> [--radio <ip|discover>] [--freq <MHz>]
                            [--antenna ANT1] [--mode DIGU] [--seconds 3]

                Keys the radio's own tune carrier (transmit tune on/off) on an ordinary slice
                and reports which meters streamed. Diagnostic only - no waveform, no IQ.
                """);
            return a is null ? 2 : 0;
        }

        string radio = a.Str("radio", "discover");
        int power = a.Int("rf-power", -1);
        if (power is < 0 or > 100)
        {
            throw new ArgumentException("--rf-power <0-100> is required");
        }

        double seconds = a.Dbl("seconds", 3);
        string freq = a.Str("freq", "18.106500");
        string antenna = a.Str("antenna", "ANT1");
        string mode = a.Str("mode", "DIGU");

        Log($"connecting to '{radio}'…");
        M0LTE.Flex.FlexClient client = string.Equals(radio, "discover", StringComparison.OrdinalIgnoreCase)
            ? await M0LTE.Flex.FlexClient.DiscoverAndConnectAsync("", TimeSpan.FromSeconds(10))
            : await M0LTE.Flex.FlexClient.ConnectAsync(radio);
        await using (client)
        {
            Log($"connected: handle {client.Handle}");

            // Confirm the PA actually keyed. Without this the whole test is worthless: a tune
            // that never left RECEIVE would produce exactly the same "no power meters" result
            // and would look like evidence when it is nothing of the kind.
            client.StatusUpdated += u =>
            {
                if (u.Object.StartsWith("interlock", StringComparison.OrdinalIgnoreCase)
                    && u.Updated.TryGetValue("state", out string? st))
                {
                    Log($"interlock state={st}");
                }
            };

            await using M0LTE.Flex.FlexStation station = await M0LTE.Flex.FlexStation.SetUpHeadlessAsync(
                client,
                M0LTE.Flex.DaxStreamFormat.FullBandwidth,
                new M0LTE.Flex.FlexStationOptions
                {
                    Frequency = freq,
                    Antenna = antenna,
                    SliceMode = mode,
                    Keepalive = false,
                });
            Log($"slice {station.SliceIndex} at {freq} MHz {antenna} {mode}");

            using FlexMeters meters = await FlexMeters.SubscribeAsync(client);
            Log($"meters: {meters.Descriptors.Count} described");

            await client.SendCommandExpectOkAsync(
                string.Create(CultureInfo.InvariantCulture, $"transmit set tunepower={power}"));
            await client.SendCommandExpectOkAsync(
                string.Create(CultureInfo.InvariantCulture, $"transmit set rfpower={power}"));

            Log($"tune ON at power {power} for {seconds:F1} s…");
            await client.SendCommandExpectOkAsync("transmit tune on");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }
            finally
            {
                await client.SendCommandExpectOkAsync("transmit tune off");
                Log("tune OFF");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Console.WriteLine();
            Console.WriteLine($"{meters.PacketsReceived} meter packets, " +
                              $"{meters.UnknownIdSamples} unknown-id samples");
            Console.WriteLine($"{"name",-12} {"unit",-6} {"raw",8}  {"scaled",10}");
            foreach (FlexMeterReading r in meters.Snapshot().OrderBy(r => r.Descriptor.Id))
            {
                Console.WriteLine($"{r.Descriptor.Name,-12} {r.Descriptor.Unit,-6} {r.Raw,8}  {r.Value,10:F3}");
            }

            var seen = meters.SamplesById;
            string missing = string.Join(", ", meters.Descriptors.Values
                .Where(d => d.Fps > 0 && !seen.ContainsKey(d.Id))
                .OrderBy(d => d.Id).Select(d => $"{d.Name}(id {d.Id})"));
            Console.WriteLine($"described at >0 fps but never sent: {(missing.Length == 0 ? "(none)" : missing)}");

            double? derived = meters.SwrFromPowers();
            Console.WriteLine(derived is null
                ? "derived SWR: unavailable"
                : $"derived SWR (FWDPWR/REFPWR): {derived:F2}");
        }

        return 0;
    }

    /// <summary>Raw-wire meter probe that uses none of M0LTE.Flex - see <see cref="RawMeterProbe"/>.</summary>
    public static async Task<int> RawMetersAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota rawmeters --radio <ip> [--freq 18.106500] [--antenna ANT1]
                                 [--rf-power 10] [--seconds 3]

                Speaks the Flex TCP/UDP protocol directly and dumps EVERY datagram, including
                any the M0LTE.Flex parser would discard. Keys a tune carrier so the transmit
                meters have something to report.
                """);
            return a is null ? 2 : 0;
        }

        await RawMeterProbe.RunAsync(
            a.Req("radio"), a.Str("freq", "18.106500"), a.Str("antenna", "ANT1"),
            a.Int("rf-power", 10), a.Dbl("seconds", 3), Log);
        return 0;
    }

    /// <summary>Dumps the radio-level status object - the radio's own account of its
    /// settings, including any frequency-calibration offset.</summary>
    public static async Task<int> RadioStateAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("sm-ota radio --radio <ip|discover> [--filter <substring>] [--set <command>]");
            return a is null ? 2 : 0;
        }

        string radio = a.Str("radio", "10.45.0.76");
        string? filter = a.Str("filter", null);
        M0LTE.Flex.FlexClient client = string.Equals(radio, "discover", StringComparison.OrdinalIgnoreCase)
            ? await M0LTE.Flex.FlexClient.DiscoverAndConnectAsync("", TimeSpan.FromSeconds(10))
            : await M0LTE.Flex.FlexClient.ConnectAsync(radio);
        await using (client)
        {
            var lines = new List<string>();
            client.StatusUpdated += u =>
            {
                foreach ((string k, string v) in u.Updated)
                {
                    lines.Add($"{u.Object}.{k} = {v}");
                }
            };

            if (a.Str("set", null) is string setCommand)
            {
                M0LTE.Flex.FlexResult r = await client.SendCommandAsync(setCommand);
                Console.WriteLine($"'{setCommand}' -> error 0x{r.Error:X8} {r.Message}");
            }

            await client.SendCommandExpectOkAsync("sub radio all");
            client.SendCommandNoWait("sub tx all");
            await Task.Delay(2500);

            // Arrival order, NOT sorted: a status dump that reorders its lines cannot show a
            // value changing, and an ordinal sort put "-1497" before "0" and made a setting
            // that had just been applied look like it had reverted.
            foreach (string line in lines)
            {
                if (filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(line);
                }
            }
        }

        return 0;
    }

    public static async Task<int> MetersAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota meters [--radio <ip|discover>] [--seconds <s>] [--all]

                Connects, runs `meter list`, subscribes, and prints live readings. Never keys
                the transmitter. Use this to confirm the meter metadata parse and the value
                scaling before trusting the safety interlock - TX meters read idle values
                until something transmits, but the descriptors and the packet decode are
                fully exercised. --all prints every meter, not just the transmitter set.
                """);
            return a is null ? 2 : 0;
        }

        string radio = a.Str("radio", "discover");
        double seconds = a.Dbl("seconds", 5);
        bool all = a.Has("all");
        bool dumpStatus = a.Has("dump-status");

        Log($"connecting to '{radio}'…");
        M0LTE.Flex.FlexClient client = string.Equals(radio, "discover", StringComparison.OrdinalIgnoreCase)
            ? await M0LTE.Flex.FlexClient.DiscoverAndConnectAsync("", TimeSpan.FromSeconds(10))
            : await M0LTE.Flex.FlexClient.ConnectAsync(radio);
        await using (client)
        {
            if (dumpStatus)
            {
                // The reference client (kc2g-flex-tools/flexclient) maintains its meter table
                // from the STATUS channel, special-casing `S<handle>|meter …`, and handles
                // meters being added and removed as slices/modes change. This dump shows what
                // this radio actually pushes, so the table can be built from the live truth
                // rather than a one-shot `meter list` snapshot that may already be stale.
                client.StatusUpdated += u =>
                {
                    if (u.Object.Contains("meter", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[status] object='{u.Object}'");
                        foreach ((string k, string v) in u.Updated)
                        {
                            Console.WriteLine($"           {k} = {v}");
                        }
                    }
                };
            }

            await client.InitUdpAsync();
            using FlexMeters meters = await FlexMeters.SubscribeAsync(client);

            Console.WriteLine($"{meters.Descriptors.Count} meters described:");
            Console.WriteLine($"{"id",4}  {"src",-5} {"name",-12} {"unit",-6} {"fps",4} {"low",8} .. {"hi",-8} description");
            foreach (FlexMeterDescriptor d in meters.Descriptors.Values.OrderBy(d => d.Id))
            {
                if (!all && !IsTransmitterMeter(d))
                {
                    continue;
                }

                Console.WriteLine($"{d.Id,4}  {d.Source,-5} {d.Name,-12} {d.Unit,-6} {d.Fps,4:F0} " +
                                  $"{d.Low,8:F1} .. {d.High,-8:F1} {d.Description}");
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds));

            Console.WriteLine();
            Console.WriteLine($"after {seconds:F0} s: {meters.PacketsReceived} meter packets, " +
                              $"{meters.UnknownIdSamples} samples for unknown ids");
            Console.WriteLine($"{"name",-10} {"unit",-6} {"raw",8}  {"scaled",10}");
            foreach (FlexMeterReading r in meters.Snapshot().OrderBy(r => r.Descriptor.Id))
            {
                if (!all && !IsTransmitterMeter(r.Descriptor))
                {
                    continue;
                }

                Console.WriteLine($"{r.Descriptor.Name,-10} {r.Descriptor.Unit,-6} {r.Raw,8}  {r.Value,10:F3}");
            }

            double? derived = meters.SwrFromPowers();
            Console.WriteLine(derived is null
                ? "derived SWR: unavailable (forward power below the trust floor - expected while idle)"
                : $"derived SWR (from FWDPWR/REFPWR): {derived:F2}");
        }

        return 0;
    }

    /// <summary>
    /// Modulates an MS110D burst, upconverts it, and writes the result as an IQ capture WAV -
    /// a synthetic capture, with no radio and no receiver involved.
    /// </summary>
    /// <remarks>This is the offline gate the plan calls E0: the whole transmit chain and the
    /// whole scoring chain, exercised against a signal whose contents are known exactly. It is
    /// also the quickest way to look at what the upconverter actually emits, which is how the
    /// band-placement bug was found - a bandpass selects, it does not translate.</remarks>
    public static int Synth(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota synth --out <iq.wav> [--wn 2] [--offset-hz 2000] [--payload-bits 0]
                             [--rate 24000] [--seed 1] [--amplitude 0.9]

                Writes an int16 stereo IQ WAV of one modulated, upconverted burst.
                """);
            return a is null ? 2 : 0;
        }

        int rate = a.Int("rate", 24000);

        // --tone-hz emits the exact IQ the transmitter would send for a test tone, so the
        // software can be cleared (or convicted) without involving a radio at all.
        if (a.Has("tone-hz"))
        {
            float[] toneIq = ToneGenerator.Complex(
                [a.Dbl("tone-hz", 2000)], a.Dbl("amplitude", 0.9), a.Dbl("seconds", 30), rate,
                rampSeconds: 0.005);
            WriteIqWav(a.Str("out", "synth-tone.wav"), toneIq, rate);
            Console.Error.WriteLine($"tone {a.Dbl("tone-hz", 2000):F0} Hz, {toneIq.Length / 2} complex @ {rate} Hz");
            Console.WriteLine(a.Str("out", "synth-tone.wav"));
            return 0;
        }

        int wn = a.Int("wn", 2);
        double offset = a.Dbl("offset-hz", 2000);
        var interleaver = Ms110dInterleaverKind.Short;
        int defaultBits = Ms110dInterleaverParams.Get3k(wn, interleaver).InputBits - 32;
        int payloadBits = a.Int("payload-bits", 0) is var pb && pb > 0 ? pb : defaultBits;

        var random = new Random(a.Int("seed", 1));
        var payload = new byte[payloadBits];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = interleaver,
            ConstraintLength = 7,
            PreambleSuperframes = 3,
        }).Modulate(payload);

        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions
        {
            OutputRate = rate,
            OffsetHz = offset,
            Amplitude = a.Dbl("amplitude", 0.9),
        }).Convert(audio);

        string outPath = a.Str("out", "synth-iq.wav");
        WriteIqWav(outPath, iq, rate);

        Console.Error.WriteLine(
            $"wn{wn} {payloadBits} payload bits → {audio.Length} audio samples @ 9600 Hz → " +
            $"{iq.Length / 2} complex @ {rate} Hz, offset {offset:F0} Hz, peak " +
            $"{ToneGenerator.PeakMagnitude(iq):F3}");
        Console.WriteLine(outPath);
        return 0;
    }

    /// <summary>
    /// The whole chain in one command: modulate a seeded MS110D burst, upconvert it,
    /// transmit it, capture it, convert it back to audio, demodulate it, and score the bits.
    /// </summary>
    /// <remarks>Every stage that precedes this has an oracle of its own, so a failure here is
    /// attributable rather than mysterious - which is the entire reason the tone, the meters,
    /// the calibration and the upconverter were built first.</remarks>
    public static async Task<int> BurstAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota burst --capture-host <host> --rf-power <n> [options]

                  --wn <n>            waveform number (default 2)
                  --seed <n>          payload PRNG seed, recorded for exact scoring (default 1)
                  --offset-hz <Hz>    modem offset from the waveform centre (default 2000)
                  --dial-correction   Hz to add to the capture frequency for the measured
                                      reference error (see `sm-ota measure` against RWM).
                                      RE-MEASURE PER RECEIVER, not merely per session: the
                                      acquisition grid is ±75 Hz, and a receiver sitting near
                                      its edge presents as a dead demodulator - nothing
                                      acquires and the modem looks broken
                  --no-capture        transmit only, into a capture held open elsewhere. A
                                      session lingers ~240 s after use, so one capture per
                                      burst exhausts a receiver with your own stale sessions;
                                      for a run of bursts hold ONE sm-iqcapture open, fire
                                      them with this, and score with `--at` off the key times
                  --radio/--freq/--antenna/--capture-* as for `tone`
                """);
            return a is null ? 2 : 0;
        }

        int wn = a.Int("wn", 2);
        int seed = a.Int("seed", 1);
        double offset = a.Dbl("offset-hz", 2000);
        var interleaver = Ms110dInterleaverKind.Short;

        // Payload from a seeded PRNG: the seed is all that need be recorded for the decode to
        // be scored bit-exact afterwards, however long afterwards that is.
        int payloadBits = Ms110dInterleaverParams.Get3k(wn, interleaver).InputBits - 32;
        var random = new Random(seed);
        var payload = new byte[payloadBits];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = interleaver,
            ConstraintLength = 7,
            PreambleSuperframes = 3,
        };
        float[] audio = new Ms110dModulator(settings).Modulate(payload);
        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions
        {
            OffsetHz = offset,
            Amplitude = a.Dbl("amplitude", 0.9),
        }).Convert(audio);

        double burstSeconds = (iq.Length / 2.0) / FlexIqTransmitter.SampleRate;
        Log($"WN{wn} seed {seed}: {payloadBits} payload bits → {burstSeconds:F2} s, " +
            $"offset {offset:F0} Hz, peak {ToneGenerator.PeakMagnitude(iq):F2}");

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "10.45.0.76"),
            FrequencyMHz = a.Str("freq", "18.106500"),
            OccupiedBandwidthHz = a.Int("obw", 6000),
            Antenna = a.Str("antenna", "ANT1"),
            RfPower = a.Int("rf-power", -1) is var p && p >= 0
                ? p
                : throw new ArgumentException("--rf-power is required"),
            MaxSwr = a.Dbl("max-swr", 1.5),
            RfPowerCeiling = a.Int("rf-power-ceiling", 30),
            Callsign = a.Str("id-call", null),
            IdMode = a.Str("id-mode", $"MS110D WN{wn}"),
            Identify = !a.Has("no-id"),
            InterBurstSettle = TimeSpan.FromSeconds(a.Dbl("settle", 2)),
        };

        // The modem band tops out at offset + the SSB high edge; a band declared narrower
        // than that would have its top silently absent from the air.
        if (offset + 3450 > options.OccupiedBandwidthHz)
        {
            throw new ArgumentException(
                $"--offset-hz {offset:F0} puts the MS110D band top at {offset + 3450:F0} Hz, outside "
                + $"the declared --obw {options.OccupiedBandwidthHz} Hz");
        }

        await using FlexIqTransmitter tx = await FlexIqTransmitter.OpenAsync(options, Log);

        bool ssl = !a.Has("capture-no-ssl");
        double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
        // The capture is tuned to the --freq reference (where baseband 0 Hz lands) PLUS the
        // measured reference error, so the residual carrier offset the demodulator sees is
        // near zero. Without it the combined TX+RX error at 18 MHz exceeds the ±75 Hz
        // acquisition grid and nothing acquires.
        double correction = a.Dbl("dial-correction", 0);
        double idSeconds = options.Identify
            ? MorseGenerator.DurationSeconds(MorseGenerator.IdText("M0LTE", options.IdMode), 30)
              + options.InterBurstSettle.TotalSeconds
            : 0;
        var capOpt = new UberSdrCaptureOptions
        {
            // Required only when we are opening a capture of our own; --no-capture transmits
            // into one held elsewhere and must not demand a host it will never use.
            Host = a.Has("no-capture") ? "" : a.Req("capture-host"),
            Port = a.Int("capture-port", ssl ? 443 : 80),
            Ssl = ssl,
            FrequencyHz = (int)Math.Round(centreHz + correction),
            Name = a.Str("capture-name", $"otaburst-wn{wn}"),
            OutputDir = a.Str("out-dir", "."),
            DurationSeconds = (int)Math.Ceiling(burstSeconds + idSeconds + 6 + 10),
        };
        // One session per burst exhausts a receiver: an UberSDR session lingers for its
        // session_timeout (240 s on the instances measured) after the client disconnects, so a
        // run of short bursts refuses itself with 503s that look like the receiver being busy.
        // --no-capture transmits into a capture somebody else is holding open: run sm-iqcapture
        // for the whole pass, fire the bursts, then score with `sm-ota score --at <seconds>`
        // using the key times printed here.
        using var cts = new CancellationTokenSource();
        TransmitReport report;
        if (a.Has("no-capture"))
        {
            Log("no capture of our own - transmitting into a capture held elsewhere; "
                + "note the key time below and score with --at");
            await tx.EnsureIdentifiedAsync();
            await tx.PreflightAsync();
            report = await tx.TransmitAsync(iq);
            Report(report, tx);
            Log($"burst key-down {report.KeyUtc:yyyy-MM-ddTHH:mm:ss.fffZ} - "
                + $"subtract the capture's sample0 to get its --at offset");
            return 0;
        }

        Log($"capture: {capOpt.Host} at {capOpt.FrequencyHz} Hz for {capOpt.DurationSeconds} s");
        var captureClient = new UberSdrIqClient(m => Log($"[rx] {m}"));
        Task<CaptureResult> capture = captureClient.CaptureAsync(capOpt, cts.Token);
        await AwaitReceiverAsync(captureClient, capture, capOpt.Host);

        await tx.EnsureIdentifiedAsync();
        await tx.PreflightAsync();
        report = await tx.TransmitAsync(iq);
        Report(report, tx);

        CaptureResult result = await capture;
        Log($"capture: {result.WavPath}");

        // --- score ---------------------------------------------------------------------
        (float[] iCh, int rate) = WavFile.ReadMono(result.WavPath, channel: 0);
        (float[] qCh, _) = WavFile.ReadMono(result.WavPath, channel: 1);
        var iD = Array.ConvertAll(iCh, v => (double)v);
        var qD = Array.ConvertAll(qCh, v => (double)v);

        float[] recovered = new SsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = rate,
            OutputRate = 9600,
            // The capture is already tuned to null the reference error, so the suppressed
            // carrier sits at exactly the transmit offset in its baseband.
            DialHz = offset,
            NormalisePeak = 0.7f,
        }).Convert(iD, qD);

        var decoded = new List<byte>();
        Ms110dLockInfo? locked = null;
        Ms110dBurstEndReason? reason = null;
        var demod = new Ms110dDemodulator();
        // Take the lock details while the burst is live: EndBurst clears them, so reading
        // demod.Lock after Process() returns null on a perfectly successful decode.
        demod.BlockDecoded += b =>
        {
            locked ??= demod.Lock;
            decoded.AddRange(b.Bits);
        };
        demod.BurstCompleted += b => reason = b.Reason;
        demod.Process(recovered);

        Console.WriteLine();
        Console.WriteLine("=== decode ===");
        if (locked is null)
        {
            Console.WriteLine("NO ACQUISITION - the demodulator never locked.");
            Console.WriteLine($"peak search metric {demod.PeakSearchMetric:F3} (threshold 0.32)");
        }
        else
        {
            Console.WriteLine($"acquired: WN{locked.WaveformNumber} {locked.Interleaver} K{locked.ConstraintLength}, " +
                              $"CFO {locked.CfoHz:+0.0;-0.0} Hz");
            bool widOk = locked.WaveformNumber == wn && locked.Interleaver == interleaver;
            Console.WriteLine(widOk ? "WID matches what was sent" : "*** WID MISMATCH ***");
        }

        int compare = Math.Min(decoded.Count, payload.Length);
        int errors = 0;
        for (int k = 0; k < compare; k++)
        {
            if (decoded[k] != payload[k])
            {
                errors++;
            }
        }

        Console.WriteLine($"decoded {decoded.Count} bits, compared {compare} of {payload.Length}");
        Console.WriteLine(compare == 0
            ? "no payload bits recovered"
            : $"bit errors {errors}  BER {(double)errors / compare:E2}" +
              (errors == 0 ? "   *** BIT-EXACT ***" : ""));
        Console.WriteLine($"burst end: {reason?.ToString() ?? "(none)"}");
        Console.WriteLine(result.WavPath);
        return errors == 0 && compare == payload.Length ? 0 : 1;
    }

    /// <summary>Writes interleaved I,Q as an int16 stereo WAV, the capture format.</summary>
    private static void WriteIqWav(string path, float[] iq, int rate)
    {
        var pcm = new byte[iq.Length * 2];
        for (int k = 0; k < iq.Length; k++)
        {
            short v = (short)Math.Clamp(Math.Round(iq[k] * 32767.0), short.MinValue, short.MaxValue);
            pcm[2 * k] = (byte)(v & 0xFF);
            pcm[(2 * k) + 1] = (byte)((v >> 8) & 0xFF);
        }

        using var writer = new PcmWavWriter(path, rate);
        writer.Write(pcm);
    }

    public static int Measure(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("in"))
        {
            Console.Error.WriteLine("""
                sm-ota measure --in <iq.wav> [--tone-hz <Hz>] [--tone2-hz <Hz>]
                               [--offset-hz <Hz>] [--fft <2^n>]

                Levels, averaged spectrum, tone frequency/level/SNR, image rejection, carrier
                leakage, largest spur, and (with --tone2-hz) intermodulation products.

                --tone-hz/--tone2-hz are the transmitted offsets from the waveform centre.
                --offset-hz is where the capture is tuned relative to that centre, so the
                expected tone position in the capture is (tone-hz − offset-hz). When the
                receiver is tuned to the tone itself (the sm-ota tone default), pass the same
                value for both, or leave --offset-hz equal to --tone-hz.
                """);
            return a is null ? 2 : 0;
        }

        if (a.Has("purity"))
        {
            CarrierPurity(a.Req("in"), a.Dbl("tone-hz", 2000), a.Dbl("span-hz", 2000),
                a.Int("fft", 32768), a.Dbl("from", 0), a.Dbl("length", 0));
            return 0;
        }

        if (a.Has("survey"))
        {
            SurveyBand(a.Req("in"), a.Dbl("slot-khz", 2.0), a.Dbl("centre-hz", 0), a.Int("fft", 8192));
            return 0;
        }

        double toneHz = a.Dbl("tone-hz", 2000);
        double[] tones = a.Has("tone2-hz") ? [toneHz, a.Dbl("tone2-hz", 0)] : [toneHz];
        Analyse(a.Req("in"), tones, a.Dbl("offset-hz", toneHz), a.Int("fft", 8192));
        return 0;
    }

    /// <summary>
    /// Lists the strongest components close to a carrier, masking only the carrier itself.
    /// </summary>
    /// <remarks>The ordinary spur search masks a guard band either side of the tone so that
    /// the carrier's own skirt is not reported as a spur - which also makes it blind to
    /// close-in sidebands, the very things that make a carrier sound rough. A human listening
    /// to the transmission heard rasp that the spur figure did not show, so this exists to
    /// look where that measurement deliberately does not.</remarks>
    private static void CarrierPurity(
        string wavPath, double toneHz, double spanHz, int fftSize,
        double fromSeconds = 0, double lengthSeconds = 0)
    {
        (float[] iFull, int rate) = WavFile.ReadMono(wavPath, channel: 0);
        (float[] qFull, _) = WavFile.ReadMono(wavPath, channel: 1);

        // A time window, so a capture spanning a deliberate change - a cable pulled, a supply
        // swapped - can be compared against itself rather than against a separate run.
        ReadOnlySpan<float> i = iFull;
        ReadOnlySpan<float> q = qFull;
        if (lengthSeconds > 0)
        {
            int start = Math.Clamp((int)(fromSeconds * rate), 0, iFull.Length - 1);
            int count = Math.Min((int)(lengthSeconds * rate), iFull.Length - start);
            i = iFull.AsSpan(start, count);
            q = qFull.AsSpan(start, count);
            Console.WriteLine($"window {fromSeconds:F1}-{fromSeconds + (count / (double)rate):F1} s");
        }

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, rate, fftSize);

        // Reference against the carrier's own PEAK BIN, not a lobe-summed tone power: the
        // components being listed are read as single bins, and mixing the two normalisations
        // produces positive dBc, which is obvious nonsense.
        int carrierBin = -1;
        double carrierBinPower = 0;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            if (f < toneHz - 500 || f > toneHz + 500)
            {
                continue;
            }

            if (spectrum.BinPower(k) > carrierBinPower)
            {
                carrierBinPower = spectrum.BinPower(k);
                carrierBin = k;
            }
        }

        if (carrierBin < 0)
        {
            // No bin within ±500 Hz of where the carrier was said to be. Continuing would take
            // FrequencyOfBin(-1) and report a purity figure referenced to nothing.
            throw new ArgumentException(
                $"no spectrum within ±500 Hz of {toneHz:F0} Hz - check --tone-hz against the "
                + "capture's tune frequency, and that the window contains the transmission");
        }

        double carrierHz = spectrum.FrequencyOfBin(carrierBin);
        double carrierPower = spectrum.TonePower(carrierHz);
        Console.WriteLine();
        Console.WriteLine($"=== carrier purity: {Path.GetFileName(wavPath)} ===");
        Console.WriteLine($"carrier {carrierHz:F2} Hz, {IqAnalysis.Db(carrierPower):F1} dBFS, " +
                          $"bin {spectrum.BinWidthHz:F3} Hz, {spectrum.Segments} segments");
        Console.WriteLine($"{"offset Hz",12} {"dBc",8}   (masking only ±25 Hz of the carrier)");

        var found = new List<(double Offset, double Dbc)>();
        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            double offset = f - carrierHz;
            if (Math.Abs(offset) > spanHz || Math.Abs(offset) < 25)
            {
                continue;
            }

            found.Add((offset, IqAnalysis.Db(spectrum.BinPower(k)) - IqAnalysis.Db(carrierBinPower)));
        }

        foreach ((double offset, double dbc) in found.OrderByDescending(x => x.Dbc).Take(20))
        {
            Console.WriteLine($"{offset,12:F1} {dbc,8:F1}");
        }
    }

    /// <summary>Band occupancy across the capture span, for choosing an operating frequency
    /// that is measurably clear rather than nominally clear.</summary>
    private static void SurveyBand(string wavPath, double slotKhz, double centreHz, int fftSize)
    {
        (float[] i, int rate) = WavFile.ReadMono(wavPath, channel: 0);
        (float[] q, _) = WavFile.ReadMono(wavPath, channel: 1);
        IqSpectrum spectrum = IqAnalysis.Welch(i, q, rate, fftSize);

        Console.WriteLine();
        Console.WriteLine($"=== band survey: {Path.GetFileName(wavPath)}, {slotKhz:F1} kHz slots ===");
        Console.WriteLine($"{"offset kHz",12} {"absolute",14}  {"mean dBFS",10} {"peak dBFS",10}");

        IReadOnlyList<(double CentreHz, double MeanPower, double PeakPower)> slots =
            IqAnalysis.Survey(spectrum, slotKhz * 1000.0);
        double quietest = double.PositiveInfinity;
        double quietestHz = 0;
        foreach ((double hz, double mean, double peak) in slots)
        {
            double meanDb = IqAnalysis.Db(mean / spectrum.BinWidthHz * 1.0);
            string absolute = centreHz > 0
                ? $"{(centreHz + hz) / 1e6:F4} MHz"
                : "";
            Console.WriteLine($"{hz / 1000.0,12:F1} {absolute,14}  {IqAnalysis.Db(mean),10:F1} {IqAnalysis.Db(peak),10:F1}");
            if (IqAnalysis.Db(peak) < quietest)
            {
                quietest = IqAnalysis.Db(peak);
                quietestHz = hz;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"quietest slot by peak: {quietestHz / 1000.0:F1} kHz offset " +
                          (centreHz > 0 ? $"= {(centreHz + quietestHz) / 1e6:F4} MHz " : "") +
                          $"({quietest:F1} dBFS peak)");
    }

    private static bool IsTransmitterMeter(FlexMeterDescriptor d) =>
        d.Source.StartsWith("TX", StringComparison.OrdinalIgnoreCase)
        || d.Source.StartsWith("RAD", StringComparison.OrdinalIgnoreCase);

    private static void Analyse(
        string wavPath, double[] transmittedTones, double expectedOffsetHz, int fftSize = 8192,
        (DateTime Sample0, DateTime Start, DateTime End)? window = null)
    {
        (float[] i, int rate) = WavFile.ReadMono(wavPath, channel: 0);
        (float[] q, _) = WavFile.ReadMono(wavPath, channel: 1);

        // Restrict to the burst under measurement. A capture also holds the pre-flight tone,
        // and averaging across both transmissions silently biases every level - it is what
        // made two equal-amplitude tones read 3.7 dB apart.
        string windowNote = "whole capture";
        if (window is (DateTime sample0, DateTime start, DateTime end))
        {
            double from = (start - sample0).TotalSeconds + 0.6;   // clear the ramp and PTT settle
            double length = (end - start).TotalSeconds - 1.0;
            int startSample = (int)Math.Round(from * rate);
            int count = (int)Math.Round(length * rate);
            if (startSample >= 0 && count > fftSize && startSample + count <= i.Length)
            {
                i = i[startSample..(startSample + count)];
                q = q[startSample..(startSample + count)];
                windowNote = $"burst window {from:F1}-{from + length:F1} s";
            }
        }

        // Where the tones should land in the capture's own baseband.
        var expected = new double[transmittedTones.Length];
        for (int t = 0; t < transmittedTones.Length; t++)
        {
            expected[t] = transmittedTones[t] - expectedOffsetHz;
        }

        IqLevels levels = IqAnalysis.Levels(i, q, rate);
        Console.WriteLine();
        Console.WriteLine($"=== {Path.GetFileName(wavPath)} - {i.Length} samples @ {rate} Hz " +
                          $"({i.Length / (double)rate:F2} s, {windowNote}) ===");
        Console.WriteLine($"peak {levels.PeakDbfs:F1} dBFS   rms {levels.RmsDbfs:F1} dBFS   " +
                          $"headroom {-levels.PeakDbfs:F1} dB");
        Console.WriteLine("per-second rms dBFS: " + string.Join(" ", levels.PerSecondRmsDbfs.Select(v => $"{v:F1}")));
        if (levels.PeakDbfs > -1.0)
        {
            Console.WriteLine("WARNING: within 1 dB of full scale - the receiver's ADC may be clipping. " +
                              "Reduce TX power before trusting any number here.");
        }

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, rate, fftSize);
        double noiseDensity = spectrum.NoiseVariance() / rate;
        Console.WriteLine($"spectrum: {spectrum.Segments} × {fftSize}-point, bin {spectrum.BinWidthHz:F2} Hz");
        Console.WriteLine($"noise floor: {IqAnalysis.Db(noiseDensity):F1} dBFS/Hz " +
                          $"({IqAnalysis.Db(spectrum.NoiseInBandwidth(3000)):F1} dBFS in 3 kHz)");
        Console.WriteLine();

        var mask = new List<double> { 0 };
        var measured = new List<double>();
        for (int t = 0; t < expected.Length; t++)
        {
            // Search ±500 Hz around the expected position: the combined Flex TX and
            // (undisciplined) receiver dial error is the thing being measured, so the tone
            // is not assumed to be exactly where it was commanded.
            (double hz, double power) = spectrum.FindPeak(expected[t] - 500, expected[t] + 500);
            double snr3k = IqAnalysis.Db(power) - IqAnalysis.Db(spectrum.NoiseInBandwidth(3000));
            Console.WriteLine($"tone {t + 1}: expected {expected[t]:F1} Hz, found {hz:F2} Hz " +
                              $"(error {hz - expected[t]:+0.00;-0.00} Hz)");
            Console.WriteLine($"        level {IqAnalysis.Db(power):F1} dBFS, SNR in 3 kHz {snr3k:F1} dB");

            double image = IqAnalysis.Db(spectrum.TonePower(-hz)) - IqAnalysis.Db(power);
            Console.WriteLine($"        image at {-hz:F1} Hz: {image:F1} dBc");
            measured.Add(hz);
            mask.Add(hz);
            mask.Add(-hz);
        }

        double carrier = IqAnalysis.Db(spectrum.TonePower(-expectedOffsetHz));
        double strongest = double.NegativeInfinity;
        for (int t = 0; t < expected.Length; t++)
        {
            strongest = Math.Max(strongest, IqAnalysis.Db(spectrum.TonePower(expected[t])));
        }

        Console.WriteLine($"carrier/LO leakage at the waveform centre ({-expectedOffsetHz:F0} Hz): " +
                          $"{carrier - strongest:F1} dBc");
        mask.Add(-expectedOffsetHz);

        (double spurHz, double spurPower) = IqAnalysis.LargestSpur(spectrum, mask, guardHz: 200);
        Console.WriteLine($"largest spur: {IqAnalysis.Db(spurPower) - strongest:F1} dBc at {spurHz:F1} Hz");

        if (expected.Length == 2)
        {
            Console.WriteLine();
            // Products must be computed from the MEASURED tone positions, not the commanded
            // ones: a dial error shifts both tones - and therefore every product - by the same
            // amount, so looking at 2f1−f2 of the nominal frequencies just samples noise.
            Console.WriteLine($"intermodulation about the measured tones " +
                              $"({measured[0]:F1} / {measured[1]:F1} Hz), dBc vs the stronger:");
            foreach ((string label, double hz, double dbc) in IqAnalysis.Intermod(spectrum, measured[0], measured[1]))
            {
                Console.WriteLine($"  {label,-9} {hz,9:F1} Hz  {dbc,7:F1} dBc");
            }
        }
    }


    /// <summary>
    /// Blocks until the receiver is actually recording, or explains why nothing will be keyed.
    /// </summary>
    /// <remarks>
    /// The harness used to wait a flat three seconds and then transmit regardless. When a receiver
    /// answered 503 the capture task faulted during that window and the transmitter keyed anyway -
    /// a transmission that occupied a frequency, spent the operator's licence and produced no
    /// measurement. Waiting on the capture's own readiness signal makes "we are recording" a
    /// precondition of key-down rather than an assumption about timing.
    /// </remarks>
    private static async Task AwaitReceiverAsync(UberSdrIqClient client, Task capture, string host)
    {
        Task ready = client.Recording;
        Task done = await Task.WhenAny(ready, capture, Task.Delay(TimeSpan.FromSeconds(30)));
        if (done == ready && ready.IsCompletedSuccessfully)
        {
            return;
        }

        // Surface the capture's own failure where there is one: it carries the receiver's reason.
        if (capture.IsFaulted)
        {
            Exception reason = capture.Exception!.GetBaseException();
            throw new InvalidOperationException(
                $"not transmitting: {host} is not recording - {reason.Message}", reason);
        }

        if (ready.IsFaulted)
        {
            Exception reason = ready.Exception!.GetBaseException();
            throw new InvalidOperationException(
                $"not transmitting: {host} is not recording - {reason.Message}", reason);
        }

        throw new InvalidOperationException(
            $"not transmitting: {host} did not start recording within 30 s. Nothing is listening, "
            + "so a transmission now would measure nothing.");
    }

    private static void Report(TransmitReport r, FlexIqTransmitter tx)
    {
        Console.WriteLine();
        Console.WriteLine("=== transmit report ===");
        // Two different durations, and conflating them reads as a truncated transmission. The
        // key→unkey window is how long WE fed the radio: Drain returns once the radio has taken
        // the samples, not once it has radiated them, so on a burst that fits the ring it can be
        // tens of milliseconds. The signal's own length is what went on the air - measured at
        // 2.02 s on air for a burst whose feed window read 0.03 s (2026-08-03 §E4 session).
        Console.WriteLine(
            $"signal {r.Samples / (double)FlexIqTransmitter.SampleRate:F2} s on air; " +
            $"fed {r.KeyUtc:HH:mm:ss.fff} → {r.UnkeyUtc:HH:mm:ss.fff} ({r.Duration.TotalSeconds:F2} s - " +
            "the feed window, not the transmission)");
        Console.WriteLine($"{r.Samples} complex samples, {r.PacketsReflected} buffers reflected, " +
                          $"{r.SamplesStarved} starved, drained={r.Drained}");
        // Forward power and SWR are reported separately because they have different
        // preconditions. SWR compares forward against reflected, so it needs a constant
        // envelope - on a modulated burst the two samples describe different instants and the
        // ratio is meaningless. Peak forward power has no such requirement: it is the highest
        // instantaneous reading while keyed, and on a data burst it is exactly the number an
        // operator needs, because the constant-envelope pre-flight tone reads the drive level
        // rather than what the modulated waveform actually puts on the air. Printing it only
        // alongside a valid SWR threw away the measurement on every burst.
        Console.WriteLine(r.PeakForwardDbm is null
            ? "forward power: no FWDPWR samples arrived while keyed"
            : $"peak forward {r.PeakForwardDbm:F1} dBm " +
              $"({FlexMeters.DbmToWatts(r.PeakForwardDbm ?? 0):F1} W)");
        Console.WriteLine(r.PeakSwr is null
            ? "SWR: not evaluated (needs a constant envelope - see the pre-flight tone)"
            : $"peak SWR {r.PeakSwr:F2}");

        // Which meters actually streamed while keyed - the diagnostic that distinguishes
        // "the radio never sent transmit meters" from "we failed to decode them".
        Console.WriteLine($"meter packets this session: {tx.Meters.PacketsReceived} " +
                          $"({tx.Meters.UnknownIdSamples} for unknown ids); " +
                          $"{r.Meters.Count} samples arrived while keyed");
        foreach (IGrouping<string, FlexMeterReading> g in r.Meters.GroupBy(m => m.Descriptor.Name))
        {
            FlexMeterReading last = g.Last();
            Console.WriteLine($"  {g.Key,-10} {g.Count(),4} samples  last raw {last.Raw,7} " +
                              $"→ {last.Value,10:F3} {last.Descriptor.Unit}");
        }

        // Raw per-id arrival counts, un-interpreted: separates "the radio never sent this
        // meter" from "we decoded or labelled it wrongly".
        Console.WriteLine("ids seen (raw, id→count; * = no descriptor):");
        var seen = tx.Meters.SamplesById;
        Console.WriteLine("  " + string.Join("  ", seen.OrderBy(kv => kv.Key).Select(kv =>
            $"{kv.Key}{(tx.Meters.Descriptors.ContainsKey(kv.Key) ? "" : "*")}:{kv.Value}")));
        string missing = string.Join(", ", tx.Meters.Descriptors.Values
            .Where(d => d.Fps > 0 && !seen.ContainsKey(d.Id))
            .OrderBy(d => d.Id)
            .Select(d => $"{d.Name}(id {d.Id}, {d.Fps:F0} fps)"));
        if (missing.Length > 0)
        {
            Console.WriteLine($"described at >0 fps but NEVER sent: {missing}");
        }

        Console.WriteLine("interlock / transmit state transitions:");
        foreach (string line in tx.StateLog)
        {
            Console.WriteLine($"  {line}");
        }

        if (r.Aborted)
        {
            Console.WriteLine($"ABORTED: {r.AbortReason}");
        }
    }

    private static void Log(string message) =>
        Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
}

/// <summary>Minimal <c>--key value</c> / <c>--flag</c> parser.</summary>
internal sealed class Args : Dictionary<string, string>
{
    public static Args? Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unexpected argument: {argv[i]}");
                return null;
            }

            string key = argv[i][2..];
            a[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? argv[++i]
                : "true";
        }

        return a;
    }

    public bool Has(string k) => ContainsKey(k);

    public string Req(string k) =>
        TryGetValue(k, out string? v) ? v : throw new ArgumentException($"--{k} is required");

    public string Str(string k, string? d) => TryGetValue(k, out string? v) ? v : d!;

    public int Int(string k, int d) =>
        TryGetValue(k, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;

    public double Dbl(string k, double d) =>
        TryGetValue(k, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : d;
}
