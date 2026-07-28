using System.Globalization;
using System.Text.Json;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota score</c> — convert a captured IQ48 pass and score every burst in it.
/// </summary>
/// <remarks>
/// This is the offline half of a campaign pass and the only entry point that produces numbers
/// worth quoting. It streams: capture → audio → one demodulator → per-burst scores, in constant
/// memory, so an hour-long pass is scored the same way a ten-second one is.
/// </remarks>
internal static class ScoreCommand
{
    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("in"))
        {
            Console.Error.WriteLine("""
                sm-ota score --in <iq48.wav> [options]

                Capture:
                  --dial-hz <Hz>       IQ-baseband frequency of the suppressed carrier
                                       (our TX offset from the capture's tune frequency).
                                       Default 2000, matching `sm-ota burst`.
                  --ssb-low <Hz>       passband edge above the carrier (default 150)
                  --ssb-high <Hz>      upper edge (default 3450); narrow these to emulate a
                                       tighter RX filter and measure what it costs

                Schedule — what was transmitted, so the bits can be regenerated:
                  --wn <n>             waveform number (default 6)
                  --interleaver <k>    short|long (default short)
                  --constraint <7|9>   default 7
                  --blocks <n>         interleaver blocks per burst (default 1)
                  --count <n>          bursts in the pass (default 1)
                  --seed <n>           payload seed of the first burst (default 1); each
                                       subsequent burst uses seed+1, as the transmitter does
                  --at <s,s,...>       expected start time of each burst, seconds into the
                                       capture. Omit to match by order alone — but then a
                                       missed burst cannot be told from a late one.

                Output:
                  --csv <path>         one row per burst, for the campaign record
                  --audio <path>       also write the converted 9600 Hz audio
                  --diagnostics        print the demodulator's acquisition/WID trace under
                                       each burst — an autopsy for bursts that never lock

                Bursts are found by acquisition, never by slicing at the scheduled times: a
                burst the receiver never heard is the result that matters most, and slicing
                would hide it.
                """);
            return a is null ? 2 : 0;
        }

        string inPath = a.Req("in");

        // A schedule file describes a mixed pass — different waveforms, seeds and SNRs — which
        // the flags below cannot. It also carries the burst positions the pass actually
        // achieved, so nothing has to be retyped from a log.
        if (a.Str("schedule", null) is { } schedulePath)
        {
            return ScoreAgainstSchedule(a, inPath, schedulePath);
        }

        int wn = a.Int("wn", 6);
        var interleaver = a.Str("interleaver", "short").Equals("long", StringComparison.OrdinalIgnoreCase)
            ? Ms110dInterleaverKind.Long
            : Ms110dInterleaverKind.Short;
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = interleaver,
            ConstraintLength = a.Int("constraint", 7),
            PreambleSuperframes = a.Int("preamble", 3),
        };

        int blocks = a.Int("blocks", 1);
        int count = a.Int("count", 1);
        int seed = a.Int("seed", 1);
        double[] times = a.Has("at")
            ? a.Str("at", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.Parse(t.Trim(), CultureInfo.InvariantCulture)).ToArray()
            : [];
        if (times.Length > 0 && times.Length != count)
        {
            throw new ArgumentException($"--at lists {times.Length} times but --count is {count}");
        }

        int payloadBits = Ms110dReferenceBits.PayloadBitsForBlocks(wn, interleaver, blocks);
        var schedule = new List<ScheduledBurst>(count);
        for (int k = 0; k < count; k++)
        {
            schedule.Add(new ScheduledBurst(
                new Ms110dReferenceBits(settings, payloadBits, seed + k),
                times.Length > 0 ? times[k] : -1));
        }

        var options = new IqToAudioOptions
        {
            OutputRate = 9600,
            DialHz = a.Dbl("dial-hz", 2000),
            SsbLowHz = a.Dbl("ssb-low", 150),
            SsbHighHz = a.Dbl("ssb-high", 3450),
            NormalisePeak = 0f,
        };

        string? audioPath = a.Str("audio", null);
        CaptureScore score = new BurstScorer(schedule, new BurstScorerOptions
        {
            CaptureDiagnostics = a.Has("diagnostics"),
        }).Score(Convert(inPath, options, audioPath));

        Report(inPath, wn, score);
        if (a.Str("csv", null) is { } csvPath)
        {
            WriteCsv(csvPath, score);
            Console.Error.WriteLine($"wrote {csvPath}");
        }

        // Zero even when bursts were missed. A missed burst is a measurement result — at the
        // bottom of an E2 ladder it is the expected one — and a non-zero exit would make every
        // low-SNR pass look like the tool had failed.
        return 0;
    }

    /// <summary>Scores against a schedule or a manifest, which is how a real pass is scored: the
    /// bursts differ from one another, and their positions came from what actually happened.</summary>
    private static int ScoreAgainstSchedule(Args a, string inPath, string schedulePath)
    {
        // A manifest is a superset of a schedule — take the burst positions from it when it is
        // one, so a pass is scored where the transmissions really were.
        (CampaignSchedule schedule, IReadOnlyList<double>? starts, string revision) =
            CampaignFiles.LoadScheduleOrManifest(schedulePath);

        var options = new IqToAudioOptions
        {
            OutputRate = a.Int("out-rate", 9600),
            DialHz = a.Dbl("dial-hz", schedule.OffsetHz),
            SsbLowHz = a.Dbl("ssb-low", 150),
            SsbHighHz = a.Dbl("ssb-high", 3450),
            NormalisePeak = 0f,
        };

        List<ScheduledBurst> bursts = schedule.ToScorerSchedule(starts);
        CaptureScore score = new BurstScorer(bursts, new BurstScorerOptions
        {
            OccupiedLowHz = options.SsbLowHz,
            OccupiedHighHz = options.SsbHighHz,
            CaptureDiagnostics = a.Has("diagnostics"),
        }).Score(Convert(inPath, options, a.Str("audio", null)));

        Console.WriteLine();
        Console.WriteLine($"schedule \"{schedule.Name}\" — {schedule.Bursts.Count} burst(s), "
                          + $"modem {revision}");
        if (schedule.Notes is { Length: > 0 } notes)
        {
            Console.WriteLine(notes);
        }

        ReportWithSchedule(inPath, schedule, score);
        if (a.Str("csv", null) is { } csvPath)
        {
            WriteCsv(csvPath, score, schedule);
            Console.Error.WriteLine($"wrote {csvPath}");
        }

        return 0;
    }

    /// <summary>Streams the capture through the converter, so the scorer never sees a file and
    /// the pass length never becomes a memory question.</summary>
    private static IEnumerable<ReadOnlyMemory<float>> Convert(
        string path, IqToAudioOptions options, string? audioPath)
    {
        const int blockFrames = 1 << 16;
        using var reader = new PcmWavReader(path);
        if (reader.Channels != 2)
        {
            throw new InvalidDataException($"expected a 2-channel IQ capture, found {reader.Channels}");
        }

        var converter = new StreamingIqToAudioConverter(new IqToAudioOptions
        {
            InputRate = reader.SampleRate,
            OutputRate = options.OutputRate,
            DialHz = options.DialHz,
            SsbLowHz = options.SsbLowHz,
            SsbHighHz = options.SsbHighHz,
            NormalisePeak = 0f,
        });
        using PcmWavWriter? audio = audioPath is null
            ? null
            : new PcmWavWriter(audioPath, options.OutputRate, channels: 1);

        var input = new short[blockFrames * 2];
        var output = new float[converter.MaxOutputFor(blockFrames) + converter.MaxFlushOutput];

        int frames;
        while ((frames = reader.ReadFrames(input)) > 0)
        {
            int wrote = converter.Process(input.AsSpan(0, frames * 2), output);
            audio?.WriteSamples(output.AsSpan(0, wrote));
            yield return output.AsMemory(0, wrote);
        }

        int tail = converter.Flush(output);
        audio?.WriteSamples(output.AsSpan(0, tail));
        yield return output.AsMemory(0, tail);
    }

    private static void Report(string path, int wn, CaptureScore score)
    {
        Console.WriteLine();
        Console.WriteLine($"=== score: {Path.GetFileName(path)} — {score.AudioSeconds:F1} s, WN{wn} ===");
        Console.WriteLine($"{"#",3} {"start s",9} {"WN",4} {"CFO Hz",8} {"SNR dB",8} " +
                          $"{"coded BER",11} {"uncoded BER",12} {"blocks",7}  end");

        foreach (BurstScore b in score.Bursts)
        {
            string snr = b.Snr is null ? "—" : b.Snr.SnrDb.ToString("F1", CultureInfo.InvariantCulture);
            string coded = b.Scheduled ? Rate(b.CodedBer) : "unscheduled";
            string uncoded = b.Scheduled ? Rate(b.UncodedBer) : "—";
            string wid = b.WaveformNumber?.ToString(CultureInfo.InvariantCulture) ?? "—";
            Console.WriteLine($"{b.Index,3} {b.StartSeconds,9:F2} {wid,4} {b.CfoHz,8:F1} {snr,8} " +
                              $"{coded,11} {uncoded,12} {b.Blocks,7}  {b.Reason}"
                              + (b.Scheduled && !b.WidCorrect ? "  WID MISMATCH" : ""));
            foreach (string line in b.Diagnostics)
            {
                Console.WriteLine($"      {line}");
            }
        }

        int scored = score.Bursts.Count(b => b.Scheduled);
        long payloadBits = score.Bursts.Where(b => b.Scheduled).Sum(b => b.PayloadBits);
        long payloadErrors = score.Bursts.Where(b => b.Scheduled).Sum(b => b.PayloadErrors);
        long uncodedBits = score.Bursts.Where(b => b.Scheduled).Sum(b => b.UncodedBits);
        long uncodedErrors = score.Bursts.Where(b => b.Scheduled).Sum(b => b.UncodedErrors);
        long uncodedErasures = score.Bursts.Where(b => b.Scheduled).Sum(b => b.UncodedErasures);

        Console.WriteLine();
        Console.WriteLine($"scored {scored} burst(s); {score.Bursts.Count - scored} unscheduled; "
                          + $"{score.Missed.Count} MISSED");

        // Pooled across every scored burst. That is the figure of merit for repeats at one
        // operating point and NOT a meaningful number for a ladder, where the bursts were
        // deliberately sent at different SNRs — say so rather than let the row be quoted.
        if (payloadBits > 0)
        {
            Console.WriteLine($"coded   {payloadErrors}/{payloadBits} = {Rate(payloadErrors / (double)payloadBits)}"
                              + (scored > 1 ? "   (pooled — per-burst rows above)" : ""));
        }

        if (uncodedBits > 0)
        {
            Console.WriteLine($"uncoded {uncodedErrors}/{uncodedBits} = {Rate(uncodedErrors / (double)uncodedBits)}"
                              + (scored > 1 ? "   (pooled — per-burst rows above)" : "")
                              + (uncodedErasures > 0
                                  ? $"   ({uncodedErasures} erasure(s) excluded — see uncodedErasures)"
                                  : ""));
        }

        // A missed burst is the headline, so it is said plainly rather than left to be inferred
        // from a count that happens to be smaller than expected.
        foreach (ScheduledBurst missed in score.Missed)
        {
            Console.WriteLine($"MISSED: WN{missed.Reference.Settings.WaveformNumber} seed "
                              + $"{missed.Reference.Seed?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                              + (missed.ExpectedSeconds >= 0 ? $" expected at {missed.ExpectedSeconds:F1} s" : ""));
        }
    }

    /// <summary>The per-burst table, with the requested SNR beside the measured one — the
    /// comparison a ladder exists to make, and one a bare score table cannot show.</summary>
    private static void ReportWithSchedule(string path, CampaignSchedule schedule, CaptureScore score)
    {
        Console.WriteLine();
        Console.WriteLine($"=== score: {Path.GetFileName(path)} — {score.AudioSeconds:F1} s ===");
        Console.WriteLine($"{"#",3} {"start s",9} {"WN",4} {"asked",7} {"got",7} {"CFO Hz",8} "
                          + $"{"coded BER",11} {"uncoded BER",12}  end");

        int scheduleIndex = 0;
        foreach (BurstScore b in score.Bursts)
        {
            CampaignBurst? row = b.Scheduled && scheduleIndex < schedule.Bursts.Count
                ? schedule.Bursts[scheduleIndex++]
                : null;
            string asked = row?.SnrDb is double s ? s.ToString("F1", CultureInfo.InvariantCulture) : "—";
            string got = b.Snr is null ? "—" : b.Snr.SnrDb.ToString("F1", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{b.Index,3} {b.StartSeconds,9:F2} {b.WaveformNumber?.ToString(CultureInfo.InvariantCulture) ?? "—",4} "
                + $"{asked,7} {got,7} {b.CfoHz,8:F1} "
                + $"{(b.Scheduled ? Rate(b.CodedBer) : "unscheduled"),11} "
                + $"{(b.Scheduled ? Rate(b.UncodedBer) : "—"),12}  {b.Reason}"
                + (b.Scheduled && !b.WidCorrect ? "  WID MISMATCH" : ""));
            foreach (string line in b.Diagnostics)
            {
                Console.WriteLine($"      {line}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"scored {score.Bursts.Count(b => b.Scheduled)} burst(s); "
                          + $"{score.Bursts.Count(b => !b.Scheduled)} unscheduled; {score.Missed.Count} MISSED");
        foreach (ScheduledBurst missed in score.Missed)
        {
            Console.WriteLine($"MISSED: WN{missed.Reference.Settings.WaveformNumber} seed "
                              + $"{missed.Reference.Seed?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                              + (missed.ExpectedSeconds >= 0 ? $" expected at {missed.ExpectedSeconds:F1} s" : ""));
        }
    }

    private static string Rate(double value)
        => double.IsNaN(value) ? "—"
            : value == 0 ? "0"
            : value.ToString("0.00E+00", CultureInfo.InvariantCulture);

    private static void WriteCsv(string path, CaptureScore score, CampaignSchedule? schedule = null)
    {
        using var w = new StreamWriter(path);
        // requestedSnrDb sits beside the measured one so the two are never separated: a row that
        // records only what was measured cannot say whether the rig delivered what it was asked.
        w.WriteLine("index,startSeconds,endSeconds,acquired,scheduled,wn,interleaver,cfoHz,widCorrect,"
                    + "requestedSnrDb,snrDb,payloadBits,payloadErrors,uncodedBits,uncodedErrors,"
                    + "uncodedErasures,blocks,reason,turboConverged,turboReverted,turboAborted");
        int scheduleIndex = 0;
        foreach (BurstScore b in score.Bursts)
        {
            w.WriteLine(string.Join(',',
                b.Index, F(b.StartSeconds), F(b.EndSeconds), b.Acquired, b.Scheduled,
                b.WaveformNumber?.ToString(CultureInfo.InvariantCulture) ?? "", b.Interleaver?.ToString() ?? "",
                F(b.CfoHz), b.WidCorrect,
                b.Scheduled && schedule is not null && scheduleIndex < schedule.Bursts.Count
                    && schedule.Bursts[scheduleIndex++].SnrDb is double asked ? F(asked) : "",
                b.Snr is null ? "" : F(b.Snr.SnrDb),
                b.PayloadBits, b.PayloadErrors, b.UncodedBits, b.UncodedErrors, b.UncodedErasures,
                b.Blocks,
                b.Reason?.ToString() ?? "", b.TurboConverged, b.TurboReverted, b.TurboAborted));
        }

        foreach (ScheduledBurst missed in score.Missed)
        {
            // Missed bursts are rows too. A campaign summary built by counting rows must not be
            // able to miss them by their absence.
            w.WriteLine(string.Join(',',
                -1, F(missed.ExpectedSeconds), "", false, true,
                missed.Reference.Settings.WaveformNumber, missed.Reference.Settings.Interleaver,
                "", false, "", "", missed.Reference.PayloadBits.Length, missed.Reference.PayloadBits.Length,
                0, 0, 0, 0, "Missed", 0, 0, 0));
        }
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
