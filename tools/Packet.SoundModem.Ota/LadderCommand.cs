using System.Globalization;
using M0LTE.Dsp;
using M0LTE.Flex;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>Which transmit route a live ladder pass drives.</summary>
internal enum LadderRoute
{
    /// <summary>DAX audio through the radio's own DIGU SSB modulator — the modem's real
    /// deployment path, and the ladder's default. See <see cref="FlexDaxTransmitter"/>.</summary>
    Dax,

    /// <summary>Complex IQ through a headless waveform — the bench/instrument path that bypasses
    /// the radio's SSB modulator, ALC and TX DSP. See <see cref="FlexIqTransmitter"/>.</summary>
    Iq,
}

/// <summary>
/// <c>sm-ota ladder</c> — the §E2 pass: the mask suite's own channel injected at the
/// transmitter, sent through real hardware, and scored against the same reference bits.
/// </summary>
/// <remarks>
/// <para><c>--dry-run</c> renders the whole pass to an IQ file without touching a radio, which is
/// how the chain is proved before any power is applied. The rendered file is the real transmit
/// path's output, so scoring it with <c>sm-ota score</c> exercises everything except the radio
/// itself.</para>
/// <para>Live, each point is transmitted as one keyed burst carrying its own noise lead-in, and
/// the burst's position in the capture is recorded from the actual key-up time rather than from
/// the plan — the schedule is what we asked for, the manifest is what happened.</para>
/// </remarks>
internal static class LadderCommand
{
    public static async Task<int> RunAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota ladder --wn <n> --snr <a,b,c> [--repeats 1] [options]

                Ladder:
                  --wn <n>              waveform number
                  --snr <a,b,c>         SNR rungs in dB, 3 kHz reference bandwidth
                  --repeats <n>         passes over the rung list (default 1). Rungs are
                                        interleaved, so a pass cut short still covers the ladder
                  --channel awgn|poor   default awgn (D-LXIV); poor is D.6.1 / ITU-R F.1487
                  --interleaver s|l     default short
                  --blocks <n>          interleaver blocks per burst (default 1)
                  --seed <n>            first payload seed (default 1)

                Rehearsal (no radio):
                  --dry-run             render the pass and write it, transmitting nothing
                  --out <iq.wav>        where to write it
                  --rate <Hz>           render rate (default 48000 — the capture rate, so the
                                        result can be fed straight to `sm-ota score`)

                Live:
                  --route dax|iq        transmit route (default dax). dax is the modem's real
                                        deployment path: real audio through the radio's own DIGU
                                        SSB modulator, so the radio does what the software
                                        up-converter does on the iq route. iq is the bench
                                        instrument: software single-sideband IQ through a headless
                                        waveform, bypassing the radio's SSB modulator/ALC/TX DSP
                  --rf-power <0-100>    REQUIRED to transmit — no default, by design
                  --radio <ip|discover> default discover
                  --freq <MHz>          waveform slice centre (default 18.106500)
                  --offset-hz <Hz>      carrier offset from the centre (default 2000; iq route)
                  --gap <s>             quiet between transmissions (default 3)
                  --capture-host <h>    capture the pass on an UberSDR and score it
                  --dial-correction <Hz>  RE-MEASURE THIS EVERY SESSION (see the handover)
                  --out-dir <dir>       where captures and the CSV land

                Every point carries its own noise lead-in on the air, so the receiver measures
                the SNR actually delivered rather than the one requested. That is what makes a
                rung's position on the comparison curve a measurement instead of a claim.
                """);
            return a is null ? 2 : 0;
        }

        int wn = a.Int("wn", 6);
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var channel = a.Str("channel", "awgn").StartsWith('p') ? LadderChannel.Poor : LadderChannel.Awgn;
        var interleaver = a.Str("interleaver", "short").StartsWith('l')
            ? Ms110dInterleaverKind.Long
            : Ms110dInterleaverKind.Short;

        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(
            wn, snrs, a.Int("repeats", 1), channel, interleaver, a.Int("blocks", 1), a.Int("seed", 1));

        double offsetHz = a.Dbl("offset-hz", 2000);
        bool dryRun = a.Has("dry-run");
        int rate = a.Int("rate", dryRun ? 48000 : FlexIqTransmitter.SampleRate);

        var pass = new LadderPass(new LadderPassOptions
        {
            OutputRate = rate,
            OffsetHz = offsetHz,
            PreambleSuperframes = a.Int("preamble", 3),
            TxSsbLowHz = a.Dbl("tx-ssb-low", 150),
            TxSsbHighHz = a.Dbl("tx-ssb-high", 3450),
        });

        var route = a.Str("route", "dax").StartsWith('i') ? LadderRoute.Iq : LadderRoute.Dax;

        Console.Error.WriteLine(
            $"ladder: WN{wn} {channel}, {snrs.Length} rung(s) × {a.Int("repeats", 1)} = {plan.Count} bursts");
        IReadOnlyList<RenderedPoint> rendered = pass.Render(plan);
        Console.Error.WriteLine(
            $"rendered at {rate} Hz, pass IQ gain {pass.Gain:G4}, audio gain {pass.AudioGain:G4}");

        // The schedule is the request; the manifest is the record. Both are written for every
        // pass, rehearsal included, because a rehearsal whose settings cannot be reproduced is
        // not a rehearsal of anything.
        var schedule = new CampaignSchedule(
            Name: a.Str("name", $"wn{wn}-{channel}".ToLowerInvariant()),
            OffsetHz: offsetHz,
            Bursts: [.. plan.Select(p => new CampaignBurst(
                p.WaveformNumber, p.Interleaver, p.Blocks, p.Seed, p.SnrDb, p.Channel,
                ConstraintLength: 7, PreambleSuperframes: a.Int("preamble", 3)))],
            Notes: a.Str("notes", null));

        return dryRun
            ? DryRun(a, rendered, rate, pass.Gain, schedule)
            : await LiveAsync(a, rendered, rate, pass, route, schedule).ConfigureAwait(false);
    }

    /// <summary>Writes the pass as one IQ file laid out the way a capture would hold it.</summary>
    private static int DryRun(
        Args a, IReadOnlyList<RenderedPoint> rendered, int rate, float gain,
        CampaignSchedule schedule)
    {
        string outPath = a.Str("out", "ladder-dryrun.wav");
        double gap = a.Dbl("gap", 3);
        int gapFrames = (int)(gap * rate);
        var random = new Random(1);
        var starts = new List<double>();
        long frames = 0;

        using (var writer = new PcmWavWriter(outPath, rate, channels: 2))
        {
            // A real capture is never digital silence between transmissions, and a scorer that
            // has only ever seen silence in the gaps has not been tested on anything real.
            void Quiet(int count)
            {
                var block = new float[Math.Min(count, 1 << 16) * 2];
                int left = count;
                while (left > 0)
                {
                    int n = Math.Min(left, block.Length / 2);
                    for (int k = 0; k < n * 2; k++)
                    {
                        block[k] = (float)((random.NextDouble() - 0.5) * 0.002);
                    }

                    writer.WriteSamples(block.AsSpan(0, n * 2));
                    left -= n;
                }

                frames += count;
            }

            Quiet(gapFrames);
            foreach (RenderedPoint point in rendered)
            {
                starts.Add(frames / (double)rate);
                writer.WriteSamples(point.Iq);
                frames += point.Iq.Length / 2;
                Quiet(gapFrames);
            }
        }

        // The burst itself starts after its noise lead-in — that is the time the scorer matches.
        var burstTimes = rendered.Select((p, k) => starts[k] + p.LeadInSeconds).ToArray();
        string manifestPath = Path.ChangeExtension(outPath, ".manifest.json");
        CampaignFiles.Save(manifestPath, new CampaignManifest(
            Schedule: schedule,
            ModemRevision: CampaignFiles.ModemRevision(),
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: "none (rehearsal)",
            FrequencyMHz: a.Str("freq", "18.106500"),
            RfPower: null,
            PassGain: gain,
            DialCorrectionHz: 0,
            CapturePath: Path.GetFileName(outPath),
            CaptureSha256: CampaignFiles.Sha256(outPath),
            BurstStartSeconds: burstTimes,
            SupplyNote: a.Str("supply", null)));

        Console.Error.WriteLine($"wrote {outPath}: {frames} frames, {frames / (double)rate:F1} s");
        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"score it with:  sm-ota score --in {outPath} --schedule {manifestPath}");
        Console.WriteLine(outPath);
        return 0;
    }

    private static async Task<int> LiveAsync(
        Args a, IReadOnlyList<RenderedPoint> rendered, int rate,
        LadderPass pass, LadderRoute route, CampaignSchedule schedule)
    {
        if (!a.Has("rf-power"))
        {
            throw new ArgumentException(
                "--rf-power is required to transmit (there is no default, by design). "
                + "Add --dry-run to rehearse the pass without a radio.");
        }

        // Both live routes work at 24 kHz — the waveform IQ rate and the reduced-bandwidth DAX
        // rate are the same — so this guard holds for either. The DAX route resamples each point's
        // 9600 Hz audio to that rate itself (below); the IQ was rendered there.
        if (rate != FlexIqTransmitter.SampleRate)
        {
            throw new ArgumentException(
                $"a live pass must render at the waveform rate ({FlexIqTransmitter.SampleRate} Hz); "
                + $"--rate {rate} is for --dry-run");
        }

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "discover"),
            FrequencyMHz = a.Str("freq", "18.106500"),
            Antenna = a.Str("antenna", "ANT1"),
            RfPower = a.Int("rf-power", 0),
            IdMode = "MS110D",
        };

        void Log(string m) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");

        double gap = a.Dbl("gap", 3);

        // The route selector picks the transmitter; everything downstream — capture, timing,
        // manifest — is route-independent and runs against the IOtaTransmitter seam. The DAX route
        // is the modem's deployment path and the default; the IQ route is the bench instrument.
        Log($"route: {(route == LadderRoute.Dax ? "dax (deployment audio path)" : "iq (bench instrument)")}");
        await using FlexClient client = await FlexClient.ConnectAsync(options.Radio);
        await using IOtaTransmitter tx = route == LadderRoute.Dax
            ? (IOtaTransmitter)await FlexDaxTransmitter.AttachAsync(client, options, Log)
            : await FlexIqTransmitter.AttachAsync(client, options, Log);

        // Capture the whole pass in one session, so every burst is scored from one timebase.
        double captureSeconds = rendered.Sum(p => (p.Iq.Length / 2.0 / rate) + gap) + 30;
        UberSdrIqClient? capture = null;
        Task<CaptureResult>? capturing = null;
        using var cts = new CancellationTokenSource();
        if (a.Str("capture-host", null) is { } host)
        {
            double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
            var capOpt = new UberSdrCaptureOptions
            {
                Host = host,
                Port = a.Int("capture-port", 443),
                FrequencyHz = (int)Math.Round(centreHz + a.Dbl("dial-correction", 0)),
                Name = $"ladder-wn{rendered[0].Point.WaveformNumber}",
                OutputDir = a.Str("out-dir", "."),
                DurationSeconds = (int)Math.Ceiling(captureSeconds),
            };
            capture = new UberSdrIqClient(m => Log($"[rx] {m}"));
            capturing = capture.CaptureAsync(capOpt, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
        await tx.PreflightAsync().ConfigureAwait(false);

        var keyed = new List<DateTime>();
        foreach (RenderedPoint point in rendered)
        {
            // The identification interval is checked between bursts, never mid-burst: an
            // identification owed during a pass belongs in a gap.
            await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
            Log($"WN{point.Point.WaveformNumber} {point.Point.SnrDb:+0.0;-0.0;0} dB "
                + $"{point.Point.Channel} seed {point.Point.Seed}");

            // The only route-specific step: the IQ route sends the pre-scaled complex baseband as
            // rendered; the DAX route sends real audio, so it resamples the point's natural-scale
            // 9600 Hz audio to the DAX rate and applies the one pass audio gain here (post-resample,
            // so the level the radio sees is the pass constant — see LadderPass.AudioGain).
            float[] payload = route == LadderRoute.Dax
                ? ScaleInPlace(
                    Resample(point.Audio, LadderPass.Ms110dAudioRate, FlexDaxTransmitter.SampleRate),
                    pass.AudioGain)
                : point.Iq;
            TransmitReport report = await tx.TransmitAsync(payload).ConfigureAwait(false);
            keyed.Add(report.KeyUtc);
            if (report.Aborted)
            {
                Log($"ABORTED: {report.AbortReason} — stopping the pass");
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(gap)).ConfigureAwait(false);
        }

        if (capturing is null)
        {
            Log("no --capture-host: transmitted only, nothing scored");
            return 0;
        }

        CaptureResult result = await capturing.ConfigureAwait(false);
        Log($"capture: {result.WavPath}");

        // Burst positions come from what actually happened, not from the plan: the schedule is
        // the request and the key-up times are the record.
        var burstStarts = new List<double>();
        for (int k = 0; k < keyed.Count; k++)
        {
            // Measured against the capture's own first sample, not against the plan: the
            // schedule is the request and the key-up times are the record.
            burstStarts.Add((keyed[k] - result.Sample0Utc).TotalSeconds
                            + rendered[k].LeadInSeconds); // the burst follows its noise lead-in
        }

        string manifestPath = Path.Combine(
            a.Str("out-dir", "."), Path.GetFileNameWithoutExtension(result.WavPath) + ".manifest.json");
        CampaignFiles.Save(manifestPath, new CampaignManifest(
            Schedule: schedule with { Bursts = [.. schedule.Bursts.Take(keyed.Count)] },
            ModemRevision: CampaignFiles.ModemRevision(),
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: options.Radio,
            FrequencyMHz: options.FrequencyMHz,
            RfPower: options.RfPower,
            // The pass level that actually reached the radio: the audio gain on the DAX route, the
            // IQ gain on the waveform route.
            PassGain: route == LadderRoute.Dax ? pass.AudioGain : pass.Gain,
            DialCorrectionHz: a.Dbl("dial-correction", 0),
            CapturePath: Path.GetFileName(result.WavPath),
            CaptureSha256: result.WavSha256,
            CaptureSample0Utc: result.Sample0Utc,
            ReceiverHost: a.Str("capture-host", null),
            BurstStartSeconds: [.. burstStarts],
            SupplyNote: a.Str("supply", null)));

        Console.Error.WriteLine();
        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");
        Console.Error.WriteLine(
            $"score it with:  sm-ota score --in {result.WavPath} --schedule {manifestPath}");
        return 0;
    }

    /// <summary>
    /// Resamples real audio through the same integer path <see cref="Ms110dIqUpconverter"/> uses —
    /// ×5 to the 48 kHz intermediate, then ÷2 — for the DAX route. Just the resample: no bandpass
    /// and no NCO, because the radio's DIGU modulator does the sideband placement the up-converter
    /// does in software on the IQ route.
    /// </summary>
    private static float[] Resample(float[] audio, int fromRate, int toRate)
    {
        // 401 taps, matching the up-converter's resampler so the two routes' band-limiting agrees.
        const int taps = 401;
        int intermediate = toRate * 2; // 48000 for the 24 kHz DAX rate
        if (intermediate % fromRate != 0)
        {
            throw new ArgumentException(
                $"no integer ×N/÷2 path from {fromRate} Hz to {toRate} Hz", nameof(fromRate));
        }

        var upsampler = new Upsampler(intermediate, intermediate / fromRate, taps);
        var mid = new float[upsampler.OutputLength(audio.Length)];
        upsampler.Process(audio, mid);

        var decimator = new Decimator(intermediate, 2, taps);
        var outBuf = new float[decimator.MaxOutput(mid.Length)];
        int n = decimator.Process(mid, outBuf);
        return n == outBuf.Length ? outBuf : outBuf[..n];
    }

    /// <summary>Scales a buffer in place and returns it — the pass audio gain applied to the
    /// resampled DAX audio at transmit time.</summary>
    private static float[] ScaleInPlace(float[] samples, float gain)
    {
        for (int k = 0; k < samples.Length; k++)
        {
            samples[k] *= gain;
        }

        return samples;
    }
}
