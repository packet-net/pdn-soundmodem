using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// What the radio's transmit filter has to pass for a given set of modems: where each one sits
/// in the audio band, and the high cut that clears the highest of them.
/// </summary>
/// <remarks>
/// <para>The transmit filter is a <b>global, persistent</b> radio setting — whatever last touched
/// the radio — so a station inherits some previous session's filter and a mode wider than it is
/// truncated on air with nothing said. A band-planned station states the high cut from the plan
/// (<see cref="BandPlanner.TransmitFilterHighHz"/>); a station placed by audio centre has no plan
/// to read it off, so the modems themselves are asked.</para>
/// <para>Widths come from <see cref="ModemBandProbe"/>, the same probe the band planner and the
/// waterfall use, so what is drawn, what is planned and what the filter passes cannot disagree.
/// ARDOP is the exception, as it is everywhere else here: its bandwidth is negotiated per session,
/// so there is nothing to measure and the configured cap (or the widest it can reach) stands in.
/// </para>
/// </remarks>
internal static class TransmitFilterPlan
{
    /// <summary>One modem's occupied audio band.</summary>
    /// <param name="SubChannel">KISS sub-channel, for naming it in a message.</param>
    /// <param name="Mode">Its mode string.</param>
    /// <param name="LowHz">Lower edge of the occupied band.</param>
    /// <param name="HighHz">Upper edge of the occupied band.</param>
    internal readonly record struct Band(int SubChannel, string Mode, double LowHz, double HighHz);

    /// <summary>
    /// Each modem's occupied audio band, at the centre it is configured for. Modems that cannot
    /// be measured are left out rather than guessed at: a filter derived from a guess would be
    /// worse than the one the radio already had.
    /// </summary>
    internal static IReadOnlyList<Band> Bands(IReadOnlyList<ModemConfig> modems, int dspRate)
    {
        ArgumentNullException.ThrowIfNull(modems);

        var bands = new List<Band>(modems.Count);
        foreach (ModemConfig modem in modems)
        {
            if (TryMeasure(modem, dspRate, out Band band))
            {
                bands.Add(band);
            }
        }

        return bands;
    }

    /// <summary>
    /// The transmit-filter high cut these bands need, or null when none of them could be
    /// measured — in which case the radio's own filter is left alone.
    /// </summary>
    internal static int? HighCutFor(IReadOnlyList<Band> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return bands.Count == 0 ? null : BandPlanner.HighCutClearing(bands.Max(b => b.HighHz));
    }

    private static bool TryMeasure(ModemConfig modem, int dspRate, out Band band)
    {
        band = default;
        if (DaemonConfig.IsArdop(modem.Mode))
        {
            // Nothing to modulate a probe frame through — the engine is a whole TNC and its
            // width is a per-session negotiation. Plan for the cap, or for the widest it can ask
            // for, exactly as BandPlanner does.
            double centre = modem.Frequency ?? ArdopChannelShift.NativeCentreHz;
            double half = (modem.Bandwidth ?? ArdopChannelShift.WidestBandwidthHz) / 2;
            band = new Band(modem.SubChannel, modem.Mode, centre - half, centre + half);
            return true;
        }

        try
        {
            // Built at the centre this modem will actually transmit on: unlike the planner's
            // width-only question, where a band sits is the whole point here. A fixed-centre
            // mode is built with no centre at all — passing one throws, and by this point the
            // start-up checks have already rejected a config that states one.
            IModem probe = ModemCatalog.Create(
                modem.Mode, dspRate, static _ => { },
                new ModemOptions(CentreFrequencyHz: ModemCatalog.AcceptsCentreFrequency(modem.Mode)
                    ? modem.Frequency
                    : null));
            if (!ModemBandProbe.TryMeasure(probe, dspRate, out double low, out double high))
            {
                return false;
            }

            band = new Band(modem.SubChannel, modem.Mode, low, high);
            return true;
        }
        catch (ArgumentException)
        {
            // Unknown mode, or a centre on a mode that has none: both are reported far more
            // usefully by the start-up checks that run before this.
            return false;
        }
    }
}
