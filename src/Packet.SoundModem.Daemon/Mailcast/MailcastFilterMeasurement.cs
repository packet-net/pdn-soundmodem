using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// What "Measure my filter" found: the rig's receive passband edges, its width, and the dial
/// that follows from them, or a plain note instead of a number where the edges did not come out
/// clean.
/// </summary>
/// <param name="LowHz">The passband's lower edge, in audio Hz, or null when it could not be found.</param>
/// <param name="HighHz">Its upper edge, in audio Hz, or null likewise.</param>
/// <param name="WidthHz">The gap between them, or null when either edge is.</param>
/// <param name="SuggestedDialHz">The dial <see cref="MailcastOnAir.SuggestedDialHz"/> gives for
/// this passband's middle, or null when it has none to work from.</param>
/// <param name="Note">A plain-language caveat: the passband measured narrower than about
/// 2.4 kHz (use the widest or DATA filter instead), or the measurement came out unclear. Null
/// when there is nothing to add.</param>
internal sealed record MailcastFilterMeasurement(
    double? LowHz, double? HighHz, double? WidthHz, double? SuggestedDialHz, string? Note)
{
    /// <summary>As the API and the page read it. Frequencies to the nearest Hz, the dial to the
    /// nearest 10 Hz it already is, read as MHz.</summary>
    internal JsonObject ToJson() => new()
    {
        ["lowHz"] = LowHz is { } low ? Math.Round(low) : null,
        ["highHz"] = HighHz is { } high ? Math.Round(high) : null,
        ["widthHz"] = WidthHz is { } width ? Math.Round(width) : null,
        ["suggestedDialMhz"] = SuggestedDialHz is { } dial ? Math.Round(dial / 1e6, 6) : null,
        ["note"] = Note,
    };
}

/// <summary>
/// Finds a sound-card rig's receive passband edges from an averaged spectrum of its own received
/// audio - no signal needed, just the ordinary noise the filter already shapes.
/// </summary>
/// <remarks>
/// <para><b>The method.</b> A receive filter passes noise flat across its passband and rolls off
/// outside it, so the averaged spectrum of enough of that noise traces the filter's own shape:
/// a plateau with two shoulders. The flat middle's level is taken as the strongest bin across the
/// search range (noise has no sharp peak to mistake for it: the loudest bin is somewhere on the
/// plateau). The edges are where the averaged power first falls <see cref="EdgeDropDb"/> - 6 dB,
/// the usual way a filter's own bandwidth is quoted - below that level, scanning outward from the
/// peak in each direction. Averaging matters: one FFT frame of noise is too ragged bin-to-bin for
/// a 6 dB step to mean anything, which is why the measurement runs for about
/// <see cref="MailcastFilterMeasurer.TargetSeconds"/> of audio rather than a single snapshot.</para>
/// <para>Scanning stops, and the result says so, if a shoulder never drops 6 dB before the search
/// range runs out (an edge below <see cref="SearchLowHz"/> or above <see cref="SearchHighHz"/>,
/// or a passband so flat across the whole range that nothing reads as a plateau at all).</para>
/// </remarks>
internal static class MailcastFilterAnalysis
{
    /// <summary>How far below the flat middle's level an edge is taken to be: the usual way a
    /// filter's own passband is quoted.</summary>
    internal const double EdgeDropDb = 6.0;

    /// <summary>Below this a passband is too narrow for MS110D's full width: a plain note says
    /// so instead of leaving the operator to work it out from the number.</summary>
    internal const double NarrowFilterWidthHz = 2400;

    /// <summary>The lowest audio frequency considered: below the lowest a sideband filter's
    /// shoulder plausibly sits, and clear of mains hum and its low harmonics.</summary>
    internal const double SearchLowHz = 200;

    /// <summary>The highest audio frequency considered: above the highest an SSB filter's upper
    /// shoulder plausibly reaches.</summary>
    internal const double SearchHighHz = 3500;

    /// <summary>
    /// Analyses an averaged power spectrum (linear power per bin, bin 0 = DC, as
    /// <see cref="Packet.SoundModem.Dsp.WaterfallSource"/> produces once its bytes are read back
    /// to linear power) and returns the passband it describes.
    /// </summary>
    internal static MailcastFilterMeasurement Analyze(ReadOnlySpan<double> powerLinear, double binWidthHz)
    {
        int lowBin = Math.Max(1, (int)Math.Round(SearchLowHz / binWidthHz));
        int highBin = Math.Min(powerLinear.Length - 1, (int)Math.Round(SearchHighHz / binWidthHz));
        if (highBin - lowBin < 4)
        {
            return Unclear("the station's audio has too few bins between 200 and 3500 Hz to measure a filter");
        }

        int peakBin = lowBin;
        double peakPower = powerLinear[lowBin];
        for (int bin = lowBin + 1; bin <= highBin; bin++)
        {
            if (powerLinear[bin] > peakPower)
            {
                peakPower = powerLinear[bin];
                peakBin = bin;
            }
        }

        if (peakPower <= 0)
        {
            return Unclear("no audio was heard to measure");
        }

        double thresholdPower = peakPower * Math.Pow(10, -EdgeDropDb / 10);

        int? lowEdgeBin = ScanDown(powerLinear, peakBin, lowBin, thresholdPower);
        int? highEdgeBin = ScanUp(powerLinear, peakBin, highBin, thresholdPower);
        if (lowEdgeBin is null || highEdgeBin is null)
        {
            return Unclear(
                "the signal never fell 6 dB below its flat middle before the search ran out, so the "
                + "edges are unclear; try again with the rig quiet for longer, or type the edges "
                + "from your filter's datasheet instead");
        }

        double lowHz = lowEdgeBin.Value * binWidthHz;
        double highHz = highEdgeBin.Value * binWidthHz;
        if (highHz <= lowHz)
        {
            return Unclear("the measured edges came out the wrong way round; try again");
        }

        double widthHz = highHz - lowHz;
        double dialHz = MailcastOnAir.SuggestedDialHz(MailcastOnAir.PassbandCentreHz(lowHz, highHz));
        string? note = widthHz < NarrowFilterWidthHz
            ? $"that is narrower than about 2.4 kHz: switch to your rig's widest or \"DATA\" filter "
                + "before using this dial, or MS110D will lose frames at its edges"
            : null;
        return new MailcastFilterMeasurement(lowHz, highHz, widthHz, dialHz, note);
    }

    /// <summary>The first bin below <paramref name="fromBin"/> whose power is under
    /// <paramref name="thresholdPower"/>, or null if none down to <paramref name="floorBin"/> is.</summary>
    private static int? ScanDown(ReadOnlySpan<double> powerLinear, int fromBin, int floorBin, double thresholdPower)
    {
        for (int bin = fromBin; bin >= floorBin; bin--)
        {
            if (powerLinear[bin] < thresholdPower)
            {
                return bin;
            }
        }

        return null;
    }

    /// <summary>The first bin above <paramref name="fromBin"/> whose power is under
    /// <paramref name="thresholdPower"/>, or null if none up to <paramref name="ceilingBin"/> is.</summary>
    private static int? ScanUp(ReadOnlySpan<double> powerLinear, int fromBin, int ceilingBin, double thresholdPower)
    {
        for (int bin = fromBin; bin <= ceilingBin; bin++)
        {
            if (powerLinear[bin] < thresholdPower)
            {
                return bin;
            }
        }

        return null;
    }

    private static MailcastFilterMeasurement Unclear(string why) => new(null, null, null, null,
        $"the measurement is unclear: {why}");
}
