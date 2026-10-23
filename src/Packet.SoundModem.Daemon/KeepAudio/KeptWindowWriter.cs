using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Packet.SoundModem.Audio;
using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// Writes one kept window: <c>audio.wav</c>, <c>detector.csv</c>, <c>waterfall.png</c> and, last,
/// <c>window.json</c>.
/// </summary>
/// <remarks>
/// Everything is written into a hidden <c>.NAME.partial</c> directory beside the final one and
/// renamed into place at the end, so a directory under its real name is always complete and a
/// reader never meets half a window.
/// </remarks>
internal static class KeptWindowWriter
{
    /// <summary>The CSV's header line.</summary>
    internal const string CsvHeader =
        "utc,busy,busy_fraction,transmitting,lease_subchannel,decided_by,level_db,threshold_db,radio_dbm,detectors";

    /// <summary>Writes the window and returns its final directory.</summary>
    public static string Write(
        string root, KeptWindow window, float[] audio, int sampleRate, int heldSamples,
        IReadOnlyList<DetectorSample> samples)
    {
        string final = Path.Combine(root, window.Id);
        string partial = Path.Combine(root, $".{window.Id}.partial");
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        Directory.CreateDirectory(partial);
        WavFile.WriteMono(Path.Combine(partial, "audio.wav"), audio, sampleRate);
        string csv = Csv(window.From, window.To, samples, out int busySeconds);
        File.WriteAllText(Path.Combine(partial, "detector.csv"), csv, new UTF8Encoding(false));
        File.WriteAllBytes(
            Path.Combine(partial, "waterfall.png"),
            WaterfallPicture.Render(audio, sampleRate, window.From, samples));

        var description = new JsonObject
        {
            ["label"] = window.Label,
            ["from"] = TxLeaseApi.Utc(window.From),
            ["to"] = TxLeaseApi.Utc(window.To),
            ["requestedAt"] = TxLeaseApi.Utc(window.RequestedAt),
            ["sampleRate"] = sampleRate,
            ["seconds"] = (window.To - window.From).TotalSeconds,
            ["heldSeconds"] = Math.Round((double)heldSamples / sampleRate, 3),
            ["busySeconds"] = busySeconds,
            ["note"] = window.Note,
        };
        File.WriteAllText(
            Path.Combine(partial, "window.json"),
            description.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

        Directory.Move(partial, final);
        return final;
    }

    /// <summary>
    /// One row per second of the window: busy if any look inside that second said so, with the
    /// fraction of looks that did, and the reasons from the look that best explains the second
    /// (the busiest one in a busy second, the one nearest to firing in a clear one). A second
    /// nothing was sampled in has its time and nothing else.
    /// </summary>
    internal static string Csv(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyList<DetectorSample> samples, out int busySeconds)
    {
        var text = new StringBuilder();
        text.Append(CsvHeader).Append('\n');
        busySeconds = 0;
        int next = 0;
        for (DateTimeOffset second = from; second < to; second += TimeSpan.FromSeconds(1))
        {
            DateTimeOffset end = second + TimeSpan.FromSeconds(1);
            while (next < samples.Count && samples[next].At < second)
            {
                next++;
            }

            int count = 0;
            int busy = 0;
            bool transmitting = false;
            DetectorSample? best = null;
            for (int i = next; i < samples.Count && samples[i].At < end; i++)
            {
                DetectorSample s = samples[i];
                count++;
                transmitting |= s.Transmitting;
                if (s.Busy)
                {
                    busy++;
                }

                if (best is null || Better(s, best))
                {
                    best = s;
                }
            }

            text.Append(second.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (best is null)
            {
                text.Append(",,,,,,,,,\n");
                continue;
            }

            if (busy > 0)
            {
                busySeconds++;
            }

            text.Append(',').Append(busy > 0 ? '1' : '0')
                .Append(',').Append(((double)busy / count).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(',').Append(transmitting ? '1' : '0')
                .Append(',').Append(best.LeaseSubChannel?.ToString(CultureInfo.InvariantCulture) ?? "")
                .Append(',').Append(best.DecidedBy ?? "")
                .Append(',').Append(DetectorSample.Number(best.Reading?.LevelDb))
                .Append(',').Append(DetectorSample.Number(best.Reading?.ThresholdDb))
                .Append(',').Append(DetectorSample.Number(best.RadioSignalDbm))
                .Append(",\"").Append(best.Detectors.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"\n");
        }

        return text.ToString();
    }

    /// <summary>Whether <paramref name="s"/> explains its second better than <paramref name="than"/>:
    /// busy beats clear, then the reading nearer to (or further over) its threshold.</summary>
    private static bool Better(DetectorSample s, DetectorSample than)
    {
        if (s.Busy != than.Busy)
        {
            return s.Busy;
        }

        double mine = s.Reading is BusyReading r ? r.MarginDb : double.NegativeInfinity;
        double theirs = than.Reading is BusyReading t ? t.MarginDb : double.NegativeInfinity;
        return mine > theirs;
    }
}
