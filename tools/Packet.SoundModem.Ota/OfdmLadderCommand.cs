using System.Globalization;
using M0LTE.Flex;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota ladder --mode freedv-datac*</c> - the §E2 pass for the FreeDV datac OFDM modes: the sim
/// baseline's own channel injected at the transmitter, sent through real hardware on the DAX audio
/// route, and scored with the OFDM demodulator against the same datac packets.
/// </summary>
/// <remarks>
/// <para>This is the OFDM sibling of <see cref="LadderCommand"/>. The datac waveform is real audio, so
/// its natural transmit route is DAX through the radio's own DIGU SSB modulator - the same route the
/// MS110D ladder uses by default and the one the modem deploys on - so this command is DAX-only (there
/// is no software-IQ leg for it). Rendering drives the datac engine (<see cref="OfdmLadderPass"/>) and
/// scoring drives the datac receiver (<see cref="OfdmBurstScorer"/>); nothing here touches the MS110D
/// modulator or scorer.</para>
/// <para><c>--dry-run</c> renders the whole pass, lays it out as an IQ capture would hold it, and scores
/// it offline - render → channel → SSB → IQ → back to audio → decode, everything except the radio - so
/// the chain is proved before any power is applied. The rehearsal reports the delivered SNR the scorer
/// measures beside the SNR the rig was asked to inject; if the two disagree the whole ladder is
/// mis-plotted, so that agreement is the thing the rehearsal exists to show.</para>
/// </remarks>
internal static class OfdmLadderCommand
{
    /// <summary>The DAX route places audio f at dial+f directly (carrier suppressed at the dial), so
    /// the datac band sits at its native ~1500 Hz audio centre and the offset is 0 - the value flows
    /// both to the SSB placement of the rehearsal's simulated capture and to the scorer's down-shift.</summary>
    private const double DaxOffsetHz = 0.0;

    public static async Task<int> RunAsync(Args a)
    {
        if (a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota ladder --mode freedv-datac<N> --snr <a,b,c> [options]

                The FreeDV datac OFDM §E2 ladder. Renders a datac packet per rung, injects the sim
                baseline's channel at a known SNR, transmits it on the DAX (DIGU) route, captures it,
                and scores it with the OFDM demodulator - per-burst CRC, post-LDPC coded BER, LDPC
                margin, CFO, acquisition, and the delivered SNR measured against each burst's own
                noise lead-in.

                Ladder:
                  --mode freedv-datac<N>  datac0|datac1|datac3|datac4|datac13|datac14
                  --snr <a,b,c>           SNR rungs in dB, 3 kHz reference bandwidth
                  --repeats <n>           passes over the rung list (default 1); rungs interleaved
                  --channel awgn|good|poor  default awgn (the sim baseline's rig)
                  --seed <n>              first payload seed (default 1)

                Rehearsal (no radio):
                  --dry-run               render the pass, lay it out as a capture, and score it
                  --out <iq.wav>          where to write the simulated capture
                  --rate <Hz>             render/simulated-capture rate (default 48000)

                Live (DAX route - audio through the radio's DIGU SSB modulator):
                  --rf-power <0-100>      REQUIRED to transmit - no default, by design
                  --max-watts <W>         abort any burst whose forward power exceeds this (default
                                          15 W with --capture rsp, the RSP1 rig's ceiling)
                  --audio-amplitude <a>   DAX audio drive into DIGU, 0..1 (default 0.9)
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
                MS110D ladder uses.
                """);
            return 0;
        }

        string mode = a.Req("mode");
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        SimChannelKind channel = SimChannel.Parse(a.Str("channel", "awgn"));
        int repeats = a.Int("repeats", 1);
        int firstSeed = a.Int("seed", 1);
        bool dryRun = a.Has("dry-run");
        int rate = a.Int("rate", 48000); // the simulated-capture rate; only consumed by --dry-run

        // Plan validates the mode (fail fast on an unknown datac mode).
        IReadOnlyList<OfdmLadderPoint> plan = OfdmLadderPass.Plan(mode, snrs, repeats, channel, firstSeed);

        // Render the simulated-capture IQ only for the rehearsal; a live pass transmits the
        // natural-scale audio and would only throw the IQ away, so skip the SSB up-conversion there.
        var pass = new OfdmLadderPass(new OfdmLadderPassOptions
        {
            OutputRate = rate,
            OffsetHz = DaxOffsetHz,
            RenderIq = dryRun,
            AudioAmplitude = a.Dbl("audio-amplitude", 0.9),
        });

        Console.Error.WriteLine(
            $"ofdm ladder: {mode} {channel}, {snrs.Length} rung(s) × {repeats} = {plan.Count} bursts");
        IReadOnlyList<OfdmRenderedPoint> rendered = pass.Render(plan);
        Console.Error.WriteLine(
            $"rendered at 8000 Hz, pass audio gain {pass.AudioGain:G4}, burst {rendered[0].BurstSeconds:F2} s");

        return dryRun
            ? DryRun(a, mode, channel, rendered, rate, pass.AudioGain)
            : await LiveAsync(a, mode, channel, rendered, pass.AudioGain).ConfigureAwait(false);
    }

    /// <summary>Lays the pass out as an IQ capture would hold it, writes the manifest, and scores it
    /// offline - the whole chain minus the radio.</summary>
    private static int DryRun(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<OfdmRenderedPoint> rendered,
        int rate, float audioGain)
    {
        string outPath = a.Str("out", "ofdm-ladder-dryrun.wav");
        double gap = a.Dbl("gap", 3);
        string name = a.Str("name",
            $"ofdm-{mode.Replace("freedv-", "", StringComparison.OrdinalIgnoreCase)}-{channel}".ToLowerInvariant());
        string freqMHz = a.Str("freq", "18.106500");
        string? notes = a.Str("notes", null);

        // Every flag the rehearsal path reads has now been touched - reject anything left over
        // before a single frame is written, not after the file already holds the wrong experiment.
        a.RejectUnknown("ladder");

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
            foreach (OfdmRenderedPoint point in rendered)
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

        OfdmCampaignManifest manifest = BuildManifest(
            name, mode, channel, rendered, burstStarts, rate, audioGain, freqMHz, notes,
            radio: "none (rehearsal)", rfPower: null, capturePath: Path.GetFileName(outPath),
            captureSha256: CampaignFiles.Sha256(outPath), sample0Utc: null, receiverHost: null,
            dialCorrectionHz: 0);
        string manifestPath = Path.ChangeExtension(outPath, ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {outPath}: {frames} frames, {frames / (double)rate:F1} s");
        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        // Score the rehearsal inline: the whole point is to see the bits come back and the measured
        // SNR track the request before any power is applied.
        OfdmCaptureScore score = OfdmBurstScorer.FromCapture(outPath, DaxOffsetHz).Score(manifest);
        Report(outPath, mode, manifest, score);
        Console.WriteLine(outPath);
        return 0;
    }

    private static async Task<int> LiveAsync(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<OfdmRenderedPoint> rendered,
        float audioGain)
    {
        if (!a.Has("rf-power"))
        {
            throw new ArgumentException(
                "--rf-power is required to transmit (there is no default, by design). "
                + "Add --dry-run to rehearse the pass without a radio.");
        }

        bool captureRsp = string.Equals(a.Str("capture", ""), "rsp", StringComparison.OrdinalIgnoreCase);

        // The RSP1 rig's transmit ceiling: the commanded rfpower LEVEL is capped and the measured-watts
        // abort cuts a burst the instant FWDPWR exceeds --max-watts (the real guard). datac is a
        // high-PAPR OFDM waveform, so keep the audio drive below the ALC knee and take output power
        // from rfpower rather than from drive - over-driving compresses the OFDM peaks first.
        double? maxWatts = captureRsp || a.Has("max-watts") ? a.Dbl("max-watts", 15.0) : null;

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "discover"),
            FrequencyMHz = a.Str("freq", "18.106500"),
            Antenna = a.Str("antenna", captureRsp ? "ANT2" : "ANT1"),
            RfPower = a.Int("rf-power", 0),
            RfPowerCeiling = captureRsp ? 20 : 30,
            MaxForwardWatts = maxWatts,
            IdMode = mode.Replace("freedv-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant(),
        };

        bool antennaGiven = a.Has("antenna");
        double gap = a.Dbl("gap", 3);
        double dialCorrectionHz = a.Dbl("dial-correction", 0);
        string outDir = a.Str("out-dir", ".");
        string name = a.Str("name",
            $"ofdm-{mode.Replace("freedv-", "", StringComparison.OrdinalIgnoreCase)}-{channel}".ToLowerInvariant());
        string? notes = a.Str("notes", null);

        // Capture backend flags, read up front so a mistyped one is caught before the radio is
        // connected to and before any RF goes out, not after.
        string? rspHost = null;
        string? rspSshKey = null;
        int? rspFreqOverride = null;
        int rspRate = 96000;
        string rspGain = RspIqClient.DefaultGain;
        if (captureRsp)
        {
            rspHost = a.Str("rsp-host", "studybox");
            rspSshKey = LadderCommand.ExpandUser(a.Str("rsp-ssh-key", "~/.ssh/id_ed25519"));
            rspFreqOverride = a.Has("rsp-freq") ? a.Int("rsp-freq", 0) : null;
            rspRate = a.Int("rsp-rate", 96000);
            rspGain = a.Str("rsp-gain", RspIqClient.DefaultGain);
        }

        // Every flag this live path recognises has now been read - reject anything left over
        // before the radio is even connected to, so a mistyped flag cannot key a transmitter
        // into the wrong experiment.
        a.RejectUnknown("ladder");

        void Log(string m) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");

        Log($"transmit antenna: {options.Antenna}"
            + (captureRsp && !antennaGiven ? "  (defaulted to ANT2 for the RSP1 capture rig)" : ""));
        if (maxWatts is double mw)
        {
            Log($"transmit power ceiling: {mw:F1} W measured (rfpower level capped at {options.RfPowerCeiling})");
        }

        Log("route: dax (deployment audio path) - the datac waveform's natural route");

        await using FlexClient client = await FlexClient.ConnectAsync(options.Radio);
        await using var tx = await FlexDaxTransmitter.AttachAsync(client, options, Log);

        // One capture spans the whole pass so every burst is scored from one timebase. Only the RSP1
        // backend is wired for the OFDM ladder (the datac on-air rig); without it the pass transmits
        // and nothing is scored.
        double captureSeconds = rendered.Sum(
            p => (p.LeadInSeconds + p.BurstSeconds + 1.0) + gap) + 30;
        double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
        Task<CaptureResult>? capturing = null;
        using var cts = new CancellationTokenSource();
        if (captureRsp)
        {
            var rspOpt = new RspCaptureOptions
            {
                Host = rspHost!,
                SshKeyPath = rspSshKey!,
                FrequencyHz = rspFreqOverride ?? (int)Math.Round(centreHz + dialCorrectionHz),
                SampleRate = rspRate,
                Gain = rspGain,
                Name = $"ofdm-{mode.Replace("freedv-", "", StringComparison.OrdinalIgnoreCase)}",
                OutputDir = outDir,
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
        foreach (OfdmRenderedPoint point in rendered)
        {
            await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
            Log($"{mode} {point.Point.SnrDb:+0.0;-0.0;0} dB {point.Point.Channel} seed {point.Point.Seed}");

            // Resample the native 8 kHz audio (channel + noise lead-in included) to the DAX rate and
            // apply the one pass audio gain here - the same last-moment level policy the MS110D DAX
            // route uses (LadderCommand), so signal power is a pass constant and only noise varies.
            float[] payload = LadderCommand.ScaleInPlace(
                LadderCommand.Resample(point.Audio, OfdmLadderPass.NativeRate, FlexDaxTransmitter.SampleRate),
                audioGain);
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
        // silence lead-in + the burst's own noise lead-in lands us on the active burst. (The scorer
        // windows on this, so it carries the transmitter lead-in the MS110D scorer's acquisition
        // absorbs for free.)
        var burstStarts = new List<double>();
        for (int k = 0; k < keyed.Count; k++)
        {
            burstStarts.Add((keyed[k] - result.Sample0Utc).TotalSeconds
                            + options.LeadInSeconds + rendered[k].LeadInSeconds);
        }

        OfdmCampaignManifest manifest = BuildManifest(
            name, mode, channel, rendered.Take(keyed.Count).ToList(), burstStarts, result.SampleRate,
            audioGain, options.FrequencyMHz, notes, radio: options.Radio, rfPower: options.RfPower,
            capturePath: Path.GetFileName(result.WavPath), captureSha256: result.WavSha256,
            sample0Utc: result.Sample0Utc, receiverHost: rspHost,
            dialCorrectionHz: dialCorrectionHz);
        string manifestPath = Path.Combine(
            outDir, Path.GetFileNameWithoutExtension(result.WavPath) + ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        OfdmCaptureScore score = OfdmBurstScorer.FromCapture(result.WavPath, DaxOffsetHz).Score(manifest);
        Report(result.WavPath, mode, manifest, score);
        return 0;
    }

    private static OfdmCampaignManifest BuildManifest(
        string name, string mode, SimChannelKind channel, IReadOnlyList<OfdmRenderedPoint> rendered,
        IReadOnlyList<double> burstStarts, int captureRate, float audioGain, string freqMHz, string? notes,
        string radio, int? rfPower, string? capturePath, string? captureSha256,
        DateTimeOffset? sample0Utc, string? receiverHost, double dialCorrectionHz)
    {
        var bursts = new List<OfdmCampaignBurst>(rendered.Count);
        for (int k = 0; k < rendered.Count; k++)
        {
            OfdmRenderedPoint p = rendered[k];
            bursts.Add(new OfdmCampaignBurst(
                p.Point.Mode, p.Point.Seed, p.Point.SnrDb, p.Point.Channel,
                burstStarts[k], p.BurstSeconds));
        }

        return new OfdmCampaignManifest(
            Name: name,
            Mode: mode,
            OffsetHz: DaxOffsetHz,
            CaptureRate: captureRate,
            Bursts: bursts,
            ModemRevision: CampaignFiles.ModemRevision(),
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: radio,
            FrequencyMHz: freqMHz,
            RfPower: rfPower,
            PassAudioGain: audioGain,
            DialCorrectionHz: dialCorrectionHz,
            CapturePath: capturePath,
            CaptureSha256: captureSha256,
            CaptureSample0Utc: sample0Utc,
            ReceiverHost: receiverHost,
            Notes: notes);
    }

    private static void Report(
        string path, string mode, OfdmCampaignManifest manifest, OfdmCaptureScore score)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ofdm score: {Path.GetFileName(path)} - {score.AudioSeconds:F1} s, {mode} "
                          + $"({manifest.Bursts.Count} burst(s), modem {manifest.ModemRevision}) ===");
        Console.WriteLine($"{"#",3} {"start s",8} {"asked",7} {"got",7} {"d(dB)",6} {"CFO Hz",8} "
                          + $"{"acq",4} {"crc",4} {"codedBER",10} {"ldpc it/pc",11}");

        int decoded = 0;
        int crcOk = 0;
        double snrErrSum = 0;
        int snrErrCount = 0;
        foreach (OfdmBurstScore b in score.Bursts)
        {
            string got = b.Snr is null ? "-" : b.Snr.SnrDb.ToString("F1", CultureInfo.InvariantCulture);
            string delta = b.Snr is null ? "-"
                : (b.Snr.SnrDb - b.AskedSnrDb).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            if (b.Snr is not null)
            {
                snrErrSum += Math.Abs(b.Snr.SnrDb - b.AskedSnrDb);
                snrErrCount++;
            }

            decoded += b.Acquired ? 1 : 0;
            crcOk += b.CrcOk ? 1 : 0;
            Console.WriteLine(
                $"{b.Index,3} {b.StartSeconds,8:F2} {b.AskedSnrDb,7:F1} {got,7} {delta,6} {b.CfoHz,8:F1} "
                + $"{(b.Acquired ? "yes" : "NO"),4} {(b.CrcOk ? "ok" : "-"),4} "
                + $"{Rate(b.CodedBer),10} {$"{b.LdpcIterations}/{b.ParityChecks}",11}");
        }

        Console.WriteLine();
        Console.WriteLine($"acquired {decoded}/{score.Bursts.Count}, CRC-OK {crcOk}/{score.Bursts.Count}");
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

    private static string Rate(double value)
        => double.IsNaN(value) ? "-"
            : value == 0 ? "0"
            : value.ToString("0.00E+00", CultureInfo.InvariantCulture);
}
