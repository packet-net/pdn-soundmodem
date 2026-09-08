using System.Diagnostics;
using Packet.SoundModem.Modems;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.TncTest;

/// <summary>One frame the modem handed up, and what the receiver knew about it.</summary>
/// <param name="Bytes">The AX.25 frame, FCS already checked and stripped.</param>
/// <param name="AtSeconds">Roughly where in the track it was heard, to the feed chunk.</param>
/// <param name="OffsetHz">The winning branch's distance from the channel centre, where the
/// modem runs a diversity bank.</param>
/// <param name="EmphasisDb">The winning branch's input pre-emphasis, likewise.</param>
internal sealed record ScoredFrame(byte[] Bytes, double AtSeconds, double? OffsetHz, double? EmphasisDb)
{
    /// <summary>The frame as hex, which is what a run written to JSON is compared on.</summary>
    public string Hex => Convert.ToHexString(Bytes);
}

/// <summary>What one mode made of one track.</summary>
internal sealed record ModeScore(
    string Mode,
    string Receiver,
    int DspRate,
    double? CentreHz,
    IReadOnlyList<ScoredFrame> Frames,
    int Delivered,
    double AudioSeconds,
    TimeSpan Elapsed)
{
    /// <summary>The leaderboard number: frames the modem passed to its host.</summary>
    public int Score => Frames.Count;

    /// <summary>Frames whose bytes nothing else in the run repeated. A busy APRS channel
    /// carries genuine repeats (a beacon every two minutes is the same bytes), so this is
    /// lower than the score by design and is not a duplicate-decode count.</summary>
    public int Distinct => Frames.Select(f => f.Hex).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Frames that parse as AX.25. Everything here passed an FCS, so a frame that
    /// does not parse is a real transmission of something that is not AX.25 - or, rarely, a
    /// short frame that passed the check by luck.</summary>
    public int Ax25Shaped => Frames.Count(f => FrameText.Ax25Header(f.Bytes) is not null);

    /// <summary>Distinct source callsigns, SSID included: how much of the channel's population
    /// the receiver reached, which moves differently from the frame count when the misses are
    /// concentrated on a few weak stations.</summary>
    public IReadOnlyCollection<string> Stations =>
        [.. Frames.Select(f => Source(f.Bytes)).Where(s => s is not null).Distinct(StringComparer.Ordinal)!];

    /// <summary>Total decoded frame bytes.</summary>
    public long Bytes => Frames.Sum(f => (long)f.Bytes.Length);

    /// <summary>How much faster than real time the run went.</summary>
    public double RealTimeFactor =>
        Elapsed.TotalSeconds <= 0 ? double.PositiveInfinity : AudioSeconds / Elapsed.TotalSeconds;

    private static string? Source(byte[] frame)
    {
        string? header = FrameText.Ax25Header(frame);
        if (header is null)
        {
            return null;
        }

        int arrow = header.IndexOf('>', StringComparison.Ordinal);
        return arrow > 0 ? header[..arrow] : null;
    }
}

/// <summary>Runs one mode over one track's audio and counts what came out.</summary>
internal static class Scorer
{
    /// <summary>How much audio goes in per call. Small enough that a frame's reported time is
    /// useful for finding it in the recording, large enough that the per-call overhead is
    /// nothing next to the DSP inside it.</summary>
    private const double FeedSeconds = 0.5;

    /// <summary>
    /// Feeds <paramref name="samples"/> through <paramref name="mode"/> and reports the result.
    /// </summary>
    /// <param name="mode">A <see cref="ModemCatalog"/> mode name.</param>
    /// <param name="samples">Audio already at <paramref name="dspRate"/>.</param>
    /// <param name="dspRate">The rate the modem is built for.</param>
    /// <param name="centreHz">Audio centre override, or null for the mode's own.</param>
    /// <param name="offsetPairs">Diversity-bank width override, where the mode runs one.</param>
    /// <param name="progress">Called with a 0..1 fraction as the run proceeds.</param>
    public static ModeScore Run(
        string mode,
        float[] samples,
        int dspRate,
        double? centreHz,
        int? offsetPairs,
        Action<double>? progress = null)
    {
        var frames = new List<ScoredFrame>();
        int delivered = 0;
        double at = 0;

        IModem modem = ModemCatalog.Create(
            mode, dspRate, _ => delivered++,
            new ModemOptions(CentreFrequencyHz: centreHz, OffsetPairs: offsetPairs));

        // The sink counts deliveries and the event carries the diagnostics; they fire from the
        // same decode, sink first. Recording on the event and reconciling the two afterwards
        // keeps a mode that reports more than it delivers (a monitor-only decode) visible as a
        // disagreement rather than silently inflating the score.
        modem.FrameDecoded += (frame, quality) => frames.Add(
            new ScoredFrame(frame, at, quality.FrequencyOffsetHz, quality.EmphasisDb));

        int chunk = Math.Max(1, (int)(FeedSeconds * dspRate));

        // A track can end flush with a closing flag, which would otherwise be stranded inside
        // the demodulator's filters - a live channel never ends, so nothing in the modem
        // flushes itself. Half a second of silence past the end is the same tail sm-decode adds.
        int tail = dspRate / 2;
        double audioSeconds = (double)samples.Length / dspRate;

        var clock = Stopwatch.StartNew();
        for (int position = 0; position < samples.Length; position += chunk)
        {
            int length = Math.Min(chunk, samples.Length - position);
            at = (position + length) / (double)dspRate;
            modem.Process(samples.AsSpan(position, length));
            progress?.Invoke((double)(position + length) / samples.Length);
        }

        var silence = new float[Math.Max(1, Math.Min(chunk, tail))];
        for (int fed = 0; fed < tail; fed += silence.Length)
        {
            modem.Process(silence.AsSpan(0, Math.Min(silence.Length, tail - fed)));
        }

        clock.Stop();

        return new ModeScore(
            mode, modem.Mode, dspRate, centreHz ?? DefaultCentre(mode),
            frames, delivered, audioSeconds, clock.Elapsed);
    }

    private static double? DefaultCentre(string mode) =>
        ModemCatalog.AcceptsCentreFrequency(mode) ? ModemCatalog.DefaultCentreFrequencyFor(mode) : null;
}
