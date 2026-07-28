using System.Globalization;
using M0LTE.Flex;
using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota ladder --mode &lt;fm-mode&gt;</c> — the §E2 pass for the FM-native modes (afsk1200,
/// fsk9600/4800, c4fsk, qpsk3600-over-FM): the sim baseline's own channel injected at the
/// transmitter, frequency-modulated at the mode's <b>target peak deviation</b>, sent through real
/// hardware on the DAX FM route, and scored with the mode's own demodulator behind an FM
/// discriminator. The FM sibling of <see cref="LadderCommand"/> / <see cref="OfdmLadderCommand"/>.
/// </summary>
/// <remarks>
/// <para><c>--dry-run</c> renders the pass, software-FM-modulates it at the target deviation, lays it
/// out as an IQ capture would hold it, FM-discriminates it back and scores it — render → channel →
/// FM-mod → FM-demod → decode, everything except the radio — so the chain is proved before any power
/// is applied. It reports the delivered SNR the scorer measures beside the SNR the rig was asked to
/// inject, and the achieved peak deviation beside the target.</para>
/// <para><b>Deviation calibration is a radio step.</b> The software modulator hits the target
/// deviation exactly, but the live radio's FM modulator turns audio drive into deviation by its own
/// gain, so before each mode the operator transmits, measures the achieved peak deviation
/// (<c>sm-ota fm-deviation</c>), and adjusts <c>--audio-amplitude</c> until it matches the target.</para>
/// </remarks>
internal static class FmLadderCommand
{
    // The DAX FM route places the modem's audio on the carrier at the dial with no offset, so the
    // discriminator's down-shift is 0 (the RX is tuned to the carrier).
    private const double DaxOffsetHz = 0.0;

    public static async Task<int> RunAsync(Args a)
    {
        if (a.Has("help"))
        {
            Console.Error.WriteLine("""
                sm-ota ladder --mode <fm-mode> --snr <a,b,c> [options]

                The FM §E2 ladder. Renders a frame per rung, injects the sim baseline's channel at a
                known SNR, frequency-modulates it at the mode's target peak deviation, transmits it on
                the DAX FM route, captures it, FM-discriminates it and scores it with the mode's own
                demodulator — per-burst frame decode, delivered SNR against each burst's noise lead-in,
                and the achieved peak deviation.

                FM modes and their target peak deviation (docs/mode-modulation-reference.md):
                  afsk1200 (+-fx25/-fx25rx/-il2p/-il2p-nocrc)  3.0 kHz
                  fsk9600 (+-il2p)                              2.4 kHz
                  fsk4800-il2p                                  1.2 kHz
                  c4fsk9600                                     2.5 kHz
                  c4fsk19200                                    5.0 kHz
                  qpsk3600  (QPSK audio over FM)                5.0 kHz

                Ladder:
                  --mode <fm-mode>        one of the modes above
                  --snr <a,b,c>           SNR rungs in dB, 3 kHz reference bandwidth
                  --repeats <n>           passes over the rung list (default 1); rungs interleaved
                  --channel awgn|good|poor  default awgn (the sim baseline's rig)
                  --frame-bytes <n>       AX.25 payload size (default 48)
                  --seed <n>              first payload seed (default 1)
                  --target-dev-khz <k>    override the mode's target peak deviation (default: table)

                Rehearsal (no radio):
                  --dry-run               render, FM-modulate, lay out as a capture, discriminate, score
                  --out <iq.wav>          where to write the simulated capture
                  --rate <Hz>             render/simulated-capture rate (default 48000)

                Live (DAX FM route — audio frequency-modulating the carrier):
                  --rf-power <0-100>      REQUIRED to transmit — no default, by design
                  --max-watts <W>         abort any burst whose forward power exceeds this (default
                                          15 W with --capture rsp, the RSP1 rig's ceiling)
                  --audio-amplitude <a>   DAX FM drive, 0..1 (default 0.9). THIS is the deviation
                                          knob: calibrate it so the achieved peak deviation = target
                  --slice-mode <FM|NFM>   radio slice mode for the FM carrier (default FM)
                  --radio <ip|discover>   default discover
                  --freq <MHz>            slice centre / dial (default 18.106500)
                  --antenna <port>        default ANT1; --capture rsp defaults it to ANT2
                  --gap <s>               quiet between transmissions (default 3)
                  --capture rsp           capture on the RSP1/studybox SDR and score. RSP1 options:
                    --rsp-host <h>          default studybox
                    --rsp-freq <Hz>         RX tune (default: --freq centre + --dial-correction)
                    --rsp-rate <Hz>         complex sample rate (default 48000 — the FM signal fits)
                    --rsp-gain <str>        rx_sdr -g string
                    --rsp-ssh-key <path>    ssh identity (default ~/.ssh/id_ed25519)
                  --dial-correction <Hz>  RE-MEASURE THIS EVERY SESSION (see the handover)
                  --out-dir <dir>         where the capture and manifest land

                Each rung carries its own noise lead-in on the air, so the receiver measures the SNR
                actually delivered — the same self-calibrating method the SSB and OFDM ladders use.
                """);
            return 0;
        }

        string mode = a.Req("mode");
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        SimChannelKind channel = SimChannel.Parse(a.Str("channel", "awgn"));
        int repeats = a.Int("repeats", 1);
        int firstSeed = a.Int("seed", 1);
        int frameBytes = a.Int("frame-bytes", 48);
        bool dryRun = a.Has("dry-run");
        int rate = a.Int("rate", 48000); // the simulated-capture rate; only consumed by --dry-run

        // Plan validates the mode (fail fast on a non-FM mode).
        IReadOnlyList<FmLadderPoint> plan = FmLadderPass.Plan(mode, snrs, repeats, channel, firstSeed);

        var pass = new FmLadderPass(new FmLadderPassOptions
        {
            OutputRate = rate,
            FrameBytes = frameBytes,
            TxDelayMs = a.Int("txdelay-ms", 150),
            RenderIq = dryRun,
            AudioAmplitude = a.Dbl("audio-amplitude", 0.9),
        });

        Console.Error.WriteLine(
            $"fm ladder: {mode} {channel}, {snrs.Length} rung(s) × {repeats} = {plan.Count} bursts");
        IReadOnlyList<FmRenderedPoint> rendered = pass.Render(plan);
        double targetDevHz = a.Has("target-dev-khz")
            ? a.Dbl("target-dev-khz", 0) * 1000.0
            : pass.TargetDeviationHz;
        Console.Error.WriteLine(
            $"rendered at DSP {pass.DspRate} Hz, target dev {targetDevHz / 1000:F1} kHz, "
            + $"DAX FM drive {pass.AudioGain:G4}, burst {rendered[0].BurstSeconds:F2} s");

        return dryRun
            ? DryRun(a, mode, channel, rendered, rate, pass, targetDevHz)
            : await LiveAsync(a, mode, channel, rendered, pass, targetDevHz).ConfigureAwait(false);
    }

    private static int DryRun(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<FmRenderedPoint> rendered,
        int rate, FmLadderPass pass, double targetDevHz)
    {
        string outPath = a.Str("out", "fm-ladder-dryrun.wav");
        double gap = a.Dbl("gap", 3);
        int gapFrames = (int)(gap * rate);
        var random = new Random(1);
        var burstStarts = new List<double>();
        long frames = 0;

        using (var writer = new PcmWavWriter(outPath, rate, channels: 2))
        {
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
            foreach (FmRenderedPoint point in rendered)
            {
                burstStarts.Add((frames / (double)rate) + point.LeadInSeconds);
                writer.WriteSamples(point.Iq);
                frames += point.Iq.Length / 2;
                Quiet(gapFrames);
            }
        }

        FmCampaignManifest manifest = BuildManifest(
            a, mode, channel, rendered, burstStarts, rate, pass, targetDevHz,
            radio: "none (rehearsal)", rfPower: null, capturePath: Path.GetFileName(outPath),
            captureSha256: CampaignFiles.Sha256(outPath), sample0Utc: null, receiverHost: null,
            measuredPeakDevHz: null, dialCorrectionHz: 0);
        string manifestPath = Path.ChangeExtension(outPath, ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {outPath}: {frames} frames, {frames / (double)rate:F1} s");
        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        FmCaptureScore score =
            FmBurstScorer.FromCapture(outPath, DaxOffsetHz, pass.DspRate, targetDevHz).Score(manifest);
        Report(outPath, mode, targetDevHz, manifest, score);
        Console.WriteLine(outPath);
        return 0;
    }

    private static async Task<int> LiveAsync(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<FmRenderedPoint> rendered,
        FmLadderPass pass, double targetDevHz)
    {
        if (!a.Has("rf-power"))
        {
            throw new ArgumentException(
                "--rf-power is required to transmit (there is no default, by design). "
                + "Add --dry-run to rehearse the pass without a radio.");
        }

        bool captureRsp = string.Equals(a.Str("capture", ""), "rsp", StringComparison.OrdinalIgnoreCase);
        double? maxWatts = captureRsp || a.Has("max-watts") ? a.Dbl("max-watts", 15.0) : null;

        var options = new FlexTransmitterOptions
        {
            Radio = a.Str("radio", "discover"),
            FrequencyMHz = a.Str("freq", "18.106500"),
            Antenna = a.Str("antenna", captureRsp ? "ANT2" : "ANT1"),
            RfPower = a.Int("rf-power", 0),
            RfPowerCeiling = captureRsp ? 20 : 30,
            MaxForwardWatts = maxWatts,
            SliceMode = a.Str("slice-mode", "FM"),
            IdMode = mode.ToUpperInvariant(),
        };

        void Log(string m) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");

        Log($"transmit antenna: {options.Antenna}"
            + (captureRsp && !a.Has("antenna") ? "  (defaulted to ANT2 for the RSP1 capture rig)" : ""));
        Log($"slice mode: {options.SliceMode} (FM carrier); DAX FM drive {pass.AudioGain:G4} — "
            + $"CALIBRATE this so the achieved peak deviation is {targetDevHz / 1000:F1} kHz "
            + "(sm-ota fm-deviation on a test burst)");
        if (maxWatts is double mw)
        {
            Log($"transmit power ceiling: {mw:F1} W measured (rfpower level capped at {options.RfPowerCeiling})");
        }

        double gap = a.Dbl("gap", 3);

        await using FlexClient client = await FlexClient.ConnectAsync(options.Radio);
        await using var tx = await FlexDaxTransmitter.AttachAsync(client, options, Log);

        double captureSeconds = rendered.Sum(p => p.LeadInSeconds + p.BurstSeconds + 1.0 + gap) + 30;
        double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
        Task<CaptureResult>? capturing = null;
        int captureRate = a.Int("rsp-rate", 48000);
        using var cts = new CancellationTokenSource();
        if (captureRsp)
        {
            var rspOpt = new RspCaptureOptions
            {
                Host = a.Str("rsp-host", "studybox"),
                SshKeyPath = LadderCommand.ExpandUser(a.Str("rsp-ssh-key", "~/.ssh/id_ed25519")),
                FrequencyHz = a.Int("rsp-freq", (int)Math.Round(centreHz + a.Dbl("dial-correction", 0))),
                SampleRate = captureRate,
                Gain = a.Str("rsp-gain", RspIqClient.DefaultGain),
                Name = $"fm-{mode}",
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
        foreach (FmRenderedPoint point in rendered)
        {
            await tx.EnsureIdentifiedAsync().ConfigureAwait(false);
            Log($"{mode} {point.Point.SnrDb:+0.0;-0.0;0} dB {point.Point.Channel} seed {point.Point.Seed}");

            // Resample the mode's DSP-rate audio (channel + noise lead-in included) to the DAX rate
            // and apply the one pass FM drive — the radio's FM modulator turns this into deviation.
            float[] payload = LadderCommand.ScaleInPlace(
                LadderCommand.Resample(point.Audio, point.DspRate, FlexDaxTransmitter.SampleRate),
                pass.AudioGain);
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
            Log("no --capture rsp: transmitted only, nothing scored");
            return 0;
        }

        CaptureResult result = await capturing.ConfigureAwait(false);
        Log($"capture: {result.WavPath}");

        var burstStarts = new List<double>();
        for (int k = 0; k < keyed.Count; k++)
        {
            burstStarts.Add((keyed[k] - result.Sample0Utc).TotalSeconds
                            + options.LeadInSeconds + rendered[k].LeadInSeconds);
        }

        FmCampaignManifest manifest = BuildManifest(
            a, mode, channel, rendered.Take(keyed.Count).ToList(), burstStarts, result.SampleRate,
            pass, targetDevHz, radio: options.Radio, rfPower: options.RfPower,
            capturePath: Path.GetFileName(result.WavPath), captureSha256: result.WavSha256,
            sample0Utc: result.Sample0Utc, receiverHost: a.Str("rsp-host", "studybox"),
            measuredPeakDevHz: a.Has("measured-dev-khz") ? a.Dbl("measured-dev-khz", 0) * 1000 : null,
            dialCorrectionHz: a.Dbl("dial-correction", 0));
        string manifestPath = Path.Combine(
            a.Str("out-dir", "."), Path.GetFileNameWithoutExtension(result.WavPath) + ".manifest.json");
        CampaignFiles.Save(manifestPath, manifest);

        Console.Error.WriteLine($"wrote {manifestPath} (modem {CampaignFiles.ModemRevision()})");

        FmCaptureScore score = FmBurstScorer
            .FromCapture(result.WavPath, DaxOffsetHz + a.Dbl("dial-correction", 0), pass.DspRate, targetDevHz)
            .Score(manifest);
        Report(result.WavPath, mode, targetDevHz, manifest, score);
        return 0;
    }

    private static FmCampaignManifest BuildManifest(
        Args a, string mode, SimChannelKind channel, IReadOnlyList<FmRenderedPoint> rendered,
        IReadOnlyList<double> burstStarts, int captureRate, FmLadderPass pass, double targetDevHz,
        string radio, int? rfPower, string? capturePath, string? captureSha256,
        DateTimeOffset? sample0Utc, string? receiverHost, double? measuredPeakDevHz, double dialCorrectionHz)
    {
        var bursts = new List<FmCampaignBurst>(rendered.Count);
        for (int k = 0; k < rendered.Count; k++)
        {
            FmRenderedPoint p = rendered[k];
            bursts.Add(new FmCampaignBurst(
                p.Point.Mode, p.Point.Seed, p.Point.SnrDb, p.Point.Channel,
                a.Int("frame-bytes", 48), burstStarts[k], p.BurstSeconds));
        }

        return new FmCampaignManifest(
            Name: a.Str("name", $"fm-{mode}-{channel}".ToLowerInvariant()),
            Mode: mode,
            DspRate: pass.DspRate,
            TargetDeviationHz: targetDevHz,
            OffsetHz: DaxOffsetHz,
            CaptureRate: captureRate,
            Bursts: bursts,
            ModemRevision: CampaignFiles.ModemRevision(),
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: radio,
            FrequencyMHz: a.Str("freq", "18.106500"),
            RfPower: rfPower,
            PassAudioGain: pass.AudioGain,
            MeasuredPeakDeviationHz: measuredPeakDevHz,
            DialCorrectionHz: dialCorrectionHz,
            CapturePath: capturePath,
            CaptureSha256: captureSha256,
            CaptureSample0Utc: sample0Utc,
            ReceiverHost: receiverHost,
            Notes: a.Str("notes", null));
    }

    private static void Report(
        string path, string mode, double targetDevHz, FmCampaignManifest manifest, FmCaptureScore score)
    {
        Console.WriteLine();
        Console.WriteLine($"=== fm score: {Path.GetFileName(path)} — {score.AudioSeconds:F1} s, {mode} "
                          + $"(target dev {targetDevHz / 1000:F1} kHz, {manifest.Bursts.Count} burst(s), "
                          + $"modem {manifest.ModemRevision}) ===");
        Console.WriteLine($"{"#",3} {"start s",8} {"asked",7} {"got",7} {"d(dB)",6} {"decode",7} "
                          + $"{"peakDev",8} {"frames",6}");

        int decoded = 0;
        double snrErrSum = 0;
        int snrErrCount = 0;
        foreach (FmBurstScore b in score.Bursts)
        {
            string got = b.Snr is null ? "—" : b.Snr.SnrDb.ToString("F1", CultureInfo.InvariantCulture);
            string delta = b.Snr is null ? "—"
                : (b.Snr.SnrDb - b.AskedSnrDb).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            if (b.Snr is not null)
            {
                snrErrSum += Math.Abs(b.Snr.SnrDb - b.AskedSnrDb);
                snrErrCount++;
            }

            decoded += b.Decoded ? 1 : 0;
            Console.WriteLine(
                $"{b.Index,3} {b.StartSeconds,8:F2} {b.AskedSnrDb,7:F1} {got,7} {delta,6} "
                + $"{(b.Decoded ? "ok" : "MISS"),7} {b.PeakDeviationHz / 1000,7:F2}k {b.FramesDecoded,6}");
        }

        Console.WriteLine();
        Console.WriteLine($"decoded {decoded}/{score.Bursts.Count}");
        if (snrErrCount > 0)
        {
            Console.WriteLine($"mean |measured − asked| SNR: {snrErrSum / snrErrCount:F2} dB "
                              + "(the self-calibration check — the ladder is only plotted correctly if this is small)");
        }

        if (manifest.CapturePath is { } cap)
        {
            Console.WriteLine($"capture: {cap}");
        }
    }

    /// <summary>
    /// <c>sm-ota fm-deviation</c> — measure the peak FM deviation of an IQ capture. The instrument
    /// the on-air calibration uses: transmit a burst, run this, and adjust the drive until the peak
    /// deviation matches the mode's target.
    /// </summary>
    public static int Deviation(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("in"))
        {
            Console.Error.WriteLine("""
                sm-ota fm-deviation --in <iq.wav> [options]

                FM-discriminates a 2-channel IQ capture and prints the peak / RMS / mean instantaneous-
                frequency deviation in kHz — the achieved deviation, for calibrating the drive to a
                mode's target (docs/mode-modulation-reference.md).

                  --in <iq.wav>       the capture (interleaved I,Q 16-bit stereo)
                  --from <s>          window start in seconds (default 0)
                  --length <s>        window length in seconds (default: to end). Restrict this to the
                                      keyed burst so guard-interval noise does not inflate the peak
                  --dial-hz <Hz>      carrier offset within the baseband to remove first (default 0)

                The mean is the residual carrier offset; a large mean means the capture is not tuned to
                the carrier — pass --dial-hz to null it (or re-tune), then re-measure.
                """);
            return a is null ? 2 : 0;
        }

        string path = a.Req("in");
        double dialHz = a.Dbl("dial-hz", 0);
        (float[] i, int rate) = WavFile.ReadMono(path, channel: 0);
        (float[] q, _) = WavFile.ReadMono(path, channel: 1);

        double from = a.Dbl("from", 0);
        double length = a.Dbl("length", 0);
        int start = Math.Clamp((int)(from * rate), 0, Math.Max(0, i.Length - 1));
        int count = length > 0
            ? Math.Min((int)(length * rate), i.Length - start)
            : i.Length - start;

        // Remove the carrier offset first, so the deviation is measured about the carrier rather than
        // about DC — the same NCO the discriminator applies.
        var id = new double[count];
        var qd = new double[count];
        double dPhi = -2.0 * Math.PI * dialHz / rate;
        double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
        double pr = 1.0, pi = 0.0;
        for (int k = 0; k < count; k++)
        {
            double ii = i[start + k], qq = q[start + k];
            id[k] = (ii * pr) - (qq * pi);
            qd[k] = (ii * pi) + (qq * pr);
            double npr = (pr * wr) - (pi * wi);
            pi = (pr * wi) + (pi * wr);
            pr = npr;
            if ((k & 0x3FFF) == 0x3FFF)
            {
                double m = Math.Sqrt((pr * pr) + (pi * pi));
                pr /= m;
                pi /= m;
            }
        }

        FmDeviation dev = FmDeviationMeter.Measure(id, qd, rate);
        Console.WriteLine($"=== fm deviation: {Path.GetFileName(path)} ===");
        Console.WriteLine($"window {from:F2}–{from + (count / (double)rate):F2} s @ {rate} Hz"
                          + (dialHz != 0 ? $", dial −{dialHz:F0} Hz removed" : ""));
        Console.WriteLine($"peak  {dev.PeakHz / 1000:F3} kHz");
        Console.WriteLine($"rms   {dev.RmsHz / 1000:F3} kHz");
        Console.WriteLine($"mean  {dev.MeanHz / 1000:+0.000;-0.000} kHz  (residual carrier offset)");
        return 0;
    }
}
