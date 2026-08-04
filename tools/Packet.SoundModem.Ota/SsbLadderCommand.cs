using System.Globalization;
using M0LTE.Flex;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota ladder --mode afsk300|bpsk1200|qpsk600|…</c> - the §E2 pass for the SSB audio-carrier
/// modes: the sim baseline's own channel injected at the transmitter, sent through real hardware on
/// the DAX audio route, and scored with the mode's own demodulator against the same frames.
/// </summary>
/// <remarks>
/// <para>This is the SSB audio-carrier sibling of <see cref="LadderCommand"/> and
/// <see cref="OfdmLadderCommand"/>. These modes place their carrier - a BPSK/QPSK carrier or an AFSK
/// tone-pair - <em>inside</em> the audio, so they can go out two ways, both single-sideband:
/// (<c>qpsk3600</c> is an FM mode and is not one of these; see <see cref="SsbLadderPass.IsSsbMode"/>.)</para>
/// <list type="bullet">
/// <item><b><c>--route iq</c> (the default)</b> - the audio is up-converted to single-sideband IQ in
/// software (<see cref="Ms110dIqUpconverter"/>, a fixed <c>--offset-hz</c> above the dial) and
/// transmitted through a headless waveform via <see cref="FlexIqTransmitter"/>, <em>bypassing DIGU and
/// its ALC</em>. This is the coverage/waterfall default because measured on the rig the DAX/DIGU route
/// arrives ~5&#160;dB weaker at the receiver (DIGU conversion loss + ALC shaving) - only ~13&#160;dB
/// headroom over the RSP1 floor - so the self-calibrating noise at the higher-SNR rungs lands down near
/// the floor and the delivered SNR reads low. The IQ route delivers the carrier at the full waveform
/// level (~21&#160;dB headroom), so the self-cal works. The SSB modes ARE single-sideband, so software
/// SSB is faithful to the on-air signal.</item>
/// <item><b><c>--route dax</c></b> - real audio through the radio's own DIGU SSB modulator, the modem's
/// actual deployment path. Kept because it is deployment-realistic and it characterises the DIGU/ALC
/// cost, but its lower headroom makes the self-cal unreliable, so it is not the default.</item>
/// </list>
/// <para>Rendering drives the mode through the <see cref="Packet.SoundModem.Modems.IModem"/> seam
/// (<see cref="SsbLadderPass"/>) and scoring drives the same seam (<see cref="SsbBurstScorer"/>);
/// nothing here touches the MS110D or datac paths.</para>
/// <para><c>--dry-run</c> renders the whole pass, lays it out as an IQ capture would hold it, and scores
/// it offline - render → channel → SSB → IQ → back to audio → decode, everything except the radio - so
/// the chain is proved before any power is applied. The rehearsal reports the delivered SNR the scorer
/// measures beside the SNR the rig was asked to inject; if the two disagree the whole ladder is
/// mis-plotted, so that agreement is the thing the rehearsal exists to show.</para>
/// </remarks>
internal static class SsbLadderCommand
{
    /// <summary>True for the SSB audio-carrier modes this command handles.</summary>
    public static bool IsSsbMode(string? mode) => SsbLadderPass.IsSsbMode(mode);

    public static async Task<int> RunAsync(Args a)
    {
        if (a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota ladder --mode afsk300|bpsk1200|qpsk600|... --snr <a,b,c> [options]

                The SSB audio-carrier §E2 ladder. Renders a frame per rung on the mode's audio carrier
                (AFSK tone-pair or BPSK/QPSK), injects the sim baseline's channel at a known SNR,
                transmits it single-sideband (IQ by default, or DAX/DIGU), captures it, and scores it
                with the mode's own demodulator - per-burst decode (frame-error rate), acquisition
                (carrier detect), CFO, FEC-corrected bytes, and the delivered SNR measured against each
                burst's own noise lead-in.

                Ladder:
                  --mode <ssb audio mode> afsk300|afsk300-il2p|afsk300-il2pc|bpsk1200|qpsk600
                                          (and bpsk300|qpsk2400 as a check). NOT qpsk3600 - that is an
                                          FM mode, handled by the FM coverage path
                  --snr <a,b,c>           SNR rungs in dB, 3 kHz reference bandwidth
                  --repeats <n>           passes over the rung list (default 1); rungs interleaved
                  --channel awgn|good|poor  default awgn (the sim baseline's rig)
                  --frame-bytes <n>       AX.25 frame size (default 60)
                  --seed <n>              first payload seed (default 1)

                Rehearsal (no radio):
                  --dry-run               render the pass, lay it out as a capture, and score it
                  --out <iq.wav>          where to write the simulated capture
                  --rate <Hz>             render/simulated-capture rate (default 48000)

                Live:
                  --route iq|dax          transmit route (default iq). iq up-converts the audio to
                                          single-sideband IQ in software and transmits it through a
                                          headless waveform, bypassing DIGU's ALC - ~21 dB headroom
                                          over the RSP1 floor, so the self-cal works; it is the
                                          coverage/waterfall default. dax is real audio through the
                                          radio's DIGU SSB modulator - deployment-realistic and it
                                          characterises the DIGU/ALC cost, but ~5 dB lossier (~13 dB
                                          headroom), so the delivered SNR reads low
                  --offset-hz <Hz>        iq route: carrier offset above the dial (default 2000), to
                                          clear baseband DC. The dax route places it at 0
                  --rf-power <0-100>      REQUIRED to transmit - no default, by design
                  --max-watts <W>         abort any burst whose forward power exceeds this (default
                                          15 W with --capture rsp, the RSP1 rig's ceiling)
                  --audio-amplitude <a>   dax route only: audio drive into DIGU, 0..1 (default 0.9).
                                          Lower backs off the ALC; a sweep isolates ALC cost
                  --radio <ip|discover>   default discover
                  --freq <MHz>            waveform slice centre (default 18.106500)
                  --antenna <port>        default ANT1; --capture rsp defaults it to ANT2
                  --gap <s>               quiet between transmissions (default 3)
                  --capture rsp           capture on the RSP1/studybox SDR and score. RSP1 options:
                    --rsp-host <h>          default studybox
                    --rsp-freq <Hz>         RX tune (default: --freq centre + --dial-correction)
                    --rsp-rate <Hz>         complex sample rate (default 96000)
                    --rsp-gain <str>        rx_sdr -g string (default AGC=false,IFGR=20,RFGR=0)
                    --rsp-ssh-key <path>    ssh identity (default ~/.ssh/id_ed25519)
                  --dial-correction <Hz>  RE-MEASURE THIS EVERY SESSION (see the handover)
                  --out-dir <dir>         where the capture and manifest land

                Each rung carries its own noise lead-in on the air, so the receiver measures the SNR
                actually delivered rather than the one requested - the same self-calibrating method the
                MS110D and datac ladders use.
                """);
            return 0;
        }

        string mode = a.Req("mode");
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        SimChannelKind channel = SimChannel.Parse(a.Str("channel", "awgn"));
        int repeats = a.Int("repeats", 1);
        int frameBytes = a.Int("frame-bytes", 60);
        int firstSeed = a.Int("seed", 1);
        bool dryRun = a.Has("dry-run");

        // The IQ route is the default (see the class remarks: it clears the DIGU/ALC headroom loss that
        // makes the DAX self-cal read low). --route dax opts into the deployment audio path.
        var route = a.Str("route", "iq").StartsWith('d') ? LadderRoute.Dax : LadderRoute.Iq;

        // OffsetHz is a property of the route, not a free knob. The IQ route applies a software NCO
        // offset (default 2000 Hz) so the occupied band clears baseband DC in its software SSB; the DAX
        // route applies none - the radio's DIGU modulator places the audio carrier directly, already
        // clear of DC - so its effective offset is 0. This one value flows to the SSB up-converter
        // (which renders both the live IQ and the dry-run's simulated capture) AND to the manifest,
        // whose OffsetHz the scorer uses as the down-shift that lands the carrier back at its audio
        // centre. Getting it wrong misplaces every burst by the offset, so it is derived from the route.
        double offsetHz = route == LadderRoute.Dax ? 0.0 : a.Dbl("offset-hz", 2000);

        // Rehearsal renders the simulated capture at the capture rate; a live pass renders/transmits at
        // the waveform rate (both live routes work at that rate - the IQ is rendered there and the DAX
        // audio is resampled to it).
        int rate = a.Int("rate", dryRun ? 48000 : FlexIqTransmitter.SampleRate);
        if (!dryRun && rate != FlexIqTransmitter.SampleRate)
        {
            throw new ArgumentException(
                $"a live pass must render at the waveform rate ({FlexIqTransmitter.SampleRate} Hz); "
                + $"--rate {rate} is for --dry-run");
        }

        // Plan validates the mode (fail fast on a non-SSB mode, including the FM qpsk3600).
        IReadOnlyList<SsbLadderPoint> plan = SsbLadderPass.Plan(
            mode, snrs, repeats, channel, firstSeed, frameBytes);
        int nativeRate = SsbLadderPass.NativeRateFor(mode);

        // Render the SSB-IQ for the rehearsal (which lays it out as a capture) and for the live IQ
        // route (which transmits it); skip it on the live DAX route, which transmits the audio and
        // would only throw the IQ away - the up-conversion is the dominant render cost.
        var pass = new SsbLadderPass(new SsbLadderPassOptions
        {
            OutputRate = rate,
            OffsetHz = offsetHz,
            RenderIq = dryRun || route == LadderRoute.Iq,
            AudioAmplitude = a.Dbl("audio-amplitude", 0.9),
        });

        Console.Error.WriteLine(
            $"ssb ladder: {mode} {channel}, {snrs.Length} rung(s) × {repeats} = {plan.Count} bursts, "
            + $"route {(route == LadderRoute.Iq ? "iq" : "dax")}");
        IReadOnlyList<SsbRenderedPoint> rendered = pass.Render(plan);
        Console.Error.WriteLine(
            $"rendered at {nativeRate} Hz, pass audio gain {pass.AudioGain:G4}, IQ gain {pass.Gain:G4}, "
            + $"burst {rendered[0].BurstSeconds:F2} s");

        return dryRun
            ? DryRun(a, mode, channel, nativeRate, rendered, rate, offsetHz, route, pass)
            : await LiveAsync(a, mode, channel, nativeRate, rendered, offsetHz, route, pass)
                .ConfigureAwait(false);
    }

    /// <summary>Lays the pass out as an IQ capture would hold it, writes the manifest, and scores it
    /// offline - the whole chain minus the radio.</summary>
    private static int DryRun(
        Args a, string mode, SimChannelKind channel, int nativeRate,
        IReadOnlyList<SsbRenderedPoint> rendered, int rate, double offsetHz, LadderRoute route,
        SsbLadderPass pass)
    {
        // The rehearsal always lays the pre-rendered SSB-IQ out as a capture, whichever live route the
        // pass targets - the offset (and so the pass gain recorded) is the route's, so a DAX rehearsal
        // places the carrier where a real DAX capture would and an IQ rehearsal where a real IQ one
        // would.
        float passGain = route == LadderRoute.Dax ? pass.AudioGain : pass.Gain;
        string outPath = a.Str("out", "ssb-ladder-dryrun.wav");
        double gap = a.Dbl("gap", 3);
        int gapFrames = (int)(gap * rate);
        var random = new Random(1);
        var burstStarts = new List<double>();
        long frames = 0;

        using (var writer = new PcmWavWriter(outPath, rate, channels: 2))
        {
            // A real capture is never digital silence between transmissions, and a scorer that has
            // only ever seen silence in the gaps has not been tested on anything real.
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
            foreach (SsbRenderedPoint point in rendered)
            {
                // The active burst starts after its noise lead-in - that is the time the scorer
                // windows on and measures the delivered SNR from.
                double startSeconds = (frames / (double)rate) + point.LeadInSeconds;
                burstStarts.Add(startSeconds);
                writer.WriteSamples(point.Iq);
                frames += point.Iq.Length / 2;
                Quiet(gapFrames);
            }
        }

        SsbCampaignManifest manifest = BuildManifest(
            a, mode, channel, rendered, burstStarts, rate, offsetHz, passGain,
            radio: "none (rehearsal)", rfPower: null, capturePath: Path.GetFileName(outPath),
            captureSha256: CampaignFiles.Sha256(outPath), sample0Utc: null, receiverHost: null,
            dialCorrectionHz: 0);
        string manifestPath = Path.ChangeExtension(outPath, ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {outPath}: {frames} frames, {frames / (double)rate:F1} s");
        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        // Score the rehearsal inline: the whole point is to see the frames come back and the measured
        // SNR track the request before any power is applied.
        SsbCaptureScore score = SsbBurstScorer.FromCapture(outPath, offsetHz, nativeRate).Score(manifest);
        Report(outPath, mode, manifest, score);
        Console.WriteLine(outPath);
        return 0;
    }

    private static async Task<int> LiveAsync(
        Args a, string mode, SimChannelKind channel, int nativeRate,
        IReadOnlyList<SsbRenderedPoint> rendered, double offsetHz, LadderRoute route, SsbLadderPass pass)
    {
        if (!a.Has("rf-power"))
        {
            throw new ArgumentException(
                "--rf-power is required to transmit (there is no default, by design). "
                + "Add --dry-run to rehearse the pass without a radio.");
        }

        // The pass gain that actually reaches the radio: the audio gain on the DAX route, the IQ gain
        // on the waveform route (already baked into each point's IQ).
        float passGain = route == LadderRoute.Dax ? pass.AudioGain : pass.Gain;

        bool captureRsp = string.Equals(a.Str("capture", ""), "rsp", StringComparison.OrdinalIgnoreCase);

        // The RSP1 rig's transmit ceiling: the commanded rfpower LEVEL is capped and the measured-watts
        // abort cuts a burst the instant FWDPWR exceeds --max-watts (the real guard). The PSK carriers
        // have a higher PAPR than a single tone, so keep the audio drive below the ALC knee and take
        // output power from rfpower rather than from drive.
        double? maxWatts = captureRsp || a.Has("max-watts") ? a.Dbl("max-watts", 15.0) : null;

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "discover"),
            FrequencyMHz = a.Str("freq", "18.106500"),
            Antenna = a.Str("antenna", captureRsp ? "ANT2" : "ANT1"),
            RfPower = a.Int("rf-power", 0),
            RfPowerCeiling = captureRsp ? 20 : 30,
            MaxForwardWatts = maxWatts,
            IdMode = mode.ToUpperInvariant(),
        };

        void Log(string m) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");

        Log($"transmit antenna: {options.Antenna}"
            + (captureRsp && !a.Has("antenna") ? "  (defaulted to ANT2 for the RSP1 capture rig)" : ""));
        if (maxWatts is double mw)
        {
            Log($"transmit power ceiling: {mw:F1} W measured (rfpower level capped at {options.RfPowerCeiling})");
        }

        Log(route == LadderRoute.Iq
            ? $"route: iq (bench instrument) - software SSB at +{offsetHz:F0} Hz, bypassing DIGU/ALC"
            : "route: dax (deployment audio path) - audio through the radio's DIGU SSB modulator");
        double gap = a.Dbl("gap", 3);

        // The route selector picks the transmitter; everything downstream - capture, timing, manifest,
        // scoring - is route-independent and runs against the IOtaTransmitter seam.
        await using FlexClient client = await FlexClient.ConnectAsync(options.Radio);
        await using IOtaTransmitter tx = route == LadderRoute.Dax
            ? (IOtaTransmitter)await FlexDaxTransmitter.AttachAsync(client, options, Log)
            : await FlexIqTransmitter.AttachAsync(client, options, Log);

        // One capture spans the whole pass so every burst is scored from one timebase. Only the RSP1
        // backend is wired for the SSB ladder (the off-air rig); without it the pass transmits and
        // nothing is scored.
        double captureSeconds = rendered.Sum(
            p => (p.LeadInSeconds + p.BurstSeconds + 1.0) + gap) + 30;
        double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
        Task<CaptureResult>? capturing = null;
        using var cts = new CancellationTokenSource();
        if (captureRsp)
        {
            var rspOpt = new RspCaptureOptions
            {
                Host = a.Str("rsp-host", "studybox"),
                SshKeyPath = LadderCommand.ExpandUser(a.Str("rsp-ssh-key", "~/.ssh/id_ed25519")),
                FrequencyHz = a.Int("rsp-freq", (int)Math.Round(centreHz + a.Dbl("dial-correction", 0))),
                SampleRate = a.Int("rsp-rate", 96000),
                Gain = a.Str("rsp-gain", RspIqClient.DefaultGain),
                Name = $"ssb-{mode}",
                OutputDir = a.Str("out-dir", "."),
                DurationSeconds = (int)Math.Ceiling(captureSeconds),
            };
            Log($"capture: RSP1 on {rspOpt.Host} at {rspOpt.FrequencyHz} Hz, {rspOpt.SampleRate} S/s "
                + $"for {rspOpt.DurationSeconds} s");
            capturing = new RspIqClient(m => Log($"[rsp] {m}")).CaptureAsync(rspOpt, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }

        await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
        await tx.PreflightAsync().ConfigureAwait(false);

        var keyed = new List<DateTime>();
        foreach (SsbRenderedPoint point in rendered)
        {
            await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
            Log($"{mode} {point.Point.SnrDb:+0.0;-0.0;0} dB {point.Point.Channel} seed {point.Point.Seed}");

            // The only route-specific step: the IQ route sends the pre-scaled complex baseband as
            // rendered; the DAX route resamples the point's natural-scale native-rate audio to the DAX
            // rate and applies the one pass audio gain here (post-resample, so the level the radio sees
            // is the pass constant - the same last-moment policy the MS110D and datac DAX routes use).
            float[] payload = route == LadderRoute.Dax
                ? LadderCommand.ScaleInPlace(
                    LadderCommand.Resample(point.Audio, nativeRate, FlexDaxTransmitter.SampleRate),
                    pass.AudioGain)
                : point.Iq;
            TransmitReport report = await tx.TransmitAsync(payload).ConfigureAwait(false);
            keyed.Add(report.KeyUtc);
            if (report.Aborted)
            {
                Log($"ABORTED: {report.AbortReason} - stopping the pass");
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(gap)).ConfigureAwait(false);
        }

        if (capturing is null)
        {
            Log("no --capture rsp: transmitted only, nothing scored");
            return 0;
        }

        CaptureResult result = await capturing.ConfigureAwait(false);
        Log($"capture: {result.WavPath}");

        // Burst positions come from what actually happened: key-up time + the transmitter's PTT-ramp
        // silence lead-in + the burst's own noise lead-in lands us on the active burst.
        var burstStarts = new List<double>();
        for (int k = 0; k < keyed.Count; k++)
        {
            burstStarts.Add((keyed[k] - result.Sample0Utc).TotalSeconds
                            + options.LeadInSeconds + rendered[k].LeadInSeconds);
        }

        SsbCampaignManifest manifest = BuildManifest(
            a, mode, channel, rendered.Take(keyed.Count).ToList(), burstStarts, result.SampleRate,
            offsetHz, passGain, radio: options.Radio, rfPower: options.RfPower,
            capturePath: Path.GetFileName(result.WavPath), captureSha256: result.WavSha256,
            sample0Utc: result.Sample0Utc, receiverHost: a.Str("rsp-host", "studybox"),
            dialCorrectionHz: a.Dbl("dial-correction", 0));
        string manifestPath = Path.Combine(
            a.Str("out-dir", "."), Path.GetFileNameWithoutExtension(result.WavPath) + ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        SsbCaptureScore score = SsbBurstScorer.FromCapture(result.WavPath, offsetHz, nativeRate).Score(manifest);
        Report(result.WavPath, mode, manifest, score);
        return 0;
    }

    private static SsbCampaignManifest BuildManifest(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<SsbRenderedPoint> rendered,
        IReadOnlyList<double> burstStarts, int captureRate, double offsetHz, float passGain,
        string radio, int? rfPower, string? capturePath, string? captureSha256,
        DateTimeOffset? sample0Utc, string? receiverHost, double dialCorrectionHz)
    {
        var bursts = new List<SsbCampaignBurst>(rendered.Count);
        for (int k = 0; k < rendered.Count; k++)
        {
            SsbRenderedPoint p = rendered[k];
            bursts.Add(new SsbCampaignBurst(
                p.Point.Mode, p.Point.Seed, p.Point.FrameBytes, p.Point.SnrDb, p.Point.Channel,
                burstStarts[k], p.BurstSeconds));
        }

        return new SsbCampaignManifest(
            Name: a.Str("name", $"ssb-{mode}-{channel}".ToLowerInvariant()),
            Mode: mode,
            OffsetHz: offsetHz,
            CaptureRate: captureRate,
            Bursts: bursts,
            ModemRevision: CampaignFiles.ModemRevision(),
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: radio,
            FrequencyMHz: a.Str("freq", "18.106500"),
            RfPower: rfPower,
            PassGain: passGain,
            DialCorrectionHz: dialCorrectionHz,
            CapturePath: capturePath,
            CaptureSha256: captureSha256,
            CaptureSample0Utc: sample0Utc,
            ReceiverHost: receiverHost,
            Notes: a.Str("notes", null));
    }

    private static void Report(
        string path, string mode, SsbCampaignManifest manifest, SsbCaptureScore score)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ssb score: {Path.GetFileName(path)} - {score.AudioSeconds:F1} s, {mode} "
                          + $"({manifest.Bursts.Count} burst(s), modem {manifest.ModemRevision}) ===");
        Console.WriteLine($"{"#",3} {"start s",8} {"asked",7} {"got",7} {"d(dB)",6} {"CFO Hz",8} "
                          + $"{"acq",4} {"decode",7} {"corr B",7}");

        int acquired = 0;
        int decoded = 0;
        double snrErrSum = 0;
        int snrErrCount = 0;
        foreach (SsbBurstScore b in score.Bursts)
        {
            string got = b.Snr is null ? "-" : b.Snr.SnrDb.ToString("F1", CultureInfo.InvariantCulture);
            string delta = b.Snr is null ? "-"
                : (b.Snr.SnrDb - b.AskedSnrDb).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            if (b.Snr is not null)
            {
                snrErrSum += Math.Abs(b.Snr.SnrDb - b.AskedSnrDb);
                snrErrCount++;
            }

            acquired += b.Acquired ? 1 : 0;
            decoded += b.Decoded ? 1 : 0;
            string cfo = double.IsNaN(b.CfoHz) ? "-" : b.CfoHz.ToString("F1", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{b.Index,3} {b.StartSeconds,8:F2} {b.AskedSnrDb,7:F1} {got,7} {delta,6} {cfo,8} "
                + $"{(b.Acquired ? "yes" : "NO"),4} {(b.Decoded ? "ok" : "LOST"),7} {b.CorrectedBytes,7}");
        }

        Console.WriteLine();
        Console.WriteLine($"acquired {acquired}/{score.Bursts.Count}, decoded {decoded}/{score.Bursts.Count} "
                          + $"(FER {1.0 - (decoded / (double)Math.Max(1, score.Bursts.Count)):0.000})");
        if (snrErrCount > 0)
        {
            Console.WriteLine($"mean |measured − asked| SNR: {snrErrSum / snrErrCount:F2} dB "
                              + "(the self-calibration check - the ladder is only plotted correctly if this is small)");
        }

        if (manifest.CapturePath is { } cap)
        {
            Console.WriteLine($"capture: {cap}");
        }
    }
}
